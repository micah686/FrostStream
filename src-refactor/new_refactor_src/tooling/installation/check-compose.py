#!/usr/bin/env python3
"""Check generated source paths without starting services (PyYAML 6.0.3)."""
import sys
import yaml
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
for edition in sys.argv[1:] or ['Lite','Full']:
    if edition not in ['Lite','Full']:sys.exit('Expected Lite or Full')
    path=ROOT/edition/'docker-compose/docker-compose.yaml'
    spec=yaml.safe_load(path.read_text())
    for name,service in spec['services'].items():
        build=service.get('build')
        if build:
            context=(path.parent/build['context']).resolve()
            if context!=ROOT:sys.exit(f'{edition}/{name}: build context escapes standalone tree: {context}')
            dockerfile=context/build['dockerfile']
            if not dockerfile.is_file():sys.exit(f'{edition}/{name}: missing Dockerfile {dockerfile}')
        for volume in service.get('volumes',[]):
            if volume.get('type')=='bind' and '${' not in volume['source']:
                source=(path.parent/volume['source']).resolve()
                if not source.is_relative_to(ROOT):sys.exit(f'{edition}/{name}: external bind {source}')
                # Recovery/media roots can be created on first startup. Config files must exist.
                if ('configs' in source.parts or 'static' in source.parts) and not source.exists():sys.exit(f'{edition}/{name}: missing bind input {source}')
    print(f'PASS: {edition}, {len(spec["services"])} services, portable source/build paths.')
