import math
import unittest
from analyze_crossover_drift import candidates, quarter_stats, distribution, trend


def step(a,b):
    return [a]*8+[b]*8


class DriftForensicsTests(unittest.TestCase):
    def test_tiny_stage_relative_vs_materiality(self):
        d=candidates(step(.030,.060),3.0)
        self.assertAlmostEqual(1,d['diagnostic']['relativeDrift'])
        self.assertAlmostEqual(.03,d['diagnostic']['absoluteDriftMs'])
        self.assertTrue(d['AStageTriggers'])
        self.assertFalse(d['BStageTriggers'])  # exactly 1%, strict >
        self.assertFalse(d['CStageTriggers'])
        self.assertTrue(candidates(step(.030,.060),.3)['BStageTriggers'])

    def test_material_absolute_drift(self):
        d=candidates(step(.30,.60),3)
        self.assertTrue(all(d[k] for k in ('AStageTriggers','BStageTriggers','CStageTriggers')))

    def test_stable_stage(self):
        d=candidates([.06]*16,3)
        self.assertFalse(any(d[k] for k in ('v4StageCriterionTriggers','AStageTriggers','BStageTriggers','CStageTriggers')))

    def test_primary_cpu_drift_cannot_be_hidden_by_stable_gpu(self):
        d=candidates([.03]*16,3,step(2,3))
        self.assertTrue(d['primaryCpuCriterionTriggers'])
        self.assertFalse(d['BStageTriggers'])

    def test_zero_and_near_zero_denominator(self):
        zero=candidates([0]*16,3)
        self.assertEqual(0,zero['diagnostic']['relativeDrift'])
        tiny=candidates(step(0,1e-9),3)
        self.assertIsNone(tiny['diagnostic']['relativeDrift'])
        self.assertTrue(tiny['v4StageCriterionTriggers'])
        self.assertFalse(tiny['BStageTriggers'])
        material=candidates(step(0,.1),3)
        self.assertTrue(material['BStageTriggers'])
        near=quarter_stats(step(1e-12,2e-12))
        self.assertEqual(1,near['relativeDrift'])

    def test_malformed_nonfinite_and_missing_architecture_scale(self):
        for xs in ([],[1]*3,[1,2,3,float('nan')],[1,2,3,float('inf')],[1,2,3,-1],[1,2,3,None],[True]*4):
            with self.subTest(xs=xs),self.assertRaises(ValueError):quarter_stats(xs)
        for scale in (0,-1,float('nan'),float('inf')):
            with self.subTest(scale=scale),self.assertRaises(ValueError):candidates([.1]*4,scale)
        self.assertIsNone(candidates([.1]*4,None)['BStageTriggers'])

    def test_primary_hierarchy_sees_low_relative_material_change(self):
        d=candidates(step(1,1.1),3)
        self.assertFalse(d['BStageTriggers'])
        self.assertTrue(d['CStageTriggers'])

    def test_linear_rank_trend_and_constant_distribution(self):
        t=trend([1,2,3,4])
        self.assertAlmostEqual(1,t['slopeMsPerFrame'])
        self.assertAlmostEqual(1,t['spearmanFrameRank'])
        self.assertEqual(0,distribution([0]*4)['CV'])
        self.assertIsNone(trend([1]*4)['spearmanFrameRank'])


if __name__=='__main__':unittest.main()
