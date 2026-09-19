# Optional operational quiet-window observer

From the repository root:

```powershell
python tools/watch_gpu_quiet.py --threshold 5 --consecutive 10 --interval 1
```

This displays UTC time, anonymous adapter utilization/temperature, and the current consecutive sample streak. Every adapter must satisfy the requested operational threshold. Busy, unavailable or malformed observations reset the streak. Success exits 0 after ten consecutive observations; these samples span approximately nine seconds plus query time, not proof of continuous inactivity between observations. Sampling waits one second after each query, so query latency makes the actual period slightly longer than one second.

The observer **never launches a Player, writes benchmark evidence or changes benchmark settings**. A candidate window does not replace the runner's independent three pre-launch samples, all <=5%. Changing the observer threshold cannot alter the formal gate. No process identities, paths or window titles are queried. Error text is sanitized. Redirected console output is operational history only, never formal acceptance evidence.

Default operation continues until success or Ctrl+C (exit 130). An operator can explicitly bound observation, for example `--timeout 300` (seconds; timeout exits 1). A query can take up to ten seconds, so timeout is checked between queries and is not a hard process deadline.

After a candidate window the operator may invoke the next predetermined control. Retain existing CPU OFF exactly; pair A is OFF then ON, pair B ON then OFF. Do not select ordering based on load. A blocked formal launch is retained and stops that attempt. No automatic retry. The observer can be used before a next authorized process but cannot turn a blocked launch into a retry loop.

The current continuation uses a separate local attempt directory and explicit prerequisite reuse hashes, retaining the old blocked ON receipt. The accepted OFF is reused only as authorized overhead-control evidence. It is not a crossover result or a timestamp-ON pilot. CPU overhead must be accepted before GPU overhead, then CPU pilot, GPU pilot, three balanced pairs and remaining correctness conditions. No formal 216-process study is authorized.
