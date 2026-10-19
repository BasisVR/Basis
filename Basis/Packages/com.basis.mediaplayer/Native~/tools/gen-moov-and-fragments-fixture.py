#!/usr/bin/env python3
"""Generate fixtures/h264-aac-moov-and-frag.mp4: an MP4 whose `moov` holds
the first second of samples itself, with the rest in movie fragments
after it.

6 s of H.264 (320x180, 24 fps, GOP 24, two B-frames) + stereo AAC.
`+frag_keyframe` without `+empty_moov` is ffmpeg's layout for this: the
first fragment's samples go in `moov` and its `mdat`, and every later
keyframe opens a `moof`. With no edit list, which ffmpeg does not
write here, the video presents two frames in, as the file states.
`+skip_trailer` leaves out the `mfra`: a reader has to walk the
fragments.

Needs ffmpeg on PATH. Run from Native~ (the fixture path is relative to
it):

    python tools/gen-moov-and-fragments-fixture.py
"""

import os
import struct
import subprocess

OUT = os.path.join("fixtures", "h264-aac-moov-and-frag.mp4")
SECONDS = 6


def top_level(data):
    pos = 0
    while pos + 8 <= len(data):
        size, kind = struct.unpack(">I4s", data[pos:pos + 8])
        assert size >= 8, f"bad box size at {pos}"
        yield kind.decode("latin-1")
        pos += size
    assert pos == len(data), "boxes do not tile the file"


def main():
    os.makedirs("fixtures", exist_ok=True)
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y",
         "-f", "lavfi", "-i", f"testsrc2=duration={SECONDS}:size=320x180:rate=24",
         "-f", "lavfi", "-i", f"sine=frequency=440:duration={SECONDS}",
         "-c:v", "libx264", "-preset", "slow", "-crf", "32", "-g", "24",
         "-bf", "2", "-pix_fmt", "yuv420p",
         "-c:a", "aac", "-b:a", "64k", "-ar", "48000", "-ac", "2",
         "-movflags", "+frag_keyframe+default_base_moof+skip_trailer",
         "-bitexact", "-fflags", "+bitexact", OUT], check=True)
    subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-i", OUT,
                    "-f", "null", "-"], check=True)

    data = open(OUT, "rb").read()
    kinds = list(top_level(data))
    assert kinds[:3] == ["ftyp", "moov", "mdat"], f"{OUT}: {kinds[:3]}"
    assert "moof" in kinds and "mfra" not in kinds and "sidx" not in kinds, kinds
    assert b"elst" not in data, f"{OUT}: carries an edit list"
    print(f"wrote {OUT}: {len(data)} bytes, {kinds.count('moof')} fragments after moov")


if __name__ == "__main__":
    main()
