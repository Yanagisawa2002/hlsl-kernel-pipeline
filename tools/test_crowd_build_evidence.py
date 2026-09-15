"""Controls for false clean-build/debug claims. Fixtures are synthetic; no GPU is used."""
import importlib.util
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("evidence", Path(__file__).with_name("crowd_build_evidence.py"))
evidence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evidence)
spec = importlib.util.spec_from_file_location("legacy", Path(__file__).with_name("run_crowd_whole_task.py"))
legacy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(legacy)


class DebugEvidenceControls(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.snapshot = {"available": True, "cleared": True, "discardedMessages": 0, "deniedByStorageFilter": 0,
                         "storedMessages": 1, "retrievableMessages": 1, "storedMessagesAfterRead": 1,
                         "messages": [{"severity": "Warning"}]}
        empty_filter = {k: [] for k in ["allowedCategories", "allowedSeverities", "allowedIds", "deniedCategories", "deniedSeverities", "deniedIds"]}
        empty_filter["available"] = True
        self.snapshot.update(storageFilter=empty_filter, retrievalFilter=empty_filter, filtersStable=True)

    def tearDown(self): self.tmp.cleanup()
    def verify(self):
        evidence.save(self.root / "debug.json", {"schemaVersion": 2, "passed": True, "complete": True,
                                               "snapshots": [{"snapshot": self.snapshot}]})
        evidence.verify_debug(self.root)

    def test_complete_warning_passes(self): self.verify()

    def test_recorded_info_only_filtering_is_distinguished_from_loss(self):
        self.snapshot["storageFilter"] = {**self.snapshot["storageFilter"], "deniedSeverities": ["Info"]}
        self.snapshot["deniedByStorageFilter"] = 119
        self.verify()
        self.snapshot["storageFilter"]["deniedSeverities"] = ["Info", "Error"]
        with self.assertRaisesRegex(ValueError, "filtered"): self.verify()

    def test_truncated_tail_fails_even_if_stored_messages_are_only_warnings(self):
        self.snapshot["discardedMessages"] = 77
        with self.assertRaisesRegex(ValueError, "lost"): self.verify()

    def test_retrieval_filter_cannot_hide_an_error(self):
        self.snapshot["storedMessages"] = self.snapshot["storedMessagesAfterRead"] = 2
        with self.assertRaisesRegex(ValueError, "filtered"): self.verify()

    def test_legacy_flat_1024_warning_list_is_not_accepted(self):
        evidence.save(self.root / "debug.json", ["Warning: truncated legacy fixture"] * 1024)
        with self.assertRaises(ValueError): evidence.verify_debug(self.root)


class BuildBindingControls(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.receipt = self.root / "build.json"
        self.source = {"head": "a" * 40, "trackedFiles": {"source.cs": "abc"}}
        self.binary = {"runner.dll": "binary-a"}
        self.host = {"path": "synthetic-dotnet", "sha256": "host-a"}
        log = self.root / "build.log"; log.write_text("synthetic build only")
        evidence.save(self.receipt, {"schemaVersion": 2, "kind": "clean-rebuild", "passed": True,
            "sourceBefore": self.source, "sourceAfter": self.source, "binaries": self.binary, "host": self.host,
            "commands": [{"exitCode": 0, "log": str(log), "logSha256": evidence.sha(log)}] * 3})
        self.mocks = [patch.object(evidence, "source_state", return_value=self.source),
                      patch.object(evidence, "binaries", return_value=self.binary),
                      patch.object(evidence, "host_identity", return_value=self.host)]
        for item in self.mocks: item.start()

    def tearDown(self):
        for item in reversed(self.mocks): item.stop()
        self.tmp.cleanup()

    def test_consistent_binding_passes(self): evidence.verify_build(self.receipt, "synthetic")

    def test_different_commit_with_identical_binaries_is_rejected(self):
        with patch.object(evidence, "source_state", return_value={**self.source, "head": "b" * 40}):
            with self.assertRaisesRegex(ValueError, "Source/commit"): evidence.verify_build(self.receipt, "synthetic")

    def test_replaced_dependency_is_rejected(self):
        with patch.object(evidence, "binaries", return_value={**self.binary, "dxcompiler.dll": "replacement"}):
            with self.assertRaisesRegex(ValueError, "binary differs"): evidence.verify_build(self.receipt, "synthetic")

    def test_modified_build_log_is_rejected(self):
        (self.root / "build.log").write_text("modified")
        with self.assertRaisesRegex(ValueError, "log identity"): evidence.verify_build(self.receipt, "synthetic")

    def test_post_launch_drift_keeps_failed_receipt(self):
        output = self.root / "check"; output.mkdir()
        args = types.SimpleNamespace(output=output, check="validate", dotnet="synthetic", build_receipt=self.receipt, references=None)
        with patch.object(evidence, "require_lock", return_value={}), \
             patch.object(evidence, "source_state", side_effect=[self.source, self.source, {**self.source, "head": "changed"}]), \
             patch.object(evidence, "process", return_value={"exitCode": 0}):
            with self.assertRaisesRegex(ValueError, "changed during check"): evidence.check(args)
        saved = evidence.read(output / "check-receipt.json")
        self.assertFalse(saved["passed"])
        self.assertIn("changed during check", saved["error"])

    def test_cpu_test_dependency_drift_after_launch_is_rejected(self):
        output = self.root / "cpu-drift"; output.mkdir()
        args = types.SimpleNamespace(output=output, check="cpu-tests", dotnet="synthetic", build_receipt=self.receipt, references=None)
        build = evidence.read(self.receipt); build["testBinaries"] = {"test.dll": "original"}
        with patch.object(evidence, "require_lock", return_value={}), \
             patch.object(evidence, "verify_build", return_value=build), \
             patch.object(evidence, "file_manifest", side_effect=[{"test.dll": "original"}, {"test.dll": "replaced"}]), \
             patch.object(evidence, "process", return_value={"exitCode": 0}):
            with self.assertRaisesRegex(ValueError, "changed during check"): evidence.check(args)
        self.assertFalse(evidence.read(output / "check-receipt.json")["passed"])


class ProtocolControls(unittest.TestCase):
    def test_retired_matrix_cannot_launch_any_process(self):
        with patch.object(legacy.subprocess, "Popen") as popen:
            for command in [legacy.discover, legacy.freeze, legacy.confirm]:
                with self.assertRaisesRegex(ValueError, "128-process protocol is retired"): command(None)
            popen.assert_not_called()


if __name__ == "__main__": unittest.main()
