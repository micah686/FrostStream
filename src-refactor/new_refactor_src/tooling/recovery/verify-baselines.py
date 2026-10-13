#!/usr/bin/env python3
"""Check reviewed fresh-install SQL artifacts against their recorded source fingerprints."""
import hashlib
import json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
manifest=json.loads((ROOT/'tooling/recovery/schema-baselines.json').read_text())
def check(path,expected):
 data=(ROOT/path).read_bytes()
 assert b'\r\n' not in data, f'{path}: checksummed resources require LF endings'
 assert hashlib.sha256(data).hexdigest().upper()==expected, f'{path}: baseline changed; review the release boundary and fingerprint explicitly'
check('Full/Infrastructure/Postgres/Baseline/001-baseline.sql',manifest['full']['sqlSha256'])
check('Shared/Persistence/Persistence/Sqlite/Schema/001-baseline.sql',manifest['lite']['baselineSqlSha256'])
check('Shared/Persistence/Persistence/Sqlite/Schema/postgres-v97-manifest.json',manifest['lite']['seedManifestSha256'])
print(json.dumps({'freshInstallBaselines':'passed','fullVersion':manifest['full']['baselineVersion'],'liteApplicationVersions':manifest['lite']['applicationVersions']}))
