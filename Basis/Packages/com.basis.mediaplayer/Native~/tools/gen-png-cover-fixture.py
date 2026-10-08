#!/usr/bin/env python3
"""Generate fixtures/h264-aac-png-cover.mp4: H.264 and AAC with a PNG cover.

2 s of 320x180 H.264 at 30 fps and a 440 Hz stereo AAC sine, with a
64x64 PNG cover stored in `moov/udta/meta/ilst/covr`. ffmpeg writes an
`attached_pic` stream into an MP4 as that `covr` item, with a `data` box
whose type is 14 (PNG); the script checks it is still 14. The cover is a
frame of testsrc2, so a flipped or mirrored picture shows.

Needs ffmpeg on PATH. Run from Native~ (the paths are relative to it):

    python tools/gen-png-cover-fixture.py
"""

import os
import subprocess
import tempfile

OUT = os.path.join("fixtures", "h264-aac-png-cover.mp4")
PNG_TYPE = 14


def boxes(data, start, end):
    """Each box in data[start:end] as (type, body start, body end)."""
    at = start
    while at + 8 <= end:
        size = int.from_bytes(data[at : at + 4], "big")
        kind = data[at + 4 : at + 8]
        header = 8
        if size == 1:
            size = int.from_bytes(data[at + 8 : at + 16], "big")
            header = 16
        elif size == 0:
            size = end - at
        assert header <= size and at + size <= end, f"bad {kind!r} box at {at}"
        yield kind, at + header, at + size
        at += size


def only_child(data, body, kind):
    """The body of the one `kind` box within `body`, a (start, end) pair."""
    found = [(s, e) for k, s, e in boxes(data, *body) if k == kind]
    assert len(found) == 1, f"expected one {kind!r} box, found {len(found)}"
    return found[0]


def covr_data_type(data):
    """The type of the `data` box at moov/udta/meta/ilst/covr, walked box
    by box so nothing in the media data can be mistaken for it."""
    body = (0, len(data))
    for kind in (b"moov", b"udta", b"meta"):
        body = only_child(data, body, kind)
    # `meta` is a full box: version and flags come before its children.
    body = (body[0] + 4, body[1])
    for kind in (b"ilst", b"covr", b"data"):
        body = only_child(data, body, kind)
    # The data box opens with a version byte and the 24-bit type.
    return int.from_bytes(data[body[0] + 1 : body[0] + 4], "big")


def main():
    with tempfile.TemporaryDirectory() as tmp:
        cover = os.path.join(tmp, "cover.png")
        subprocess.run(
            ["ffmpeg", "-v", "error", "-y", "-f", "lavfi",
             "-i", "testsrc2=size=64x64:rate=1", "-frames:v", "1", cover],
            check=True,
        )
        subprocess.run(
            ["ffmpeg", "-v", "error", "-y",
             "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=2",
             "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
             "-i", cover,
             "-map", "0:v", "-map", "1:a", "-map", "2",
             "-c:v:0", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
             "-g", "30", "-c:a", "aac", "-ac", "2", "-b:a", "96k",
             "-c:v:1", "copy", "-disposition:v:1", "attached_pic",
             "-movflags", "+faststart", OUT],
            check=True,
        )
    with open(OUT, "rb") as f:
        data = f.read()
    kind = covr_data_type(data)
    assert kind == PNG_TYPE, f"covr data type {kind}, wanted {PNG_TYPE} (PNG)"
    print(f"{OUT}: {len(data)} bytes, covr type {kind}")


if __name__ == "__main__":
    main()
