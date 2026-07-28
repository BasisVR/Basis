# Testing the yt-dlp integration

How to verify page-URL resolution (YouTube, Twitch, and friends) after changing this package.
The base player's guide —
[`com.basis.mediaplayer/TESTING.md`](../com.basis.mediaplayer/TESTING.md) — covers everything
downstream of resolution and deliberately uses **direct stream URLs only**; page URLs belong
here, because they only work when this integration (and its `com.yewnyx.ytdlp` dependency)
is installed.

## Prerequisites

- `com.basis.mediaplayer` and `com.yewnyx.ytdlp` present — this package compiles to nothing
  without both (asmdef define constraints), so first confirm it's actually active: loading a
  page URL without it reports that a resolver is needed.
- **Windows** — the yt-dlp native plugin is Windows-first.
- The base player passing its own matrix. If direct streams are broken, nothing here will
  tell you anything about the resolver.

## Rule zero: separate resolver bugs from player bugs

Resolution and playback are different failure domains. Before blaming either:

```csharp
// Inspect what resolution actually produced, without playing it:
BasisMediaSource source = await BasisYtDlpResolver.ResolveSourceAsync(pageUrl);
```

If the resolved `Url`/`AudioUri` look sane, load them **directly** in the player — if that
also fails, it's a player bug and the base guide applies. If resolution itself fails,
remember the other moving part: **the sites change constantly.** YouTube rotates signature
challenges; a resolution failure on unchanged code is usually an aged yt-dlp, not your
regression. Confirm the yt-dlp runtime is current before filing anything.

## What to test

Pick stable, public content you have the right to view — long-standing official uploads
(e.g. the Blender Foundation films) beat trending links that vanish. Twitch: any live channel
from the front page, plus a recent VOD from the same channel.

| Scenario | Expected |
| --- | --- |
| YouTube VOD, >360p | Resolves to a **split stream** (capability-selected H.264/VP9/AV1 video-only + AAC or Opus-fallback audio-only), plays paced as on-demand, A/V locked |
| YouTube VOD, ≤360p (or format-forced) | Single muxed stream, delivery auto-detected |
| YouTube live | Single HLS playlist, joining near the live edge. Worth re-running after any HLS change: unlike Twitch, the media playlist lists the entire DVR window — thousands of segments, several MB of text, segment URIs past 1 KB — so it is the lane that exercises the parser's size limits |
| Twitch live | HLS live; join near the live edge |
| Twitch VOD | HLS VOD |
| Format selection, VP9-capable platform without AV1 decode | A 4K upload resolves to the **VP9 video-only rung up to 2160p** (WebM or MP4 carriage) + `mp4a` audio as a split stream; a 1080p-max upload still resolves to `avc1` (avc1 wins at equal height) |
| Format selection, AV1-capable platform | Where both the AV1 and VP9 probes pass (hardware AV1: RTX 30+/RX 6000+/Arc on Windows, Quest 3), a popular 4K upload carrying both ladders resolves to the **av01 rung** over vp9 at equal height (`av01.0.*.08`, MP4 or WebM carriage) — 8-bit SDR only |
| Format selection, no VP9/AV1 decode | On a machine where a probe fails (no Store extension, or no hardware decode on the GPU), that codec's rungs are never offered: no AV1 → VP9 4K still resolves; neither → selection stays `avc1` ≤1080p |
| HDR upload | Resolves to the parallel **SDR** ladder (vp9/av01 8-bit, or avc1), never an HDR/10-bit rung |
| Metadata | Title / uploader / thumbnail appear on the player after resolve |
| First-ever resolution | One-off multi-second pause while the bundled Python runtime unpacks — expected, not a hang; later loads skip it |
| Every resolution | A few seconds of in-process resolving is normal; the player shows nothing during the gap by design |
| Direct URLs with this package installed | `.mp4`/`.ts`/`.m3u8`/transport-scheme URLs load **untouched** — no resolver round-trip |
| Extensionless direct HTTP stream | Known gap: routed to yt-dlp and fails; documented, not a regression |
| Invalid / dead page URL | Clean failure surfaced to the player — no crash, no silent hang |
| Package removed | Page URLs report "resolver needed"; direct streams unaffected |

## Subtitles (sidecar caption tracks)

YouTube captions are sidecar files, not in-band data: the resolver surfaces them as
`BasisSubtitleTrack` metadata and the player fetches/parses the selected one. The panel's
"Subtitles" dropdown only exists while the CC toggle is on **and** the loaded media actually
has tracks; a missing dropdown on a captionless video is the feature working. Selection is
per-viewer — nothing syncs.

For content, pick VODs you can verify against YouTube's own caption display: one with
uploader subtitles in several languages, one with only auto-generated captions, and one
Japanese-language video (checks the CJK font fallbacks and per-character wrapping).

| Scenario | Expected |
| --- | --- |
| VOD with uploader subtitles | Tracks listed without "(auto)"; cues match YouTube's timing; multi-line cues keep their line breaks; opacity sliders apply |
| VOD with auto-captions only | Track labelled "(auto)"; clean sequential cues — no rolling duplicates or overlap flicker |
| VOD with both | Uploader tracks first; no duplicate languages; original + system language only — never the full auto-translation list |
| Japanese track | CJK renders (global TMP fallbacks) and wraps per-character; repeat once on Quest |
| Very long unbroken auto-caption line | Wraps inside the caption background; never overflows the screen edges |
| YouTube live / captionless VOD / direct stream URL | Dropdown entirely absent; panel identical to a build without this feature |
| Seek / stop→play | Cue matches the playback position within a frame; nothing to reset |
| Track switch mid-play | Old cue clears immediately; new track's cues take over |
| Back to "CC (embedded)" | In-band captions resume (current cue reappears at its next text change — known dedup nit, not a regression) |
| New URL while a track is selected | Selection reverts to embedded; dropdown repopulates after the new resolve |
| Two players / second client | Selections independent; same track list on every client; nothing synced |
| Network killed mid-fetch | Redacted warning in the console, selection reverts to embedded, no crash |

## Security and networking

- Resolved stream URLs pass `BasisMediaPlayerSecurity` like any other URL — a resolver change
  must never become a way around the host gate. Negative-test with a page URL crafted to
  resolve somewhere refused (or verify the gate log lines fire on the resolved hosts). The
  same applies to subtitle track fetches: they run through the identical gate, with HTTP
  redirects refused outright.
- In multiplayer, the **page URL** syncs and each client resolves independently. Two clients
  on the same video may legitimately hold different CDN URLs; state (play/pause/position
  intent) must still agree. Test with two clients minimum.
- Resolution runs off the main thread — the Editor should stay responsive during the resolve
  gap; a frozen editor during resolution is a regression.

## Reporting

Base-guide report contents apply, plus: the page URL, the yt-dlp runtime version, and —
for resolution failures — whether `ResolveSourceAsync` fails too or only playback of the
resolved result.
