import contextlib
import io
import gzip
import json
from pathlib import Path
import unittest
from unittest.mock import patch
import watch_gpu_quiet as watcher


class QuietObserverTests(unittest.TestCase):
    def test_resets_on_busy_and_requires_all_adapters(self):
        samples = [[(1,40)],[(2,40),(6,40)],[(0,40)],[(5,40)]]
        with patch.object(watcher,'query',side_effect=samples) as query, patch.object(watcher.time,'sleep'), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0,watcher.observe(5,2,1))
            self.assertEqual(4,query.call_count)

    def test_unavailable_resets_streak(self):
        with patch.object(watcher,'query',side_effect=[[(0,40)],ValueError(),[(0,40)],[(0,40)]]) as query, patch.object(watcher.time,'sleep'), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0,watcher.observe(5,2,1))
            self.assertEqual(4,query.call_count)

    def test_timeout_does_not_query_or_launch(self):
        with patch.object(watcher.time,'monotonic',side_effect=[0,2]), patch.object(watcher.subprocess,'run') as run, contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(1,watcher.observe(5,10,1,1))
            run.assert_not_called()

    def test_complete_on_run_still_fails_quality_drift(self):
        from check_integrated_crossover import validate
        path = Path(__file__).resolve().parents[1] / 'docs/evidence/gpu-quiet-window-20260919/overhead-cpu-0-on.json.gz'
        result = json.loads(gzip.decompress(path.read_bytes()))
        self.assertEqual(1000, validate(result, False)['timestampResolved'])
        with self.assertRaisesRegex(ValueError, 'GPU drift'):
            validate(result, True)

    def test_query_only_asks_for_aggregate_data(self):
        with patch.object(watcher.subprocess,'run') as run:
            run.return_value.stdout='3, 42\n'
            self.assertEqual([(3,42)],watcher.query())
            self.assertEqual(['nvidia-smi','--query-gpu=utilization.gpu,temperature.gpu','--format=csv,noheader,nounits'],run.call_args.args[0])


if __name__=='__main__':unittest.main()
