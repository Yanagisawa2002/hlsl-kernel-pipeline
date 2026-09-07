"""Independent, read-only acceptance audit for the declared eight-block matrix."""
import argparse
import hashlib
import json
import math
import statistics
from collections import defaultdict
from datetime import datetime
from pathlib import Path


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def audit(path):
    report = load(path)
    evidence = report["pairedEvidence"]
    require(report["measurementProtocol"] == "gpu-paired-abba-independent-confirmation-v2", "wrong protocol")
    options = evidence["options"]
    require(options["calibrationBlocks"] == options["confirmationBlocks"] == 8, "undeclared block count")
    observations = evidence["observations"]
    require(observations, "missing observations")
    checks = 0
    seeds = defaultdict(set)
    digests = defaultdict(set)
    selected = evidence["selectedAfterCalibration"]
    confirmation_ids = set()
    blocks = defaultdict(list)
    for row in observations:
        slot, result = row["slot"], row["result"]
        require(slot["candidateId"] == result["candidateId"], "candidate/result mismatch")
        require(result["compiled"] and result["correctness"]["passed"], "compile or correctness failure")
        require(len(result["samplesMilliseconds"]) == 1, "sample shape mismatch")
        sample = result["samplesMilliseconds"][0]
        require(math.isfinite(sample) and sample > 0, "invalid timing")
        scenario = row["scenario"]
        require(scenario and len(scenario["slots"]) == options["residentSlots"], "resident ring mismatch")
        require(scenario["committedAllocationBytes"] <= options["maximumAllocationBytesPerArm"], "allocation cap exceeded")
        slot_seeds = [item["inputSeed"] for item in scenario["slots"]]
        require(slot_seeds == list(range(slot["inputSeed"], slot["inputSeed"] + options["residentSlots"])), "slot seed mismatch")
        key = (slot["phase"], slot["candidateId"])
        seeds[key].update(slot_seeds)
        digests[key].update(item["inputSha256"] for item in scenario["slots"])
        verifications = row["slotVerifications"]
        require(len(verifications) == options["residentSlots"], "verification slot count mismatch")
        require({v["slot"] for v in verifications} == set(range(options["residentSlots"])), "duplicate or missing verified slot")
        for verification in verifications:
            correctness = verification["correctness"]
            require(correctness["passed"], "resident output failed")
            expected_outputs = scenario["slots"][verification["slot"]]["expectedOutputs"]
            by_resource = defaultdict(list)
            for output in correctness.get("outputs", []):
                require(output["passed"] and output["actualSha256"] == output["expectedSha256"], "output hash failed")
                by_resource[output["resource"]].append(output)
                checks += 1
            require(by_resource, "missing per-output poison evidence")
            require(set(by_resource) == set(expected_outputs), "declared output omitted from verification")
            for resource, attempts in by_resource.items():
                require(sorted(v["attempt"] for v in attempts) == [1, 2], f"missing poison attempts: {resource}")
                require({v["poisonByte"] for v in attempts} == {0xA5, 0x5A}, f"poison mismatch: {resource}")
                require(all(v["expectedSha256"] == expected_outputs[resource] for v in attempts), "output oracle identity mismatch")
        if slot["phase"] == "confirmation":
            confirmation_ids.add(slot["challengerId"])
            require(datetime.fromisoformat(row["startedUtc"]) >= datetime.fromisoformat(evidence["selectionLockedUtc"]), "confirmation preceded selection lock")
        blocks[(slot["phase"], slot["challengerId"], slot["block"])].append(row)
    require(confirmation_ids == {selected}, "confirmation selected another candidate")
    for candidate in {selected, report["baselineCandidateId"]}:
        require(seeds[("calibration", candidate)] and seeds[("confirmation", candidate)], "missing independent phases")
        require(seeds[("calibration", candidate)].isdisjoint(seeds[("confirmation", candidate)]), "seed reuse")
        require(digests[("calibration", candidate)].isdisjoint(digests[("confirmation", candidate)]), "actual input reuse")
    ratios = defaultdict(list)
    for (phase, candidate, block), rows in blocks.items():
        rows.sort(key=lambda row: row["slot"]["position"])
        require(len(rows) == 4, "incomplete ABBA block")
        roles = [row["slot"]["isBaseline"] for row in rows]
        require(roles in ([True, False, False, True], [False, True, True, False]), "not ABBA/BAAB")
        a = [row["result"]["samplesMilliseconds"][0] for row in rows if row["slot"]["isBaseline"]]
        b = [row["result"]["samplesMilliseconds"][0] for row in rows if not row["slot"]["isBaseline"]]
        ratios[(phase, candidate)].append(math.log(math.sqrt(a[0] * a[1]) / math.sqrt(b[0] * b[1])))
    # Independent Student-t(df=7) reference, tighter than the implementation's
    # upward-rounded 2.365. Reported intervals must contain this reference interval.
    for phase, comparison in [("calibration", c) for c in evidence["calibration"]] + [("confirmation", evidence["confirmation"])]:
        values = ratios[(phase, comparison["candidateId"])]
        require(len(values) == 8, "missing paired blocks")
        mean = statistics.mean(values)
        radius = 2.3646242515927844 * statistics.stdev(values) / math.sqrt(8)
        require(comparison["geometricMeanSpeedup"] is not None, "missing comparison estimate")
        require(math.isclose(comparison["geometricMeanSpeedup"], math.exp(mean), rel_tol=1e-10), "ratio replay differs")
        require(comparison["lower95Speedup"] <= math.exp(mean - radius) + 1e-10, "lower interval not conservative")
        require(comparison["upper95Speedup"] >= math.exp(mean + radius) - 1e-10, "upper interval not conservative")
    profile = path.parent / "profile.json"
    require(profile.exists() == evidence["deployable"], "profile publication differs from deployment decision")
    if profile.exists():
        import jsonschema
        schema = load(Path(__file__).resolve().parents[1] / "schemas/profile.schema.v3.json")
        data = load(profile)
        jsonschema.Draft202012Validator(schema).validate(data)
        require(data["candidateId"] == selected, "profile changed frozen selection")
    return {
        "name": path.parent.name, "reportSha256": sha(path), "observations": len(observations),
        "poisonOutputChecks": checks, "deployable": evidence["deployable"],
        "selectedCandidate": selected, "confirmation": evidence["confirmation"],
        "rejections": evidence["rejections"],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("evidence_root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    reports = sorted(args.evidence_root.glob("*/run.json"))
    results, failures = [], []
    for path in reports:
        try:
            results.append(audit(path))
        except Exception as error:
            failures.append({"path": str(path), "error": str(error)})
    summary = {"passed": bool(reports) and not failures, "reports": len(reports), "results": results, "failures": failures}
    args.output.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({"passed": summary["passed"], "reports": len(reports), "failures": failures}, indent=2))
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
