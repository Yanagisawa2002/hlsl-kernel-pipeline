"""Deterministic source/entrypoint contracts; no subprocesses and no GPU execution."""
import hashlib
import json
from pathlib import Path
import re
import tempfile
import unittest
from verify_external_sources import verify

ROOT=Path(__file__).resolve().parents[1]

class ExternalContracts(unittest.TestCase):
    def test_upstream_sources_and_licenses_are_exact(self):
        self.assertGreaterEqual(verify(ROOT)['files'],114)

    def test_published_review_bytes_and_commit_urls(self):
        lock=json.loads((ROOT/'benchmarks/external/review-lock.json').read_text())
        for source in lock['sources']:
            self.assertRegex(source['commit'],r'^[0-9a-f]{40}$')
            for file in source['files']:
                self.assertIn('/'+source['commit']+'/',file['url'])
                self.assertEqual(file['sha256'],hashlib.sha256((ROOT/file['localPath']).read_bytes()).hexdigest())

    def test_native_entries_guard_before_device_and_keep_upstream_batches(self):
        for name,call in [('scan','BatchTimingInclusiveInitOne(1 << 28, 100)'),
                          ('sort','BatchTiming(1 << 28, 100, 10, GPUSorting::ENTROPY_PRESET_1)')]:
            entry=(ROOT/f'benchmarks/external/native/{name}-main.cpp').read_text()
            body=entry[entry.index('int main('):]
            self.assertLess(body.index('HlslPerfExecutionAuthorized()'),body.index('InitDevice()'))
            self.assertIn(call,body)
            self.assertIn('HlslPerfOriginalUpstreamMain()',body)

    def test_native_preparer_has_only_build_commands(self):
        text=(ROOT/'tools/prepare_external_benchmarks.py').read_text()
        self.assertIn("'/t:Build'",text)
        self.assertNotIn("add_argument('--run'",text)
        self.assertNotIn("shell=True",text)

    def test_corruption_and_optimized_python_cannot_disable_verification(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder); (root/'third_party').mkdir()
            lock={'schema':'hlslperf.external-source-lock.v1','adaptedInPlace':False,'sources':[{
                'commit':'a'*40,'repository':'https://github.com/example/project','name':'example','license':'MIT','files':[{
                    'path':'LICENSE','localPath':'third_party/LICENSE','bytes':3,'sha256':'0'*64,
                    'gitBlobSha1':'0'*40,'url':'https://raw.githubusercontent.com/example/project/'+'a'*40+'/LICENSE'}]}]}
            (root/'third_party/upstream-lock.json').write_text(json.dumps(lock)); (root/'third_party/LICENSE').write_bytes(b'bad')
            with self.assertRaises(ValueError): verify(root)
        self.assertNotIn('assert ',(ROOT/'tools/verify_external_sources.py').read_text())

if __name__=='__main__': unittest.main()
