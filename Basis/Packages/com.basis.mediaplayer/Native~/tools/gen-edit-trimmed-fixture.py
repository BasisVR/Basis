#!/usr/bin/env python3
"""Generate fixtures/h264-aac-edit-trimmed.mp4: six seconds of H.264 with
B-frames and AAC, whose edit lists present only the first 3.9.

An editor that trims a clip without re-encoding writes this: the samples
stay, and each track's edit list says how much of them to present. The cut
falls inside a group of pictures: the P-frame shown at 3.9 s is decoded
ahead of the B-frame shown at 3.867 s, so a player decodes it and does not
show it. It also falls inside an AAC frame, so the sound is cut part-way
through one.

ffmpeg writes edit lists that cover the whole media, so the trim is made
afterwards: each track's media edit, `tkhd` and the `mvhd` are rewritten to
3.9 s in the movie timescale. Nothing else in the file changes.

Needs ffmpeg + ffprobe on PATH. Run from Native~ (the fixture path is
relative to it):

    python tools/gen-edit-trimmed-fixture.py
"""

import json
import os
import struct
import subprocess

FIXTURE = os.path.join("fixtures", "h264-aac-edit-trimmed.mp4")
SECONDS = 6
PRESENTED_MS = 3900
FPS = 30


def boxes(data, start, end):
    """(type, offset, size) of each box between start and end."""
    pos = start
    while pos + 8 <= end:
        size, kind = struct.unpack(">I4s", data[pos:pos + 8])
        assert size >= 8, f"unsupported box size {size} at {pos}"
        yield kind.decode("latin1"), pos, size
        pos += size


def child(data, parent_offset, parent_size, kind):
    for name, offset, size in boxes(data, parent_offset + 8, parent_offset + parent_size):
        if name == kind:
            return offset, size
    raise AssertionError(f"no {kind} in box at {parent_offset}")


def set_duration(data, offset, field_v0, field_v1, value):
    version = data[offset + 8]
    if version == 0:
        struct.pack_into(">I", data, offset + field_v0, value)
    else:
        struct.pack_into(">Q", data, offset + field_v1, value)


def main():
    os.makedirs("fixtures", exist_ok=True)
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y",
         "-f", "lavfi", "-i", f"testsrc2=duration={SECONDS}:size=320x180:rate={FPS}",
         "-f", "lavfi", "-i", f"sine=frequency=440:duration={SECONDS}:sample_rate=48000",
         "-c:v", "libx264", "-preset", "slow", "-crf", "32",
         "-g", "30", "-bf", "2", "-pix_fmt", "yuv420p",
         "-c:a", "aac", "-b:a", "64k", "-ar", "48000", "-ac", "2",
         "-movflags", "+faststart", FIXTURE],
        check=True)

    data = bytearray(open(FIXTURE, "rb").read())
    moov = next((o, s) for k, o, s in boxes(data, 0, len(data)) if k == "moov")
    mvhd = child(data, *moov, "mvhd")
    # mvhd: version/flags, then creation and modification times, then the
    # timescale and the duration (32-bit fields in version 0).
    movie_timescale = struct.unpack(">I", data[mvhd[0] + 20:mvhd[0] + 24])[0] \
        if data[mvhd[0] + 8] == 0 else struct.unpack(">I", data[mvhd[0] + 28:mvhd[0] + 32])[0]
    presented = PRESENTED_MS * movie_timescale // 1000
    set_duration(data, mvhd[0], 24, 32, presented)
    traks = [(o, s) for k, o, s in boxes(data, moov[0] + 8, sum(moov)) if k == "trak"]
    assert len(traks) == 2, traks
    for trak in traks:
        tkhd = child(data, *trak, "tkhd")
        set_duration(data, tkhd[0], 28, 36, presented)
        elst = child(data, *child(data, *trak, "edts"), "elst")
        version = data[elst[0] + 8]
        count = struct.unpack(">I", data[elst[0] + 12:elst[0] + 16])[0]
        assert count == 1, f"expected one edit, found {count}"
        # The one entry: segment duration, then media time, then rate.
        if version == 0:
            struct.pack_into(">I", data, elst[0] + 16, presented)
        else:
            struct.pack_into(">Q", data, elst[0] + 16, presented)
    open(FIXTURE, "wb").write(data)

    probe = json.loads(subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries",
         "stream=codec_type,nb_frames:format=duration", "-of", "json", FIXTURE],
        check=True, capture_output=True, text=True).stdout)
    kinds = sorted(s["codec_type"] for s in probe["streams"])
    assert kinds == ["audio", "video"], kinds
    assert abs(float(probe["format"]["duration"]) * 1000 - PRESENTED_MS) < 1, probe["format"]
    print(f"wrote {FIXTURE}: {os.path.getsize(FIXTURE)} bytes, "
          f"{SECONDS} s of media, edit lists presenting {PRESENTED_MS} ms")


if __name__ == "__main__":
    main()
