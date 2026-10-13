#!/usr/bin/env python3
"""Verify optional chat failure remains distinct from application readiness in an owned Lite stack."""
import argparse
import json
from pathlib import Path
import subprocess
import time
import urllib.request
import yaml

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('package',type=Path);p.add_argument('--project',required=True)
p.add_argument('--base-url',required=True);p.add_argument('--output',type=Path,required=True)
a=p.parse_args();assert a.project.startswith('froststream-phase6-')
package=a.package.resolve();assert (package/'INSTALLATION').is_file()
spec=yaml.safe_load((package/'docker-compose.yaml').read_text());assert 'lite' in spec['services'] and 'clickhouse' in spec['services']
base=['docker','compose','-p',a.project,'-f',str(package/'docker-compose.yaml')]
override=package/'unavailable-chat.override.yaml'
override.write_text(yaml.safe_dump({'services':{'lite':{'environment':{'LiveChat__Url':'http://127.0.0.1:1'}}}}))
def start(args):subprocess.run([*args,'up','-d','--no-build','--no-deps','lite'],check=True,stdout=subprocess.DEVNULL)
def status():
 with urllib.request.urlopen(a.base_url+'/readyz',timeout=5) as r:assert r.read()==b'Healthy'
 with urllib.request.urlopen(a.base_url+'/api/system/integrations',timeout=5) as r:return json.load(r)
def wait(expected):
 deadline=time.monotonic()+240
 while time.monotonic()<deadline:
  try:
   result=status()
   if result['liveChatSchema'] in expected:return
  except OSError:pass
  time.sleep(1)
 raise TimeoutError('Optional integration did not reach expected state')
try:
 start([*base,'-f',str(override)]);wait(('Failed',3))
finally:
 start(base);wait(('Ready',2))
a.output.write_text(json.dumps({'passed':True,'unreachableChatSchemaState':'Failed','applicationReadinessDuringChatFailure':'Healthy','restoredChatSchemaState':'Ready'},indent=2)+'\n')
