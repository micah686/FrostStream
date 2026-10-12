#!/usr/bin/env python3
"""Exercise packaged Lite restart/recovery in an explicitly owned disposable installation."""
import argparse
import json
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request
import yaml

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('package', type=Path)
p.add_argument('--project', required=True)
p.add_argument('--base-url', default='http://127.0.0.1:25000')
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
assert a.project.startswith('froststream-phase6-'), 'Use a disposable Phase 6 Compose project'
assert (a.package/'INSTALLATION').is_file()
package = a.package.resolve()
compose = ['docker', 'compose', '-p', a.project, '-f', str(package/'docker-compose.yaml')]
def run(*args):
    subprocess.run([*compose, *args], check=True, stdout=subprocess.DEVNULL)
def request(path, body=None, method=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(a.base_url+path, data=data, method=method, headers={'Content-Type':'application/json'})
    with urllib.request.urlopen(req, timeout=20) as r:
        data = r.read()
        return json.loads(data) if r.headers.get_content_type() == 'application/json' else data

def ready():
    until = time.monotonic()+300
    while time.monotonic()<until:
        try:
            if request('/readyz') == b'Healthy': return
        except (OSError, urllib.error.HTTPError): pass
        time.sleep(1)
    raise TimeoutError('Packaged application did not become ready')

run('up', '-d', '--no-build'); ready()
status = request('/api/system/integrations')
assert status['search'] in ('Ready', 1)
assert status['liveChatSchema'] in ('Disabled', 0)
request('/api/user/cookies/phase6-before', {'content':'# Netscape HTTP Cookie File\n.example.test\tTRUE\t/\tFALSE\t2147483647\tsession\tphase6-secret\n'}, 'PUT')
backup = request('/api/global/backups', {'name':'phase6-owned-recovery', 'type':'full'})
until = time.monotonic()+120
while backup['status'] not in ('completed', 'failed') and time.monotonic()<until:
    time.sleep(1); backup = request('/api/global/backups/jobs/'+backup['jobId'])
assert backup['status'] == 'completed', backup
label = backup['label']
assert label and Path(label).name == label
request('/api/user/cookies/phase6-after', {'content':'after backup'}, 'PUT')
run('restart', 'lite'); ready()
assert len(request('/api/user/cookies')) == 2
run('stop', 'lite')
run('run', '--rm', '--no-deps', 'lite', '--Recovery:Snapshot=/data/backups/'+label, '--Recovery:Destination=/data/recovered')
# The command must refuse to replace the now-existing destination.
refused = subprocess.run([*compose, 'run', '--rm', '--no-deps', 'lite', '--Recovery:Snapshot=/data/backups/'+label, '--Recovery:Destination=/data/recovered'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
assert refused.returncode != 0
spec = {'services': {'lite': {'environment': {'Persistence__Sqlite__Path':'/data/recovered/core.sqlite', 'Secrets__Local__KeyRingPath':'/data/recovered/core.sqlite.keys'}}}}
override = package/'recovered.override.yaml'; override.write_text(yaml.safe_dump(spec))
compose += ['-f', str(override)]
run('up', '-d', '--no-build'); ready()
profiles = request('/api/user/cookies')
assert [item['profileKey'] for item in profiles] == ['phase6-before'], profiles
assert request('/api/search/status')['state'] in ('Ready', 1)
a.output.write_text(json.dumps({'scope':'Packaged Lite fresh install, restart, snapshot + key-ring recovery with retained search volume','passed':True,'secretValidatedByRecoveryCommand':True,'postSnapshotMutationRemoved':True,'existingDestinationRejected':True,'searchState':'Ready'}, indent=2)+'\n')
run('down') # Preserve all volumes and backups for inspection.
