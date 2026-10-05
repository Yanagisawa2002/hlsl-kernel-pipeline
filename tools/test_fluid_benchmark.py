"""CPU-only regressions for source preparation and measurement attribution."""
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

import analyze_fluid_benchmark as analysis
import compare_fluid_benchmark as comparison
import prepare_fluid_benchmark as preparation


class FluidChecks(unittest.TestCase):
    def test_archive_rejects_traversal_and_links(self):
        for name, link in (("../outside", False), ("C:/outside", False), ("safe-link", True)):
            stream = io.BytesIO()
            with tarfile.open(fileobj=stream, mode="w") as archive:
                item = tarfile.TarInfo(name)
                item.size = 1
                if link:
                    item.type = tarfile.SYMTYPE
                    item.linkname = "../outside"
                    item.size = 0
                archive.addfile(item, None if link else io.BytesIO(b"x"))
            with self.assertRaises(ValueError):
                preparation.archive_files(stream.getvalue())

    def test_source_lock_matches_vendor_bytes(self):
        lock = json.loads((preparation.BUNDLE / "dependencies.json").read_text())
        for path, expected in lock["gpuPrefixSums"]["files"].items():
            self.assertEqual(preparation.digest((preparation.BUNDLE / "ThirdParty/GPUPrefixSums" / path).read_bytes()), expected)

    def test_existing_destination_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                preparation.prepare(Path(directory), Path("missing-source"))

    def fixture(self, directory, rows):
        folder = Path(directory)
        run = {"schema": "hlslperf.fluid-scan.run.v1", "firstMeasuredUnityFrame": 10, "lastMeasuredUnityFrame": 11,
               "settings": {"measureFrames": 2}, "iterationsPerFrame": 3, "gpuDelayValidated": False}
        (folder / "run.json").write_text(json.dumps(run))
        (folder / "correctness.json").write_text(json.dumps({"status": "passed", "fullOutputValidated": True}))
        (folder / "observations.csv").write_text("observed_unity_frame,source_unity_frame,metric,ms,sample_blocks,status\n" + rows)
        return folder

    def test_missing_gpu_is_null_and_not_zero(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = self.fixture(directory, "11,10,wall_frame,10,1,observed\n12,11,wall_frame,20,1,observed\n13,10,scan_complete,,0,unavailable\n")
            result = analysis.summarize(folder)
            self.assertEqual(result["metrics"]["wall_frame"]["p50Ms"], 15)
            self.assertIsNone(result["metrics"]["scan_complete"]["p50Ms"])
            self.assertEqual(result["metrics"]["scan_complete"]["unavailableSamples"], 2)

    def test_gpu_remains_provisional(self):
        with tempfile.TemporaryDirectory() as directory:
            result = analysis.summarize(self.fixture(directory, "13,10,scan_complete,1.5,3,requires_gpu_delay_validation\n14,11,scan_complete,2.5,3,requires_gpu_delay_validation\n"))
            self.assertEqual(result["metrics"]["scan_complete"]["status"], "provisional_requires_gpu_delay_review")

    def test_bad_block_count_is_unavailable(self):
        with tempfile.TemporaryDirectory() as directory:
            result = analysis.summarize(self.fixture(directory, "13,10,scan_complete,1.5,1,requires_gpu_delay_validation\n"))
            self.assertEqual(result["metrics"]["scan_complete"]["validSamples"], 0)

    def test_duplicate_or_outside_frame_is_rejected(self):
        for rows in ("13,10,scan_complete,1.5,3,requires_gpu_delay_validation\n14,10,scan_complete,2.5,3,requires_gpu_delay_validation\n",
                     "13,9,scan_complete,1.5,3,requires_gpu_delay_validation\n"):
            with tempfile.TemporaryDirectory() as directory:
                with self.assertRaises(ValueError):
                    analysis.summarize(self.fixture(directory, rows))

    def cohort_fixture(self, directory):
        folders = []
        for index in range(9):
            folder = Path(directory) / str(index)
            folder.mkdir()
            rows = "".join(f"{frame+3},{frame},{metric},{1+index/100},"
                           f"{1 if metric in ('wall_frame', 'simulation_complete') else 3},native_frame_fence_validated\n"
                           for frame in (10, 11) for metric in comparison.METRICS)
            self.fixture(folder, rows)
            run = json.loads((folder / "run.json").read_text())
            run.update(device="fixture", api="Direct3D12", unity="fixture", width=1920, height=1080,
                       particles=100, foamCapacity=1000, fixedTimestep=1/60,
                       gpuTimingMethod="d3d12_query_frame_fence", gpuDelayValidated=True,
                       nativeTimingBuild="fixture", provenance=json.dumps({"payloadSha256": "fixture"}))
            run["settings"].update(arm=index//3, seed=42, spawnDensity=600, warmupFrames=120, nativeGpuTiming=True)
            (folder / "run.json").write_text(json.dumps(run))
            folders.append(folder)
        return folders

    def test_cohort_requires_balanced_independent_repeats(self):
        with tempfile.TemporaryDirectory() as directory:
            folders = self.cohort_fixture(directory)
            result = comparison.compare(folders)
            self.assertEqual(result["arms"]["original"]["repeats"], 3)
            self.assertAlmostEqual(result["arms"]["original"]["metrics"]["wall_frame"]["medianOfRunP50Ms"], 1.01)
            with self.assertRaisesRegex(ValueError, "equally repeated"):
                comparison.compare(folders[:-1])
            with self.assertRaisesRegex(ValueError, "Duplicate process evidence"):
                comparison.compare(folders + folders[:1])
            # Renaming a copied run does not turn it into independent evidence.
            (folders[-1] / "observations.csv").write_bytes((folders[0] / "observations.csv").read_bytes())
            with self.assertRaisesRegex(ValueError, "Duplicate process evidence"):
                comparison.compare(folders)

    def test_cohort_rejects_changed_configuration_or_source(self):
        for field in ("seed", "payloadSha256"):
            with tempfile.TemporaryDirectory() as directory:
                folders = self.cohort_fixture(directory)
                run_file = folders[-1] / "run.json"
                run = json.loads(run_file.read_text())
                if field == "seed":
                    run["settings"][field] += 1
                else:
                    run["provenance"] = json.dumps({field: "different"})
                run_file.write_text(json.dumps(run))
                with self.assertRaisesRegex(ValueError, "configuration/source mismatch"):
                    comparison.compare(folders)


if __name__ == "__main__":
    unittest.main()
