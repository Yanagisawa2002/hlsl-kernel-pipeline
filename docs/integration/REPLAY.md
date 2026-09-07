# Replaying the vNext integration checks

Run from a clean checkout with .NET SDK 10.0.302, Unity 6000.5.3f1 and the existing
AMD RGA installation. The shared runner is an external coordination dependency;
pass its path explicitly. These commands never push or modify the original branch.
Use a fresh evidence directory for each attempt. No measurement directory may be
reused to erase a rejected or noisy run.

```powershell
$serial = '<control-directory>/Invoke-SerializedValidation.ps1'
$unity = '<Unity-6000.5.3f1>/Editor/Unity.exe'
& ./tools/Invoke-IntegrationRegression.ps1 -SerializedValidationRunner $serial `
    -UnityEditor $unity -EvidenceRoot ./.hlslperf/replay-regression

# Native dynamic/radix and hardware evidence smoke commands are documented by
# their corresponding tool directories. Keep the shared lock around every run.

& ./tools/Invoke-VNextFormalMatrix.ps1 -Phase Declare `
    -EvidenceRoot ./.hlslperf/replay-matrix
& ./tools/Invoke-VNextFormalMatrix.ps1 -Phase RadixScreen `
    -EvidenceRoot ./.hlslperf/replay-matrix -SerializedValidationRunner $serial
& ./tools/Invoke-VNextFormalMatrix.ps1 -Phase RadixCompare `
    -EvidenceRoot ./.hlslperf/replay-matrix -SerializedValidationRunner $serial
& ./tools/Invoke-VNextFormalMatrix.ps1 -Phase Scenarios `
    -EvidenceRoot ./.hlslperf/replay-matrix -SerializedValidationRunner $serial
python ./tools/validate_vnext_evidence.py ./.hlslperf/replay-matrix `
    --output ./.hlslperf/replay-matrix/independent-audit.json
```

`-CellFilter` can execute a declared subset, but an incomplete subset does not
establish whole-project acceptance. Declaration freezes executable/dependency
hashes, generated manifests, source identity and the runner. Derived radix
comparisons record the calibration-selected binary control before sampling.

The Python audit requires `jsonschema` for emitted profile schema validation.
Its paired ratio and Student-t interval replay uses Python's standard library
independently of the C# statistics implementation. It also checks every ring slot,
two distinct output poison attempts, actual input separation, selection time,
allocation limits and profile publication against the deployment decision.

A noisy or non-improving result is retained with its original data. Zero benefit
is a valid outcome. Compiler or correctness failures are not performance evidence;
missing required native or RGA work must remain an explicit unmet gate. Broader
historical before/after comparisons should use separate declared evidence roots
and preserve both binaries, protocols and input identities.
