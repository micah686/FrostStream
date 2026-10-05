#!/usr/bin/env python3
"""Verify fresh Compose installs. CI ONLY: removes the profiles' persistent volumes."""
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.request
import urllib.error


def request(path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    message = urllib.request.Request('http://127.0.0.1:25000' + path, data=data,
                                    headers={'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(message, timeout=30) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


def wait(check, description):
    deadline = time.monotonic() + 600
    while time.monotonic() < deadline:
        try:
            if check():
                return
        except (OSError, ValueError, subprocess.CalledProcessError):
            pass
        time.sleep(3)
    raise RuntimeError('Timed out: ' + description)


def smoke(profile, mode):
    command = ['docker', 'compose', '--project-directory', str(profile), '-f', str(profile / 'docker-compose.yaml')]
    def compose(*args, **kwargs):
        return subprocess.run(command + list(args), check=True, **kwargs)
    try:
        compose('up', '-d', '--build')
        wait(lambda: request('/api/system/capabilities')[0] == 200, mode + ' API readiness')
        status, body = request('/api/system/capabilities')
        capabilities = json.loads(body)
        assert capabilities['deploymentMode'] == mode
        assert capabilities['backups']['full'] and capabilities['backups']['verification']
        assert capabilities['backups']['differential'] == (mode == 'Full')
        assert capabilities['backups']['pointInTimeRecovery'] == (mode == 'Full')
        assert request('/')[0] == 200
        assert request('/api/auth/me')[0] == (200 if mode == 'Lite' else 401)
        if mode == 'Lite':
            status, body = request('/api/global/backups')
            assert status == 200
            repository = json.loads(body)
            assert repository['databasePath'] == '/data/frostreamlitedb'
            assert repository['backupDirectory'] == '/data/backups'
            assert repository['keyRingPath'] == '/data/frostreamlitedb.keys'
            assert request('/api/global/backups', {'type': 'diff'})[0] == 400
            assert request('/api/global/backups/verify', {'deep': True})[0] == 400
            with ThreadPoolExecutor(max_workers=4) as writers:
                futures = [writers.submit(request, '/api/storage/local/create',
                    {'key': f'release-{i}', 'protocol': 'Local', 'path': f'/data/release-{i}'}) for i in range(8)]
                status, body = request('/api/global/backups', {'name': 'release', 'type': 'full'})
                assert status == 202
                job = json.loads(body)
                assert job['status'] == 'completed'
                label = job['label']
                assert all(future.result()[0] in (200, 201) for future in futures)
            assert request('/api/global/backups/verify', {'label': label})[0] == 202
            manifest = '/data/backups/' + label + '.recovery.json'
            compose('exec', '-T', 'lite', 'sh', '-ec',
                    'cp "$1" "$1.saved"; printf invalid > "$1"', 'sh', manifest)
            assert request('/api/global/backups/verify', {'label': label})[0] == 422
            compose('exec', '-T', 'lite', 'sh', '-ec', 'mv "$1.saved" "$1"', 'sh', manifest)
            # Exercise the documented offline file replacement with the complete key ring.
            container = compose('ps', '-q', 'lite', capture_output=True, text=True).stdout.strip()
            compose('stop', 'lite')
            subprocess.run(['docker', 'run', '--rm', '--volumes-from', container,
                '--entrypoint=sh', 'localhost/froststream-lite:latest', '-ec',
                'mkdir /data/pre-restore; '
                'mv /data/frostreamlitedb /data/pre-restore/; '
                'for suffix in -wal -shm; do '
                'if [ -f "/data/frostreamlitedb$suffix" ]; then mv "/data/frostreamlitedb$suffix" /data/pre-restore/; fi; done; '
                'mv /data/frostreamlitedb.keys /data/pre-restore/; '
                'cp "/data/backups/$1" /data/frostreamlitedb; '
                'cp -a "/data/backups/$1.keys" /data/frostreamlitedb.keys', 'sh', label], check=True)
            compose('start', 'lite')
            wait(lambda: request('/api/auth/me')[0] == 200, 'Lite restored host readiness')
            assert request('/api/global/backups/verify', {'label': label})[0] == 202
            # Runtime tools must execute without any network access.
            image = 'localhost/froststream-lite:latest'
            subprocess.run(['docker', 'run', '--rm', '--network=none', '--entrypoint=sh', image, '-ec',
                'tools/ffmpeg -version; tools/ffprobe -version; tools/deno --version; case "$(uname -m)" in aarch64) tools/yt-dlp_linux_aarch64 --version ;; *) tools/yt-dlp_linux --version ;; esac'], check=True)
        else:
            # Full protects shared backup routes; the HTTP service remains reachable internally.
            assert request('/api/global/backups')[0] == 401
            compose('exec', '-T', 'frontend', 'wget', '-qO-', 'http://backupservice:8080/internal/backups/jobs', stdout=subprocess.DEVNULL)
            def internal(path, payload=None):
                arguments = ['exec', '-T', 'frontend', 'wget', '-qO-']
                if payload is not None:
                    arguments += ['--header=Content-Type:application/json', '--post-data=' + json.dumps(payload)]
                result = compose(*arguments, 'http://backupservice:8080' + path, capture_output=True, text=True)
                return json.loads(result.stdout)
            def completed(job_id):
                job = internal('/internal/backups/jobs/' + job_id)
                assert job['status'] != 'failed', job.get('errorMessage')
                return job['status'] == 'completed'
            backup = internal('/internal/backups/jobs', {'name': 'release', 'type': 'full'})
            wait(lambda: completed(backup['jobId']), 'Full backup creation')
            repository = internal('/internal/backups/backups')
            assert repository['repositoryOk'] and repository['backups']
            latest = repository['backups'][0]
            assert latest['openBaoExportPresent']
            verify = internal('/internal/backups/verify', {'label': latest['label'], 'deep': False})
            wait(lambda: completed(verify['jobId']), 'Full backup verification')
            wait(lambda: compose('exec', '-T', 'frontend', 'wget', '-qO-',
                'http://authentik:9000/-/health/ready/', stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL).returncode == 0, 'Full identity provider readiness')
        print(mode + ': fresh install, frontend, capabilities, access and adapter checks passed')
    except BaseException:
        # Logs can contain credentials. Retain them privately in CI, never print them.
        with (profile / 'smoke.log').open('w') as log:
            subprocess.run(command + ['logs', '--no-color'], stdout=log, stderr=log)
        raise
    finally:
        compose('down', '--volumes', '--remove-orphans')


if __name__ == '__main__':
    if os.environ.get('CI') != 'true':
        raise SystemExit('This destructive fresh-install check runs only with CI=true on a disposable runner.')
    app = Path(__file__).resolve().parent.parent
    for mode in ('Lite', 'Full'):
        smoke(app / ('docker-compose-' + mode.lower()), mode)
