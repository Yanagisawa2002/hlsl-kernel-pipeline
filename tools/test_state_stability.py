import unittest,math,tempfile,json
from pathlib import Path
from unittest.mock import patch
from analyze_state_stability import analyze,index,ratio_summary,stable,settling,histogram,tv,aligned,nearest,boundary
from run_state_stability import quiet_preflight,ORDER
from prepare_state_stability import transform
ROOT=Path(__file__).resolve().parents[1]
class StateStabilityTests(unittest.TestCase):
 def test_cyclic_identity(self):
  for g in range(5000):self.assertEqual((g//1000,g%1000),index(g))
  for view in range(1000):self.assertEqual(index(view)[1],index(view+1000)[1])
 def test_matched_ratios_and_stability(self):
  base=[1+i/1000 for i in range(1000)];r=ratio_summary(base,[x*1.02 for x in base]);self.assertAlmostEqual(1.02,r['median']);self.assertTrue(stable(r))
  self.assertFalse(stable({'median':1,'p10':.7,'p90':1.3}));self.assertFalse(stable({'median':1.06,'p10':1,'p90':1.08}))
 def test_three_transitions_need_four_cycles_and_report_relapse(self):
  self.assertIsNone(settling([True,True],[1,2,3]))
  d=settling([False,True,True,True,False],[1,2,3,4,5,6]);self.assertEqual(1,d['candidateSettlingCycle']);self.assertEqual(2000,d['candidateSettlingFrame']);self.assertEqual(2,d['candidateSettlingTimeSeconds']);self.assertEqual(5,d['confirmationTimeSeconds']);self.assertEqual([5],d['laterUnstableTransitions'])
 def test_incomplete_cycle_rejected(self):
  with self.assertRaises(ValueError):ratio_summary([1]*999,[1]*999)
 def test_zero_nonfinite_timing_rejected(self):
  for v in [0,-1,float('nan'),float('inf')]:
   a=[1]*1000;a[2]=v
   with self.assertRaises(ValueError):ratio_summary(a,[1]*1000)
 def test_nvml_alignment_boundaries(self):
  rows=[{'utcMs':100},{'utcMs':200},{'utcMs':300}];self.assertEqual(rows[:2],aligned(rows,100,300));self.assertIsNone(nearest(rows,451));self.assertEqual(rows[-1],nearest(rows,450))
 def test_pstate_histogram_tv(self):
  a=histogram([0,0,8,8]);b=histogram([0,0,0,8]);self.assertEqual(.25,tv(a,b));self.assertEqual({'0':2,'8':2},a['counts']);self.assertIsNone(tv(a,None))
 def test_passive_boundary_not_fabricated_idle(self):
  d=boundary(100,105,103);self.assertEqual(5,d['warmupDrainMs']);self.assertIsNone(d['postWarmupIdleGapMs']);self.assertEqual(-2,d['signedFirstAfterFenceObservationMs'])
  self.assertEqual(2,boundary(100,103,105)['postWarmupIdleGapMs']);self.assertFalse(boundary(0,0,0)['available'])
 def test_quiet_block_no_launch(self):
  with tempfile.TemporaryDirectory() as td,patch('run_state_stability.snapshot',return_value={'adapters':[{'utilization':6}]}),patch('run_state_stability.time.sleep'),patch('run_state_stability.subprocess.Popen') as launch:
   path=Path(td)/'receipt.json';self.assertFalse(quiet_preflight(path,{'diagnosticOnly':True}));launch.assert_not_called();self.assertIn('blocked',json.loads(path.read_text())['status'])
 def test_complete_cycles_exclude_partial_tail_and_keep_view_identity(self):
  n=4500;start=1000.0;samples=[]
  for i in range(n):
   tick=1+i*1000000;utc=start+i*30000/(n-1)
   samples.append(dict(cycleIndex=i//1000,viewId=i%1000,globalFrameIndex=i,submissionId=i+1,resolvedSubmissionId=i+1,timestampRingSlot=i%32,gpuTimestampT0=tick,gpuTimestampT1=tick,gpuTimestampT2=tick+100000,gpuTimestampFrequency=1000000000,requiredFence=i+1,completedFence=i+1,gpuDrawMs=.1,gpuRangeMs=.1,gpuCullMs=None,submissionUtcMs=utc,availabilityUtcMs=utc+1,elapsedSeconds=(utc-start)/1000,visibleCount=25000))
  d=dict(schema=105,diagnosticOnly=True,eligibleForPerformanceAcceptance=False,completed=True,error='',samples=samples,framesExecuted=n,timestampSubmitted=n,timestampResolved=n,timestampInvalid=0,maxRingOccupancy=2,batchFenceCompleted=True,graphicsApi='Direct3D12',renderingThreadingMode='MultiThreaded',profilerEnabled=False,elapsedSeconds=30,arm='cpu',timestampFrequency=1000000000,firstWorkloadTimestamp=start,diagnosticEndTimestamp=start+30000,runId='synthetic',processId=0,warmupLastSubmissionTimestamp=samples[299]['submissionUtcMs'],warmupFencePassedTimestamp=samples[299]['submissionUtcMs']+1,firstMeasuredSubmissionTimestamp=samples[300]['submissionUtcMs'])
  rows=[dict(utcMs=u,adapters=[dict(graphicsMHz=1000,memoryMHz=1000,powerMilliwatts=10000,utilizationPercent=1,temperatureC=40,pstate=8)]) for u in range(900,31300,100)]
  result=analyze(d,dict(diagnosticOnly=True,error='',samples=rows))
  self.assertEqual(4,result['cyclesCompleted']);self.assertEqual(500,result['incompleteFinalCycleFrames']);self.assertEqual(0,result['candidate']['candidateSettlingCycle']);self.assertEqual(3,result['candidate']['confirmationCycle']);self.assertTrue(result['frame300']['beforeCandidate'])
  samples[1001]['viewId']=9
  with self.assertRaisesRegex(ValueError,'trajectory'):analyze(d,dict(diagnosticOnly=True,error='',samples=rows))
 def test_generator_preserves_arm_commands_and_native_backend(self):
  src=(ROOT/'unity/GpuDrivenCrowdBenchmark/CrossoverBenchmark.cs').read_text();out=transform(src)
  self.assertIn('schema = 105',out);self.assertIn('int index=nextFrame%1000;',out);self.assertNotIn('nextFrame == 0 && !result.warmupFenceCompleted',out)
  for token in ['Model.Cull(population, view, cpuIds)','visible.SetData(cpuIds, 0, 0, count)','commands.DispatchCompute(culling, 0, (options.agents + 255) / 256, 1, 1)','commands.CopyCounterValue(visible,args,4)','commands.DrawProceduralIndirect(Matrix4x4.identity, material, 0, MeshTopology.Triangles, args)','native.Stamp(commands,id,0)','native.Stamp(commands,id,1)','native.Stamp(commands,id,2)']:self.assertIn(token,out)
  self.assertEqual(['cpu','gpu','gpu','cpu','cpu','gpu'],ORDER)
if __name__=='__main__':unittest.main()
