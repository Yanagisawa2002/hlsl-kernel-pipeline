"""Validate every preregistered native process and compute process-paired estimates."""
import argparse
import csv
from datetime import datetime
import hashlib
import json
import math
from pathlib import Path
import re
import statistics

def read(p): return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def check(condition, message):
    if not condition: raise ValueError(message)

def analyze(root):
    all_rows, phases, process_windows = [], [], []
    for phase, registration, confirmation in [
        (1, 'registration-01', 'confirmation-01'),
        (2, 'registration-tile8-02', 'confirmation-tile8-02')]:
        plan_path, result_path = root / registration / 'plan.json', root / confirmation / 'confirmation.json'
        plan, result = read(plan_path), read(result_path)
        check(result['complete'] and result['planSha256'] == sha(plan_path), 'Incomplete run or altered preregistration.')
        check(len(result['results']) == len(plan['schedule']), 'Missing scheduled processes.')
        rows = []
        for expected, actual in zip(plan['schedule'], result['results']):
            check(all(actual[k] == v for k, v in expected.items()), 'Process order or workload differs from registration.')
            check(actual['exitCode'] == 0 and actual['processExited'], 'Failed process must not enter an estimate.')
            log = root / confirmation / (actual['tag'] + '.log')
            check(actual['logSha256'] == sha(log), 'Raw output hash changed.')
            check(actual['executableSha256'] == plan['identity']['binaries'][actual['program'] + '/' + actual['program'] + '.exe'], 'Executable identity mismatch.')
            text = log.read_text(encoding='utf-8', errors='strict')
            totals = re.findall(r'Total time elapsed: ([0-9.eE+-]+)', text)
            sizes = re.findall(r'^Size: (\d+)', text, re.MULTILINE)
            iterations = re.findall(r'^Test size: (\d+)', text, re.MULTILINE)
            check(len(totals) == 1 and sizes == [str(actual['count'])] and iterations == ['100'], 'Native workload/iteration mismatch.')
            total = float(totals[0])
            check(math.isfinite(total) and total > 0 and actual['totalsSeconds'] == [total], 'Invalid timestamp total.')
            throughput = float(re.findall(r'32-bit elements: ([0-9.eE+-]+) keys/sec', text)[0])
            check(math.isclose(throughput, actual['count'] * 100 / total, rel_tol=1e-5), 'Printed time/throughput disagree.')
            events = actual['events']
            device = [e for e in events if e['kind'] == 'device']
            check(len(device) == 1 and device[0]['luid'] == 76566 and device[0]['driver'] == '32.0.31041.1004', 'Unexpected device.')
            check(any(e['kind'] == 'fullSizeValidation' and e['passed'] and e['count'] == actual['count'] for e in events), 'Missing full-size validation.')
            started, finished = datetime.fromisoformat(actual['startedUtc']), datetime.fromisoformat(actual['finishedUtc'])
            check(started < finished, 'Invalid process interval.')
            process_windows.append((started, finished, actual['pid']))
            rows.append({**expected, 'phase': phase, 'meanMilliseconds': total * 10,
                'totalSeconds': total, 'pid': actual['pid'], 'log': str(log.resolve()), 'logSha256': sha(log)})
        all_rows.extend(rows)
        phases.append({'phase': phase, 'registeredUtc': plan['registeredUtc'], 'sourceCommit': plan['identity']['sourceCommit'],
            'planPath': str(plan_path.resolve()), 'planSha256': sha(plan_path), 'confirmationPath': str(result_path.resolve()),
            'confirmationSha256': sha(result_path), 'processes': len(rows), 'identity': plan['identity']})
    windows = sorted(process_windows)
    check(all(a[1] <= b[0] for a, b in zip(windows, windows[1:])), 'Concurrent test processes detected.')
    groups, pairs = [], []
    # The df=4 t critical value is checked against the closed-form t4 CDF.
    critical = 2.7764451051977987
    s = critical / math.sqrt(critical * critical + 4)
    check(abs((.5 + .75 * s - .25 * s**3) - .975) < 1e-10, 'Incorrect confidence critical value.')
    for name in dict.fromkeys(r['group'] for r in all_rows):
        cells = [r for r in all_rows if r['group'] == name]
        check(len(cells) == 10, 'Every group must have ten independent process outputs.')
        group_pairs = []
        for pair in range(1, 6):
            arms = [r for r in cells if r['pair'] == pair]
            check(len(arms) == 2, 'A pair must have exactly two arms.')
            baseline = next(r for r in arms if r['arm'] == r['baseline'])
            candidate = next(r for r in arms if r['arm'] == r['candidate'])
            ratio = baseline['meanMilliseconds'] / candidate['meanMilliseconds']
            record = {'phase': baseline['phase'], 'group': name, 'pair': pair, 'count': baseline['count'],
                'baseline': baseline['arm'], 'candidate': candidate['arm'], 'baselineMilliseconds': baseline['meanMilliseconds'],
                'candidateMilliseconds': candidate['meanMilliseconds'], 'baselineOverCandidate': ratio,
                'order': '/'.join(r['arm'] for r in arms), 'baselineLogSha256': baseline['logSha256'], 'candidateLogSha256': candidate['logSha256']}
            group_pairs.append(record)
        ratios = [p['baselineOverCandidate'] for p in group_pairs]
        logs = [math.log(value) for value in ratios]
        log_mean = statistics.mean(logs)
        margin = critical * statistics.stdev(logs) / math.sqrt(5)
        mean_b = statistics.mean(p['baselineMilliseconds'] for p in group_pairs)
        mean_c = statistics.mean(p['candidateMilliseconds'] for p in group_pairs)
        ratio = math.exp(log_mean)
        # Independent geometric-mean arithmetic spot check.
        check(math.isclose(ratio, math.prod(ratios)**.2, rel_tol=1e-12), 'Geometric-mean check failed.')
        groups.append({'phase': group_pairs[0]['phase'], 'group': name, 'count': group_pairs[0]['count'],
            'baseline': group_pairs[0]['baseline'], 'candidate': group_pairs[0]['candidate'], 'pairs': 5,
            'baselineMeanMilliseconds': mean_b, 'candidateMeanMilliseconds': mean_c,
            'baselineGigaElementsPerSecond': group_pairs[0]['count'] / (mean_b * 1e6),
            'candidateGigaElementsPerSecond': group_pairs[0]['count'] / (mean_c * 1e6),
            'baselineOverCandidateGeometricMean': ratio, 'ratio95PercentInterval': [math.exp(log_mean - margin), math.exp(log_mean + margin)],
            'candidateOverBaselineGeometricMean': 1 / ratio})
        pairs.extend(group_pairs)
    return {'schema': 'hlslperf.native-confirmation-analysis.v1', 'date': '2026-09-09',
        'assessment': 'Share with caveats: complete paired comparisons on one device and driver; workload-specific, nominal intervals, no default promotion.',
        'formalProcesses': len(all_rows), 'formalFailures': 0, 'excludedProcesses': 0, 'processOverlap': False,
        'device': device[0], 'firstProcessStartedUtc': windows[0][0].isoformat(), 'lastProcessFinishedUtc': windows[-1][1].isoformat(),
        'method': 'Equal 100-iteration process means. Geometric mean of five within-pair baseline/candidate ratios. 95% Student-t interval on logs, df4. Nominal per-comparison intervals; no multiplicity adjustment or best-run selection.',
        'caveats': ['Original native aggregates are printed to six decimal seconds; precision is retained without inventing per-iteration data.',
            'One device/driver and one sequential session. User applications, temperature and clocks were not controlled or measured.',
            'Phase one and phase two are independently registered; cross-phase tile4/tile8 values are not a direct paired experiment.',
            'Baseline native algorithms are unchanged; the host selects a LUID, exposes separate validation and batches, and applies an identified FFX allocation-only fix.',
            'FFX is the version vendored by pinned GPUSorting, not an interchangeable result for the SDK 1.1.4 UnifiedBench adapter or historical measurements.'],
        'phases': phases, 'groups': groups, 'pairs': pairs, 'processRows': all_rows}

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    summary = analyze(args.root.resolve())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8')
    csv_path = args.output.with_suffix('.pairs.csv')
    with csv_path.open('w', newline='', encoding='utf-8') as stream:
        writer = csv.DictWriter(stream, fieldnames=list(summary['pairs'][0]))
        writer.writeheader(); writer.writerows(summary['pairs'])
    for g in summary['groups']:
        print(f"{g['group']}: baseline {g['baselineMeanMilliseconds']:.5f} ms; candidate {g['candidateMeanMilliseconds']:.5f} ms; B/C {g['baselineOverCandidateGeometricMean']:.6f}; 95% {g['ratio95PercentInterval']}")
