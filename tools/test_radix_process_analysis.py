import copy
import importlib.util
import math
from pathlib import Path
import sys
import unittest
sys.dont_write_bytecode=True
spec=importlib.util.spec_from_file_location('radix_audit',Path(__file__).with_name('analyze_radix_process_matrix.py'))
a=importlib.util.module_from_spec(spec);spec.loader.exec_module(a)

def fixture(i,median=1):
    latency=dict(meanMs=median,medianMs=median,p95Ms=median,p99Ms=median,maxMs=median)
    return dict(cell='synthetic',round=i,supported=True,deployable=True,candidateSummaryStable=True,comparisons=[dict(passed=True)],confirmation=dict(geometricSpeedup=1.2,baselineLatency=latency,candidateLatency=latency),selected='fixed8',selectedBits=8,costs=[])

class GateTests(unittest.TestCase):
    def test_all_five_required(self):
        with self.assertRaises(AssertionError):a.preflight_gate([fixture(i) for i in range(1,5)])
    def test_one_failed_confirmation_blocks_entry(self):
        rows=[fixture(i) for i in range(1,6)];self.assertTrue(a.preflight_gate(rows)['eligible']);rows[2]['deployable']=False;self.assertFalse(a.preflight_gate(rows)['eligible'])
    def test_failed_calibration_cannot_hide_behind_confirmation(self):
        rows=[fixture(i) for i in range(1,6)];rows[2]['comparisons'][0]['passed']=False;self.assertFalse(a.preflight_gate(rows)['eligible'])
    def test_unstable_candidate_summary_blocks_entry(self):
        rows=[fixture(i) for i in range(1,6)];rows[0]['candidateSummaryStable']=False;self.assertFalse(a.preflight_gate(rows)['eligible'])
    def test_process_median_variation_blocks_entry(self):
        rows=[fixture(i,1 if i<5 else 1.2) for i in range(1,6)];r=a.preflight_gate(rows);self.assertFalse(r['eligible']);self.assertGreater(r['processMedianCv'],.05);self.assertGreater(r['processMedianDrift'],.15)
    def test_memory_or_unsupported_process_cannot_enter(self):
        rows=[fixture(i) for i in range(1,6)];rows[4]=dict(cell='synthetic',round=5,supported=False);r=a.preflight_gate(rows);self.assertFalse(r['eligible']);self.assertEqual(r['status'],'unsupported_or_failed')
    def test_mixed_comparison_selection_not_pooled(self):
        rows=[fixture(i) for i in range(1,6)];rows[1]['selected']='fixed1';rows[1]['selectedBits']=1;r=a.comparison_summary(rows);self.assertFalse(r['accepted']);self.assertIsNone(r['speedup'])
    def test_favorable_aggregate_does_not_override_one_rejection(self):
        rows=[fixture(i) for i in range(1,6)];self.assertTrue(a.comparison_summary(rows)['accepted']);rows[0]['deployable']=False;self.assertFalse(a.comparison_summary(rows)['accepted'])
    def test_retained_baseline_not_a_gain(self):
        rows=[fixture(i) for i in range(1,6)]
        for r in rows:r['selected']='fixed1';r['selectedBits']=1
        self.assertFalse(a.comparison_summary(rows)['accepted'])
    def test_process_equal_weight_log_interval(self):
        r=a.interval([math.exp(x) for x in range(5)],True);self.assertAlmostEqual(r['estimate'],math.exp(2));self.assertAlmostEqual(r['lower95'],math.exp(2-a.T4/math.sqrt(2)));self.assertEqual(r['df'],4)
    def test_tail_interpolation(self):
        self.assertAlmostEqual(a.percentile([1,2,3,4],.95),3.85);self.assertAlmostEqual(a.percentile([1,2,3,4],.99),3.97)

if __name__=='__main__':unittest.main(verbosity=2)
