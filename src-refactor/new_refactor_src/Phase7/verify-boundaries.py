#!/usr/bin/env python3
"""Final composition rules, in addition to inventory and provider SDK checks."""
import argparse
import json
from pathlib import Path
import re
import runpy
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'Phase0'))
import verify
provider_check = runpy.run_path(str(ROOT / 'Phase2/verify-boundaries.py'))['check']

def check(graph, root=ROOT, require_restored=False):
    provider_check(graph, root, require_restored)
    errors = []
    for key, project in graph.items():
        if key.startswith('Tests/') or '/AppHost/' in key:
            continue
        seen = set()
        def visit(name):
            if name in seen:
                return
            seen.add(name)
            for ref in graph[name]['references']:
                visit(ref)
        visit(key)
        for dependency in seen - {key}:
            if key.startswith(('Shared/Core/', 'Shared/Contracts/', 'Shared/Persistence/', 'Shared/Modules/')) and dependency.startswith(('Lite/', 'Full/')):
                errors.append(f'{key}: shared code depends on edition {dependency}')
            if key.startswith('Full/') and dependency.startswith('Lite/'):
                errors.append(f'{key}: Full depends on Lite {dependency}')
            if key.startswith(('Shared/Core/', 'Shared/Persistence/')) and dependency.startswith('Shared/Modules/'):
                errors.append(f'{key}: lower layer depends on application module {dependency}')
            if dependency == 'Full/Security/Security.csproj' and key != 'Full/Hosts/WebAPI/WebAPIHost.csproj':
                errors.append(f'{key}: Full security is owned by the WebAPI host')
            if dependency.startswith('Shared/Integrations/Typesense/') and key != 'Full/Hosts/DataBridge/DataBridgeHost.csproj':
                errors.append(f'{key}: Typesense is owned by the Full DataBridge host')
        if key.startswith('Shared/Contracts/') and project['references']:
            errors.append(f'{key}: contracts must not reference implementation projects')
        if any(package.startswith('FluentMigrator') for package in project['packages']):
            errors.append(f'{key}: historical migration runner is forbidden')
    for folder in ('Shared/Core', 'Shared/Contracts', 'Shared/Persistence', 'Shared/Modules'):
        for source in (root / folder).rglob('*.cs'):
            if {'bin', 'obj'}.intersection(source.parts):
                continue
            code = re.sub(r'//[^\n]*|/\*.*?\*/', '', source.read_text(), flags=re.S)
            code = re.sub(r'"(?:[^"\\]|\\.)*"', '""', code)
            if re.search(r'\b(?:Lucene\.|Typesense\b|ITypesense\w*\b|Typesense\w*\b|SearchParameters\b|FilterBy\b|\w+CollectionSchema\b|FrostStream\.(?:Lite|Full)\.)', code):
                errors.append(f'{source.relative_to(root)}: edition/provider implementation in shared code')
    if (root / 'Full/Infrastructure/Postgres/Migrations').exists():
        errors.append('Historical PostgreSQL migrations must not be shipped')
    if errors:
        raise ValueError('\n'.join(sorted(set(errors))))
    return {'projects': len(graph), 'boundaries': 'passed', 'lite_provider': 'Lucene', 'full_provider': 'Typesense'}

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--require-restored', action='store_true')
    args = parser.parse_args()
    try:
        print(json.dumps(check(verify.inventory()['projects'], require_restored=args.require_restored), indent=2))
    except ValueError as error:
        sys.exit(str(error))
