#!/usr/bin/env python3
"""Generate fixtures/h264-608-640x360-30fps.ts — the caption-bearing TS.

No public stream carries CEA-608 reliably, so the fixture is authored: a
9 s H.264 (no B-frames, GOP 60) + AAC sine mux with ATSC A/53 caption SEI
injected into scripted access units. The cue script below is the ground
truth the engine tests assert against.

Needs ffmpeg + ffprobe on PATH. Run from Native~ (the fixture paths are
relative to it):

    python tools/gen-caption-fixture.py

The scripted cues (pts relative to the first video frame):
    0 s  pop-on  "HELLO WORLD"
    2 s  pop-on  "CAFÉ MAÑANA"      (special + extended characters)
    4 s  clear   (EDM)
    6 s  roll-up "ROLL UP"
    7 s  roll-up "ROLL UP" / "SECOND" (carriage return + second row)
    8 s  clear   (EDM — also lets ffmpeg's srt writer close the last cue)
"""

import json
import os
import subprocess
import sys
import tempfile

FIXTURE = os.path.join("fixtures", "h264-608-640x360-30fps.ts")
FIXTURE_MP4 = os.path.join("fixtures", "h264-608-640x360-30fps.mp4")


def parity(b):
    """Set bit 7 so the byte has odd parity (CEA-608 transmission format)."""
    return b | 0x80 if bin(b & 0x7F).count("1") % 2 == 0 else b


def pairs_ctrl(b0, b1):
    """A control pair, transmitted doubled per CEA-608."""
    return [(b0, b1), (b0, b1)]


def pairs_text(text):
    """Basic-set text pairs (2 chars per pair, space-padded)."""
    encoded = []
    for ch in text:
        table = {"á": 0x2A, "é": 0x5C, "í": 0x5E, "ó": 0x5F, "ú": 0x60,
                 "ç": 0x7B, "÷": 0x7C, "Ñ": 0x7D, "ñ": 0x7E}
        encoded.append(table.get(ch, ord(ch)))
    if len(encoded) % 2:
        encoded.append(0x00)  # pad; second byte < 0x20 is ignored
    return [(encoded[i], encoded[i + 1]) for i in range(0, len(encoded), 2)]


# Control opcodes (channel 1): {RCL, EOC, EDM, RU2, CR} + PAC row 15 / row 14.
RCL, EOC, EDM, RU2, CR = (0x14, 0x20), (0x14, 0x2F), (0x14, 0x2C), (0x14, 0x25), (0x14, 0x2D)
PAC_R15, PAC_R14 = (0x14, 0x70), (0x14, 0x50)


def popon(text_rows):
    out = pairs_ctrl(*RCL)
    pacs = [PAC_R14, PAC_R15] if len(text_rows) > 1 else [PAC_R15]
    for pac, row in zip(pacs, text_rows):
        out += pairs_ctrl(*pac) + pairs_text(row)
    return out + pairs_ctrl(*EOC)


# É via extended EXT_12[1] after its fallback 'E'; Ñ is basic 0x7D.
cafe = pairs_ctrl(*RCL) + pairs_ctrl(*PAC_R15) + pairs_text("CAFE")
cafe += pairs_ctrl(0x12, 0x21) + pairs_text(" MAÑANA") + pairs_ctrl(*EOC)

SCRIPT = {
    0: popon(["HELLO WORLD"]),
    60: cafe,
    120: pairs_ctrl(*EDM),
    180: pairs_ctrl(*RU2) + pairs_ctrl(*PAC_R15) + pairs_text("ROLL UP"),
    210: pairs_ctrl(*CR) + pairs_text("SECOND"),
    240: pairs_ctrl(*EDM),
}


def a53_sei_nal(cc_pairs):
    cc = bytes([0x40 | len(cc_pairs), 0x00])
    for b0, b1 in cc_pairs:
        cc += bytes([0xFC, parity(b0), parity(b1)])  # marker+valid, cc_type 0
    payload = b"\xb5\x00\x31GA94\x03" + cc + b"\xff"
    body = bytes([0x06, 0x04, len(payload)]) + payload + b"\x80"
    # Emulation prevention: 00 00 -> 00 00 03 before a byte <= 3.
    rbsp = bytearray()
    zeros = 0
    for b in body:
        if zeros >= 2 and b <= 3:
            rbsp.append(3)
            zeros = 0
        rbsp.append(b)
        zeros = zeros + 1 if b == 0 else 0
    return b"\x00\x00\x01" + bytes(rbsp)


