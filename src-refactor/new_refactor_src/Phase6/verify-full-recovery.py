#!/usr/bin/env python3
"""Packaged physical PostgreSQL + paired OpenBao recovery in an owned disposable stack."""
import argparse
import json
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request
import yaml

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('package', type=Path)
p.add_argument('--project', required=True)
p.add_argument('--compose-executable')
p.add_argument('--base-url', default='http://127.0.0.1:25000')
p.add_argument('--restore-url', default='http://127.0.0.1:25900')
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
assert a.project.startswith('froststream-phase6-')
package = a.package.resolve(); assert (package/'INSTALLATION').is_file()
compose = ([a.compose_executable] if a.compose_executable else ['docker','compose']) + ['-p',a.project,'-f',str(package/'docker-compose.yaml')]
def command(*args, capture=False):
    result = subprocess.run([*compose,*args],check=True,stdout=subprocess.PIPE if capture else subprocess.DEVNULL)
    return result.stdout.decode().strip() if capture else None

def http(path, base=None):
    with urllib.request.urlopen((base or a.base_url)+path, timeout=10) as response:
        data = response.read()
        return json.loads(data) if response.headers.get_content_type() == 'application/json' else data

def wait_ready():
    deadline = time.monotonic()+300
    while time.monotonic()<deadline:
        try:
            if http('/readyz') == b'Healthy': return
        except OSError: pass
        time.sleep(1)
    raise TimeoutError('Full did not become ready')

def sql(statement):
    return command('exec','-T','postgres','sh','-c','export PGPASSWORD="$POSTGRES_PASSWORD"; exec psql -h 127.0.0.1 -U "$POSTGRES_USER" -d froststreamdb -v ON_ERROR_STOP=1 -Atc "$1"','sh',statement,capture=True)

def bao(*args):
    return command('exec','-T','openbao','sh','-c','export BAO_TOKEN="$OPENBAO_APP_TOKEN"; exec bao "$@"','sh',*args,capture=True)

def internal(path, body=None):
    args = ['exec','-T','backupservice','curl','--fail','--silent','http://localhost:8080/internal/backups/'+path]
    if body is not None: args += ['-H','Content-Type: application/json','--data',json.dumps(body)]
    return json.loads(command(*args,capture=True))

command('up','-d','--no-build'); wait_ready()
status = http('/api/system/integrations'); assert status['search'] in ('Ready',1)
chat = 'clickhouse' in yaml.safe_load((package/'docker-compose.yaml').read_text())['services']
assert status['liveChatSchema'] in (('Ready',2) if chat else ('Disabled',0)), status
sql("CREATE TABLE public.phase6_recovery_probe(value text); INSERT INTO public.phase6_recovery_probe VALUES('before')")
bao('kv','put','secret/phase6-recovery','value=before')
job = internal('jobs',{'name':'phase6-owned-recovery','type':'full'})
deadline = time.monotonic()+180
while job['status'] not in ('completed','failed') and time.monotonic()<deadline:
    time.sleep(1); job = internal('jobs/'+job['jobId'])
assert job['status'] == 'completed', job
label = job['label']; assert label and Path(label).name == label
sql("UPDATE public.phase6_recovery_probe SET value='after'"); bao('kv','put','secret/phase6-recovery','value=after')
# Explicit application readiness must fail when the request handlers disappear.
command('stop','databridge')
try:
    http('/readyz'); raise AssertionError('Readiness remained healthy without DataBridge')
except urllib.error.HTTPError as error:
    assert error.code == 503
spec = yaml.safe_load((package/'docker-compose.yaml').read_text())
stop = [name for name in spec['services'] if name not in ('backupservice','openbao')]
command('stop',*stop)
assert b'<html' in http('/login',a.restore_url).lower()
try:
    http('/internal/backups/jobs',a.restore_url); raise AssertionError('Internal API exposed on restore UI port')
except urllib.error.HTTPError as error: assert error.code == 404
assert command('exec','-T','backupservice','curl','--fail','--silent','http://localhost:8080/health',capture=True) == 'Healthy'
command('exec','-T','backupservice','pgbackrest','--stanza=froststream','restore','--delta','--set='+label,'--type=immediate','--target-action=promote')
# Same checksum and Raft restore endpoint as the application pairing adapter.
restore = 'test "$(sha256sum "$1" | cut -d" " -f1)" = "$(cat "$1.sha256")" && exec curl --fail --silent -H "X-Vault-Token: $Backup__OpenBaoToken" --data-binary "@$1" "$Backup__OpenBaoAddress/v1/sys/storage/raft/snapshot-force"'
command('exec','-T','backupservice','sh','-c',restore,'sh','/backups/openbao/'+label+'.raft-snapshot')
time.sleep(2)
command('run','--rm','--no-deps','openbao-bootstrap')
assert bao('kv','get','-field=value','secret/phase6-recovery') == 'before'
command('up','-d','--no-deps','postgres')
deadline = time.monotonic()+120
while True:
    try:
        if sql('SELECT value FROM public.phase6_recovery_probe') == 'before': break
    except subprocess.CalledProcessError: pass
    if time.monotonic()>deadline: raise TimeoutError('Restored PostgreSQL did not return backup data')
    time.sleep(1)
override = package/'recovered.override.yaml'
override.write_text(yaml.safe_dump({'services':{'databridge':{'environment':{'Search__RebuildOnStartup':'true'}}}}))
compose += ['-f',str(override)]
command('up','-d','--no-build'); wait_ready()
assert http('/api/system/integrations')['search'] in ('Ready',1)
a.output.write_text(json.dumps({'scope':'Packaged Full PostgreSQL physical backup + paired OpenBao Raft recovery','passed':True,'liveChatEnabled':chat,'postgresPostBackupMutationRemoved':True,'openBaoPostBackupMutationRemoved':True,'standaloneRestoreUi':True,'restoreUiInternalApiBlocked':True,'applicationReadinessDetectsMissingHandlers':True,'searchRebuildRequestedAfterRestore':True},indent=2)+'\n')
command('down') # Preserve recovery material and volumes.
