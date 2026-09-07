import copy
import importlib.util
import math
from pathlib import Path
import sys
import unittest

sys.dont_write_bytecode=True
spec=importlib.util.spec_from_file_location('scan_audit',Path(__file__).with_name('analyze_scan_process_matrix.py'))
a=importlib.util.module_from_spec(spec);spec.loader.exec_module(a)

def process(round):
    latency=dict(meanMs=1,medianMs=1,p95Ms=1,p99Ms=1,maxMs=1)
    c=dict(phase='confirmation',backend=3,geometricSpeedup=1.2,passed=True,stableTiming=True,baselineLatency=latency,candidateLatency=latency)
    comparisons=[dict(c,phase='calibration',backend=b) for b in (2,3)]
    return dict(round=round,cell='fixture',selected='candidate3',selectedBackend=3,deployable=True,confirmation=c,comparisons=comparisons,costs=[])

class ProcessStatisticsTests(unittest.TestCase):
    def test_t_interval_uses_five_process_means_and_df4(self):
        result=a.interval([math.exp(v) for v in (0,1,2,3,4)],True)
        radius=a.T4/math.sqrt(2)
        self.assertAlmostEqual(result['estimate'],math.exp(2))
        self.assertAlmostEqual(result['lower95'],math.exp(2-radius))
        self.assertEqual(result['df'],4)
    def test_incomplete_process_count_cannot_be_a_full_interval(self):
        with self.assertRaises(AssertionError):a.interval([1.2]*4,True)
    def test_linear_tail_quantiles(self):
        self.assertAlmostEqual(a.percentile(list(range(1,101)),.95),95.05)
        self.assertAlmostEqual(a.percentile(list(range(1,101)),.99),99.01)
    def test_one_rejected_process_blocks_recommendation(self):
        rows=[process(i) for i in range(1,6)]
        self.assertTrue(a.aggregate(rows)['accepted'])
        rows[2]['deployable']=False
        self.assertFalse(a.aggregate(rows)['accepted'])
    def test_mixed_selection_has_no_pooled_confirmation_claim(self):
        rows=[process(i) for i in range(1,6)];rows[2]['selected']='candidate1';rows[2]['selectedBackend']=1
        r=a.aggregate(rows)
        self.assertFalse(r['accepted']);self.assertIsNone(r['speedup']);self.assertEqual(r['confirmationLatency'],{})
    def test_baseline_control_is_not_a_gain(self):
        rows=[process(i) for i in range(1,6)]
        for r in rows:r['selectedBackend']=1;r['selected']='candidate1'
        r=a.aggregate(rows)
        self.assertFalse(r['accepted']);self.assertEqual(r['status'],'baseline_retained')
    def test_repeated_round_cannot_create_five_independent_rounds(self):
        rows=[process(i) for i in range(1,6)];rows[4]['round']=4
        with self.assertRaises(AssertionError):a.aggregate(rows)

if __name__=='__main__':unittest.main(verbosity=2)
