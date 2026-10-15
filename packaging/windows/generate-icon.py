#!/usr/bin/env python3
"""Pack the PNGs from macOS generate-icon.swift into the matching Windows ICO."""
from pathlib import Path
import struct
import sys

source, destination = map(Path, sys.argv[1:])
images = {}
for path in sorted(source.glob("*.png")):
    data = path.read_bytes()
    assert data.startswith(b"\x89PNG\r\n\x1a\n")
    width, height = struct.unpack(">II", data[16:24])
    if width == height and width in {16, 32, 64, 128, 256}:
        images[width] = data
assert len(images) == 5, "Mancano le dimensioni richieste nell'iconset"
offset = 6 + 16 * len(images)
header = bytearray(struct.pack("<HHH", 0, 1, len(images)))
payload = bytearray()
for size, data in sorted(images.items()):
    header.extend(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
    payload.extend(data)
    offset += len(data)
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_bytes(header + payload)
