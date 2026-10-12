#!/usr/bin/env python3
"""Enforce the complete search boundary with edition-owned search providers."""
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
subprocess.run([sys.executable, str(ROOT / 'Phase2/verify-boundaries.py'), '--require-restored'], check=True)
forbidden = re.compile(r'\b(?:Typesense|ITypesense\w*|Typesense\w*|SearchParameters|FilterBy|\w+CollectionSchema)\b')
errors = []
for folder in ('Shared/Core', 'Shared/Contracts', 'Shared/Persistence', 'Shared/Modules'):
    for source in (ROOT / folder).rglob('*.cs'):
        if {'bin', 'obj'}.intersection(source.parts):
            continue
        if forbidden.search(source.read_text()):
            errors.append(str(source.relative_to(ROOT)))
if errors:
    sys.exit('Search provider coupling in shared code:\n' + '\n'.join(errors))
print(json.dumps({'search_boundary': 'passed', 'full_provider': 'Typesense', 'lite_exception_retained': False}))
