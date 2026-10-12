#!/usr/bin/env python3
"""Sample an explicitly named disposable Compose project and Linux host memory."""
import argparse
import json
import re
import subprocess
import time
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--project', required=True)
parser.add_argument('--seconds', type=int, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
if not re.fullmatch(r'froststream-install-[a-z0-9-]+', args.project) or args.seconds <= 0:
    parser.error('Use a disposable Phase 0 project and a positive duration')
containers = subprocess.check_output(['docker', 'ps', '--filter', f'label=com.docker.compose.project={args.project}', '--format', '{{.Names}}'], text=True).splitlines()
if not containers:
    parser.error('No running project containers')

def size(value):
    match = re.fullmatch(r'([0-9.]+)\s*([KMGT]?i?B)', value.strip(), re.I)
    if not match:
        raise ValueError('Unrecognized container memory unit')
    units = {'b': 1, 'kb': 1000, 'mb': 1000**2, 'gb': 1000**3, 'tb': 1000**4,
             'kib': 1024, 'mib': 1024**2, 'gib': 1024**3, 'tib': 1024**4}
    return float(match[1]) * units[match[2].lower()]

samples = []
start = time.monotonic()
while True:
    raw = subprocess.check_output(['docker', 'stats', '--no-stream', '--format', 'json', *containers], text=True, timeout=30)
    try:
        rows = json.loads(raw)
    except json.JSONDecodeError:
        rows = [json.loads(line) for line in raw.splitlines() if line.strip()]
    if isinstance(rows, dict):
        rows = [rows]
    values = []
    for row in rows:
        row = {k.lower().replace('_', ''): v for k, v in row.items()}
        values.append({'name': row['name'], 'memoryBytes': size(row['memusage'].split('/')[0]),
                       'cpuPercent': float(row.get('cpupercent', row.get('cpuperc', '0%')).rstrip('%')),
                       'cpuTime': row.get('cputime'), 'pids': row.get('pids')})
    memory = {k: int(v.split()[0])*1024 for k, v in (line.split(':', 1) for line in Path('/proc/meminfo').read_text().splitlines()) if v.strip().endswith('kB')}
    samples.append({'elapsedSeconds': time.monotonic()-start, 'containers': values, 'hostMemory': memory})
    if time.monotonic()-start >= args.seconds:
        break
    time.sleep(min(5, args.seconds-(time.monotonic()-start)))
report = {'project': args.project, 'requestedSeconds': args.seconds, 'elapsedSeconds': time.monotonic()-start,
          'peakContainerMemoryBytes': max(sum(c['memoryBytes'] for c in s['containers']) for s in samples),
          'minimumHostAvailableBytes': min(s['hostMemory']['MemAvailable'] for s in samples),
          'samples': samples,
          'limitations': ['Container memory uses engine/cgroup accounting and includes children; it is not process RSS.',
                          'Host filesystem cache is recorded, not attributed exclusively to FrostStream.',
                          'CPU percentages are engine-reported; Podman may average over container lifetime.',
                          'Development hardware; this does not certify physical 8 GB acceptance.']}
def cpu_seconds(value):
    if value is None:
        return None
    units = {'ns': 1e-9, 'us': 1e-6, 'µs': 1e-6, 'ms': 1e-3, 's': 1, 'm': 60, 'h': 3600}
    parts = re.findall(r'([0-9.]+)(ns|us|µs|ms|s|m|h)', value)
    if ''.join(number+unit for number, unit in parts) != value:
        raise ValueError('Unrecognized cumulative CPU duration')
    return sum(float(number)*units[unit] for number, unit in parts)

first = {c['name']: cpu_seconds(c['cpuTime']) for c in samples[0]['containers']}
last = {c['name']: cpu_seconds(c['cpuTime']) for c in samples[-1]['containers']}
if len(samples) > 1 and set(first) == set(last) and all(value is not None for value in [*first.values(), *last.values()]):
    report['containerCpuSeconds'] = sum(last[name]-first[name] for name in first)
    report['containerCpuPercentOneCore'] = report['containerCpuSeconds'] / (samples[-1]['elapsedSeconds']-samples[0]['elapsedSeconds']) * 100
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, indent=2)+'\n')
print(json.dumps({k: v for k, v in report.items() if k != 'samples'}))
