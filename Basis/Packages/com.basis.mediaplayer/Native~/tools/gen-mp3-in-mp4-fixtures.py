#!/usr/bin/env python3
"""Generate the MP3-in-MP4 fixtures: H.264 with MP3 sound, once under each
sample entry that carries it.

- fixtures/h264-mp3-320x180.mp4: an `mp4a` entry whose `esds` names
  MPEG-1 audio (object type 0x6B).
- fixtures/h264-mp3-320x180.mov: a QuickTime `.mp3` sound description,
  with no `esds`.
- fixtures/h264-mp3-320x180-frag.mov: the `.mov` remuxed into fragments
  behind a `sidx`, so its samples are found through the index, not `moov`.
  Its sound is offset by 1.5 s, which ffmpeg writes as a first sound
  frame at zero lasting over the gap, stored after the first fragment,
  so that fragment holds only picture.

4 s of 320x180 H.264 at 24 fps and stereo MP3 at 44.1 kHz. The sound is
a 440 Hz sine in the left channel for one second, then the right, and
again, so a decoder that swaps or merges the channels is heard.

Needs ffmpeg with libmp3lame on PATH. Run from Native~ (the fixture paths
are relative to it):

    python tools/gen-mp3-in-mp4-fixtures.py
"""

import os
import struct
import subprocess

OUTS = [os.path.join("fixtures", f"h264-mp3-320x180.{ext}") for ext in ("mp4", "mov")]
FRAG = os.path.join("fixtures", "h264-mp3-320x180-frag.mov")
LATE = "1.5"
SECONDS = 4


def object_type(data):
    """The object type the first `esds` names. Its ES descriptor must have
    none of the optional fields, as ffmpeg writes it."""
    at = data.index(b"esds") + 8
    assert data[at] == 0x03
    at += 1
    while data[at] & 0x80:
        at += 1
    at += 4
    assert data[at] == 0x04
    at += 1
    while data[at] & 0x80:
        at += 1
    return data[at + 1]


def fragment_tracks(data):
    """The track ids each top-level `moof` carries, in file order."""
    out, pos = [], 0
    while pos + 8 <= len(data):
        size, kind = struct.unpack(">I4s", data[pos:pos + 8])
        if kind == b"moof":
            ids, at = [], pos + 8
            while at < pos + size:
                traf, name = struct.unpack(">I4s", data[at:at + 8])
                if name == b"traf":
                    # The traf's first child is its tfhd: header, version and
                    # flags, then the track id.
                    ids.append(struct.unpack(">I", data[at + 20:at + 24])[0])
                at += traf
            out.append(ids)
        pos += size
    return out


def main():
    os.makedirs("fixtures", exist_ok=True)
    left = "if(lt(mod(t\\,2)\\,1)\\,0.25*sin(2*PI*440*t)\\,0)"
    right = "if(lt(mod(t\\,2)\\,1)\\,0\\,0.25*sin(2*PI*440*t))"
    for out in OUTS:
        subprocess.run(
            ["ffmpeg", "-nostdin", "-v", "error", "-y",
             "-f", "lavfi", "-i", f"testsrc2=duration={SECONDS}:size=320x180:rate=24",
             "-f", "lavfi", "-i",
             f"aevalsrc=exprs={left}|{right}:c=stereo:s=44100:d={SECONDS}",
             "-c:v", "libx264", "-preset", "slow", "-crf", "32", "-g", "24",
             "-pix_fmt", "yuv420p",
             "-c:a", "libmp3lame", "-b:a", "96k",
             "-movflags", "+faststart",
             "-bitexact", "-fflags", "+bitexact", out], check=True)

        data = open(out, "rb").read()
        if out.endswith(".mp4"):
            assert b"mp4a" in data, f"{out}: not an mp4a entry"
            assert object_type(data) == 0x6B, f"{out}: esds does not name MPEG-1 audio"
        else:
            assert b".mp3" in data and b"esds" not in data, f"{out}: not a .mp3 entry"
        print(f"wrote {out}: {len(data)} bytes")

    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y", "-i", OUTS[1],
         "-itsoffset", LATE, "-i", OUTS[1], "-map", "0:v", "-map", "1:a", "-c", "copy",
         "-movflags", "frag_keyframe+empty_moov+default_base_moof+global_sidx",
         "-bitexact", "-fflags", "+bitexact", FRAG], check=True)
    data = open(FRAG, "rb").read()
    assert b".mp3" in data and b"sidx" in data, f"{FRAG}: not an indexed .mp3 file"
    tracks = fragment_tracks(data)
    assert len(tracks[0]) == 1 and len(tracks[1]) == 2, f"{FRAG}: tracks by fragment {tracks}"
    print(f"wrote {FRAG}: {len(data)} bytes")


if __name__ == "__main__":
    main()
