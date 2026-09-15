"""Synthetic controls for whole-lifetime costs, CPU eligibility, bounded launch and one primary contrast."""
import math
from datetime import datetime, timedelta, timezone
from pathlib import Path
import statistics
import tempfile
import types
import unittest
from unittest.mock import patch

import crowd_build_evidence as ev
import run_crowd_v2 as v2


class MetricsTests(unittest.TestCase):
    def result(self):
        return {"firstUseMilliseconds": 100, "cleanupMilliseconds": 5, "processPeakWorkingSetBytes": 1024,
                "samples": [{"request": i, "phase": "first-use" if i == 0 else "warmup" if i < 4 else "measured",
                             "completedMilliseconds": 2, "exportMilliseconds": .5} for i in range(12)]}

    def test_lifetime_includes_first_use_warmups_all_requests_and_cleanup(self):
        result = v2.process_metrics(self.result())
        self.assertEqual(result["lifetime12PerRequestMs"], 127 / 12)
        self.assertEqual(result["steadyMeanMs"], 2)

    def test_boolean_and_negative_timings_are_rejected(self):
        for value in [True, -1, float("nan")]:
            data = self.result(); data["samples"][3]["completedMilliseconds"] = value
            with self.assertRaisesRegex(ValueError, "Invalid timing"): v2.process_metrics(data)

    def test_rehearsal_is_not_promoted_to_performance(self):
        data = self.result(); data["samples"][4]["phase"] = "rehearsal"
        with self.assertRaisesRegex(ValueError, "Ineligible rehearsal"): v2.process_metrics(data)

    def test_best_cpu_worker_choice_can_be_the_comparator(self):
        rows = [{"case": "large", "key": key, "lifetime12PerRequestMs": value}
                for key, value in [("hierarchical", 10), ("wave-tiled", 5), ("cpu-frame-parallel-w1", 4), ("cpu-frame-parallel-w12", 2)]
                for _ in [1, 2]]
        key, _ = v2.choose_comparator(rows)
        self.assertEqual(key, "cpu-frame-parallel-w12")
        with self.assertRaisesRegex(ValueError, "CPU alternative absent"):
            v2.choose_comparator([r for r in rows if not r["key"].startswith("cpu")])

    def test_confirmation_has_one_balanced_primary_pair_and_descriptive_cases(self):
        schedule = v2.confirmation_schedule({"arm": "wave-tiled", "workers": None}, {"arm": "cpu-frame-parallel", "workers": 12}, 8)
        self.assertEqual(len(schedule), 28)
        primary = [r for r in schedule if r["case"] == "large"]
        self.assertEqual(len(primary), 16)
        self.assertEqual(sum(r["position"] == 1 and r["arm"] == "wave-tiled" for r in primary), 4)
        self.assertEqual([r["index"] for r in schedule], list(range(28)))
        with self.assertRaisesRegex(ValueError, "Unsupported"): v2.confirmation_schedule({}, {}, 10)

    def test_interval_uses_registered_process_pairs(self):
        values = list(range(8)); result = v2.paired_interval(values)
        expected = 3.5 + 2.364624251 * statistics.stdev(values) / math.sqrt(8)
        self.assertAlmostEqual(result["upper95"], expected)
        constant = v2.paired_interval([math.log(.5)] * 8)
        for value in constant.values(): self.assertAlmostEqual(math.exp(value), .5)

    def test_two_primary_claims_cannot_be_analyzed(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "plan.json"
            ev.save(path, {"schemaVersion": 2, "phase": "confirmation", "confirmatoryHypotheses": 2})
            with self.assertRaisesRegex(ValueError, "One registered primary"): v2.analyze(types.SimpleNamespace(plan=path))


class LaunchControls(unittest.TestCase):
    def test_busy_machine_launches_no_measurement_and_keeps_receipt(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp); path = folder / "plan.json"
            ev.save(path, {"schemaVersion": 2, "phase": "discovery", "schedule": [{"index": 0}]})
            args = types.SimpleNamespace(plan=path, mode="discovery", output=folder, batches=[], max_processes=2,
                                         dotnet="synthetic", references=folder)
            with patch.object(v2, "verify_plan"), patch.object(ev, "require_lock"), patch.object(v2, "collect_batches", return_value=[]), \
                 patch.object(v2, "load_gate", return_value={"passed": False}), patch.object(ev, "check") as check:
                with self.assertRaisesRegex(ValueError, "no new process launched"): v2.execute(args)
                check.assert_not_called()
            receipt = ev.read(folder / "batch.json")
            self.assertFalse(receipt["passed"]); self.assertTrue(receipt["stoppedBeforeLaunch"])
            self.assertEqual(receipt["jobs"], [])

    def test_confirmation_pair_cannot_be_split_by_process_limit(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp); path = folder / "plan.json"
            ev.save(path, {"schemaVersion": 2, "phase": "confirmation"})
            args = types.SimpleNamespace(plan=path, mode="confirmation", output=folder, batches=[], max_processes=1,
                                         dotnet="synthetic", references=folder)
            with patch.object(v2, "verify_plan"), patch.object(ev, "require_lock"), patch.object(v2, "collect_batches", return_value=[]), patch.object(ev, "check") as check:
                with self.assertRaisesRegex(ValueError, "one invocation"): v2.execute(args)
                check.assert_not_called()

    def test_retired_plan_is_ineligible(self):
        with self.assertRaisesRegex(ValueError, "Version-2 protocol required"): v2.verify_plan({"schemaVersion": 1}, "synthetic", None)


class CompleteSyntheticCohort(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory(prefix="crowd-synthetic-only-")
        cls.root = Path(cls.tmp.name); cls.batch = cls.root / "batch"; cls.batch.mkdir()
        build = cls.root / "synthetic-build.json"; ev.save(build, {"synthetic": True})
        cls.plan_path = cls.root / "plan.json"
        runtime = [{"name": n, "path": "synthetic", "sha256": "synthetic", "version": "fixture"}
                   for n in ["coreclr.dll", "hostfxr.dll", "dxcompiler.dll", "dxil.dll"]]
        comparator = {"arm": "cpu-frame-parallel", "workers": 12}
        schedule = v2.confirmation_schedule({"arm": "wave-tiled", "workers": None}, comparator, 8)
        plan = {"schemaVersion": 2, "phase": "confirmation", "syntheticFixture": True, "scope": "synthetic unit test only",
                "confirmatoryHypotheses": 1, "primaryPairs": 8, "comparator": comparator, "schedule": schedule,
                "buildReceipt": v2.identity(build), "cpuMachine": {"synthetic": True}, "device": {"synthetic": True},
                "gpuRuntime": runtime, "resourceBudgetBytes": 2 * 1024**3}
        ev.save(cls.plan_path, plan)
        jobs = []
        for cell in schedule:
            index = cell["index"]; directory = cls.batch / f"synthetic-{index:03d}"; output = directory / "result"; output.mkdir(parents=True)
            cpu = cell["arm"] == "cpu-frame-parallel"; value = 2 if cpu else 4
            samples = []
            for request in range(12):
                atlas = output / f"atlas-{request:02d}.rgba"; atlas.write_bytes(bytes([request]))
                samples.append({"request": request, "phase": "first-use" if request == 0 else "warmup" if request < 4 else "measured",
                    "fullByteComparisonPassed": True, "actualHash": ev.sha(atlas), "completedMilliseconds": value,
                    "exportMilliseconds": .1, "cpuTiming": {"renderMilliseconds": value - .1},
                    "timing": {"gpuRenderMilliseconds": 1., "gpuReadbackMilliseconds": .1, "cpuRecordMilliseconds": .1,
                               "cpuCopyMilliseconds": .1, "submission": {"cpuSubmitMilliseconds": .1, "cpuFenceWaitMilliseconds": 1.}}})
            result = {"syntheticFixture": True, "arm": cell["arm"], "workers": cell["workers"], "backend": "cpu" if cpu else "d3d12",
                      "id": "confirmation-" + cell["case"], "passed": True, "performanceEligible": True, "fullOutputChecks": 12,
                      "stableListAndBinsPassed": True, "stableVisibleSequencePassed": True, "inputUnchanged": True,
                      "cpuMachine": plan["cpuMachine"], "device": plan["device"], "runtime": runtime[:2] if cpu else runtime,
                      "allocationCapBytes": plan["resourceBudgetBytes"], "logicalBytes": 4, "hostLogicalBytes": 4,
                      "samples": samples, "firstUseMilliseconds": value * 12, "cleanupMilliseconds": 0,
                      "processPeakWorkingSetBytes": 1024, "pid": index + 1}
            ev.save(output / "result.json", result); ev.save(output / "samples.json", samples)
            log = directory / "process.log"; log.write_text("Synthetic fixture; no hardware was measured.\n")
            start = datetime(2020, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=index * 2)
            receipt = {"passed": True, "kind": "bound-check", "mode": "run", "before": {}, "after": {},
                       "buildReceipt": v2.identity(build), "artifacts": ev.file_manifest(output),
                       "performancePreflight": {"passed": True}, "performancePostflight": {"passed": True},
                       "protocol": {"sha256": ev.sha(cls.plan_path), "cell": cell},
                       "process": {"pid": result["pid"], "exitCode": 0, "log": str(log), "logSha256": ev.sha(log),
                                   "startedUtc": start.isoformat(), "endedUtc": (start + timedelta(seconds=1)).isoformat()}}
            ev.save(directory / "check-receipt.json", receipt)
            jobs.append({"cell": cell, "directory": directory.name, "receiptSha256": ev.sha(directory / "check-receipt.json"),
                         "preflight": [{"passed": True}] * 2, "postflight": {"passed": True}})
        ev.save(cls.batch / "batch.json", {"passed": True, "planSha256": ev.sha(cls.plan_path), "jobs": jobs})

    @classmethod
    def tearDownClass(cls): cls.tmp.cleanup()
    def analyze(self): v2.analyze(types.SimpleNamespace(plan=self.plan_path, batches=[self.batch], output=self.root))

    def test_complete_mixed_cpu_gpu_cohort_has_known_ratio_and_one_claim(self):
        self.analyze(); result = ev.read(self.root / "analysis.json")
        self.assertEqual(result["processCount"], 28)
        self.assertAlmostEqual(result["primary"]["candidateOverComparatorRatio"]["mean"], 2)
        self.assertTrue(result["primary"]["candidateSlowerSupported"])
        self.assertFalse(result["primary"]["benefitSupported"])
        self.assertTrue(all(r["descriptiveOnly"] for r in result["descriptive"]))

    def test_changed_atlas_is_rejected(self):
        path = self.batch / "synthetic-000/result/atlas-00.rgba"; before = path.read_bytes()
        try:
            path.write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "artifact changed"): self.analyze()
        finally: path.write_bytes(before)

    def test_incomplete_confirmation_is_rejected(self):
        path = self.batch / "batch.json"; before = path.read_bytes()
        try:
            batch = ev.read(path); batch["jobs"].pop(); ev.save(path, batch)
            with self.assertRaisesRegex(ValueError, "Incomplete cohort"): self.analyze()
        finally: path.write_bytes(before)

    def test_failed_observation_is_not_replaced_by_complete_looking_records(self):
        path = self.batch / "batch.json"; before = path.read_bytes()
        try:
            batch = ev.read(path); batch.update(passed=False, stoppedBeforeLaunch=True); ev.save(path, batch)
            with self.assertRaisesRegex(ValueError, "Failed observation"): self.analyze()
        finally: path.write_bytes(before)


if __name__ == "__main__": unittest.main()
