#!/usr/bin/env python3
"""Snapshot/check the source graph without building or accessing the reference tree."""
import argparse, json, re, sys
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[1]
SNAPSHOT = ROOT / 'Phase0/dependency-baseline.json'
SKIP = {'bin', 'obj', 'node_modules', '.svelte-kit', '.phase0'}
def files(pattern):
    return sorted(p for p in ROOT.rglob(pattern) if not SKIP.intersection(p.relative_to(ROOT).parts))
def inventory():
    graph, errors = {}, []
    for p in files('*.csproj'):
        doc = ET.parse(p).getroot()
        refs = []
        for el in doc.iter('ProjectReference'):
            dest = (p.parent / el.attrib['Include'].replace('\\', '/')).resolve()
            if not dest.is_relative_to(ROOT) or not dest.is_file():
                errors.append(f'{p.relative_to(ROOT)}: missing/external reference {el.attrib["Include"]}')
            else:
                refs.append(dest.relative_to(ROOT).as_posix())
        for el in doc.iter('Compile'):
            value = el.attrib.get('Include', '')
            if value and '$' not in value and '@' not in value:
                full = (p.parent / value.replace('\\', '/')).resolve()
                if not full.is_relative_to(ROOT): errors.append(f'{p}: external compile input {value}')
                elif not list(full.parent.glob(full.name)): errors.append(f'{p}: missing compile input {value}')
        graph[p.relative_to(ROOT).as_posix()] = {
            'executable': doc.findtext('.//OutputType') == 'Exe' or 'Sdk.Web' in doc.attrib.get('Sdk', ''),
            'references': sorted(refs),
            'packages': sorted(e.attrib['Include'] for e in doc.iter('PackageReference')),
        }
    visiting, visited = set(), set()
    def visit(key):
        if key in visiting: errors.append(f'project cycle at {key}'); return
        if key in visited: return
        visiting.add(key)
        for ref in graph[key]['references']: visit(ref)
        visiting.remove(key); visited.add(key)
    if not errors:
        for key in graph: visit(key)
    routes=[]
    for p in files('*.cs'):
        if '/Modules/Api/' in p.as_posix():
            attrs=re.findall(r'\[(?:Route|HttpGet|HttpPost|HttpPut|HttpPatch|HttpDelete)\([^\n]*',p.read_text(encoding='utf-8-sig'))
            if attrs: routes.append({'source':p.relative_to(ROOT).as_posix(),'attributes':attrs})
    if errors: raise ValueError('\n'.join(errors))
    central = ET.parse(ROOT / 'Directory.Packages.props').getroot()
    versions = {el.attrib['Include']:el.attrib['Version'] for el in central.iter('PackageVersion')}
    return {'projects':graph,'centralPackageVersions':versions,'apiRouteAttributes':routes}
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--capture',action='store_true');args=parser.parse_args()
    data=inventory()
    if args.capture:
        SNAPSHOT.write_text(json.dumps(data,indent=2)+'\n')
        print(f'Captured {len(data["projects"])} projects and {len(data["apiRouteAttributes"])} API source files.')
    else:
        baseline=json.loads(SNAPSHOT.read_text())
        if data != baseline:
            sys.exit('Source graph/API inventory changed. Review the phase boundary and explicitly recapture the baseline; do not silently accept drift.')
        print('PASS: references contained, compile links valid, no cycles, graph/API baseline unchanged.')
if __name__=='__main__':
    try: main()
    except (ValueError, OSError, ET.ParseError) as e: sys.exit(str(e))
