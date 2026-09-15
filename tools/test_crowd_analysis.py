"""Statistical controls and malformed-evidence checks using synthetic, labelled data."""
import importlib.util
import contextlib
import io
import math
from pathlib import Path
import statistics
import tempfile
import types
import unittest
from datetime import datetime, timedelta, timezone

spec = importlib.util.spec_from_file_location("crowd", Path(__file__).with_name("run_crowd_whole_task.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class AnalysisTests(unittest.TestCase):
    def test_interval_uses_independent_observations(self):
        values = [1, 2, 3, 4, 5, 6, 7, 8]
        got = module.interval(values)
        half = 2.364624251 * statistics.stdev(values) / math.sqrt(8)
        self.assertEqual(got["mean"], 4.5)
        self.assertAlmostEqual(got["upper95"], 4.5 + half)
        self.assertAlmostEqual(got["lower95"], 4.5 - half)

    def test_log_ratio_preserves_exact_twofold_ratio(self):
        result = module.interval([math.log(2)] * 8)
        for value in result.values(): self.assertAlmostEqual(math.exp(value), 2)

    def test_missing_processes_cannot_produce_analysis(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            module.save(root / "plan.json", {"schedule": [{"round": 1}]})
            module.save(root / "processes.json", [])
            module.save(root / "completion.json", {"passed": True})
            with self.assertRaisesRegex(ValueError, "Incomplete matrix"):
                module.analyze(types.SimpleNamespace(confirmation=root, output=root))

    def test_wrong_order_cannot_produce_analysis(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            cell = {"round": 1, "case": "small", "position": 1, "arm": "rts"}
            module.save(root / "plan.json", {"schedule": [cell]})
            module.save(root / "processes.json", [{**cell, "position": 2}])
            module.save(root / "completion.json", {"passed": True})
            with self.assertRaisesRegex(ValueError, "Schedule mismatch"):
                module.analyze(types.SimpleNamespace(confirmation=root, output=root))


class CompleteMatrixTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.tmp.name)
        schedule, receipts = [], []
        identity = {"synthetic": "unit-test-only"}
        index = 0
        for round_id in range(1, 9):
            for case in module.CASES:
                for position, arm in enumerate(module.ARMS, 1):
                    index += 1
                    cell = {"round": round_id, "case": case, "position": position, "arm": arm}
                    schedule.append(cell)
                    directory = f"synthetic-{index:03d}"
                    run = cls.root / directory; run.mkdir()
                    samples = []
                    for request in range(12):
                        atlas = run / f"atlas-{request:02d}.rgba"; atlas.write_bytes(bytes([request]))
                        value = 2 ** (position - 1)
                        samples.append({"request": request, "phase": "first-use" if request == 0 else "warmup" if request < 4 else "measured",
                                        "fullByteComparisonPassed": True, "actualHash": module.sha(atlas),
                                        "completedMilliseconds": value, "exportMilliseconds": .1,
                                        "timing": {"gpuRenderMilliseconds": value / 2, "gpuReadbackMilliseconds": .1}})
                    result = {"pid": index, "id": "confirmation-" + case, "arm": arm, "passed": True,
                              "performanceEligible": True, "runtime": [], "fullOutputChecks": 12, "stableListAndBinsPassed": True,
                              "device": {}, "samples": samples, "firstUseMilliseconds": 100., "cleanupMilliseconds": 1.,
                              "logicalBytes": 4, "committedBytes": 65536, "committedReadbackBytes": 65536, "processPeakWorkingSetBytes": 100000}
                    module.save(run / "result.json", result); module.save(run / "samples.json", samples)
                    module.save(run / "compilation.json", {"synthetic": True})
                    (cls.root / (directory + ".log")).write_text("synthetic test only\n")
                    started = datetime(2020, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=index * 2)
                    receipts.append({**cell, "directory": directory, "pid": index, "startedUtc": started.isoformat(),
                                     "endedUtc": (started + timedelta(seconds=1)).isoformat(),
                                     "resultSha256": module.sha(run / "result.json"), "samplesSha256": module.sha(run / "samples.json"),
                                     "compilationSha256": module.sha(run / "compilation.json"), "logSha256": module.sha(cls.root / (directory + ".log"))})
        module.save(cls.root / "plan.json", {"schedule": schedule, "identity": identity, "device": {}, "runtime": [], "interval": "synthetic eight-process test"})
        module.save(cls.root / "processes.json", receipts)
        module.save(cls.root / "completion.json", {"passed": True, "identity": identity})

    @classmethod
    def tearDownClass(cls): cls.tmp.cleanup()

    def analyze(self):
        with contextlib.redirect_stdout(io.StringIO()):
            module.analyze(types.SimpleNamespace(confirmation=self.root, output=self.root))

    def test_full_matrix_has_known_means_and_paired_ratios(self):
        self.analyze()
        result = module.read(self.root / "analysis.json")
        self.assertEqual(result["processCount"], 128)
        self.assertEqual(result["fullAtlasChecks"], 1536)
        for row in result["results"]:
            self.assertEqual(row["completedMs"]["mean"], 2 ** module.ARMS.index(row["arm"]))
        contrast = result["comparisons"][0]
        for value in contrast["pairedCompletedRatio"].values(): self.assertAlmostEqual(value, .5)

    def test_modified_atlas_is_rejected(self):
        path = self.root / "synthetic-001/atlas-00.rgba"; before = path.read_bytes()
        try:
            path.write_bytes(b"corrupt")
            with self.assertRaisesRegex(ValueError, "Modified full atlas"): self.analyze()
        finally: path.write_bytes(before)

    def test_modified_process_result_is_rejected(self):
        path = self.root / "synthetic-001/result.json"; before = path.read_bytes()
        try:
            path.write_bytes(before + b" ")
            with self.assertRaisesRegex(ValueError, "Modified process artifact"): self.analyze()
        finally: path.write_bytes(before)

    def test_overlap_is_rejected(self):
        path = self.root / "processes.json"; before = path.read_bytes()
        try:
            rows = module.read(path); rows[1]["startedUtc"] = rows[0]["startedUtc"]
            module.save(path, rows)
            with self.assertRaisesRegex(ValueError, "Overlapping process"): self.analyze()
        finally: path.write_bytes(before)

    def test_negative_timings_are_rejected_even_with_consistent_receipt_hashes(self):
        paths = [self.root / "synthetic-001/result.json", self.root / "synthetic-001/samples.json", self.root / "processes.json"]
        before = [p.read_bytes() for p in paths]
        try:
            result = module.read(paths[0]); result["samples"][4]["completedMilliseconds"] = -1
            module.save(paths[0], result); module.save(paths[1], result["samples"])
            receipts = module.read(paths[2]); receipts[0]["resultSha256"] = module.sha(paths[0]); receipts[0]["samplesSha256"] = module.sha(paths[1])
            module.save(paths[2], receipts)
            with self.assertRaisesRegex(ValueError, "Invalid timing"): self.analyze()
        finally:
            for path, data in zip(paths, before): path.write_bytes(data)


if __name__ == "__main__": unittest.main()
