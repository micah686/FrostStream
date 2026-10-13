#!/usr/bin/env python3
"""Read-only validation of generated fresh-install artifacts and optional runtime probes."""
import argparse
import json
from pathlib import Path
import urllib.request
import yaml

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('edition', choices=['Lite', 'Full'])
parser.add_argument('package', type=Path)
parser.add_argument('--live-chat', action='store_true')
parser.add_argument('--base-url')
args = parser.parse_args()
spec = yaml.safe_load((args.package/'docker-compose.yaml').read_text())
services = spec['services']
assert ('clickhouse' in services) == args.live_chat
assert all(not item.get('external') and not item.get('name') for item in spec.get('volumes', {}).values()), 'Volumes must be installation/project scoped'
assert (args.package/'.env').is_file() and (args.package/'installation.env').is_file()
assert (args.package/'.env').stat().st_mode & 0o077 == 0, 'Private .env permissions required'
if args.edition == 'Lite':
    assert set(services) == {'lite', 'frontend', 'pot-provider'} | ({'clickhouse'} if args.live_chat else set())
    assert 'typesense' not in json.dumps(spec).lower()
    mounts = {v['target']: v['source'] for v in services['lite']['volumes']}
    assert mounts['/data'] != mounts['/search']
    assert not services['lite'].get('ports')
    assert all(str(port).startswith('127.0.0.1:') for port in services['frontend']['ports'])
    assert services['lite']['healthcheck']
    assert services['lite']['deploy']['resources']['limits']['memory'] == '4g'
else:
    assert {'postgres', 'postgres-init', 'backup-init', 'backupservice', 'openbao', 'openbao-bootstrap', 'authentik', 'openfga', 'nats', 'typesense', 'databridge', 'webapi'} <= set(services)
    for name in ['postgres', 'backupservice']:
        mounts = {v['target']: v['source'] for v in services[name]['volumes']}
        assert mounts['/var/lib/postgresql'] == 'froststream-postgres-data'
        assert '/backups' in mounts
    assert services['databridge']['environment']['Search__RebuildOnStartup'] == 'false'
    assert any(v['target'] == '/bootstrap' and v['type'] == 'bind' for v in services['openbao-bootstrap']['volumes'])
assert services['frontend']['healthcheck']['test'][-1].endswith('/readyz')
if args.base_url:
    def get(path):
        with urllib.request.urlopen(args.base_url.rstrip('/')+path, timeout=10) as response:
            assert response.status == 200
            data = response.read()
            return json.loads(data) if response.headers.get_content_type() == 'application/json' else data
    assert get('/readyz') == b'Healthy'
    capabilities = get('/api/system/capabilities')
    assert capabilities['deploymentMode'] == args.edition
    assert capabilities['integrations']['liveChat'] == args.live_chat
    status = get('/api/system/integrations')
    assert status['search'] in ('Ready', 1)
    assert status['liveChatSchema'] in (('Ready', 2) if args.live_chat else ('Disabled', 0))
print(json.dumps({'edition': args.edition, 'liveChat': args.live_chat, 'services': len(services), 'runtimeProbed': bool(args.base_url), 'passed': True}))
