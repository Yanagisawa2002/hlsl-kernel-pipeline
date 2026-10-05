"""CPU-only regressions for source preparation and measurement attribution."""
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

import analyze_fluid_benchmark as analysis
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


if __name__ == "__main__":
    unittest.main()
