import json
import copy
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path
from analyze_crossover import aggregate, export, paired_ratio, validate_result, drift
from run_crossover import balanced_schedule
from run_crossover import launch
from check_crossover_timing import diagnostic_status, pacing_status, quiet_status, validate_pilot_v2


def fixture(mode='cpu', pair=0):
    # Synthetic fixtures are tests only, never benchmark evidence.
    r = {'schema':1, 'completed':True, 'error':'', 'mode':mode, 'runId':f'{pair}-{mode}', 'pairId':str(pair),
         'sourceIdentity':'source', 'calibrationSha256':'calibration', 'adapter':'test', 'driver':'test', 'graphicsApi':'test',
         'unityVersion':'test', 'buildConfiguration':'test', 'width':1280, 'height':720, 'vsync':0, 'targetFrameRate':-1,
         'gc0Collections':0, 'gpuTimingStatus':'complete: synthetic fixture', 'actualVisibilityMean':.25,
         'options':{'agents':100, 'seed':1, 'density':.25, 'frames':10},
         'samples':[{'frameIndex':i, 'visibleCount':25, 'cpuCullAndListMs':1, 'cpuUploadMs':1, 'cpuSubmitMs':1,
                     'cpuTotalMs':4 if mode=='cpu' else 2, 'gpuCullMs':-1 if mode=='cpu' else 1, 'gpuDrawMs':1, 'gpuRangeMs':2} for i in range(10)]}
    c = dict(r, correctnessPassed=True, checks=[{'frameIndex':i, 'cpuCount':25, 'gpuCount':25, 'setEqual':True, 'imageEqual':True, 'nonEmptyImage':True} for i in range(10)])
    return r,c


