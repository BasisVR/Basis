#!/usr/bin/env python3
"""Demux conformance gate: diff basis_demux_dump against ffprobe, per fixture.

The protocol layer hands every access unit to a sink as (pts, dts, size, key,
payload); ffprobe reports the same per packet. When the container stores the
payload as-is the two compare down to the MD5 of the bytes, which makes this an
exact-match gate rather than a threshold. A parser change that alters what the
demuxer emits fails the gate.

Two framing traps this handles:
  * H.264/H.265 leave the demuxer as start-code Annex B while ffprobe hashes the
    length-prefixed bytes MP4 stores, so those are compared through the matching
    bitstream filter, and keyframes are exempt (the filter inlines SPS/PPS that
    the demuxer passes as extradata).
  * LPCM has no inherent packetisation, so its per-packet MD5 is not comparable;
    it is checked on announce/count/pts, not payload hash.

ffmpeg and ffprobe must be on PATH. Usage:
    demux_gate.py <fixtures_dir> [--dump path/to/basis_demux_dump]
Exit 0 if every fixture's checks pass, 1 otherwise.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

PTS_TOLERANCE_US = 1

# dumper codec name -> the ffprobe codec_name(s) for the same thing
CODEC_ALIASES = {
    "h264": {"h264"},
    "h265": {"hevc", "h265"},
    "vp9": {"vp9"},
    "av1": {"av1"},
    "aac": {"aac"},
    "opus": {"opus"},
    "mp3": {"mp3", "mp3float"},
    "lpcm": {"pcm_bluray", "pcm_s16le", "pcm_s24le", "pcm_s16be", "pcm_s24be"},
}
# ffprobe bitstream filter that reframes a stream into the sink's delivered form
PACKET_FILTERS = {"h264": "h264_mp4toannexb", "h265": "hevc_mp4toannexb", "hevc": "hevc_mp4toannexb"}


def run(cmd: list[str]) -> str:
    p = subprocess.run(cmd, capture_output=True, text=True)
    if p.returncode != 0:
        tail = "\n".join((p.stderr or "").strip().splitlines()[-8:])
        raise RuntimeError(f"{cmd[0]} exit {p.returncode}: {tail}")
    return p.stdout


def run_bin(cmd: list[str]) -> bytes:
    p = subprocess.run(cmd, capture_output=True)
    if p.returncode != 0:
        raise RuntimeError(f"{cmd[0]} exit {p.returncode}: "
                           f"{p.stderr.decode(errors='replace')[-400:]}")
    return p.stdout


def us(value) -> int | None:
    if value in (None, "N/A"):
        return None
    try:
        return round(float(value) * 1e6)
    except (TypeError, ValueError):
        return None


def probe_streams(media: Path) -> list[dict]:
    out = run(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(media)])
    return json.loads(out or "{}").get("streams", [])


def extradata_hash(media: Path, stream: str) -> str | None:
    """ffprobe's MD5 of the stream's extradata (decoder init data), or None."""
    out = run(["ffprobe", "-v", "error", "-select_streams", stream, "-show_streams",
               "-show_data_hash", "md5", "-of", "json", str(media)])
    for s in json.loads(out or "{}").get("streams", []):
        h = (s.get("extradata_hash") or "")
        if h.upper().startswith("MD5:"):
            return h[4:].strip().lower()
    return None


def probe_packets(media: Path, stream: str) -> list[dict]:
    out = run(["ffprobe", "-v", "error", "-select_streams", stream, "-show_packets",
               "-show_data_hash", "md5", "-of", "json", str(media)])
    pkts = []
    for pkt in json.loads(out or "{}").get("packets", []):
        digest = (pkt.get("data_hash") or "")
        if digest.upper().startswith("MD5:"):
            digest = digest[4:]
        pkts.append({"pts_us": us(pkt.get("pts_time")),
                     "dts_us": us(pkt.get("dts_time")),
                     "size": int(pkt.get("size", 0)),
                     "key": "K" in (pkt.get("flags") or ""),
                     "md5": (digest.strip().lower() or None)})
    return pkts


def filtered_md5s(media: Path, bsf: str, stream: str) -> list[str]:
    """Per-packet MD5 after reframing a stream to the sink's form (framemd5).

    stream is an ffmpeg selector ('v:0' / 'a:0'); the bitstream filter kind
    follows from it (v -> video, a -> audio).
    """
    kind = stream.split(":", 1)[0]
    text = run_bin(["ffmpeg", "-v", "error", "-i", str(media), "-map", f"0:{stream}",
                    "-c", "copy", f"-bsf:{kind}", bsf, "-f", "framemd5", "-"]).decode()
    out = []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = [p.strip() for p in line.split(",")]
        if len(parts) >= 6:
            out.append(parts[5].lower())
    return out


def dump(dumper: Path, media: Path) -> dict:
    out = run_bin([str(dumper), str(media)])
    return json.loads(out)


class Checks:
    def __init__(self):
        self.rows: list[tuple[str, bool | None, str]] = []

    def ok(self, name, cond, detail=""):
        self.rows.append((name, bool(cond), detail))

    def skip(self, name, detail=""):
        self.rows.append((name, None, detail))

    def failed(self):
        return [r for r in self.rows if r[1] is False]


def codec_matches(ours: str, theirs: str) -> bool:
    return theirs in CODEC_ALIASES.get(ours, {ours})


def check_track(c: Checks, d: dict, media: Path, kind: str) -> None:
    """kind is 'video' or 'audio'."""
    ann = d.get(kind)
    stream = "v:0" if kind == "video" else "a:0"
    ref_streams = [s for s in probe_streams(media)
                   if s.get("codec_type") == kind and not s.get("disposition", {}).get("attached_pic")]
    ref = ref_streams[0] if ref_streams else None

    if ref is None:
        # ffmpeg sees no such track: the demuxer must not have announced one.
        if ann is not None:
            c.ok(f"{kind}.absent", False, "demuxer announced a track ffmpeg does not see")
        return
    if ann is None:
        c.ok(f"{kind}.announced", False, f"ffmpeg sees {kind}, demuxer announced none")
        return

    c.ok(f"{kind}.codec", codec_matches(ann["codec"], ref.get("codec_name", "?")),
         f"ours={ann['codec']} ffmpeg={ref.get('codec_name')}")
    if kind == "video":
        c.ok("video.dimensions",
             ann.get("width") == ref.get("width") and ann.get("height") == ref.get("height"),
             f"ours={ann.get('width')}x{ann.get('height')} "
             f"ffmpeg={ref.get('width')}x{ref.get('height')}")
    else:
        # Announce metadata is checked even for LPCM (whose packets are skipped
        # below), so a wrong rate or channel count can't slip through.
        c.ok("audio.sample_rate",
             str(ann.get("sample_rate")) == str(ref.get("sample_rate")),
             f"ours={ann.get('sample_rate')} ffmpeg={ref.get('sample_rate')}")
        c.ok("audio.channels",
             ann.get("channels") == ref.get("channels"),
             f"ours={ann.get('channels')} ffmpeg={ref.get('channels')}")

    codec = ann["codec"]
    container_ts = d.get("demuxer") in {"ts"}

    # Extradata (decoder init) integrity: corrupted or mis-sliced CodecPrivate
    # leaves packet MD5s unchanged but breaks decoder init. Compare the announced
    # extradata hash against ffprobe's for codecs whose extradata framing matches
    # what the demuxer forwards (Opus OpusHead, AAC ASC). AV1's av1C and H.26x's
    # avcC/hvcC are reframed, so they aren't hash-comparable here.
    if codec in ("opus", "aac"):
        our_ed = (ann.get("extradata_md5") or "").lower()
        ref_ed = extradata_hash(media, stream)
        if ref_ed:
            # ffprobe has extradata, so ours must too: a demuxer that drops the
            # ASC/OpusHead breaks decoder init and must fail, not skip.
            c.ok(f"{kind}.extradata", bool(our_ed) and our_ed == ref_ed,
                 f"ours={our_ed[:12] or 'missing'} ffmpeg={ref_ed[:12]}")
        else:
            c.skip(f"{kind}.extradata", "ffprobe exposes no comparable extradata")

    aus = [a for a in d.get("access_units", []) if a["track"] == kind]

    # LPCM has no canonical packetisation (the demuxer and ffmpeg chunk it
    # differently), so per-packet count/pts/md5 are meaningless -- but emitting
    # nothing at all is still a regression, so require at least one frame.
    if codec == "lpcm":
        c.ok(f"{kind}.packets", bool(aus), "demuxer emitted no LPCM frames")
        return

    pkts = probe_packets(media, stream)

    c.ok(f"{kind}.count", len(aus) == len(pkts), f"ours={len(aus)} ffmpeg={len(pkts)}")

    if kind == "video":
        c.ok("video.keyframes",
             [a["key"] for a in aus] == [p["key"] for p in pkts],
             f"ours={sum(a['key'] for a in aus)} keyframes, ffmpeg={sum(p['key'] for p in pkts)}")

    # PTS/DTS in emit order -- ffprobe lists packets in demux order, exactly the
    # order the sink receives them. No sorting: it would turn the sequence check
    # into a multiset check and let a reordered stream pass. DTS is checked too so
    # a broken decode order (B-frame streams) can't slip through.
    n = min(len(aus), len(pkts))

    def _seq_check(field, our_vals, ref_vals):
        if not (n and all(v is not None for v in ref_vals) and all(v is not None for v in our_vals)):
            c.skip(f"{kind}.{field}", "reference or ours has partial/absent timestamps")
            return
        o, r = list(our_vals), list(ref_vals)
        if codec == "opus":
            # Opus CodecDelay: ffmpeg shifts timestamps by the encoder pre-skip
            # while the demuxer emits raw block times. Compare relative to the
            # first packet -- spacing must match, the origin convention may not.
            o = [x - o[0] for x in o]
            r = [x - r[0] for x in r]
        worst = max((abs(a - b) for a, b in zip(o, r)), default=0)
        c.ok(f"{kind}.{field}", worst <= PTS_TOLERANCE_US, f"worst delta {worst}us over {n}")

    our_pts_v = [a["pts_us"] for a in aus[:n]]
    our_dts_v = [a.get("dts_us") for a in aus[:n]]
    _seq_check("pts", our_pts_v, [p["pts_us"] for p in pkts[:n]])
    # DTS only when the demuxer actually provides decode timestamps distinct from
    # PTS. The sink contract lets a demuxer pass PTS for both (the MPEG-TS lane
    # does), and comparing that against ffmpeg's reordered DTS would be a false
    # failure; where we do emit a real DTS (e.g. MP4 B-frames), it must match.
    if our_dts_v != our_pts_v:
        _seq_check("dts", our_dts_v, [p.get("dts_us") for p in pkts[:n]])

    # Payload MD5.
    if codec == "aac" and container_ts:
        # AAC in TS is ADTS-framed; the sink strips the 7-byte header, so strip
        # it on the reference side too before hashing.
        try:
            their = filtered_md5s(media, "aac_adtstoasc", stream)
        except RuntimeError as ex:
            # A reframe failure is a gate failure, not a skip — the payload
            # comparison is the whole point of this row.
            c.ok(f"{kind}.md5", False, f"adts reframe failed: {ex}")
            return
        m = min(len(aus), len(their))
        c.ok(f"{kind}.md5",
             len(their) > 0 and len(aus) == len(their) and [a["md5"] for a in aus[:m]] == their[:m],
             f"{sum(1 for a, t in zip(aus, their) if a['md5'] != t)} of {m} differ "
             f"(ours={len(aus)} ref={len(their)})")
    elif ann.get("payload_is_container_form"):
        our = [a["md5"] for a in aus]
        their = [p["md5"] for p in pkts]
        m = min(len(our), len(their))
        c.ok(f"{kind}.md5", m > 0 and our[:m] == their[:m],
             f"{sum(1 for x, y in zip(our, their) if x != y)} of {m} differ")
    elif codec in PACKET_FILTERS:
        # H.26x: reframe ffprobe to Annex B; keyframes exempt (inline SPS/PPS).
        try:
            their = filtered_md5s(media, PACKET_FILTERS[codec], stream)
        except RuntimeError as ex:
            c.ok(f"{kind}.md5", False, f"bsf reframe failed: {ex}")
            return
        m = min(len(aus), len(their))
        mism = [i for i in range(m) if not aus[i]["key"] and aus[i]["md5"] != their[i]]
        # Require a non-empty, equal-count reframe — an empty `their` would
        # otherwise leave `not mism` True and pass without comparing anything.
        c.ok(f"{kind}.md5", len(their) > 0 and len(aus) == len(their) and not mism,
             f"{len(mism)} non-key AUs differ of {m} (ours={len(aus)} ref={len(their)})")
    else:
        c.skip(f"{kind}.md5", f"no comparison rule for {codec}")


def gate(fixtures: Path, dumper: Path) -> int:
    media = sorted(p for p in fixtures.iterdir()
                   if p.suffix.lower() in {".mp4", ".m4a", ".ts", ".m2ts", ".webm", ".wav",
                                           ".opus", ".ogg", ".mp3"})
    if not media:
        print(f"no fixtures in {fixtures}")
        return 1

    any_fail = False
    for m in media:
        c = Checks()
        try:
            d = dump(dumper, m)
        except Exception as ex:
            print(f"[FAIL] {m.name}: dumper: {ex}")
            any_fail = True
            continue
        c.ok("demuxer.ran", d.get("run_rc") == 0 and not d.get("error"),
             d.get("error") or "")
        check_track(c, d, m, "video")
        check_track(c, d, m, "audio")

        fails = c.failed()
        tag = "FAIL" if fails else "ok"
        if fails:
            any_fail = True
        checked = sum(1 for _, v, _ in c.rows if v is not None)
        print(f"[{tag:>4}] {m.name}  ({checked} checks)")
        for name, v, detail in c.rows:
            if v is False:
                print(f"         FAIL {name}: {detail}")
        for name, v, detail in c.rows:
            if v is None and detail:
                print(f"         skip {name}: {detail}")

    print()
    print("PASS: all fixtures conform" if not any_fail
          else "FAIL: a fixture's demux output diverged from ffprobe")
    return 1 if any_fail else 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("fixtures", type=Path)
    ap.add_argument("--dump", type=Path, default=None,
                    help="path to basis_demux_dump (default: ./build/basis_demux_dump[.exe])")
    args = ap.parse_args()

    dumper = args.dump
    if dumper is None:
        base = Path(__file__).parent / "build" / "basis_demux_dump"
        dumper = base if base.exists() else base.with_suffix(".exe")
    if not dumper.exists():
        print(f"dumper not found: {dumper} (run ./build.sh)")
        return 1

    return gate(args.fixtures, dumper)


if __name__ == "__main__":
    sys.exit(main())
