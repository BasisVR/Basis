#!/usr/bin/env python3
"""Generate fixtures/h264-4fps-320x180.mp4: five seconds of H.264 at 4 fps
with B-frames and no audio.

With no sound to play out, a session ends on its picture alone, and at
4 fps the last frame is on screen for a quarter of a second: long enough
that ending as it starts, rather than as it finishes, shows in position.
The B-frames give the track a reorder delay, so its edit list shifts the
media and its `mdhd` runs past the length the edit list states.

Needs ffmpeg + ffprobe on PATH. Run from Native~ (the fixture path is
relative to it):

    python tools/gen-low-rate-video-fixture.py
"""

import json
import os
import subprocess

FIXTURE = os.path.join("fixtures", "h264-4fps-320x180.mp4")
SECONDS = 5
FPS = 4


def main():
    os.makedirs("fixtures", exist_ok=True)
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y",
         "-f", "lavfi", "-i", f"testsrc2=duration={SECONDS}:size=320x180:rate={FPS}",
         "-c:v", "libx264", "-preset", "slow", "-crf", "32",
         "-g", "8", "-bf", "2", "-pix_fmt", "yuv420p",
         "-movflags", "+faststart", FIXTURE],
        check=True)
    probe = json.loads(subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries",
         "stream=codec_type,nb_frames,has_b_frames:format=duration", "-of", "json", FIXTURE],
        check=True, capture_output=True, text=True).stdout)
    streams = probe["streams"]
    assert [s["codec_type"] for s in streams] == ["video"], streams
    assert int(streams[0]["nb_frames"]) == SECONDS * FPS, streams
    assert int(streams[0]["has_b_frames"]) > 0, streams
    assert float(probe["format"]["duration"]) == SECONDS, probe["format"]
    print(f"wrote {FIXTURE}: {os.path.getsize(FIXTURE)} bytes, "
          f"{streams[0]['nb_frames']} frames, video only")


if __name__ == "__main__":
    main()
