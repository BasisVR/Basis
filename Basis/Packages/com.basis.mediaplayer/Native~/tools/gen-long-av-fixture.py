#!/usr/bin/env python3
"""Generate fixtures/h264-aac-320x180-30s.mp4 — thirty seconds of H.264 and
AAC in a seekable file.

The decode channel between the Bank and the video decoder holds 256 access
units, so a row about what happens once it fills needs a source that
outlasts it: eight and a half seconds at 30 fps before the channel is full,
and then long enough to watch what follows. The other MP4 fixtures are six
seconds. Keyframes every two seconds and two B-frames, so the decoder has a
reorder depth to it and there is a keyframe to rejoin at.

Picture and sound start together, so the file needs no empty edit to line
them up and says the same thing to every reader.

Needs ffmpeg + ffprobe on PATH. Run from Native~ (the fixture path is
relative to it):

    python tools/gen-long-av-fixture.py
"""

import json
import os
import subprocess

FIXTURE = os.path.join("fixtures", "h264-aac-320x180-30s.mp4")
SECONDS = 30
FPS = 30
GOP = 60


def main():
    os.makedirs("fixtures", exist_ok=True)
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y",
         "-f", "lavfi", "-i", f"testsrc2=duration={SECONDS}:size=320x180:rate={FPS}",
         "-f", "lavfi", "-i", f"sine=frequency=440:duration={SECONDS}",
         "-c:v", "libx264", "-preset", "slow", "-crf", "32", "-g", str(GOP),
         "-keyint_min", str(GOP), "-sc_threshold", "0",
         "-bf", "2", "-pix_fmt", "yuv420p",
         "-c:a", "aac", "-b:a", "64k", "-ar", "48000", "-ac", "2",
         "-movflags", "+faststart", FIXTURE],
        check=True)
    probe = json.loads(subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v", "-show_entries",
         "stream=nb_frames,start_time:packet=pts_time,flags", "-of", "json", FIXTURE],
        check=True, capture_output=True, text=True).stdout)
    stream = probe["streams"][0]
    keys = [float(p["pts_time"]) for p in probe["packets"] if p["flags"].startswith("K")]
    assert int(stream["nb_frames"]) == SECONDS * FPS, stream
    assert float(stream["start_time"]) == 0.0, stream
    assert keys == [float(n * GOP // FPS) for n in range(SECONDS * FPS // GOP)], keys
    print(f"wrote {FIXTURE}: {os.path.getsize(FIXTURE)} bytes, "
          f"{stream['nb_frames']} frames, keyframes every {GOP // FPS} s")


if __name__ == "__main__":
    main()
