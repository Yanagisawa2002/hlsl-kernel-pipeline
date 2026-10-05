"""CPU controls for complete and paired application evidence; never starts Unity."""
import json
from pathlib import Path
import tempfile
import unittest
import analyze_fluid_application_scaling as analysis
from compare_fluid_benchmark import METRICS, NAMES
from run_fluid_scaling import schedule


def write(path, obj):
    path.write_text(json.dumps(obj), encoding='utf-8')


class ApplicationChecks(unittest.TestCase):
    def fixture(self, root):
        root = Path(root)
        cases = schedule('application', [60,200], 2)
        write(root/'plan.json', dict(stage='application',recording=False,cases=cases,playerFilesSha256={'FluidScan.exe':'player'}))
        write(root/'completion.json', dict(status='passed',processes=len(cases)))
        for index, case in enumerate(cases):
            folder = root/case['name']; folder.mkdir()
            run = dict(schema='hlslperf.fluid-scan.run.v1',firstMeasuredUnityFrame=10,lastMeasuredUnityFrame=11,
                iterationsPerFrame=3,gpuDelayValidated=True,positionFiniteValidated=True,coreTimingEnabled=True,
                device='fixture',api='Direct3D12',unity='fixture',width=1920,height=1080,
                particles=case['density']*100,foamCapacity=1024000,fixedTimestep=1/60,
                gpuTimingMethod='d3d12_query_frame_fence',nativeTimingBuild='fixture',
                gpuTimestampFrequency='1000000000',
                provenance=json.dumps({'payloadSha256':'fixture'}),settings=dict(measureFrames=2,warmupFrames=120,
                spawnDensity=case['density'],seed=42,nativeGpuTiming=True,arm=next(k for k,v in NAMES.items() if v == case['arm'])))
            write(folder/'run.json', run)
            write(folder/'correctness.json', dict(status='passed',fullOutputValidated=True))
            receipt = dict(case,exitCode=0,playerSha256='player')
            write(folder/'process.json', receipt); write(root/(case['name']+'.process.json'), receipt)
            rows = 'observed_unity_frame,source_unity_frame,metric,ms,sample_blocks,status\n'
            for frame in (10,11):
                for metric in (*METRICS,'scan_core'):
                    value = {'scan_core':1,'scan_complete':2,'count_sort_complete':3,'spatial_hash_complete':4,'simulation_complete':5,'wall_frame':6}[metric]+index/100
                    blocks = 1 if metric in ('wall_frame','simulation_complete') else 3
                    status = 'observed' if metric == 'wall_frame' else 'native_frame_fence_validated'
                    rows += f'{frame+2},{frame},{metric},{value},{blocks},{status}\n'
            (folder/'observations.csv').write_text(rows,encoding='utf-8')
        return root

    def test_complete_cohort_and_process_pairing(self):
        with tempfile.TemporaryDirectory() as directory:
            result = analysis.analyze(self.fixture(directory))
            self.assertEqual(result['processCount'],32)
            self.assertEqual(set(result['particleCounts']),{6000,20000})
            self.assertAlmostEqual(result['particleCounts'][6000]['arms']['original']['metrics']['scan_core']['meanOfRunMeanMs'],1.135)

    def test_incomplete_plan_or_receipt_is_rejected(self):
        for field in ('completion','plan','receipt'):
            with tempfile.TemporaryDirectory() as directory:
                root = self.fixture(directory)
                if field == 'completion':
                    write(root/'completion.json',dict(status='passed',processes=31))
                elif field == 'plan':
                    plan=analysis.load(root/'plan.json');plan['cases'][0]['arm']='hlsl-wave-tiled';write(root/'plan.json',plan)
                else:
                    write(root/'app-d60-r01-original/process.json',dict(exitCode=2))
                with self.assertRaises(ValueError):analysis.analyze(root)

    def test_unassociated_nonfinite_or_non_nested_samples_are_rejected(self):
        for old,new in (('12,10,scan_core,1.0','10,10,scan_core,1.0'),('scan_core,1.0','scan_core,nan'),
                        ('scan_core,1.0','scan_core,9.0'),('native_frame_fence_validated','unavailable')):
            with tempfile.TemporaryDirectory() as directory:
                root=self.fixture(directory);p=root/'app-d60-r01-original/observations.csv'
                p.write_text(p.read_text(encoding='utf-8').replace(old,new),encoding='utf-8')
                with self.assertRaises(ValueError):analysis.analyze(root)

    def test_finite_position_source_and_duplicate_evidence_checks(self):
        for field in ('positionFiniteValidated','provenance','gpuTimestampFrequency','duplicate'):
            with tempfile.TemporaryDirectory() as directory:
                root=self.fixture(directory);p=root/'app-d200-r04-gpuprefixsums-rts/run.json';run=analysis.load(p)
                if field == 'positionFiniteValidated':run[field]=False
                elif field == 'provenance':run[field]=json.dumps({'payloadSha256':'different'})
                elif field == 'gpuTimestampFrequency':run[field]='0'
                else:
                    (p.parent/'observations.csv').write_bytes((root/'app-d60-r01-original/observations.csv').read_bytes())
                write(p,run)
                with self.assertRaises(ValueError):analysis.analyze(root)


if __name__ == '__main__':
    unittest.main()
