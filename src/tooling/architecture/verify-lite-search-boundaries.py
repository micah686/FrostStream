#!/usr/bin/env python3
"""Check embedded Lite composition, restored/published dependencies, and generated services."""
import json
import subprocess
import sys
from pathlib import Path
import yaml
ROOT = Path(__file__).resolve().parents[2]
subprocess.run([sys.executable, str(ROOT/'tooling/architecture/verify-search-boundaries.py')], check=True)
assets = json.loads((ROOT/'Lite/Backend/obj/project.assets.json').read_text())
libraries = assets['libraries']
assert any(name.startswith('Lucene.Net/') for name in libraries), 'Lite must include Lucene'
assert not any(name.startswith(('Typesense/', 'FrostStream.Integrations.Typesense/')) for name in libraries), 'Typesense leaked into Lite'
spec = yaml.safe_load((ROOT/'Lite/docker-compose/docker-compose.yaml').read_text())
assert set(spec['services']) in ({'lite', 'frontend', 'pot-provider'}, {'lite', 'frontend', 'pot-provider', 'clickhouse'}), 'Unexpected Lite services'
assert 'typesense' not in json.dumps(spec).lower(), 'Typesense remains in Lite packaging'
backend = spec['services']['lite']
assert backend['environment']['Search__Lucene__Path'] == '/search'
assert any(v.get('target') == '/search' and v.get('source') == 'froststream-lite-search' for v in backend['volumes'])
assert not backend.get('ports'), 'Backend must remain private'
assert all(str(port).startswith('127.0.0.1:') for port in spec['services']['frontend']['ports']), 'Default frontend must bind loopback'
print(json.dumps({'lite_provider': 'Lucene.Net', 'services': sorted(spec['services']), 'vendor_sdk_isolation': 'passed', 'separate_search_volume': True, 'frontend_loopback': True}))
