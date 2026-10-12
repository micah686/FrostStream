#!/usr/bin/env python3
"""Ordered Phase 0 package startup for Podman Compose's one-shot dependency issue.

Build first with `docker compose ... build`. Existing Phase 0 containers are
recreated, retaining their volumes. No volumes or bind contents are deleted.
"""
import argparse
import json
import os
import re
import subprocess
import tempfile
import time
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]


def fresh_environment(project, containers, volumes, root=ROOT):
    """Refuse retained state before creating project-specific writable bind roots."""
    if not re.fullmatch(r'froststream-phase0-[a-z0-9-]+', project):
        raise ValueError('Invalid disposable project name')
    if any(name.startswith(project + '_') or name.startswith(project + '-')
           for name in [*containers, *volumes]):
        raise ValueError('Fresh installation requires a project with no existing containers or volumes')
    folder = root / '.phase0/installations' / project
    folder.mkdir(mode=0o700, parents=True, exist_ok=False)
    overrides = {'FROSTSTREAM_BACKUP_ROOT': str(folder / 'backups'),
                 'FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT': str(folder / 'openbao-bootstrap')}
    for path in overrides.values():
        Path(path).mkdir(mode=0o700)
    (folder / 'OWNED_BY_PHASE0').write_text('Fresh disposable installation; retain state after verification.\n')
    return overrides


def ordered_services(services):
    pending = dict(services)
    ordered = []
    while pending:
        ready = [name for name, spec in pending.items()
                 if all(dependency in ordered for dependency in spec.get('depends_on', {}))]
        if not ready:
            raise ValueError('Compose dependencies contain a cycle or missing service')
        for name in ready:
            ordered.append(name)
            del pending[name]
    return ordered


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('edition', choices=['Lite', 'Full'])
    parser.add_argument('--project', required=True)
    parser.add_argument('--timeout', type=float, default=300)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--verify-only', action='store_true', help='Inspect an existing Phase 0 stack without recreating containers')
    parser.add_argument('--fresh', action='store_true', help='Require unused project/volumes and create isolated backup/bootstrap bind roots')
    args = parser.parse_args()
    if not re.fullmatch(r'froststream-phase0-[a-z0-9-]+', args.project):
        parser.error('Only froststream-phase0-* disposable projects are supported')
    if args.timeout <= 0:
        parser.error('--timeout must be positive')
    if args.fresh and args.verify_only:
        parser.error('--fresh and --verify-only are mutually exclusive')
    compose = ROOT / args.edition / 'docker-compose/docker-compose.yaml'
    definition = yaml.safe_load(compose.read_text())
    services = definition['services']
    order = ordered_services(services)
    oneshots = {dependency for spec in services.values()
                for dependency, condition in spec.get('depends_on', {}).items()
                if condition.get('condition') == 'service_completed_successfully'}
    health_required = {dependency for spec in services.values()
                       for dependency, condition in spec.get('depends_on', {}).items()
                       if condition.get('condition') == 'service_healthy'}
    report = {'edition': args.edition, 'project': args.project, 'passed': False,
              'scope': 'Container startup/explicit health only; run separate API, browser and media checks',
              'verificationOnly': args.verify_only,
              'engineWorkaround': 'Ordered --no-deps startup; standard Compose startup remains a separate gate',
              'services': []}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    try:
        environment = os.environ.copy()
        if args.fresh:
            containers = subprocess.check_output(['docker', 'ps', '-a', '--format', '{{.Names}}'], text=True).splitlines()
            volumes = subprocess.check_output(['docker', 'volume', 'ls', '--format', '{{.Name}}'], text=True).splitlines()
            overrides = fresh_environment(args.project, containers, volumes)
            environment.update(overrides)
            report['freshInstallation'] = {'preflightPassed': True, 'bindRoots': overrides,
                                           'namedVolumes': 'Unused project namespace verified before startup'}
        else:
            folder = ROOT / '.phase0/installations' / args.project
            if (folder / 'OWNED_BY_PHASE0').is_file():
                overrides = {'FROSTSTREAM_BACKUP_ROOT': str(folder / 'backups'),
                             'FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT': str(folder / 'openbao-bootstrap')}
                environment.update(overrides)
                report['retainedBindRoots'] = overrides
        for name in ([] if args.verify_only else order):
            print(f'Starting {name}', flush=True)
            started = time.monotonic()
            # A single-service view also prevents Podman Compose from recreating
            # reverse dependencies despite --no-deps. Keep the temporary file next
            # to the original so build/bind paths and .env retain their meaning.
            service = {key: value for key, value in services[name].items() if key != 'depends_on'}
            view = {key: value for key, value in definition.items() if key != 'services'}
            view['services'] = {name: service}
            with tempfile.NamedTemporaryFile(mode='w', suffix='.yaml', prefix='.phase0-start-',
                                             dir=compose.parent) as temporary:
                yaml.safe_dump(view, temporary)
                temporary.flush()
                subprocess.run(['podman-compose', '--in-pod=false', '--no-cleanup',
                                '-p', args.project, '-f', temporary.name,
                                'up', '-d', '--no-build', '--no-deps', '--force-recreate', name],
                               check=True, timeout=args.timeout, env=environment)
            container = f'{args.project}_{name}_1'
            while True:
                state = json.loads(subprocess.check_output(
                    ['docker', 'inspect', '--format', '{{json .State}}', container], text=True))
                status = state['Status']
                health = state.get('Health', {}).get('Status')
                if name in oneshots:
                    if status == 'exited':
                        if state['ExitCode'] != 0:
                            raise RuntimeError(f'{name}: initializer exited {state["ExitCode"]}')
                        break
                elif status == 'running' and (name not in health_required or health == 'healthy'):
                    break
                elif status in ('exited', 'dead'):
                    raise RuntimeError(f'{name}: exited before readiness ({state["ExitCode"]})')
                if time.monotonic() - started >= args.timeout:
                    raise TimeoutError(f'{name}: readiness timeout (status={status}, health={health})')
                time.sleep(1)
            report['services'].append({'name': name, 'status': status, 'health': health,
                                       'elapsedSeconds': round(time.monotonic() - started, 3)})
            args.output.write_text(json.dumps(report, indent=2) + '\n')
        # Unsealing must run before requiring OpenBao health. Recheck all long-lived
        # containers at the end, including services which were initially only started.
        report['finalStates'] = {}
        for name in order:
            deadline = time.monotonic() + args.timeout
            while True:
                state = json.loads(subprocess.check_output(
                    ['docker', 'inspect', '--format', '{{json .State}}', f'{args.project}_{name}_1'], text=True))
                health = state.get('Health', {}).get('Status')
                if name in oneshots:
                    if state['Status'] != 'exited' or state['ExitCode'] != 0:
                        raise RuntimeError(f'{name}: initializer did not complete successfully')
                    report['finalStates'][name] = {'status': 'exited', 'exitCode': 0}
                    break
                if state['Status'] != 'running':
                    raise RuntimeError(f'{name}: no longer running at final verification')
                if not services[name].get('healthcheck') or health == 'healthy':
                    report['finalStates'][name] = {'status': state['Status'], 'health': health}
                    break
                if time.monotonic() >= deadline:
                    raise TimeoutError(f'{name}: final health timeout ({health})')
                time.sleep(1)
        report['passed'] = True
    except (subprocess.SubprocessError, OSError, ValueError, RuntimeError) as error:
        report['error'] = str(error)
        print(f'FAILED: {error}', flush=True)
    finally:
        args.output.write_text(json.dumps(report, indent=2) + '\n')
    raise SystemExit(0 if report['passed'] else 1)


if __name__ == '__main__':
    main()
