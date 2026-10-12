#!/usr/bin/env python3
"""Enforce Phase 2 source, project, and restored Lite package boundaries."""
import argparse, json, re, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'Phase0'))
import verify
ROOT = verify.ROOT
FULL_SDK = re.compile(r'^(?:Npgsql(?:\.|$)|NATS\.|Conduit\.NATS|VaultSharp|Typesense|Cleipnir\.Flows\.PostgresSql|FluentMigrator|Microsoft\.AspNetCore\.Authentication\.(?:JwtBearer|OpenIdConnect))', re.I)
PROVIDER_SDK = re.compile(r'^(?:Npgsql(?:\.|$)|NATS\.|Conduit\.NATS|VaultSharp|Typesense|Microsoft\.Data\.Sqlite|Microsoft\.EntityFrameworkCore\.Sqlite|Cleipnir\.Flows\.PostgresSql|FluentMigrator|Lucene\.Net|Microsoft\.AspNetCore\.Authentication\.(?:JwtBearer|OpenIdConnect))', re.I)

def check(graph, root=ROOT, require_restored=False):
    errors = []
    def closure(key):
        result = set()
        def visit(k):
            if k in result: return
            result.add(k)
            for ref in graph[k]['references']: visit(ref)
        visit(key)
        return result
    for key, project in graph.items():
        shared = key.startswith(('Shared/Core/', 'Shared/Contracts/', 'Shared/Persistence/', 'Shared/Modules/'))
        lite = key.startswith('Lite/') and '/AppHost/' not in key
        if shared or lite:
            for dependency in closure(key):
                if dependency.startswith('Full/'):
                    errors.append(f'{key}: forbidden Full dependency {dependency}')
                for package in graph[dependency]['packages']:
                    if (PROVIDER_SDK if shared else FULL_SDK).match(package):
                        errors.append(f'{key}: forbidden SDK {package} through {dependency}')
                if 'Conduit.NATS.csproj' in dependency:
                    errors.append(f'{key}: forbidden NATS project {dependency}')
        for ref in project['references']:
            if ref == 'Full/Security/Security.csproj' and key != 'Full/Hosts/WebAPI/WebAPIHost.csproj' and not key.startswith('Tests/'):
                errors.append(f'{key}: only the Full WebAPI host may reference Full security')
    # Imports also catch fully qualified vendor calls even without a PackageReference edit.
    imports = re.compile(r'\b(?:using\s+(?:static\s+)?)?(?:Npgsql|NATS\.Client|Conduit\.NATS|VaultSharp|Typesense(?:\.Setup)?|Microsoft\.Data\.Sqlite)(?:\.|;)')
    for folder in ['Shared/Core', 'Shared/Contracts', 'Shared/Persistence', 'Shared/Modules']:
        for source in (root / folder).rglob('*.cs'):
            if {'bin', 'obj'}.intersection(source.parts): continue
            code = re.sub(r'//[^\n]*|/\*.*?\*/', '', source.read_text(), flags=re.S)
            code = re.sub(r'"(?:[^"\\]|\\.)*"', '""', code)
            if imports.search(code): errors.append(f'{source.relative_to(root)}: vendor SDK used in shared code')
    assets = root / 'Lite/Backend/obj/project.assets.json'
    if assets.exists():
        for name in json.loads(assets.read_text())['libraries']:
            if FULL_SDK.match(name.split('/')[0]) or name.startswith('FrostStream.Full.'):
                errors.append(f'Lite restored graph: forbidden dependency {name}')
    elif require_restored: errors.append('Restore Lite/Backend before checking its transitive package graph')
    if errors: raise ValueError('\n'.join(sorted(set(errors))))
    return {'projects': len(graph), 'lite_restored_graph_checked': assets.exists(), 'typesense_exception': False}

if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('--require-restored', action='store_true'); args = parser.parse_args()
    try: print(json.dumps(check(verify.inventory()['projects'], require_restored=args.require_restored), indent=2))
    except ValueError as error: sys.exit(str(error))
