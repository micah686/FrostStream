#!/usr/bin/env python3
"""Publish all six roots, then verify edition dependencies in the actual artifacts."""
import argparse, json, re, subprocess, sys
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=ROOT/'.artifacts/publish/edition-boundaries')
args = parser.parse_args()
subprocess.run([sys.executable, str(ROOT/'tooling/architecture/check-publish-phase1.py'), '--output', str(args.output)], check=True)
report = {'passed': False, 'hosts': []}
try:
    for name in ['Lite', 'WebAPI', 'DataBridge', 'Worker', 'MediaProcessor', 'Scheduler']:
        folder = args.output/name
        assembly = 'Lite' if name == 'Lite' else f'FrostStream.Full.{name}'
        dependencies = sorted(json.loads((folder/f'{assembly}.deps.json').read_text())['libraries'])
        full_security = (folder/'FrostStream.Full.Security.dll').exists()
        if full_security != (name == 'WebAPI'):
            raise ValueError(f'{name}: Full security assembly boundary failed')
        if name == 'Lite':
            forbidden = [item for item in dependencies if re.match(r'^(Npgsql|NATS\.|Conduit\.NATS|VaultSharp|Cleipnir\.Flows\.PostgresSql|FluentMigrator|FrostStream\.Full\.)', item, re.I)]
            if forbidden: raise ValueError(f'Lite contains forbidden dependencies: {forbidden}')
            for library in ['FrostStream.Lite.Infrastructure', 'FrostStream.Persistence', 'FrostStream.Integrations.Typesense']:
                if not (folder/f'{library}.dll').is_file(): raise ValueError(f'Lite missing {library}')
        typesense = (folder/'Typesense.dll').exists()
        if typesense != (name in ['Lite', 'DataBridge']): raise ValueError(f'{name}: search SDK leaked outside search hosts')
        postgres = (folder/'Npgsql.dll').exists()
        if postgres != (name == 'DataBridge'): raise ValueError(f'{name}: PostgreSQL SDK leaked outside DataBridge')
        report['hosts'].append({'host': name, 'fullSecurity': full_security, 'postgres': postgres, 'typesense': typesense,
                                'dependencyCount': len(dependencies)})
    report['passed'] = True
except (ValueError, OSError) as error: report['error'] = str(error)
(args.output/'edition-verification.json').write_text(json.dumps(report, indent=2)+'\n')
print(json.dumps(report, indent=2))
raise SystemExit(0 if report['passed'] else 1)
