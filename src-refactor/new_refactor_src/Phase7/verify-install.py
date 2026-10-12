#!/usr/bin/env python3
"""Build and probe a fresh owned installation, retaining volumes after shutdown."""
import argparse
import json
from pathlib import Path
import runpy
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('edition', choices=['Lite', 'Full'])
parser.add_argument('--live-chat', action='store_true')
parser.add_argument('--compose-executable', help='Standalone Docker Compose executable (for Podman API engines)')
parser.add_argument('--frontend-port', type=int, default=25000)
args = parser.parse_args()
project = 'froststream-phase7-' + args.edition.lower() + '-' + uuid.uuid4().hex[:12]
output = ROOT / '.phase0' / project
runpy.run_path(str(ROOT / 'Phase6/package.py'))['generate'](args.edition, output, args.live_chat, args.frontend_port)
compose = ([args.compose_executable] if args.compose_executable else ['docker', 'compose']) + ['-p', project, '-f', str(output/'docker-compose.yaml')]
checker = [sys.executable, str(ROOT/'Phase6/check-package.py'), args.edition, str(output)]
if args.live_chat:
    checker.append('--live-chat')
subprocess.run(checker, check=True)
try:
    # Private build/start logs can contain deployment inputs; never upload them as CI artifacts.
    with (output/'compose.log').open('w') as log:
        subprocess.run(compose + ['up', '-d', '--build'], stdout=log, stderr=subprocess.STDOUT, check=True, timeout=1800)
    deadline = time.monotonic() + 600
    while True:
        probe = subprocess.run(checker + ['--base-url', f'http://127.0.0.1:{args.frontend_port}'], capture_output=True, text=True)
        if probe.returncode == 0:
            result = json.loads(probe.stdout)
            (output/'result.json').write_text(json.dumps(result, indent=2)+'\n')
            print(json.dumps(result))
            break
        if time.monotonic() >= deadline:
            raise RuntimeError(f'Installation did not become ready. Inspect private logs in {output}')
        time.sleep(5)
finally:
    subprocess.run(compose + ['down'], check=True, stdout=subprocess.DEVNULL)
