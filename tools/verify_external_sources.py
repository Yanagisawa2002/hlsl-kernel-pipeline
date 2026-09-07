"""Verify vendored official sources without changing or downloading any file."""
import argparse
import hashlib
import json
from pathlib import Path


def verify(repo):
    lock_path = repo / 'third_party/upstream-lock.json'
    lock = json.loads(lock_path.read_text(encoding='utf-8'))
    count = 0
    for source in lock['sources']:
        assert len(source['commit']) == 40
        for item in source['files']:
            path = (repo / item['localPath']).resolve()
            assert path.is_relative_to(repo / 'third_party')
            data = path.read_bytes()
            assert len(data) == item['bytes'], item['localPath']
            assert hashlib.sha256(data).hexdigest() == item['sha256'], item['localPath']
            assert hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest() == item['gitBlobSha1'], item['localPath']
            assert source['commit'] in item['url']
            count += 1
    return dict(passed=True, files=count, lockSha256=hashlib.sha256(lock_path.read_bytes()).hexdigest(),
                commits={source['name']: source['commit'] for source in lock['sources']})


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    print(json.dumps(verify(args.repo.resolve()), indent=2))
