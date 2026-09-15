"""Create one synthetic, non-benchmark input for the file-based CPU export caller."""
import argparse
import json
from pathlib import Path
import struct

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("output", type=Path, help="new input directory")
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=False)
state = 20260916
seeds = []
for index in range(4097):
    state = (state * 1664525 + 1013904223) & 0xffffffff
    seeds.append(state ^ (state >> 16) ^ ((index * 2246822519) & 0xffffffff))
(args.output / "seeds.u32").write_bytes(struct.pack("<" + "I" * len(seeds), *seeds))
(args.output / "request.json").write_text(json.dumps({
    "schemaVersion": 1, "backend": "cpu", "seedsFile": "seeds.u32",
    "width": 128, "height": 72, "framesPerAtlas": 4, "atlasCount": 2,
    "firstFrame": 0, "visibilityMask": 15, "workers": 1
}, indent=2) + "\n", encoding="utf-8")
print("Created synthetic preview input; this is not a performance or GPU fixture.")
