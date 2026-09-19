"""Operational quiet-window observer. Never launches a benchmark or supplies its gate."""
import argparse
from datetime import datetime, timezone
import math
import subprocess
import time


def query():
    result = subprocess.run(
        ['nvidia-smi', '--query-gpu=utilization.gpu,temperature.gpu',
         '--format=csv,noheader,nounits'], capture_output=True, text=True,
        check=True, timeout=10)
    values = [tuple(map(float, line.split(','))) for line in result.stdout.strip().splitlines()]
    if not values or any(len(v) != 2 or not all(math.isfinite(x) for x in v)
                         or not 0 <= v[0] <= 100 for v in values):
        raise ValueError('invalid aggregate GPU observation')
    return values


def observe(threshold, consecutive, interval, timeout=None):
    start = time.monotonic()
    streak = 0
    count = 0
    print('OPERATIONAL ONLY: success is a candidate window, not the formal three-snapshot gate.', flush=True)
    while timeout is None or time.monotonic() - start < timeout:
        stamp = datetime.now(timezone.utc).isoformat()
        count += 1
        try:
            values = query()
            streak = streak + 1 if all(u <= threshold for u, _ in values) else 0
            data = '; '.join(f'GPU{i}: utilization={u:g}% temperature={t:g}C'
                             for i, (u, t) in enumerate(values))
        except (OSError, subprocess.SubprocessError, ValueError):
            streak = 0
            data = 'aggregate query unavailable; streak reset'
        print(f'{stamp} {data} streak={streak}/{consecutive}', flush=True)
        if streak >= consecutive:
            print(f'Candidate window: {count} observations, elapsed={time.monotonic()-start:.3f}s. '
                  'No benchmark launched; formal gate still required.', flush=True)
            return 0
        remaining = interval if timeout is None else min(interval, max(0, timeout-(time.monotonic()-start)))
        time.sleep(remaining)
    print('Observer timeout; no benchmark launched.', flush=True)
    return 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--threshold', type=float, default=5)
    parser.add_argument('--consecutive', type=int, default=10)
    parser.add_argument('--interval', type=float, default=1)
    parser.add_argument('--timeout', type=float, help='Optional operator-specified seconds; default waits until interrupted')
    args = parser.parse_args()
    if (not math.isfinite(args.threshold) or not 0 <= args.threshold <= 100
            or args.consecutive < 1 or not math.isfinite(args.interval) or args.interval <= 0
            or args.timeout is not None and (not math.isfinite(args.timeout) or args.timeout <= 0)):
        parser.error('Require threshold 0..100, consecutive >=1, finite positive interval/timeout')
    try:
        return observe(args.threshold, args.consecutive, args.interval, args.timeout)
    except KeyboardInterrupt:
        print('Observer interrupted; no benchmark launched.', flush=True)
        return 130


if __name__ == '__main__':
    raise SystemExit(main())
