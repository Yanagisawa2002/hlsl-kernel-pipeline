"""Synthetic checks for balance, independence boundaries and reported units."""
import contextlib
import importlib.util
import io
import itertools
import json
import tempfile
import unittest
from collections import Counter
from pathlib import Path

REPO=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('protocol',REPO/'tools/unified_protocol.py')
protocol=importlib.util.module_from_spec(spec);spec.loader.exec_module(protocol)


class ProtocolTests(unittest.TestCase):
    def test_frozen_orders_remain_balanced_when_one_arm_is_incorrect(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'declaration.json'
            with contextlib.redirect_stdout(io.StringIO()): protocol.generate(path,REPO)
            declaration=json.loads(path.read_text())
        self.assertEqual(24,len(declaration['cells']))
        self.assertEqual(120,len({(run['cell'],run['process']) for run in declaration['processOrder']}))
        for cell in declaration['cells']:
            for process in cell['processes']:
                for missing in [None]+cell['arms']:
                    orders=[tuple(a for a in order if a!=missing) for block in process['orders'] for order in [block,block[::-1]]]
                    remaining=[a for a in cell['arms'] if a!=missing]
                    counts=Counter(orders)
                    self.assertEqual(set(itertools.permutations(remaining)),set(counts))
                    self.assertEqual(1,len(set(counts.values())))

    def test_batch_and_operation_units_are_not_confused(self):
        timing=dict(repetitions=18,gpuTotalMilliseconds=36,gpuOperationMilliseconds=[1.9]*18,
                    cpuRecordMilliseconds=18,submission=dict(cpuSubmitMilliseconds=9),gpuAlgorithmMilliseconds=27,
                    gpuInputRestoreMilliseconds=3,gpuScratchInitializationMilliseconds=2,gpuOutputConversionMilliseconds=1)
        process=dict(statuses={'arm':'measured_correct'},observations=[dict(arm='arm',status='measured',timing=timing) for _ in range(24)])
        metrics=protocol.arm_metrics(process,'arm')
        self.assertEqual(2,metrics['meanMs']);self.assertAlmostEqual(1.9,metrics['p95Ms'])
        self.assertEqual(0,metrics['cv']);self.assertEqual(0,metrics['drift'])
        self.assertEqual(12,len(metrics['blocks']));self.assertEqual(.5,metrics['cpuSubmitMs'])
        process['statuses']['arm']='post_correctness_failed'
        self.assertIsNone(protocol.arm_metrics(process,'arm'))

    def test_quantiles_use_linear_interpolation(self):
        self.assertEqual(2.5,protocol.quantile([4,1,3,2],.5))
        self.assertAlmostEqual(3.85,protocol.quantile([4,1,3,2],.95))


if __name__=='__main__': unittest.main()
