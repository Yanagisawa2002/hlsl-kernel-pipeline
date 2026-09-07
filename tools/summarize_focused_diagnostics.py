"""Audit and summarize retained focused diagnostics separately from formal evidence."""
import argparse
import json
import math
import statistics as stats
from pathlib import Path


def summarize(root, output):
    rows, snapshots, receipts = [], [], []
    for epoch, expected_reps in [('diagnostics', 1), ('diagnostics-batched', 18)]:
        for index in range(1, 4):
            folder=root/epoch/f'process-{index}'
            p=json.loads((folder/'diagnostic.json').read_text())
            receipt=json.loads(Path(str(folder)+'.execution.json').read_text())
            assert p['completed'] and not p['errors'] and p['deviceRemovalStatus']=='00000000'
            assert p['pid']==receipt['pid'] and receipt['exitCode']==0
            receipts.append(dict(epoch=epoch, process=index, pid=p['pid'], sourceSha=receipt['sourceSha']))
            for r in p['results']:
                if 'verification' in r: assert all(v['passed'] for v in r['verification'])
                if r['phase']=='instrumented-lookback':
                    c=r['counters']; assert len(c)==2048*4
                    assert all(c[i]==sum(c[i+1:i+4]) and c[i+2]<=1 for i in range(0,len(c),4))
                    snapshots.append(dict(epoch=epoch,process=index,sample=r['sample'],
                        polls=sum(c[0::4]),aggregateHits=sum(c[1::4]),prefixHits=sum(c[2::4]),
                        notReadyPolls=sum(c[3::4]),maxPollsPerBlock=max(c[0::4])))
            for arm in ['internal-radix-8','amd-parallel-sort','internal-scan-single','gps-reduce-then-scan']:
                obs=[r['timing'] for r in p['results'] if r['phase']=='pass-timing' and r['arm']==arm]
                assert len(obs)==12
                cats={}; names={}
                for t in obs:
                    reps=t.get('repetitions',1); assert reps==expected_reps
                    assert t['timestampMarkers']==len(t['passes'])*reps+1
                    assert math.isclose(sum(x['gpuMilliseconds'] for x in t['passes']),t['gpuTotalMilliseconds']/reps,rel_tol=1e-10)
                    for x in t['passes']:
                        name=x['name'].lower()
                        if 'radix' not in arm and arm!='amd-parallel-sort': cat=name
                        else:
                            cat=('conversion' if x['stage'] in [0,3] else 'histogram' if name.endswith('-sum') or 'histogram' in name else 'scatter' if 'scatter' in name else 'scan/reduce')
                        cats[cat]=cats.get(cat,0)+x['gpuMilliseconds']/len(obs)
                        names[x['name']]=names.get(x['name'],0)+x['gpuMilliseconds']/len(obs)
                rows.append(dict(epoch=epoch,process=index,arm=arm,repetitions=expected_reps,
                    totalMs=stats.mean(t['gpuTotalMilliseconds']/expected_reps for t in obs),partsMs=cats,passesMs=names))
    result=dict(schema='hlslperf.focused-diagnostic-audit.v1',audited=True,developmentOnly=True,
        processes=receipts,passSummaries=rows,counterSnapshots=snapshots,
        limitations=['Pass markers perturb execution; no formal inference or subtraction from whole-operation confirmation.',
        'Single-operation and batched epochs remain separate; clock/cache explanations are unmeasured.',
        'Counter instrumentation changes execution; polls are instrumented software observations, not DRAM/cache counters.'])
    assert not output.exists(); output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(dict(processes=len(receipts),passSummaries=len(rows),counterSnapshots=len(snapshots),audited=True)))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('root',type=Path);p.add_argument('output',type=Path)
    a=p.parse_args();summarize(a.root,a.output)
