#!/usr/bin/env python3
"""Generate the MP4 fixtures whose tracks start after the movie does:

    fixtures/h264-aac-late-video.mp4   picture 0.5 s after the sound
    fixtures/h264-aac-late-audio.mp4   sound 0.5 s after the picture, primed
    fixtures/aac-late-start.m4a        the same sound alone, 0.5 s in

The first two are three seconds of H.264 (320x180, 24 fps, two B-frames)
and stereo AAC. A late track opens its edit list with an empty edit
(`media_time` -1) as long as the gap; a reader that ignores it plays the
track early by the gap.

The first is muxed into MPEG-TS with the video leg offset, then
stream-copied into MP4, which records the offset the way any remux of
such a source does: the empty edit, then the ordinary edit that skips the
B-frame reorder delay.

The second needs an audio edit list that states the gap and the encoder
priming separately: an empty edit of 500 ms, then an edit starting at
media time 1024, the AAC encoder's priming. ffmpeg's MP4 muxer folds the
priming into the empty edit instead, so the file is muxed with the audio
offset and its audio edit list is then rewritten in place (same entry
count, same size). The priming then decodes to 478.7 ms, ahead of the
sound at 500 ms, and must not be played. The third is the second's audio
track on its own, its edit list rewritten the same way.

Needs ffmpeg + ffprobe on PATH. Run from Native~ (the fixture paths are
relative to it):

    python tools/gen-late-start-fixtures.py
"""

import os
import struct
import subprocess
import tempfile

LATE_VIDEO = os.path.join("fixtures", "h264-aac-late-video.mp4")
LATE_AUDIO = os.path.join("fixtures", "h264-aac-late-audio.mp4")
LATE_START = os.path.join("fixtures", "aac-late-start.m4a")
SECONDS = 3
OFFSET = "0.5"
PRIMING = 1024
BITEXACT = ["-bitexact", "-fflags", "+bitexact"]
ENCODE = ["-c:v", "libx264", "-preset", "slow", "-crf", "32", "-g", "12",
          "-bf", "2", "-pix_fmt", "yuv420p",
          "-c:a", "aac", "-b:a", "64k", "-ar", "48000", "-ac", "2"]


def edit_lists(data):
    """Each track's handler, and its edit list as (offset, version, entries)."""
    tracks = []

    def walk(pos, end, track):
        while pos + 8 <= end:
            size, kind = struct.unpack(">I4s", data[pos:pos + 8])
            assert size >= 8, f"bad box size at {pos}"
            if kind == b"trak":
                track = {"handler": None, "elst": None}
                tracks.append(track)
                walk(pos + 8, pos + size, track)
            elif kind in (b"moov", b"edts", b"mdia"):
                walk(pos + 8, pos + size, track)
            elif kind == b"hdlr":
                track["handler"] = bytes(data[pos + 16:pos + 20])
            elif kind == b"elst":
                version = data[pos + 8]
                count = struct.unpack(">I", data[pos + 12:pos + 16])[0]
                entries, p = [], pos + 16
                for _ in range(count):
                    if version == 1:
                        entries.append(struct.unpack(">Qq", data[p:p + 16]))
                        p += 20
                    else:
                        entries.append(struct.unpack(">Ii", data[p:p + 8]))
                        p += 12
                track["elst"] = (pos + 16, version, entries)
            pos += size

    walk(0, len(data), None)
    return {t["handler"]: t["elst"] for t in tracks}


def movie_timescale(data):
    """The `mvhd` timescale, which an edit's segment duration is stated in."""
    pos = 0
    while pos + 8 <= len(data):
        size, kind = struct.unpack(">I4s", data[pos:pos + 8])
        assert size >= 8, f"bad box size at {pos}"
        if kind == b"moov":
            pos += 8
            continue
        if kind == b"mvhd":
            at = pos + (28 if data[pos + 8] == 1 else 20)
            return struct.unpack(">I", data[at:at + 4])[0]
        pos += size
    raise AssertionError("no mvhd")


def first_pts(path, stream):
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", stream,
         "-show_entries", "packet=pts_time", "-of", "csv=p=0", path],
        check=True, capture_output=True, text=True).stdout
    # A packet with side data (the discard marker) prints a trailing comma.
    return min(float(line.split(",")[0]) for line in out.split())


def late_video(scratch):
    legs = os.path.join(scratch, "legs.ts")
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y",
         "-itsoffset", OFFSET,
         "-f", "lavfi", "-i", f"testsrc2=duration={SECONDS}:size=320x180:rate=24",
         "-f", "lavfi", "-i", f"sine=frequency=440:duration={SECONDS}",
         *ENCODE, *BITEXACT, "-f", "mpegts", legs], check=True)
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y", "-i", legs,
         "-c", "copy", *BITEXACT, LATE_VIDEO], check=True)

    edits = edit_lists(open(LATE_VIDEO, "rb").read())[b"vide"][2]
    assert edits and edits[0][1] == -1, f"no leading empty edit: {edits}"
    video, audio = first_pts(LATE_VIDEO, "v"), first_pts(LATE_VIDEO, "a")
    assert video - audio >= 0.4, f"video {video} s, audio {audio} s"
    print(f"wrote {LATE_VIDEO}: video edits {edits}, "
          f"first pts video {video} s, audio {audio} s")


def late_audio(scratch):
    source = os.path.join(scratch, "source.mp4")
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y",
         "-f", "lavfi", "-i", f"testsrc2=duration={SECONDS}:size=320x180:rate=24",
         "-f", "lavfi", "-i", f"sine=frequency=440:duration={SECONDS}",
         *ENCODE, *BITEXACT, source], check=True)
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y", "-i", source,
         "-itsoffset", OFFSET, "-i", source, "-map", "0:v", "-map", "1:a",
         "-c", "copy", *BITEXACT, LATE_AUDIO], check=True)
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y",
         "-itsoffset", OFFSET, "-i", source, "-map", "0:a",
         "-c", "copy", *BITEXACT, LATE_START], check=True)
    for path in (LATE_AUDIO, LATE_START):
        state_gap_and_priming(path)


def state_gap_and_priming(path):
    """Rewrite the audio edit list as the gap, then the priming."""
    data = bytearray(open(path, "rb").read())
    at, version, edits = edit_lists(data)[b"soun"]
    assert version == 0 and len(edits) == 2 and edits[0][1] == -1 \
        and edits[1][1] == 0, f"{path}: unexpected audio edits {edits}"
    gap = round(float(OFFSET) * movie_timescale(data))
    total = edits[0][0] + edits[1][0]
    struct.pack_into(">Ii", data, at, gap, -1)
    struct.pack_into(">Ii", data, at + 12, total - gap, PRIMING)
    open(path, "wb").write(data)

    edits = edit_lists(bytes(data))[b"soun"][2]
    audio = first_pts(path, "a")
    assert abs(audio - (float(OFFSET) - PRIMING / 48000)) < 1e-4, \
        f"{path}: first audio pts {audio} s"
    print(f"wrote {path}: audio edits {edits}, first audio pts {audio} s")


def main():
    os.makedirs("fixtures", exist_ok=True)
    with tempfile.TemporaryDirectory() as scratch:
        late_video(scratch)
        late_audio(scratch)


if __name__ == "__main__":
    main()
