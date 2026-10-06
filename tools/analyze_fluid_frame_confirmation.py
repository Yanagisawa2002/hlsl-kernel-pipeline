"""Audit the complete six-pair fluid frame cohort and retain all outcomes."""
import argparse
import csv
from datetime import datetime
import json
import math
from pathlib import Path
import statistics

from run_fluid_frame_confirmation import ARMS, POLICY, resources, schedule, validate_run
from run_ultra_scan import T5, read, save, sha

METRICS = ('scan_core','scan_complete','count_sort_complete','spatial_hash_complete','simulation_complete','wall_frame')


def paired(baseline, candidate):
    if len(baseline)!=6 or len(candidate)!=6 or any(not math.isfinite(v) or v<=0 for v in (*baseline,*candidate)):
        raise ValueError('Six positive process means per arm are required.')
    logs=[math.log(a/b) for a,b in zip(baseline,candidate,strict=True)]
    center=statistics.mean(logs);half=T5*statistics.stdev(logs)/math.sqrt(6)
    ratio,lo,hi=map(math.exp,(center,center-half,center+half))
    reduction=lambda x:100*(1-1/x)
    return dict(baselineOverLocalRatio=ratio,pointwise95RatioInterval=[lo,hi],
                timeReductionPercent=reduction(ratio),timeReduction95CiPercent=[reduction(lo),reduction(hi)],
                favorablePointwiseInterval=lo>1,practicalOnePercentSupported=reduction(lo)>=1,
                independentRounds=6)


def signature(run):
    result={k:run[k] for k in ('device','api','unity','width','height','particles','foamCapacity',
                             'iterationsPerFrame','fixedTimestep','gpuTimingMethod','gpuTimestampFrequency',
                             'nativeTimingBuild','coreTimingEnabled')}
    result['settings']={k:run['settings'][k] for k in ('seed','spawnDensity','warmupFrames','measureFrames','nativeGpuTiming')}
    result['payloadSha256']=json.loads(run['provenance'])['payloadSha256']
    return result


def thermal_stop_audit(receipt, failure):
    expected="RuntimeError('Stop: GPU temperature reached 80 C.')"
    if receipt.get('exitCode')==0 or receipt.get('stopReason')!=expected or failure['error']!=expected or not receipt.get('pid'):
        raise ValueError('Partial audit only accepts an owned thermal-stop receipt.')
    reproduced=False
    for index,point in enumerate(receipt['telemetry']):
        try:resources(point['host'],point['gpu'],receipt['particles'] if point['phase']=='before' else 0)
        except RuntimeError as error:
            if repr(error)!=expected or index!=len(receipt['telemetry'])-1:raise ValueError('Stop telemetry differs.')
            reproduced=True
    return dict(reportedReason=expected,rejectedPointRetained=reproduced,
                stopTemperatureIndependentlyReproduced=reproduced,
                lastRetainedTemperatureC=receipt['telemetry'][-1]['gpu']['temperatureC'],
                limitation=None if reproduced else 'Measured legacy runner checked resources before appending telemetry; the rejected point is absent. Threshold trip is reported by the owned receipt/failure, but its exact temperature is unavailable.')


