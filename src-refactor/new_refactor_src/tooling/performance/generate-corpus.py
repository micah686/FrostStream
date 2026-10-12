#!/usr/bin/env python3
"""Deterministic large dataset for future provider seeders; not a product-size limit."""
import argparse, hashlib, json
from pathlib import Path
parser=argparse.ArgumentParser();parser.add_argument('output',type=Path);args=parser.parse_args()
args.output.mkdir(parents=True,exist_ok=True)
manifest={'version':1,'encoding':'UTF-8','seed':0,'files':{}}
for kind,count in [('media',10000),('captions',100000),('comments',250000)]:
    path=args.output/(kind+'.jsonl')
    if path.exists():parser.error(f'refusing to overwrite {path}')
    digest=hashlib.sha256();size=0
    with path.open('wb') as f:
        for i in range(count):
            record={'id':f'{kind}-{i:06d}','mediaId':f'media-{i%10000:06d}','text':f'FrostStream 日本語 café {kind} {i:06d} graphics card tutorial','owner':'alice' if i%2==0 else 'bob'}
            if kind=='media':record.update(durationSeconds=60+i%3600,releaseDateUnix=1672531200+i,platform='youtube',tags=['gaming' if i%2==0 else 'education'])
            data=(json.dumps(record,ensure_ascii=False,sort_keys=True)+'\n').encode();f.write(data);digest.update(data);size+=len(data)
    manifest['files'][path.name]={'records':count,'bytes':size,'sha256':digest.hexdigest()}
(args.output/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps(manifest,indent=2))
