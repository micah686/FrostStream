#!/usr/bin/env python3
"""Create a private fresh-install env from the copied template; never reset existing state."""
import argparse, os, re, secrets
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
p=argparse.ArgumentParser();p.add_argument('edition',choices=['Lite','Full']);a=p.parse_args()
folder=ROOT/'.artifacts/installations'/a.edition
folder.mkdir(parents=True,exist_ok=True)
output=folder/'fixture.env'
if output.exists():p.error(f'{output} exists; use a new checkout for another clean install')
text=(ROOT/('Lite/AppHost/lite-development.env' if a.edition=='Lite' else 'Shared/Hosting/AppHostCommon/aspire-development.env')).read_text()
values={'FROSTSTREAM_DEV_TOOLS':'false','LIVE_CHAT_ENABLED':'false','FROSTSTREAM_STORAGE_ROOT':str(folder/'data'),'FROSTSTREAM_BACKUP_ROOT':str(folder/'backups'),'FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT':str(folder/'openbao-bootstrap'),'Deployment__Mode':a.edition}
for line in text.splitlines():
    match=re.match(r'([A-Z][A-Z0-9_]+)=',line)
    if match and any(word in match[1] for word in ['PASSWORD','SECRET','TOKEN','API_KEY']):values[match[1]]=secrets.token_hex(32)
for key,value in values.items():
    pattern=r'^'+re.escape(key)+r'=.*$'
    if re.search(pattern,text,re.M):text=re.sub(pattern,lambda _:key+'='+value,text,flags=re.M)
    else:text+='\n'+key+'='+value+'\n'
fd=os.open(output,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
with os.fdopen(fd,'w') as f:f.write(text)
(folder/'OWNED_BY_FROSTSTREAM').write_text('Fresh disposable installation; no cleanup of global named volumes is authorized.\n')
print(output)
