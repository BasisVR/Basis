#!/usr/bin/env bash
#
# Generate synthetic demux fixtures with ffmpeg: a static Basis-logo video track
# plus a tone (multichannel where the fixture needs it). Content is irrelevant to
# demuxing -- this exercises container/packet parsing (PTS, sizes, keyframes,
# sample tables, PAT/PMT, EBML), so a static image and a tone are ideal: tiny and
# deterministic. Nothing is committed; CI regenerates these each run.
#
#   ./gen_fixtures.sh <out_dir> [logo.png]
#
# Codecs whose encoder the local ffmpeg lacks are skipped with a note, so the gate
# covers whatever the runner's ffmpeg supports.
set -euo pipefail

out="${1:?usage: gen_fixtures.sh <out_dir> [logo.png]}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
logo="${2:-$here/../../Basis/Images/BasisLogo.png}"
mkdir -p "$out"

[ -f "$logo" ] || { echo "logo not found: $logo"; exit 1; }
command -v ffmpeg >/dev/null 2>&1 || { echo "ffmpeg not found on PATH"; exit 1; }

has_enc() { ffmpeg -hide_banner -encoders 2>/dev/null | grep -qE "[[:space:]]$1[[:space:]]"; }

DUR=2
FPS=25
# Static logo scaled to even dimensions (libx264/vp9 need mod-2), constant SAR.
VSRC=(-loop 1 -i "$logo")
VF="scale=320:240,setsar=1,format=yuv420p"
# Stereo 440 Hz tone.
ASTEREO=(-f lavfi -i "sine=frequency=440:sample_rate=48000")
# 5.1 tone, a distinct pitch per channel (FL FR FC LFE BL BR).
A51=(-f lavfi -i "aevalsrc=0.2*sin(220*2*PI*t)|0.2*sin(277*2*PI*t)|0.2*sin(330*2*PI*t)|0.1*sin(55*2*PI*t)|0.2*sin(440*2*PI*t)|0.2*sin(554*2*PI*t):channel_layout=5.1:s=48000")

common_v=(-t "$DUR" -r "$FPS" -vf "$VF" -g "$FPS")
q="-v error -y"

made=()
note() { echo "  + $1"; made+=("$1"); }
skip() { echo "  - $1 (skipped: $2)"; }

# ---- MP4: H.264 + AAC, three moov layouts ---------------------------------
if has_enc libx264; then
    ffmpeg $q "${VSRC[@]}" "${ASTEREO[@]}" "${common_v[@]}" -c:v libx264 \
        -c:a aac -ac 2 -shortest -movflags +faststart "$out/h264_aac_faststart.mp4"
    note "h264_aac_faststart.mp4"
    # No +faststart => ffmpeg leaves moov after mdat (trailing-moov path).
    ffmpeg $q "${VSRC[@]}" "${ASTEREO[@]}" "${common_v[@]}" -c:v libx264 \
        -c:a aac -ac 2 -shortest "$out/h264_aac_tmoov.mp4"
    note "h264_aac_tmoov.mp4"
    # Fragmented (moof/mdat, empty moov).
    ffmpeg $q "${VSRC[@]}" "${ASTEREO[@]}" "${common_v[@]}" -c:v libx264 \
        -c:a aac -ac 2 -shortest -movflags +frag_keyframe+empty_moov "$out/h264_aac_frag.mp4"
    note "h264_aac_frag.mp4"
    # MPEG-TS: PAT/PMT/PES + ADTS audio.
    ffmpeg $q "${VSRC[@]}" "${ASTEREO[@]}" "${common_v[@]}" -c:v libx264 \
        -c:a aac -ac 2 -shortest -f mpegts "$out/h264_aac.ts"
    note "h264_aac.ts"
else
    skip "H.264 fixtures" "no libx264"
fi

# ---- MP4: HEVC + AAC (hvcC) -----------------------------------------------
if has_enc libx265; then
    ffmpeg $q "${VSRC[@]}" "${ASTEREO[@]}" "${common_v[@]}" -c:v libx265 -tag:v hvc1 \
        -c:a aac -ac 2 -shortest -movflags +faststart "$out/hevc_aac.mp4"
    note "hevc_aac.mp4"
else
    skip "HEVC fixture" "no libx265"
fi

# ---- VP9 (vp09 in MP4, and V_VP9 in WebM video-only) ----------------------
if has_enc libvpx-vp9; then
    vp9=(-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 300k)
    ffmpeg $q "${VSRC[@]}" "${ASTEREO[@]}" "${common_v[@]}" "${vp9[@]}" \
        -c:a aac -ac 2 -shortest "$out/vp9_aac.mp4"
    note "vp9_aac.mp4"
    ffmpeg $q "${VSRC[@]}" "${common_v[@]}" "${vp9[@]}" -an "$out/vp9.webm"
    note "vp9.webm"
