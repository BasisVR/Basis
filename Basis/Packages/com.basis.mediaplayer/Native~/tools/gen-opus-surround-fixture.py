#!/usr/bin/env python3
"""Generate fixtures/sine-48k-51.opus: 5.1 Ogg Opus, one speaker at a time.

Each speaker plays its own sine for one second, in WAV order (FL 400 Hz,
FR 800, FC 1200, LFE 60, BL 1600, BR 2000), with the others silent; the
round repeats three times, 18 s in all. libopus stores 5.1 as mapping
family 1, in Vorbis channel order, so a decoder that skips the reorder
plays the tones from the wrong speakers.

Needs ffmpeg with libopus on PATH. Run from Native~ (the fixture path is
relative to it):

    python tools/gen-opus-surround-fixture.py
"""

import os
import subprocess

OUT = os.path.join("fixtures", "sine-48k-51.opus")
TONES = [400, 800, 1200, 60, 1600, 2000]
ROUNDS = 3


def main():
    os.makedirs("fixtures", exist_ok=True)
    exprs = "|".join(
        f"if(eq(floor(mod(t\\,{len(TONES)}))\\,{i})\\,0.25*sin(2*PI*{f}*t)\\,0)"
        for i, f in enumerate(TONES))
    seconds = len(TONES) * ROUNDS
    subprocess.run(
        ["ffmpeg", "-nostdin", "-v", "error", "-y",
         "-f", "lavfi", "-i", f"aevalsrc=exprs={exprs}:c=5.1:s=48000:d={seconds}",
         "-c:a", "libopus", "-b:a", "192k",
         "-fflags", "+bitexact", "-flags:a", "+bitexact", OUT], check=True)

    data = open(OUT, "rb").read()
    head = data.index(b"OpusHead")
    assert data[head + 9] == 6, f"{OUT}: {data[head + 9]} channels"
    assert data[head + 18] == 1, f"{OUT}: mapping family {data[head + 18]}"
    print(f"wrote {OUT}: {len(data)} bytes, {seconds} s")


if __name__ == "__main__":
    main()
