"""Verify vendored official sources without changing or downloading any file."""
import argparse
import hashlib
import json
import re
from pathlib import Path


def verify(repo):
    repo = Path(repo).resolve()
    lock_path = repo / 'third_party/upstream-lock.json'
    lock = json.loads(lock_path.read_text(encoding='utf-8'))
    def require(condition, message):
        if not condition:
            raise ValueError(message)
    require(lock['schema'] == 'hlslperf.external-source-lock.v1' and lock['adaptedInPlace'] is False,
            'Unexpected source-lock contract')
    count = 0
    seen = set()
    for source in lock['sources']:
        require(re.fullmatch('[0-9a-f]{40}', source['commit']), 'Invalid pinned commit')
        require(source['license'] and any('license' in item['path'].lower() for item in source['files']),
                'Missing license for ' + source['name'])
        require(source['repository'].startswith('https://github.com/'), 'Unexpected source origin')
        for item in source['files']:
            path = (repo / item['localPath']).resolve()
            require(path.is_relative_to(repo / 'third_party'), 'Source escapes third_party')
            require(item['localPath'] not in seen, 'Duplicate source path')
            seen.add(item['localPath'])
            data = path.read_bytes()
            require(len(data) == item['bytes'], item['localPath'])
            require(hashlib.sha256(data).hexdigest() == item['sha256'], item['localPath'])
            require(hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest() == item['gitBlobSha1'], item['localPath'])
            expected = source['repository'].replace('https://github.com/', 'https://raw.githubusercontent.com/') + '/' + source['commit'] + '/' + item['path']
            require(expected == item['url'], 'Source URL does not match pinned repository/path')
            count += 1
    return dict(passed=True, files=count, lockSha256=hashlib.sha256(lock_path.read_bytes()).hexdigest(),
                commits={source['name']: source['commit'] for source in lock['sources']})


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    print(json.dumps(verify(args.repo.resolve()), indent=2))
