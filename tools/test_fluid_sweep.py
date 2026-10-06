"""CPU evidence-integrity controls for isolated scan scaling; no GPU or Unity invocation."""
import json
from pathlib import Path
import tempfile
import unittest
import analyze_fluid_sweep as analysis

class SweepChecks(unittest.TestCase):
    def fixture(self, root):
        folder=Path(root)/'process';folder.mkdir()
        run=dict(schema='hlslperf.fluid-scan.sweep.v1',status='passed',fullOutputValidated=True,
                 gpuFrameAssociationValidated=True,applicationFrameTimeMeasured=False,fullSizeValidationPassesPerLength=2,
                 settings=dict(scanLengths=[32768,16000000],scanBatch=32,measureFrames=2),validatedLengths=[32768,16000000],
                 intervals=[dict(length=32768,firstMeasuredFrame=10,lastMeasuredFrame=11),dict(length=16000000,firstMeasuredFrame=20,lastMeasuredFrame=21)])
        (folder/'sweep.json').write_text(json.dumps(run))
        (folder/'correctness.json').write_text(json.dumps(dict(status='passed',fullOutputValidated=True)))
        rows='observed_frame,source_frame,length,complete_ms_per_scan,core_ms_per_scan,batch_gpu_ms,complete_blocks,core_blocks\n'
        for n,start in ((32768,10),(16000000,20)):
            for frame in (start,start+1):rows+=f'{frame+2},{frame},{n},1.25,1,50,32,32\n'
        (folder/'sweep-observations.csv').write_text(rows)
        return folder
    def test_nested_samples_and_full_windows(self):
        with tempfile.TemporaryDirectory() as root:
            folder=self.fixture(root);_,result=analysis.process(folder)
            self.assertEqual(result[16000000]['adaptation']['p50Ms'],.25)
            p=folder/'sweep-observations.csv';p.write_text('\n'.join(p.read_text().splitlines()[:-1])+'\n')
            with self.assertRaisesRegex(ValueError,'Incomplete measurement'):analysis.process(folder)
    def test_duplicate_outside_and_invalid_nested_samples(self):
        for old,new in (('12,10,32768','10,10,32768'),('13,11,32768','13,10,32768'),('1.25,1,50','1,1.25,50'),('50,32,32','50,1,32')):
            with tempfile.TemporaryDirectory() as root:
                folder=self.fixture(root);p=folder/'sweep-observations.csv';p.write_text(p.read_text().replace(old,new))
                with self.assertRaises(ValueError):analysis.process(folder)
    def test_invalid_attempt_and_unvalidated_outputs_are_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            folder=self.fixture(root);(Path(root)/'invalid-attempt.json').write_text('{}')
            with self.assertRaisesRegex(ValueError,'marked invalid'):analysis.process(folder)
        with tempfile.TemporaryDirectory() as root:
            folder=self.fixture(root);p=folder/'sweep.json';r=json.loads(p.read_text());r['fullSizeValidationPassesPerLength']=1;p.write_text(json.dumps(r))
            with self.assertRaisesRegex(ValueError,'post-timing'):analysis.process(folder)
    def test_four_process_pairs_are_required(self):
        result=analysis.paired_ratio([2]*4,[1]*4)
        self.assertEqual(result['baselineOverCandidate'],2)
        self.assertTrue(result['supportedFaster'])
        with self.assertRaises(ValueError):analysis.paired_ratio([2]*3,[1]*3)
if __name__=='__main__':unittest.main()
