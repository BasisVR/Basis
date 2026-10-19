#!/usr/bin/env python3
"""Generate fixtures/h264-aac-no-edit-list.mp4: an MP4 whose B-frames give
its video a reorder delay that no edit list takes out.

3 s of H.264 (320x180, 24 fps, GOP 24, two B-frames) + stereo AAC, one
`moov` at the front. `-use_editlist 0` leaves out the `elst` ffmpeg
otherwise writes to start each track at its first presented sample. The
first picture then presents where its composition time puts it: two
frames in, 83 ms after the sound starts.

Needs ffmpeg on PATH. Run from Native~ (the fixture path is relative to
it):

    python tools/gen-no-edit-list-fixture.py
"""

import os
import subprocess

OUT = os.path.join("fixtures", "h264-aac-no-edit-list.mp4")
SECONDS = 3


def main():
    os.makedirs("fixtures", exist_ok=True)
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y",
         "-f", "lavfi", "-i", f"testsrc2=duration={SECONDS}:size=320x180:rate=24",
         "-f", "lavfi", "-i", f"sine=frequency=440:duration={SECONDS}",
         "-c:v", "libx264", "-preset", "slow", "-crf", "32", "-g", "24",
         "-bf", "2", "-pix_fmt", "yuv420p",
         "-c:a", "aac", "-b:a", "64k", "-ar", "48000", "-ac", "2",
         "-movflags", "+faststart", "-use_editlist", "0",
         "-bitexact", "-fflags", "+bitexact", OUT], check=True)

    data = open(OUT, "rb").read()
    assert b"elst" not in data, f"{OUT}: carries an edit list"
    first = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v", "-read_intervals", "%+#1",
         "-show_entries", "packet=pts_time", "-of", "csv=p=0", OUT],
        check=True, capture_output=True, text=True).stdout.split()[0]
    assert abs(float(first) - 2 / 24) < 1e-6, f"{OUT}: first picture at {first} s"
    print(f"wrote {OUT}: {len(data)} bytes, first picture at {first} s")


if __name__ == "__main__":
    main()