def au_sizes(h264_path):
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-show_packets", "-of", "json", h264_path],
        capture_output=True, text=True, check=True)
    return [int(p["size"]) for p in json.loads(probe.stdout)["packets"]]


def inject(h264_in, h264_out):
    data = open(h264_in, "rb").read()
    sizes = au_sizes(h264_in)
    assert sum(sizes) == len(data), "AU sizes do not tile the stream"
    out, pos = bytearray(), 0
    for index, size in enumerate(sizes):
        au = data[pos:pos + size]
        pos += size
        if index in SCRIPT:
            # Insert before the first VCL NAL (after SPS/PPS on keyframes).
            insert_at = len(au)
            offset = 0
            while offset < len(au):
                next_start = au.find(b"\x00\x00\x01", offset + 3)
                start = offset + (4 if au[offset:offset + 4] == b"\x00\x00\x00\x01" else 3)
                if au[start] & 0x1F in (1, 5):
                    insert_at = offset
                    break
                if next_start < 0:
                    break
                offset = next_start - (1 if au[next_start - 1] == 0 else 0)
            au = au[:insert_at] + a53_sei_nal(SCRIPT[index]) + au[insert_at:]
        out += au
    open(h264_out, "wb").write(bytes(out))
    print(f"injected SEI into AUs {sorted(SCRIPT)} of {len(sizes)}")


def main():
    os.makedirs("fixtures", exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        raw = os.path.join(tmp, "raw.h264")
        cap = os.path.join(tmp, "cap.h264")
        aac = os.path.join(tmp, "audio.aac")
        subprocess.run(
            ["ffmpeg", "-v", "error", "-y",
             "-f", "lavfi", "-i", "testsrc2=duration=9:size=640x360:rate=30",
             "-c:v", "libx264", "-preset", "veryfast", "-bf", "0", "-g", "60",
             "-pix_fmt", "yuv420p", "-f", "h264", raw], check=True)
        subprocess.run(
            ["ffmpeg", "-v", "error", "-y",
             "-f", "lavfi", "-i", "sine=frequency=440:duration=9",
             "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2", aac],
            check=True)
        inject(raw, cap)
        subprocess.run(
            ["ffmpeg", "-v", "error", "-y",
             "-fflags", "+genpts", "-r", "30", "-i", cap, "-i", aac,
             "-map", "0:v", "-map", "1:a", "-c", "copy", "-f", "mpegts",
             FIXTURE], check=True)
    print(f"wrote {FIXTURE}")
    # The same streams again as an MP4, untouched: the TS demuxer refuses
    # seeks, and the rows about captions across a seek need one that does
    # not (keyframes every 2 s).
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-i", FIXTURE, "-c", "copy",
         "-movflags", "+faststart", FIXTURE_MP4], check=True)
    print(f"wrote {FIXTURE_MP4}")

    # Oracle check: ffmpeg's own 608 decoder must see the scripted text.
    # (Forward slashes: the movie= filter parses backslashes as escapes.)
    srt = subprocess.run(
        ["ffmpeg", "-v", "error", "-f", "lavfi",
         "-i", f"movie={FIXTURE.replace(os.sep, '/')}[out+subcc]",
         "-map", "0:1", "-f", "srt", "-"],
        capture_output=True, text=True, encoding="utf-8")
    text = srt.stdout
    for expected in ["HELLO WORLD", "CAFÉ MAÑANA", "ROLL UP", "SECOND"]:
        if expected not in text:
            print(f"ORACLE MISS: {expected!r} not in ffmpeg subcc output:\n{text}")
            return 1
    print("ffmpeg subcc oracle: all scripted cues present")
    return 0


if __name__ == "__main__":
    sys.exit(main())
