#!/usr/bin/env python3
"""Generate fixtures/h264-sei-userdata-640x360-30fps.ts — the TS whose
video carries SEI user data (payload type 5) on every frame.

No public stream carries a per-frame user-data lane, so the fixture is
authored: a 6 s H.264 (no B-frames, GOP 60) + AAC sine mux with one
`user_data_unregistered` message injected into every access unit. The
message layout below is the ground truth the engine tests assert against.

The same streams are then remuxed, untouched, into an MP4 beside it. The
TS demuxer refuses seeks, so the rows that need a seekable source with
user data in it (keyframes at 0, 2 and 4 s) read that one.

Needs ffmpeg + ffprobe on PATH. Run from Native~ (the fixture path is
relative to it):

    python tools/gen-sei-userdata-fixture.py

Every AU carries one message under FIXTURE_UUID:
    "BMUD"            4 bytes  magic
    frame index       4 bytes  big-endian, 0-based in decode order
    filler            512 bytes, byte i = (frame + i) & 0xFF
x264 stamps its own type-5 message (its build string, under its own UUID)
into the first AU, so the fixture also exercises the UUID split on a
message the consumer is meant to ignore.
"""

import os
import struct
import subprocess
import sys
import tempfile

FIXTURE = os.path.join("fixtures", "h264-sei-userdata-640x360-30fps.ts")
FIXTURE_MP4 = os.path.join("fixtures", "h264-sei-userdata-640x360-30fps.mp4")
FIXTURE_UUID = bytes.fromhex("7a1c3e5f9b2d4c6e8f0a1b2c3d4e5f60")
FRAMES = 180
FILLER = 512


def escape_rbsp(rbsp):
    """Insert emulation-prevention bytes so no 00 00 0x run survives."""
    out = bytearray()
    zeros = 0
    for b in rbsp:
        if zeros >= 2 and b <= 3:
            out.append(3)
            zeros = 0
        out.append(b)
        zeros = zeros + 1 if b == 0 else 0
    return bytes(out)


def sei_nal(payload_type, payload):
    rbsp = bytearray()
    t = payload_type
    while t >= 255:
        rbsp.append(0xFF)
        t -= 255
    rbsp.append(t)
    n = len(payload)
    while n >= 255:
        rbsp.append(0xFF)
        n -= 255
    rbsp.append(n)
    rbsp += payload
    rbsp.append(0x80)  # rbsp_trailing_bits
    return b"\x00\x00\x00\x01\x06" + escape_rbsp(bytes(rbsp))


def message(frame):
    body = b"BMUD" + struct.pack(">I", frame)
    body += bytes((frame + i) & 0xFF for i in range(FILLER))
    return FIXTURE_UUID + body


def split_nals(data):
    """Yield (start_code, nal) for each Annex-B NAL in decode order."""
    i = 0
    n = len(data)
    starts = []
    while i + 3 <= n:
        if data[i] == 0 and data[i + 1] == 0:
            if data[i + 2] == 1:
                starts.append((i, 3))
                i += 3
                continue
            if i + 4 <= n and data[i + 2] == 0 and data[i + 3] == 1:
                starts.append((i, 4))
                i += 4
                continue
        i += 1
    for k, (pos, sc) in enumerate(starts):
        end = starts[k + 1][0] if k + 1 < len(starts) else n
        yield data[pos:pos + sc], data[pos + sc:end]


def inject(raw):
    """Place one message after each AUD, ahead of the picture."""
    out = bytearray()
    frame = -1
    for sc, nal in split_nals(raw):
        out += sc + nal
        if nal and (nal[0] & 0x1F) == 9:
            frame += 1
            out += sei_nal(5, message(frame))
    if frame + 1 != FRAMES:
        sys.exit(f"expected {FRAMES} AUs, saw {frame + 1}")
    return bytes(out)


def run(args):
    done = subprocess.run(args, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
    if done.returncode != 0:
        sys.exit(f"{args[0]} failed ({done.returncode}):\n{done.stderr}")


def main():
    os.makedirs(os.path.dirname(FIXTURE), exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        raw = os.path.join(tmp, "raw.h264")
        injected = os.path.join(tmp, "injected.h264")
        aac = os.path.join(tmp, "audio.aac")
        run([
            "ffmpeg", "-y",
            "-f", "lavfi", "-i", f"testsrc=size=640x360:rate=30:duration={FRAMES / 30}",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-x264-params", "aud=1:bframes=0:keyint=60:min-keyint=60:scenecut=0",
            "-f", "h264", raw,
        ])
        with open(raw, "rb") as f:
            data = f.read()
        with open(injected, "wb") as f:
            f.write(inject(data))
        run([
            "ffmpeg", "-y",
            "-f", "lavfi", "-i", f"sine=frequency=440:duration={FRAMES / 30}",
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2", aac,
        ])
        run([
            "ffmpeg", "-y",
            "-fflags", "+genpts", "-r", "30", "-i", injected, "-i", aac,
            "-map", "0:v", "-map", "1:a", "-c", "copy", "-f", "mpegts", FIXTURE,
        ])
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v", "-count_packets",
         "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", FIXTURE],
        check=True, capture_output=True, text=True,
    ).stdout.split()
    if not probe:
        sys.exit("ffprobe reported no video packet count")
    probe = probe[0]
    if probe != str(FRAMES):
        sys.exit(f"ffprobe counts {probe} video packets, expected {FRAMES}")
    print(f"wrote {FIXTURE}: {FRAMES} video AUs, one message each")
    run([
        "ffmpeg", "-y", "-i", FIXTURE, "-c", "copy",
        "-movflags", "+faststart", FIXTURE_MP4,
    ])
    print(f"wrote {FIXTURE_MP4}: the same streams, seekable")


if __name__ == "__main__":
    sys.exit(main())
