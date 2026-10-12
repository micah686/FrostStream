#!/usr/bin/env python3
"""Linux process-tree sampler. Raw evidence only; does not certify the 8 GB gate."""
import argparse, json, os, platform, signal, subprocess, time
from pathlib import Path

def memory():
    return {k:int(v.split()[0])*1024 for k,v in (line.split(':',1) for line in Path('/proc/meminfo').read_text().splitlines()) if v.strip().endswith('kB')}
def processes():
    result={}
    for p in Path('/proc').glob('[0-9]*'):
        try:
            stat=(p/'stat').read_text().rsplit(')',1)[1].split()
            result[int(p.name)]={'parent':int(stat[1]),'cpuTicks':int(stat[11])+int(stat[12]),'rssBytes':int(stat[21])*os.sysconf('SC_PAGE_SIZE')}
        except (OSError,ValueError,IndexError): pass
    return result

def stop_group(pid, sig):
    try: os.killpg(pid, sig)
    except ProcessLookupError: pass

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True);parser.add_argument('--label',required=True);parser.add_argument('--seconds',type=int,default=1800);parser.add_argument('command',nargs=argparse.REMAINDER);args=parser.parse_args()
    cmd=args.command[1:] if args.command[:1]==['--'] else args.command
    if not cmd: parser.error('supply a command after --')
    if args.seconds<=0: parser.error('--seconds must be positive')
    args.output.parent.mkdir(parents=True,exist_ok=True)
    started=time.monotonic();before=memory();samples=[];timed_out=False
    child=subprocess.Popen(cmd, start_new_session=True)
    try:
        while child.poll() is None:
            allp=processes();owned={child.pid}
            while True:
                expanded=owned|{pid for pid,p in allp.items() if p['parent'] in owned}
                if expanded==owned:break
                owned=expanded
            sample={'elapsedSeconds':round(time.monotonic()-started,3),'processes':{str(pid):allp[pid] for pid in sorted(owned) if pid in allp},'system':memory()}
            samples.append(sample)
            if time.monotonic()-started>args.seconds:
                timed_out=True;stop_group(child.pid, signal.SIGTERM);break
            time.sleep(.5)
        try:code=child.wait(timeout=15)
        except subprocess.TimeoutExpired:stop_group(child.pid, signal.SIGKILL);code=child.wait()
    finally:
        if child.poll() is None:stop_group(child.pid, signal.SIGKILL);child.wait()
        if timed_out:stop_group(child.pid, signal.SIGKILL)
    if timed_out:code=124
    report={'label':args.label,'platform':platform.platform(),'cpuCount':os.cpu_count(),'clockTicksPerSecond':os.sysconf('SC_CLK_TCK'),'memoryBefore':before,'elapsedSeconds':time.monotonic()-started,'exitCode':code,'timedOut':timed_out,'peakSampledTreeRssBytes':max((sum(p['rssBytes'] for p in s['processes'].values()) for s in samples),default=0),'samples':samples,'limitations':['RSS sums shared pages; sampling can miss short-lived children.','Container processes are outside the sampled parent tree: capture container stats separately.','System Cached is recorded, not attributed to FrostStream.','Command includes startup/test overhead; not a search latency or steady-state benchmark.','Development hardware is not proof of the 8 GB target.']}
    args.output.write_text(json.dumps(report,indent=2)+'\n')
    raise SystemExit(code if code>=0 else 1)
if __name__=='__main__': main()
