# Isolated GPU-state stability diagnostic

Diagnostic only; eligibleForPerformanceAcceptance=false. See [prospective protocol](PROTOCOL.md) and [blocked attempt report](../../docs/results/GPU_STATE_STABILITY_2026-09-19.md).

`tools/prepare_state_stability.py --project <fresh-project> --native-plugin <original-v4-DLL>` generates a separate Unity project from the unchanged formal source. Build with the generated `BuildCrossover.Build` entry point and `CROSSOVER_PLAYER_PATH` environment variable, as in the formal project builder. The generated schema105 Player supports the real CPU and GPU arms, preallocates samples, repeats original1000 calibrated views, and records30 seconds from first submission. It does not use pipeline-statistics queries. This is build-validated, not hardware-runtime-validated.

`tools/run_state_stability.py --player <Player.exe> --calibration <original-100k-25pct-1000-view.json> --output <fresh-output-directory>` fixes CPU/GPU/GPU/CPU/CPU/GPU ordering and reuses the100ms NVML sidecar. Any failed three-snapshot quiet gate stops the entire sequence before launch. No retry, resume or substitution is authorized by this command's existence. The recorded September19 sequence is blocked; further hardware attempts require human intervention.

`tools/analyze_state_stability.py --help` describes offline analysis inputs. It validates complete timing/index records, excludes the retained incomplete final cycle from stability classification, computes exact-view ratios and cycle-aligned telemetry, and distinguishes candidate time from later confirmation/relapse. `python -m unittest discover -s tools -p test_state_stability.py` runs synthetic/offline tests only.

The passive frame299 fence probe never pauses the diagnostic. Its observation timestamp cannot establish the formal V4 blocking transition's idle duration. No output is suitable for overhead, architecture performance, or crossover acceptance.
