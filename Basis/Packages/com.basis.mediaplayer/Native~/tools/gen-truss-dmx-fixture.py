#!/usr/bin/env python3
"""Generate fixtures/h264-truss-dmx-320x180-30fps.ts, a TS whose video
carries DMX lighting data as Truss (https://github.com/towneh/Truss) stamps
it: one SEI user_data_unregistered message per access unit, under Truss's
UUID, holding a TRUSSDMX record around a DMXS payload.

The layouts are Truss's src/record.rs and src/payload.rs (all integers
big-endian):

    record   "TRUSSDMX", version 1, carrier 1 (SEI user_data_unregistered),
             seq u32, send_unix_nanos u64, frame_index u32, payload_len u16,
             payload, CRC-32 (IEEE) over everything before it
    payload  "DMXS", version 1, flags 0x01 (absolute), block count u16, then
             per block: universe u16, start slot u16, length u16, age_us u32,
             one byte per slot

2 s of H.264 (no B-frames, GOP 60) with a 48 kHz AAC sine beside it.
Record i has seq and frame_index i. Universes 0-3 are sent whole on every
frame and universe 4 joins at frame 30, which changes the message size
mid-stream. Slot s of universe u holds (u * 520 + s + i) % 251, which
changes every frame. Every block header carries a 00 00 02 run, and the
SEI needs emulation-prevention bytes that the engine has to remove.

Needs ffmpeg + ffprobe on PATH. Run from Native~:

    python tools/gen-truss-dmx-fixture.py
"""

import os
import struct
import subprocess
import sys
import tempfile
import zlib

FIXTURE = os.path.join("fixtures", "h264-truss-dmx-320x180-30fps.ts")
TRUSS_UUID = bytes.fromhex("b1f0a7d49c3e4a528f612d7c5e0b93a8")
FRAMES = 60
FPS = 30
SLOTS = 512
LATE_UNIVERSE_FROM = 30
AGE_US = 1500
SEND_BASE_NANOS = 1_700_000_000_000_000_000


def value(universe, slot, frame):
    return (universe * 520 + slot + frame) % 251


def dmxs_payload(frame):
    universes = [0, 1, 2, 3] + ([4] if frame >= LATE_UNIVERSE_FROM else [])
    out = bytearray(b"DMXS")
    out += bytes([1, 0x01])
    out += struct.pack(">H", len(universes))
    for u in universes:
        out += struct.pack(">HHHI", u, 0, SLOTS, AGE_US)
        out += bytes(value(u, s, frame) for s in range(SLOTS))
    return bytes(out)


def truss_record(frame):
    payload = dmxs_payload(frame)
    out = bytearray(b"TRUSSDMX")
    out += bytes([1, 1])
    out += struct.pack(">I", frame)
    out += struct.pack(">Q", SEND_BASE_NANOS + frame * (1_000_000_000 // FPS))
    out += struct.pack(">I", frame)
    out += struct.pack(">H", len(payload))
    out += payload
    out += struct.pack(">I", zlib.crc32(bytes(out)) & 0xFFFFFFFF)
    return bytes(out)


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
    rbsp.append(0x80)
    escaped = escape_rbsp(bytes(rbsp))
    if len(escaped) == len(rbsp):
        sys.exit("the message needed no emulation prevention; the fixture would not exercise it")
    return b"\x00\x00\x00\x01\x06" + escaped


def split_nals(data):
    """Yield (start_code, nal) for each Annex-B NAL in decode order."""
    i, n, starts = 0, len(data), []
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
    """Place one record after each AUD, ahead of the picture."""
    out = bytearray()
    frame = -1
    for sc, nal in split_nals(raw):
        out += sc + nal
        if nal and (nal[0] & 0x1F) == 9:
            frame += 1
            out += sei_nal(5, TRUSS_UUID + truss_record(frame))
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
            "-f", "lavfi", "-i", f"testsrc2=size=320x180:rate={FPS}:duration={FRAMES / FPS}",
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
            "-f", "lavfi", "-i", f"sine=frequency=440:duration={FRAMES / FPS}",
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2", aac,
        ])
        run([
            "ffmpeg", "-y",
            "-fflags", "+genpts", "-r", str(FPS), "-i", injected, "-i", aac,
            "-map", "0:v", "-map", "1:a", "-c", "copy", "-f", "mpegts", FIXTURE,
        ])
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v", "-count_packets",
         "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", FIXTURE],
        check=True, capture_output=True, text=True,
    ).stdout.split()
    if not probe or probe[0] != str(FRAMES):
        sys.exit(f"ffprobe counts {probe} video packets, expected {FRAMES}")
    print(f"wrote {FIXTURE}: {FRAMES} video AUs, one Truss record each")


if __name__ == "__main__":
    sys.exit(main())
