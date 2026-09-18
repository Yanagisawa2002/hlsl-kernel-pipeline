import copy
import tempfile
import unittest
from pathlib import Path
from analyze_crossover import aggregate, export, paired_ratio, validate_result, drift
from run_crossover import balanced_schedule


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
