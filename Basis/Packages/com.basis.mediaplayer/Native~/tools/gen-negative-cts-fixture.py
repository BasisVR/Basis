#!/usr/bin/env python3
"""Generate fixtures/h264-aac-negcts-frag.mp4: a fragmented MP4 whose B-frames
carry negative composition offsets, with no index to read it by.

6 s of H.264 (320x180, 24 fps, GOP 24, two B-frames) + stereo AAC, a
fragment per keyframe. `+negative_cts_offsets` writes each video `trun` as
version 1, whose offsets are signed: a B-frame presented before the frame
decoded ahead of it states a negative one. `+skip_trailer` leaves out the
`mfra` ffmpeg otherwise appends and nothing writes a `sidx`: a reader
has to walk the fragments, as it does a live stream's segments.

Needs ffmpeg on PATH. Run from Native~ (the fixture path is relative to
it):

    python tools/gen-negative-cts-fixture.py
"""

import os
import struct
import subprocess

OUT = os.path.join("fixtures", "h264-aac-negcts-frag.mp4")
SECONDS = 6


def top_level(data):
    pos = 0
    while pos + 8 <= len(data):
        size, kind = struct.unpack(">I4s", data[pos:pos + 8])
        assert size >= 8, f"bad box size at {pos}"
        yield kind.decode("latin-1"), pos, size
        pos += size
    assert pos == len(data), "boxes do not tile the file"


def signed_offsets(data):
    """Every composition offset a version 1 `trun` states."""
    out = []
    at = data.find(b"trun")
    while at >= 0:
        version = data[at + 4]
        flags = int.from_bytes(data[at + 5:at + 8], "big")
        count = struct.unpack(">I", data[at + 8:at + 12])[0]
        p = at + 12 + (4 if flags & 0x1 else 0) + (4 if flags & 0x4 else 0)
        for _ in range(count):
            p += 4 * bool(flags & 0x100) + 4 * bool(flags & 0x200) + 4 * bool(flags & 0x400)
            if flags & 0x800:
                if version == 1:
                    out.append(struct.unpack(">i", data[p:p + 4])[0])
                p += 4
        at = data.find(b"trun", at + 1)
    return out


def main():
    os.makedirs("fixtures", exist_ok=True)
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y",
         "-f", "lavfi", "-i", f"testsrc2=duration={SECONDS}:size=320x180:rate=24",
         "-f", "lavfi", "-i", f"sine=frequency=440:duration={SECONDS}",
         "-c:v", "libx264", "-preset", "slow", "-crf", "32", "-g", "24",
         "-bf", "2", "-pix_fmt", "yuv420p",
         "-c:a", "aac", "-b:a", "64k", "-ar", "48000", "-ac", "2",
         "-movflags",
         "+frag_keyframe+empty_moov+default_base_moof+negative_cts_offsets+skip_trailer",
         "-bitexact", "-fflags", "+bitexact", OUT], check=True)
    subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-i", OUT,
                    "-f", "null", "-"], check=True)

    data = open(OUT, "rb").read()
    kinds = [kind for kind, _, _ in top_level(data)]
    assert "sidx" not in kinds and "mfra" not in kinds, f"{OUT}: carries an index"
    offsets = signed_offsets(data)
    assert any(o < 0 for o in offsets), f"{OUT}: no negative composition offset"
    print(f"wrote {OUT}: {len(data)} bytes, {kinds.count('moof')} fragments, "
          f"{sum(o < 0 for o in offsets)} negative offsets")


if __name__ == "__main__":
    main()
