#!/usr/bin/env python3
"""Read a fresh M097 database into the normalized schema/seed manifest. Uses psql's PG* environment."""
import argparse
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCHEMA = ROOT / 'src/App/DataBridge/Persistence/Sqlite/Schema'


def query(sql):
    result = subprocess.run(['psql', '-X', '-A', '-t', '-v', 'ON_ERROR_STOP=1', '-c', sql],
                            check=True, text=True, capture_output=True)
    return json.loads(result.stdout)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=SCHEMA / 'postgres-v97-manifest.json')
    args = parser.parse_args()
    manifest = query((ROOT / 'SPLIT_PLAN/tools/postgres_schema_manifest.sql').read_text())
    if manifest['postgresMigrationVersion'] != 97:
        parser.error('Expected migration 97. Review and version the baseline before changing its cutoff.')
    seed_tables = {'scheduling.scheduled_tasks', 'storage.storage_keys', 'storage.storage_keys_local'}
    manifest['seeds'] = []
    for table in manifest['tables']:
        name = table['schema'] + '.' + table['name']
        # Reject installations with user data: this captures only a fresh disposable migration run.
        rows = query(f'SELECT coalesce(jsonb_agg(to_jsonb(t)), \'[]\'::jsonb) FROM "{table["schema"]}"."{table["name"]}" t')
        if name not in seed_tables:
            if rows:
                parser.error(f'{name} contains rows; export only from a fresh disposable database.')
            continue
        for row in rows:
            for key in ('created_at', 'last_updated'):
                if row.get(key) is not None:
                    row[key] = '$now'
            if row.get('next_due_at') is not None:
                row['next_due_at'] = '$next-cron'
        rows.sort(key=lambda row: row.get('key', ''))
        manifest['seeds'].append({'schema': table['schema'], 'table': table['name'], 'rows': rows})
    args.output.write_text(json.dumps(manifest, indent=2, sort_keys=True) + '\n')
    print(f'Exported {len(manifest["tables"])} application tables through M097 to {args.output}')


if __name__ == '__main__':
    main()
