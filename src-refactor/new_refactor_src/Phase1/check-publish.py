#!/usr/bin/env python3
"""Publish each composition root into fresh output and verify packaging boundaries."""
import argparse
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODULES = ['WebAPI', 'DataBridge', 'Worker', 'MediaProcessor', 'Scheduler']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / '.phase1/publish')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {'passed': False, 'hosts': []}
    try:
        for name in ['Lite', *MODULES]:
            source = ROOT / ('Lite/Backend' if name == 'Lite' else f'Full/Hosts/{name}')
            project = source / (f'{name}.csproj' if name == 'Lite' else f'{name}Host.csproj')
            destination = output / name
            subprocess.run(['dotnet', 'publish', str(project), '-c', 'Release',
                            '-o', str(destination), '--nologo', '-m:1'], check=True)
            assembly = 'Lite' if name == 'Lite' else f'FrostStream.Full.{name}'
            if not (destination / f'{assembly}.runtimeconfig.json').is_file():
                raise ValueError(f'{name}: missing executable runtime config')
            expected = {p.name: p.read_bytes() for p in source.glob('appsettings*.json')}
            actual = {p.name: p.read_bytes() for p in destination.glob('appsettings*.json')}
            if expected != actual:
                raise ValueError(f'{name}: published appsettings differ from the owning host')
            for module in MODULES:
                if (destination / f'{module}.runtimeconfig.json').exists():
                    raise ValueError(f'{name}: published executable module {module}')
            required = MODULES if name == 'Lite' else [name]
            for module in required:
                if not (destination / f'{module}.dll').is_file():
                    raise ValueError(f'{name}: missing reusable module {module}')
            report['hosts'].append({'host': name, 'assembly': assembly,
                                    'settings': sorted(actual), 'modules': required})
        report['passed'] = True
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        report['error'] = str(error)
    finally:
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n')
    raise SystemExit(0 if report['passed'] else 1)


if __name__ == '__main__':
    main()