class CrossoverAnalysisTests(unittest.TestCase):
    def test_no_pilot_launch_when_background_gate_blocks(self):
        import json
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); player=root/'Crossover.exe'; player.write_bytes(b'synthetic-test-only')
            dll=root/'Crossover_Data/Managed/Assembly-CSharp.dll'; dll.parent.mkdir(parents=True); dll.write_bytes(b'test')
            for stage in ('pilot','pilot-pairs'):
                out=root/stage;out.mkdir()
                with patch('run_crossover.snapshot',return_value={'adapters':[{'utilization':33}]}), patch('run_crossover.time.sleep'), patch('run_crossover.subprocess.run') as run:
                    with self.assertRaises(RuntimeError): launch(player,out,stage,100000,.25,mode='cpu')
                    run.assert_not_called()
                receipt=json.loads(next(out.glob('*.launch.json')).read_text())
                self.assertEqual('blocked: background GPU activity',receipt['status']); self.assertEqual(3,len(receipt['before']))
    def test_diagnostic_detects_zero_and_wrong_delay(self):
        counts=[1+(i*i+7*i)%4 for i in range(96)]
        rows=[]
        for i in range(112):
            count=counts[i-3] if 3<=i<99 else 0
            rows.append({'availabilityUnityFrame':i+100,'submittedRepetitions':counts[i] if i<96 else 0,'blocks':[count]*3,'gpuNs':[count*100]*3})
        d={'completed':True,'markerValid':[True]*3,'recorderValid':[True]*3,'firstSubmissionUnityFrame':100,'observations':rows}
        self.assertTrue(diagnostic_status(d)['passed'])
        wrong=copy.deepcopy(d)
        for i,r in enumerate(wrong['observations']):
            r['blocks']=[counts[i-4]]*3 if 4<=i<100 else [0]*3
        self.assertFalse(diagnostic_status(wrong)['passed'])
        for r in d['observations']: r['gpuNs']=[0]*3
        self.assertFalse(diagnostic_status(d)['passed'])

    def test_integrated_native_recordings_and_fail_closed_mutations(self):
        from check_integrated_crossover import validate
        root=Path(__file__).resolve().parents[1]/'docs/evidence/gpu-timing-integrated-v4-20260919/runs'
        for arm in ('cpu','gpu'):
            d=json.loads((root/f'integration-{arm}.json').read_text(encoding='utf-8'))
            self.assertEqual(96,validate(d)['timestampResolved'])
            for edit in [lambda x:x.update(timestampResolved=95),lambda x:x.update(maxRingOccupancy=33),
                         lambda x:x['samples'][4].update(resolvedSubmissionId=301),lambda x:x['samples'][4].update(timestampRingSlot=999),
                         lambda x:x['samples'][4].update(completedFence=0),lambda x:x['samples'][4].update(gpuTimestampFrequency=0),
                         lambda x:x['samples'][4].update(gpuTimestampT0=0),lambda x:x['samples'][4].update(gpuDrawMs=900),
                         lambda x:x.update(batchCompletionMsPerFrame=900),lambda x:x.update(timestampInvalid=1)]:
                bad=copy.deepcopy(d);edit(bad)
                with self.assertRaises(ValueError):validate(bad)
            if arm=='cpu':
                self.assertIsNone(d['samples'][0]['gpuCullMs'])
                d['samples'][0]['gpuCullMs']=0
                with self.assertRaises(ValueError):validate(d)

    def test_real_capture_placement_and_copy_boundary(self):
        from check_integrated_crossover import validate_placement
        root=Path(__file__).resolve().parents[1]/'docs/evidence/gpu-timing-integrated-v4-20260919/placement'
        for arm in ('cpu','gpu'):
            chunks=json.loads((root/f'{arm}-commands.json').read_text(encoding='utf-8'))
            result=validate_placement(chunks,arm);self.assertTrue(result['passed'])
            bad=copy.deepcopy(chunks)
            for c in bad:
                if c['index']==result['T1Chunk']:c['index']=result['T2Chunk']+1
            with self.assertRaises(ValueError):validate_placement(bad,arm)

    def test_overhead_gate_retains_block_and_never_launches(self):
        import run_integrated_crossover as runner
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as temp:
            directory=Path(temp)
            with patch.object(runner,'snapshot',return_value={'adapters':[{'utilization':20}]}), patch.object(runner.time,'sleep'), patch.object(runner.subprocess,'run') as process:
                with self.assertRaises(RuntimeError):runner.launch(directory/'fake.exe',directory,'overhead-cpu-0-off','cpu',timestamps='off',quality=True)
                process.assert_not_called()
            receipt=json.loads((directory/'overhead-cpu-0-off.launch.json').read_text())
            self.assertEqual(3,len(receipt['before']));self.assertIn('blocked',receipt['status'])

    def test_native_explicit_ids_fences_and_timestamp_units(self):
        from run_native_timing_diagnostic import validate
        d=dict(completed=True,error='',submitted=96,resolved=96,graphicsApi='Direct3D12',ringCapacity=32,
               renderingThreadingMode='MultiThreaded',profilerEnabled=False,samples=[])
        for i in range(96):
            d['samples'].append(dict(id=i+1,t0=100,t1=200,t2=500,frequency=1000000,requiredFence=i+1,completedFence=i+1,
                submissionUnityFrame=i+100,availabilityUnityFrame=i+103,gpuCullMs=.1,gpuDrawMs=.3,gpuRangeMs=.4))
        self.assertTrue(validate(d)['passed'])
        single=copy.deepcopy(d);single['renderingThreadingMode']='SingleThreaded';self.assertTrue(validate(single)['passed'])
        for edit in [lambda r:r['samples'][32].update(id=1),lambda r:r['samples'][3].update(completedFence=0),
                     lambda r:r['samples'][3].update(t1=0),lambda r:r['samples'][3].update(frequency=0),
                     lambda r:r['samples'][3].update(gpuRangeMs=400),lambda r:r.update(profilerEnabled=True),lambda r:r.update(renderingThreadingMode='NativeGraphicsJobs'),
                     lambda r:r.update(resolved=95),lambda r:r['samples'][3].update(submissionUnityFrame=555)]:
            bad=copy.deepcopy(d);edit(bad)
            with self.assertRaises(ValueError):validate(bad)

    def test_pacing_gate_and_quiet_load(self):
        for hz in (60,120,144,165,240):
            self.assertFalse(pacing_status([1000/hz]*100)['passed'])
        self.assertTrue(pacing_status([.1,.2,.4,.6]*25)['passed'])
        q=[{'adapters':[{'utilization':3}]} for _ in range(3)]
        self.assertTrue(quiet_status(q)); q[1]['adapters'][0]['utilization']=33
        self.assertFalse(quiet_status(q)); self.assertFalse(quiet_status(q[:2]))
        with self.assertRaises(ValueError): pacing_status([0]*100)

    def test_batch_completion_and_frame_mapping_schema(self):
        r,c=fixture(); r['schema']=2; r['protocolVersion']=2
        r['options']={'agents':100000,'density':.25,'seed':69501203,'warmup':300,'frames':1000}
        c['options']=dict(r['options'])
        r.update(supportsGraphicsFence=True,warmupFenceCompleted=True,batchFenceCompleted=True,
            batchStartTicks=1000,finalSubmissionTicks=2000,firstTrueFencePollTicks=2010,lastFalseFencePollTicks=2005,
            stopwatchFrequency=1000,batchCompletionMs=1010,batchCompletionMsPerFrame=1.01,fenceObservationBoundMsPerFrame=.005,
            firstMeasuredUnityFrame=300,gpuMappedFrames=1000)
        template=r['samples'][0]
        r['samples']=[dict(template,frameIndex=i,visibleCount=25000,submissionUnityFrame=300+i,availabilityUnityFrame=303+i,updateIntervalMs=[.1,.2,.4,.6][i%4]) for i in range(1000)]
        self.assertTrue(validate_pilot_v2(r,c)['passed'])
        for edit in [lambda x:x.update(batchCompletionMsPerFrame=2),lambda x:x.update(fenceObservationBoundMsPerFrame=0),
                     lambda x:x.update(batchFenceCompleted=False),lambda x:x['samples'][4].update(availabilityUnityFrame=309),
                     lambda x:x.update(gc0Collections=1)]:
            bad=copy.deepcopy(r);edit(bad)
            with self.assertRaises(ValueError): validate_pilot_v2(bad,c)
    def test_schema_and_correctness_gate(self):
        r,c=fixture(); validate_result(r,c)
        for mutation in [lambda x:x.update(completed=False), lambda x:x.update(calibrationSha256='other'),
                         lambda x:x['samples'][0].update(gpuRangeMs=-1), lambda x:x['samples'][0].update(cpuTotalMs=float('nan')),
                         lambda x:x.update(samples=x['samples'][:-1]), lambda x:x.update(vsync=1), lambda x:x.update(gc0Collections=1)]:
            bad=copy.deepcopy(r); mutation(bad)
            with self.assertRaises(ValueError): validate_result(bad,c)
        c['checks'][0]['setEqual']=False
        with self.assertRaises(ValueError): validate_result(r,c)

    def test_bootstrap_pairs_are_independent_units(self):
        interval=paired_ratio([2,4,6,8,10,12],[1,2,3,4,5,6])
        self.assertEqual(interval,{'ratio':2.,'low':2.,'high':2.})
        with self.assertRaises(ValueError): paired_ratio([1]*4,[1]*4)
        self.assertGreater(drift([1]*10+[2]*10),.15)

    def test_no_architecture_winner_and_exports(self):
        records=[fixture(mode,pair)[0] for pair in range(6) for mode in ('cpu','gpu')]
        summary=aggregate(records)
        self.assertIn('not established',summary['architectureCrossover'])
        with tempfile.TemporaryDirectory() as temp:
            export(summary,Path(temp)); self.assertTrue((Path(temp)/'summary.csv').exists())
        with self.assertRaises(ValueError): aggregate(records+[records[0]])
        with self.assertRaises(ValueError): aggregate(records[:-1])

    def test_balance_and_condition_interleaving(self):
        schedule=list(balanced_schedule([(100,.05),(200,.25)]))
        self.assertEqual(24,len(schedule))
        for condition in [(100,.05),(200,.25)]:
            first=[mode for pair,n,d,mode in schedule if (n,d)==condition][::2]
            self.assertEqual(['cpu','gpu']*3,first)
        with self.assertRaises(ValueError): list(balanced_schedule([(100,.05)],5))


if __name__=='__main__': unittest.main()
