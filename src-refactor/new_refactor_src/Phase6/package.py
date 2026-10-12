#!/usr/bin/env python3
"""Generate private, fresh-install Compose artifacts; never overwrite an installation."""
import argparse
import os
from pathlib import Path
import re
import secrets
import subprocess
import yaml

ROOT = Path(__file__).resolve().parents[1]
OBSOLETE = {'SINGLE_USER_MODE', 'AUTH_ALLOW_SINGLE_USER_MODE_IN_PRODUCTION'}

def generate(edition, output, live_chat=False, frontend_port=None):
    output = output.resolve()
    if output.exists():
        raise ValueError('Output already exists; choose a new installation directory.')
    output.mkdir(parents=True, mode=0o700)
    template = ROOT / ('Lite/AppHost/lite-development.env' if edition == 'Lite' else 'Shared/Hosting/AppHostCommon/aspire-development.env')
    lines = template.read_text().splitlines()
    values = {'FROSTSTREAM_DEV_TOOLS': 'false', 'LIVE_CHAT_ENABLED': str(live_chat).lower(),
              'FROSTSTREAM_STORAGE_ROOT': str(output/'data'), 'FROSTSTREAM_BACKUP_ROOT': str(output/'backups'),
              'FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT': str(output/'openbao-bootstrap'), 'Deployment__Mode': edition}
    if frontend_port is not None:
        if not 1 <= frontend_port <= 65535: raise ValueError('Frontend port must be 1–65535')
        values['PORT_FRONTEND'] = str(frontend_port)
        values['FRONTEND_PUBLIC_ORIGIN'] = f'http://localhost:{frontend_port}'
    for line in lines:
        match = re.match(r'([A-Z][A-Z0-9_]+)=', line)
        if match and any(word in match[1] for word in ('PASSWORD', 'SECRET', 'TOKEN', 'API_KEY')):
            values[match[1]] = secrets.token_hex(32)
    text = '\n'.join(lines)+'\n'
    for key, value in values.items():
        pattern = '^'+re.escape(key)+'=.*$'
        text = re.sub(pattern, lambda _: key+'='+value, text, flags=re.M) if re.search(pattern, text, re.M) else text+key+'='+value+'\n'
    source = output/'installation.env'
    source.write_text(text); source.chmod(0o600)
    env = dict(os.environ, FROSTSTREAM_ENV_FILE=str(source), FROSTSTREAM_PROFILE_ENV_OUTPUT=str(output/'.env.private'), FROSTSTREAM_COMPOSE_OUTPUT_KIND='profile')
    # Fail rather than silently ignore an incompatible inherited identity setting.
    for name in OBSOLETE | {'Auth__SingleUserMode', 'Auth__AllowSingleUserModeInProduction'}:
        if edition == 'Full' and name in env:
            raise ValueError(f'{name} is obsolete and forbidden in Full')
    project = 'AppHostLite' if edition == 'Lite' else 'AppHostFull'
    with (output/'publish.log').open('w') as log:
        subprocess.run(['aspire', 'publish', '--apphost', str(ROOT/edition/'AppHost'/f'{project}.csproj'), '-o', str(output), '--non-interactive', '--nologo'], cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
    (output/'.env.private').replace(output/'.env'); (output/'.env').chmod(0o600)
    spec_path = output/'docker-compose.yaml'
    spec = yaml.safe_load(spec_path.read_text())
    for service in spec['services'].values():
        if service.get('build'):
            service['build']['context'] = str(ROOT)
        for volume in service.get('volumes', []):
            if volume.get('type') == 'bind' and volume['source'].startswith('../../'):
                volume['source'] = str(ROOT/volume['source'][6:])
    spec_path.write_text(yaml.safe_dump(spec, sort_keys=False))
    (output/'INSTALLATION').write_text(f'FrostStream {edition}; live chat={live_chat}. Private generated inputs. Preserve volumes and recovery keys.\n')
    return output

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('edition', choices=['Lite', 'Full'])
    parser.add_argument('output', type=Path)
    parser.add_argument('--live-chat', action='store_true')
    parser.add_argument('--frontend-port', type=int)
    args = parser.parse_args()
    try:
        print(generate(args.edition, args.output, args.live_chat, args.frontend_port))
    except (ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f'{error}; inspect the installation publish.log. Existing state was not reset.\n')