else
    skip "VP9 fixtures" "no libvpx-vp9"
fi

# ---- AV1 (av01 in MP4, V_AV1 in WebM), whichever encoder exists ------------
av1=""
if has_enc libsvtav1; then av1="-c:v libsvtav1 -preset 12"
elif has_enc libaom-av1; then av1="-c:v libaom-av1 -cpu-used 8"; fi
if [ -n "$av1" ]; then
    ffmpeg $q "${VSRC[@]}" "${ASTEREO[@]}" "${common_v[@]}" $av1 \
        -c:a aac -ac 2 -shortest "$out/av1_aac.mp4"
    note "av1_aac.mp4"
    ffmpeg $q "${VSRC[@]}" "${common_v[@]}" $av1 -an "$out/av1.webm"
    note "av1.webm"
else
    skip "AV1 fixtures" "no libaom-av1 / libsvtav1"
fi

# ---- Opus in WebM (A_OPUS): muxed with VP9, and audio-only ----------------
if has_enc libopus; then
    if has_enc libvpx-vp9; then
        ffmpeg $q "${VSRC[@]}" "${ASTEREO[@]}" "${common_v[@]}" \
            -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 300k \
            -c:a libopus -ac 2 -shortest "$out/vp9_opus.webm"
        note "vp9_opus.webm"
    fi
    ffmpeg $q "${ASTEREO[@]}" -t "$DUR" -c:a libopus -ac 2 "$out/opus.webm"
    note "opus.webm"
    # Ogg Opus (.opus file): the Ogg demuxer, page framing + OpusHead/OpusTags.
    ffmpeg $q "${ASTEREO[@]}" -t "$DUR" -c:a libopus -ac 2 "$out/audio.opus"
    note "audio.opus"
else
    skip "Opus fixtures" "no libopus"
fi

# ---- Audio-only: AAC in M4A -----------------------------------------------
ffmpeg $q "${ASTEREO[@]}" -t "$DUR" -c:a aac -ac 2 "$out/aac.m4a"
note "aac.m4a"

# ---- Bare MP3: CBR and VBR (the header-driven, containerless path) ---------
if has_enc libmp3lame; then
    # CBR 128k: every frame the same length; exercises steady frame-sync.
    ffmpeg $q "${ASTEREO[@]}" -t "$DUR" -c:a libmp3lame -b:a 128k -ac 2 "$out/cbr.mp3"
    note "cbr.mp3"
    # VBR (-q:a 4): frame lengths vary, and lame writes a leading Xing/Info
    # header frame the demuxer drops (as ffmpeg does) rather than emitting.
    ffmpeg $q "${ASTEREO[@]}" -t "$DUR" -c:a libmp3lame -q:a 4 -ac 2 "$out/vbr.mp3"
    note "vbr.mp3"
    # VBR carrying a large ID3v2 cover-art tag: the tag is not part of the MPEG
    # stream, so the demuxer skips it before the first frame, and the Xing byte
    # count is measured from that first frame. This is the case where a leading
    # tag makes the seek origin diverge from the first-audio offset.
    ffmpeg $q "${ASTEREO[@]}" -i "$logo" -map 0:a -map 1:v -t "$DUR" \
        -c:a libmp3lame -q:a 4 -ac 2 -c:v copy -disposition:v attached_pic \
        -id3v2_version 3 -metadata:s:v title="cover" "$out/vbr_id3.mp3"
    note "vbr_id3.mp3"
    # MP3 muxed into MP4 (esds objectTypeIndication 0x6B): the mp4a-sample-entry
    # path must read the OTI and route to MP3, not assume AAC.
    ffmpeg $q "${ASTEREO[@]}" -t "$DUR" -c:a libmp3lame -b:a 128k -ac 2 -f mp4 "$out/mp3_in_mp4.mp4"
    note "mp3_in_mp4.mp4"
else
    skip "MP3 fixtures" "no libmp3lame"
fi

# ---- WAV: 16-bit stereo, 16-bit 5.1 ---------------------------------------
ffmpeg $q "${ASTEREO[@]}" -t "$DUR" -c:a pcm_s16le -ac 2 "$out/pcm16_stereo.wav"
note "pcm16_stereo.wav"
ffmpeg $q "${A51[@]}" -t "$DUR" -c:a pcm_s16le "$out/pcm16_51.wav"
note "pcm16_51.wav"

# ---- M2TS: Blu-ray LPCM 5.1 (the full-multichannel path) ------------------
if has_enc pcm_bluray; then
    ffmpeg $q "${A51[@]}" -t "$DUR" -c:a pcm_bluray -f mpegts -mpegts_m2ts_mode 1 "$out/lpcm51.m2ts"
    note "lpcm51.m2ts"
else
    skip "LPCM m2ts fixture" "no pcm_bluray"
fi

echo "generated ${#made[@]} fixtures in $out"