def audit_stage(root, stage, partial=False):
    plan=read(root/'plan.json');complete=read(root/'completion.json') if (root/'completion.json').exists() else None
    failure=read(root/'failure.json') if (root/'failure.json').exists() else None
    full=bool(complete and complete['complete'] and complete['processes']==len(plan['cases']) and complete['planSha256']==sha(root/'plan.json'))
    if (plan['stage']!=stage or plan['policy']!=POLICY or plan['cases']!=schedule(stage)
            or full and failure or not full and not (partial and stage=='confirm' and failure and failure['complete'] is False and complete is None)):
        raise ValueError('Incomplete or changed application plan.')
    rows=[];previous=None;seen=set();configurations={};stopped=None;expected_folders=set()
    for case in plan['cases']:
        folder=root/case['name'];receipt_path=root/(case['name']+'.process.json');log=root/(case['name']+'.log')
        if stopped:
            if receipt_path.exists() or folder.exists() or log.exists():raise ValueError('A process exists after the thermal stop.')
            continue
        receipt=read(receipt_path)
        if any(receipt[k]!=v for k,v in case.items()) or receipt['playerSha256']!=plan['identity']['playerFilesSha256']['FluidScan.exe'] or receipt['logSha256']!=sha(log):
            raise ValueError('Changed process identity/log.')
        expected_folders.add(case['name'])
        if not full and receipt.get('stopReason'):
            thermal_stop_audit(receipt,failure)
            if any((folder/name).exists() for name in ('run.json','observations.csv')) or 'evidenceSha256' in receipt:
                raise ValueError('Thermal-stop output was unexpectedly finalized.')
            start,end=map(datetime.fromisoformat,(receipt['startedUtc'],receipt['endedUtc']))
            if end<=start or previous and (start-previous).total_seconds()+.1<10:raise ValueError('Thermal-stop process overlaps or lacks rest.')
            stopped=case;continue
        if (receipt!=read(folder/'process.json') or any(receipt[k]!=v for k,v in case.items())
                or receipt['exitCode'] or receipt.get('stopReason') or receipt.get('preflightRejected')
                or receipt['playerSha256']!=plan['identity']['playerFilesSha256']['FluidScan.exe']
                or receipt['logSha256']!=sha(log)):
            raise ValueError('Failed or changed process receipt: '+case['name'])
        if receipt['evidenceSha256']!={name:sha(folder/name) for name in ('run.json','correctness.json','observations.csv')}:
            raise ValueError('Raw evidence changed: '+case['name'])
        if receipt['evidenceSha256']['observations.csv'] in seen:raise ValueError('Duplicate observations are not independent processes.')
        seen.add(receipt['evidenceSha256']['observations.csv'])
        start,end=map(datetime.fromisoformat,(receipt['startedUtc'],receipt['endedUtc']))
        if end<=start or previous and (start-previous).total_seconds()+.1<10:
            raise ValueError('Overlapping processes or missing declared rest.')
        previous=end
        if receipt['telemetry'][0]['phase']!='before' or receipt['telemetry'][-1]['phase']!='after':raise ValueError('Incomplete resource telemetry.')
        for point in receipt['telemetry']:
            resources(point['host'],point['gpu'],case['particles'] if point['phase']=='before' else 0)
        run,summary=validate_run(folder,case)
        config=signature(run)
        if case['density'] in configurations and configurations[case['density']]!=config:raise ValueError('Application configuration/source changed.')
        configurations[case['density']]=config
        rows.append(dict(case=case,receipt=receipt,summary=summary,configuration=config))
    if not full and not stopped:raise ValueError('Partial audit requires one verified thermal stop.')
    if {p.name for p in root.iterdir() if p.is_dir()}!=expected_folders:raise ValueError('Missing or undeclared run directories.')
    return plan,rows


