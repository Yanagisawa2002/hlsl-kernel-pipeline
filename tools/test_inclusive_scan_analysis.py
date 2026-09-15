"""Small synthetic evidence tests for the process-level analysis, without running any GPU program."""
import contextlib
import copy
import io
import itertools
import json
import math
import statistics
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from run_inclusive_scan import ARMS, COUNT, T5, analyze, save, sha


class InclusiveAnalysisTests(unittest.TestCase):
    def fixture(self, folder):
        device = {'kind': 'device', 'adapter': 'synthetic', 'luid': 123, 'driver': 'test'}
        schedule = [{'tag': f'r{r}-{pos}-{arm}', 'round': r, 'position': pos, 'arm': arm}
            for r, order in enumerate(itertools.permutations(ARMS), 1) for pos, arm in enumerate(order, 1)]
        plan = {'schedule': schedule, 'device': device, 'identity': {'runtimeFiles': {'scan/scan.exe': 'a'*64}}}
        save(folder/'preregistered-plan.json', plan)
        start = datetime(2026, 1, 1, tzinfo=timezone.utc)
        results = []
        for index, job in enumerate(schedule):
            arm, repeat = job['arm'], job['round']
            # Pair ratios vary independently of common round drift.
            ms = {'tile': 4.0 + repeat * .2, 'tile-fused': 2.0 + repeat * .03, 'rts': 1.5 + repeat * .01}[arm]
            total = round(ms / 10, 6)
            events = [device, {'kind': 'arm', 'backend': arm, 'count': COUNT, 'batchSize': 100, 'warmupIterations': 1},
                {'kind': 'fullSizeValidation', 'count': COUNT, 'passed': True}]
            text = '\n'.join('HPJSON '+json.dumps(e) for e in events)
            text += f'\nSize: {COUNT}\nTest size: 100\nTotal time elapsed: {total:.6f}\nEstimated speed at {COUNT} 32-bit elements: {COUNT*100/total:.6E} keys/sec\n'
            (folder/(job['tag']+'.log')).write_text(text)
            row = {**job, 'mode': 'batch-only', 'count': COUNT, 'events': events, 'totalsSeconds': [total],
                'meanMs': total*10, 'exitCode': 0, 'executableSha256': 'a'*64, 'pid': 100+index,
                'startedUtc': (start+timedelta(seconds=index*2)).isoformat(),
                'finishedUtc': (start+timedelta(seconds=index*2+1)).isoformat(),
                'logSha256': sha(folder/(job['tag']+'.log'))}
            save(folder/(job['tag']+'.status.json'), row); results.append(row)
        save(folder/'confirmation.json', {'complete': True, 'planSha256': sha(folder/'preregistered-plan.json'), 'results': results})
        return results

    def test_independent_rounds_and_t_interval(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory); rows = self.fixture(folder)
            with contextlib.redirect_stdout(io.StringIO()): analyze(folder, folder/'analysis.json')
            result = json.loads((folder/'analysis.json').read_text())
            comparison = result['comparisons'][0]
            ratios = [(4+r*.2)/(2+r*.03) for r in range(1, 7)]
            logs = list(map(math.log, ratios)); mean = statistics.mean(logs)
            width = T5*statistics.stdev(logs)/math.sqrt(6)
            self.assertAlmostEqual(math.exp(mean), comparison['geometricMeanRatio'])
            self.assertAlmostEqual(math.exp(mean-width), comparison['ratio95Ci'][0])
            self.assertAlmostEqual(math.exp(mean+width), comparison['ratio95Ci'][1])
            self.assertEqual(result['independentRounds'], 6)
            self.assertEqual(result['iterationsPerProcess'], 100)
            self.assertEqual(len(rows), 18)

    def test_changed_log_missing_process_and_overlap_fail_closed(self):
        for problem in ['log', 'missing', 'overlap', 'iterations', 'device']:
            with self.subTest(problem=problem), tempfile.TemporaryDirectory() as directory:
                folder = Path(directory); rows = self.fixture(folder)
                if problem == 'log': (folder/(rows[0]['tag']+'.log')).write_text('corrupt')
                else:
                    data = json.loads((folder/'confirmation.json').read_text())
                    if problem == 'missing': data['results'].pop()
                    elif problem == 'overlap':
                        data['results'][1]['startedUtc'] = rows[0]['startedUtc']
                        save(folder/(rows[1]['tag']+'.status.json'), data['results'][1])
                    else:
                        row = data['results'][0]
                        path = folder/(row['tag']+'.log')
                        if problem == 'iterations': path.write_text(path.read_text().replace('Test size: 100', 'Test size: 10'))
                        else:
                            row['events'][0]['driver'] = 'wrong'
                            path.write_text(path.read_text().replace('"driver": "test"', '"driver": "wrong"'))
                        row['logSha256'] = sha(path)
                        save(folder/(row['tag']+'.status.json'), row)
                    save(folder/'confirmation.json', data)
                with self.assertRaises(ValueError):
                    with contextlib.redirect_stdout(io.StringIO()): analyze(folder, folder/'analysis.json')


if __name__ == '__main__': unittest.main()
