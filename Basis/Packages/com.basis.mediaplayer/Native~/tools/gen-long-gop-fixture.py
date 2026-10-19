#!/usr/bin/env python3
"""Generate fixtures/h264-aac-4fps-longgop.mp4: twenty seconds of H.264 at
4 fps with a single keyframe, plus AAC.

Low-rate encodes (a still picture over music, a slideshow) space their
keyframes by frame count, so a few hundred frames can span tens of seconds.
A seek into such a file lands on a keyframe far behind its target, yet
decoding forward to the target is cheap because there are few frames in the
way. This file has 80 frames and puts the only keyframe at 0 s.

Needs ffmpeg + ffprobe on PATH. Run from Native~ (the fixture path is
relative to it):

    python tools/gen-long-gop-fixture.py
"""

import json
import os
import subprocess

FIXTURE = os.path.join("fixtures", "h264-aac-4fps-longgop.mp4")
SECONDS = 20
FPS = 4


def main():
    os.makedirs("fixtures", exist_ok=True)
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y",
         "-f", "lavfi", "-i", f"testsrc2=duration={SECONDS}:size=320x180:rate={FPS}",
         "-f", "lavfi", "-i", f"sine=frequency=440:duration={SECONDS}",
         "-c:v", "libx264", "-preset", "slow", "-crf", "32",
         "-g", "1000", "-keyint_min", "1000", "-sc_threshold", "0",
         "-bf", "0", "-pix_fmt", "yuv420p",
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
    assert keys == [0.0], keys
    print(f"wrote {FIXTURE}: {os.path.getsize(FIXTURE)} bytes, "
          f"{stream['nb_frames']} frames, one keyframe at 0 s")


if __name__ == "__main__":
    main()
