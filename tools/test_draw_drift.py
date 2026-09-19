import copy,gzip,json,unittest
from pathlib import Path
from prepare_draw_drift import managed_source,native_source,patch
from run_draw_drift import check
from check_integrated_crossover import validate
from analyze_draw_drift import analyze,nearest,assoc
ROOT=Path(__file__).resolve().parents[1]
E=ROOT/'docs/evidence/draw-drift-diagnostic-20260919'
def read(name):return json.loads(gzip.decompress((E/(name+'.json.gz')).read_bytes()))
class DrawDriftTests(unittest.TestCase):
 def test_generated_actual_draw_method_is_unchanged(self):
  source=(ROOT/'unity/GpuDrivenCrowdBenchmark/CrossoverBenchmark.cs').read_text()
  generated=managed_source(source)
  extract=lambda s:s[s.index('        void Draw('):s.index('        void Update()')]
  self.assertEqual(extract(source),extract(generated))
 def test_native_query_encloses_draw_and_preserves_ring(self):
  source=(ROOT/'unity/GpuDrivenCrowdBenchmark/Native/CrossoverTimestamp.cpp').read_text();s=native_source(source)
  self.assertIn('constexpr unsigned Capacity=32;',s)
  self.assertLess(s.index('if(stage==2)recording.commandList->EndQuery(statsHeap'),s.index('recording.commandList->EndQuery(heap.Get(),D3D12_QUERY_TYPE_TIMESTAMP,base+stage)'))
  self.assertLess(s.index('recording.commandList->EndQuery(heap.Get(),D3D12_QUERY_TYPE_TIMESTAMP,base+stage)'),s.index('if(stage==1)recording.commandList->BeginQuery(statsHeap'))
  self.assertLess(s.index('if(completed<s.fence)return 0'),s.index('std::memcpy(output+7'))
 def test_patch_refuses_stale_or_duplicate_anchor(self):
  for s in ['missing','xx']:
   with self.assertRaises(ValueError):patch(s,'x','y')
 def test_short_and_three_runs_complete_but_never_acceptable(self):
  for name in ['short','forward','frozen','reverse']:
   d=read(name);t=read(name+'.telemetry');self.assertTrue(check(d,t)['instrumentationValid'])
   with self.assertRaises(ValueError):validate(d,False)
 def test_fail_closed_native_identity_and_stats_mutations(self):
  d=read('short');t=read('short.telemetry')
  for mutate in [lambda x:x['samples'][0].update(resolvedSubmissionId=999),lambda x:x['samples'][0].update(PSInvocations=0),lambda x:x.update(maxRingOccupancy=32),lambda x:x['samples'][0].update(gpuDrawMs=99),lambda x:x.update(diagnosticOnly=False)]:
   bad=copy.deepcopy(d);mutate(bad)
   with self.assertRaises(AssertionError):check(bad,t)
 def test_matched_view_identity_and_frozen_work(self):
  f,r,z=read('forward'),read('reverse'),read('frozen')
  for a,b in zip(f['samples'],reversed(r['samples'])):
   self.assertEqual(a['viewIndex'],b['viewIndex']);self.assertEqual(a['PSInvocations'],b['PSInvocations']);self.assertEqual(a['visibleCount'],b['visibleCount'])
  self.assertEqual({500},{s['viewIndex'] for s in z['samples']});self.assertEqual(1,len({s['PSInvocations'] for s in z['samples']}))
  self.assertEqual(f['viewProxies'],r['viewProxies']);self.assertEqual(f['viewProxies'],z['viewProxies'])
 def test_alignment_tolerance_and_constant_correlation(self):
  rows=[{'utcMs':100},{'utcMs':200}];self.assertEqual(100,nearest(rows,110)['utcMs']);self.assertIsNone(nearest(rows,500));self.assertIsNone(assoc([1]*4,[1,2,3,4])['spearman'])
 def test_analysis_reproduces_archived_results(self):
  self.assertEqual(read('analysis'),analyze(E))
if __name__=='__main__':unittest.main()
