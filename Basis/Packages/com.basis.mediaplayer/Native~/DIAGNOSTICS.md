# Diagnostics

How to read the numbers a playback run records. [`TESTING.md`](TESTING.md)
says which runs to do.

| Source | Written by | Rate | Contents |
| --- | --- | --- | --- |
| **Engine capture** | the engine | every 100 ms, written when the session closes | Stage counters, drop counters, buffer depth and lag. Enable with the descriptor's `diag_csv`, the player's **Engine capture** (`BasisMediaEngine.csv` by default), or `bm-probe play --csv` |
| **Frame capture** | `BasisMediaPlayerDiagnostics` | every Unity frame | Render cadence, how long each frame stayed on screen, the audio pull rate, what the speaker outputs mixed, and the device's audio chain. Written to `BasisMediaFrames.csv` under `Application.persistentDataPath` |
| **Debug window** | `Basis > Debug > Media Player` | every 250 ms | The same figures live, stage by stage |

The two captures use different clocks. The engine's `wall_us` counts from
session open and stops while paused; the frame capture's `unity_time` is
Unity's unscaled time. Line them up by event, not by timestamp.

## Frame capture columns

New columns are only ever added at the end. This line must match
`BasisMediaPlayerDiagnostics.Header()`:

```
unity_time,frame,frame_dt_ms,state,position_us,duration_us,banked_ms,decoded,decoded_delta,presented,presented_delta,frames_held,video_w,video_h,has_texture,audio_pulled,audio_pulled_delta,stream_rate,stream_channels,dsp_rate,dsp_buffer,dsp_buffers,listener_paused,sync_ppm,subtitle_track,caption_len,out_bound,out_playing,out_consumed,out_consumed_delta,out_peak,out_rms,out_latency_us,av_offset_us
```

| Column | Meaning | Healthy |
| --- | --- | --- |
| `state` | 0 idle, 1 opening, 2 buffering, 3 playing, 4 paused, 5 ended, 6 error | Reaches 3 and stays; never 6; on-demand runs end at 5 |
| `position_us` | The session clock | 1 s per second, within ±2%. **Never goes backwards** except at a seek |
| `banked_ms` | Buffered ahead | At or above the configured depth while playing. Falling towards 0 means the source is starving |
| `decoded_delta` | Frames decoded this Unity frame | Sums to the source frame rate over a window |
| `presented_delta` | Frames shown this Unity frame | Matches `decoded_delta` over a window; lower means frames are dropped after decoding |
| `frames_held` | Unity frames the last new frame stayed up (0 when none was new) | Constant: 24 fps at 72 Hz holds 3, 3, 3. At least 99% at that value on a steady stream |
| `frame_dt_ms` | Unity's frame time | The display interval (13.9 ms at 72 Hz, 16.7 at 60). To judge render-thread cost, count missed refreshes and compare the mean with a control in the same sitting; percentiles vary between sittings |
| `audio_pulled_delta` | Source audio frames consumed this Unity frame | `audio_pulled` advances at `stream_rate`, whatever `dsp_rate` is |
| `stream_rate`, `stream_channels` | The source's audio format | Non-zero once audio starts; channels match the source |
| `dsp_rate`, `dsp_buffer`, `dsp_buffers` | The device's audio settings | Context only (a Quest runs at 24 kHz) |
| `sync_ppm` | Shared playback's speed adjustment, parts per million | 0 unless catching up with the owner |
| `has_texture` | The output texture exists | 1 after the first frame, and stays 1 |
| `listener_paused` | `AudioListener.pause` | 0; at 1 the audio columns mean nothing |
| `subtitle_track`, `caption_len` | Selected subtitle file (-1 for in-video captions), current caption length | Context only |
| `out_bound` | AudioSources the audio component bound | 1 for stereo; one per source channel on a surround set (6 for 5.1, 8 for 7.1). **0 with audio playing means nothing is listening** |
| `out_playing` | Any bound output playing | 1 while playing |
| `out_consumed_delta` | Frames the first output mixed this Unity frame | `out_consumed` advances at `dsp_rate`; lower is audible break-up |
| `out_peak`, `out_rms` | Level of the first output's last block | Non-zero for anything but silence |
| `out_latency_us` | Estimated delay from pull to speaker | The device's DSP buffering plus a fixed 20 ms; used for A/V compensation |
| `av_offset_us` | Frame time minus the audio playhead, µs; `-2147483648` when unknown | On rows where `presented_delta` > 0: median within a few ms, nothing later than 40 ms plus a refresh. Audio pull blocks (about 21 ms at 48 kHz) widen the reading by up to that much |

## Grading a run

1. `state` reached 3, never 6, and 5 for a finite source.
2. `position_us` advanced at 1 s/s within ±2%, with no backwards step outside a
   seek.
3. `presented` is within a few frames of `decoded`.
4. `frames_held` at the ideal value for at least the threshold share, excluding
   the first seconds after a live join.
5. `audio_pulled` advanced at `stream_rate` within ±1%.
6. `banked_ms` never reached 0 while playing.
7. With audio: `out_bound` non-zero, `out_consumed` at `dsp_rate` within ±2%,
   and `out_peak` non-zero at some point.

When a step fails, the engine capture's drop counters show where, and the
event log (Editor log, debug window) shows why.

## Smoke test

`Basis > Tools > Media Player > Run Smoke Test`, or in batch:

```
Unity -batchmode -projectPath <project> -logFile - \
      -executeMethod BasisMediaSmokeTest.RunBatch
```

It plays a source through a stereo output set and grades the frame capture
against the steps above, skipping the first 2 s, and exits 0 on a pass and 1 on
a fail, with the figures in the log. Step 6 is reported but does not fail the
run, and step 4 takes the most common hold as the ideal. `BASIS_SMOKE_URL` sets the source (default: the engine's A/V
fixture), `BASIS_SMOKE_SECONDS` the length, and `BASIS_SMOKE_LIVE` marks a live
source. Frame holds are reported but only fail the run with
`BASIS_SMOKE_STRICT_HOLDS=1`, since editor frame timing is irregular; use it
for builds and devices.

When a column changes, update this file in the same commit.
