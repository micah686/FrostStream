#!/usr/bin/env python3
"""Restore a packaged Full Raft snapshot onto a new empty vault using original unseal material."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request
import uuid

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('package',type=Path);p.add_argument('--podman-unshare',action='store_true')
p.add_argument('--port',type=int,default=25406);p.add_argument('--output',type=Path,required=True)
a=p.parse_args();package=a.package.resolve();assert (package/'INSTALLATION').is_file()
root=Path(__file__).resolve().parents[1]
def read(path):
 return subprocess.check_output(['podman','unshare','cat',str(path)]) if a.podman_unshare else path.read_bytes()
folder=package/'backups/openbao'
if a.podman_unshare:
 files=subprocess.check_output(['podman','unshare','find',str(folder),'-maxdepth','1','-name','*.raft-snapshot','-type','f']).decode().splitlines()
else:files=[str(f) for f in folder.glob('*.raft-snapshot')]
assert files,'No packaged paired snapshot found'
snapshot=Path(sorted(files)[-1]);data=read(snapshot)
assert hashlib.sha256(data).hexdigest()==read(Path(str(snapshot)+'.sha256')).decode().strip().lower()
original=dict(line.split('=',1) for line in read(package/'openbao-bootstrap/init.env').decode().splitlines() if '=' in line)
source=dict(line.split('=',1) for line in (package/'installation.env').read_text().splitlines() if '=' in line and not line.startswith('#'))
name='froststream-phase6-vault-fresh-'+uuid.uuid4().hex[:12]
base=f'http://127.0.0.1:{a.port}'
def request(path,body=None,token=None,binary=False):
 headers={'Content-Type':'application/octet-stream' if binary else 'application/json'}
 if token:headers['X-Vault-Token']=token
 payload=body if binary else json.dumps(body).encode() if body is not None else None
 req=urllib.request.Request(base+path,data=payload,headers=headers)
 with urllib.request.urlopen(req,timeout=20) as response:
  result=response.read();return json.loads(result) if result else None
subprocess.run(['docker','run','-d','--name',name,'-p',f'127.0.0.1:{a.port}:8200','-v',name+':/openbao/file','-v',str(root/'Full/AppHost/configs/openbao/openbao.hcl')+':/openbao/openbao.hcl:ro','openbao/openbao:2.5.5','server','-config=/openbao/openbao.hcl'],check=True,stdout=subprocess.DEVNULL)
try:
 deadline=time.monotonic()+60
 while True:
  try:
   assert request('/v1/sys/init')['initialized'] is False;break
  except OSError:
   if time.monotonic()>deadline:raise
   time.sleep(1)
 fresh=request('/v1/sys/init',{'secret_shares':1,'secret_threshold':1})
 request('/v1/sys/unseal',{'key':fresh['keys'][0]})
 request('/v1/sys/storage/raft/snapshot-force',data,fresh['root_token'],binary=True)
 deadline=time.monotonic()+60
 while True:
  try:
   request('/v1/sys/unseal',{'key':original['UNSEAL_KEY']})
   restored=request('/v1/secret/data/phase6-recovery',token=source['OPENBAO_TOKEN'])
   assert restored['data']['data']['value']=='before'
   break
  except (OSError, AssertionError) as error:
   if time.monotonic()>deadline:
    if isinstance(error,urllib.error.HTTPError):
     detail=error.read().decode()
     for value in original.values():detail=detail.replace(value,'[redacted]')
     raise RuntimeError('Fresh vault recovery failed: '+detail) from None
    raise
   time.sleep(1)
 a.output.write_text(json.dumps({'passed':True,'newEmptyRaftVolume':True,'originalUnsealMaterialUsed':True,'originalApplicationTokenRestored':True,'pairedSecretValueRestored':True},indent=2)+'\n')
finally:
 subprocess.run(['docker','rm','-f',name],check=True,stdout=subprocess.DEVNULL)
# Keep the owned restored volume; never remove recovery material automatically.
