"""Synthetic controls for whole-lifetime costs, CPU eligibility, bounded launch and one primary contrast."""
import math
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


if __name__ == "__main__": unittest.main()