def analyze(root,pilot,output,partial=False):
    plan,rows=audit_stage(root,'confirm',partial);prior,_=audit_stage(pilot,'pilot')
    if (prior['identity']!=plan['identity'] or plan['pilot']!=dict(planSha256=sha(pilot/'plan.json'),completionSha256=sha(pilot/'completion.json'))):
        raise ValueError('Pilot source/completion differs from the registered prerequisite.')
    complete=(root/'completion.json').exists();cells=[];incomplete=[]
    for density in dict.fromkeys(c['density'] for c in plan['cases']):
        current=[r for r in rows if r['case']['density']==density]
        by={arm:sorted((r for r in current if r['case']['arm']==arm),key=lambda r:r['case']['repeat']) for arm in ARMS}
        rounds=[r['case']['repeat'] for r in by[ARMS[0]]]
        confirmed=all([r['case']['repeat'] for r in values]==list(range(1,7)) for values in by.values())
        if not confirmed:
            if complete or any([r['case']['repeat'] for r in values]!=rounds for values in by.values()) or not rounds:
                raise ValueError('Missing/unpaired round.')
            incomplete.append(dict(particles=current[0]['case']['particles'],completedRounds=len(rounds),status='not-confirmed'))
        metrics={}
        for metric in METRICS:
            values={arm:[r['summary']['metrics'][metric]['meanMs'] for r in by[arm]] for arm in ARMS}
            inference=paired(values[ARMS[0]],values[ARMS[1]]) if confirmed else dict(baselineOverLocalRatio=None,pointwise95RatioInterval=None,
                      timeReductionPercent=None,timeReduction95CiPercent=None,favorablePointwiseInterval=None,
                      practicalOnePercentSupported=None,independentRounds=len(rounds),status='incomplete-descriptive-only')
            metrics[metric]=dict(inference,
                                processMeanMs={a:statistics.mean(v) for a,v in values.items()},
                                processMeanCv={a:statistics.stdev(v)/statistics.mean(v) for a,v in values.items()},
                                processP50MeanMs={a:statistics.mean(r['summary']['metrics'][metric]['p50Ms'] for r in by[a]) for a in ARMS},
                                processP95MeanMs={a:statistics.mean(r['summary']['metrics'][metric]['p95Ms'] for r in by[a]) for a in ARMS})
        baseline=metrics['scan_complete']['processMeanMs']['original'];local=metrics['scan_complete']['processMeanMs'][ARMS[1]]
        wall=metrics['wall_frame']['processMeanMs']['original']
        cells.append(dict(density=density,particles=current[0]['case']['particles'],configuration=current[0]['configuration'],metrics=metrics,confirmationComplete=confirmed,
                          originalScanFractionOfWallPercent=100*baseline/wall,
                          scanMillisecondsSaved=baseline-local,scanSavingsFractionOfOriginalWallPercent=100*(baseline-local)/wall))
    points=[p for row in rows for p in row['receipt']['telemetry']]
    devices={(p['gpu']['adapter'],p['gpu']['driver']) for p in points}
    if len(devices)!=1:raise ValueError('GPU adapter/driver changed across resource samples.')
    adapter,driver=next(iter(devices))
    stop_case=plan['cases'][len(rows)] if not complete else None
    stop_receipt=read(root/(stop_case['name']+'.process.json')) if stop_case else None
    stop=thermal_stop_audit(stop_receipt,read(root/'failure.json')) if stop_receipt else None
    result=dict(schema='hlslperf.fluid-frame.analysis.v1',complete=complete,processes=len(rows),plannedProcesses=len(plan['cases']),
                incompleteCells=incomplete,stopCase=stop_case,stopAudit=stop,pendingCases=plan['cases'][len(rows)+1:] if stop else [],
                planSha256=sha(root/'plan.json'),
                sourceIdentity=plan['identity'],policy=plan['policy'],cells=cells,deviceTelemetry=dict(adapter=adapter,driver=driver),
                resourceSummary=dict(minimumHostPhysicalBytes=min(p['host']['availablePhysical'] for p in points),
                                     minimumHostCommitBytes=min(p['host']['availableCommit'] for p in points),
                                     maximumGpuUsedBytes=max(p['gpu']['usedBytes'] for p in points),
                                     maximumGpuTemperatureC=max(p['gpu']['temperatureC'] for p in points)),
                statistics='Six paired fresh process means, log-ratio Student-t df5; nominal pointwise 95% intervals, no multiplicity adjustment; correlated frames are not independent repeats; no exclusions.',
                boundary='wall frame includes simulation, water/foam rendering, presentation and harness; GPU simulation excludes rendering; scan GPU times accumulate three exclusive calls/frame.',
                limitation='Single frozen Unity Player/device/scene, fixed simulated 2..4-second window; unstable equal-key sorting can change trajectories; source configuration equality and finite positions do not prove image/trajectory equality or scan-only causal attribution.')
    save(output,result)
    with output.with_suffix('.csv').open('x',encoding='utf-8',newline='') as stream:
        fields=['particles','metric','originalMeanMs','localMeanMs','timeReductionPercent','reduction95Lo','reduction95Hi','practicalOnePercentSupported']
        writer=csv.DictWriter(stream,fieldnames=fields);writer.writeheader()
        for c in cells:
            for metric,m in c['metrics'].items():
                ci=m['timeReduction95CiPercent'] or [None,None]
                writer.writerow(dict(particles=c['particles'],metric=metric,originalMeanMs=m['processMeanMs'][ARMS[0]],
                                     localMeanMs=m['processMeanMs'][ARMS[1]],timeReductionPercent=m['timeReductionPercent'],
                                     reduction95Lo=ci[0],reduction95Hi=ci[1],
                                     practicalOnePercentSupported=m['practicalOnePercentSupported']))
    print(json.dumps({c['particles']:c['metrics']['wall_frame'] for c in cells},indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--evidence',type=Path,required=True)
    p.add_argument('--pilot',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    p.add_argument('--partial',action='store_true',help='Audit a stopped prefix; no inference for incomplete six-pair cells.')
    a=p.parse_args()
    if a.output.exists() or a.output.with_suffix('.csv').exists():raise FileExistsError('Preserve previous analysis.')
    analyze(a.evidence.resolve(),a.pilot.resolve(),a.output.resolve(),a.partial)
