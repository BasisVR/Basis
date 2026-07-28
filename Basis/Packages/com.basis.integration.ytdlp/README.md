# Basis yt-dlp Integration

Resolves **page URLs** — a YouTube or Twitch watch page — into the actual stream(s)
the [Basis Media Player](https://github.com/BasisVR/BasisMediaPlayer) can open, using yt-dlp
running in-process. An **optional bolt-on**: the player works without it; this just
adds common-site resolution.

## How it works

At load it registers a resolver on the player's `BasisMediaUrlRouter`. Any player URL
field — e.g. `BasisMediaPlayerStreaming.StreamUrl` — then steers each URL:

- A **directly-playable** URL (a transport scheme, or an HTTP URL whose path ends in
  `.mp4`/`.m4s`/`.ts`/`.m3u8`/`.mpd`) loads directly, untouched.
- A **page URL** (HTTP, no media extension) is handed to yt-dlp, which extracts the
  best playable format(s); the result is loaded into the player.

The player core has no reference to this package — the only link is the router seam —
so it can be added or removed cleanly (see *Removing it*).

## What it resolves

| Source | yt-dlp returns | Played as |
|---|---|---|
| YouTube VOD (>360p) | split video-only (capability-selected H.264/VP9/AV1) + audio-only (AAC, or Opus fallback) | split stream, real-time paced (on-demand) |
| YouTube / Twitch live | single HLS playlist | live |
| Progressive / muxed (≤360p) | one muxed stream | delivery auto-detected |

**Codec selection: H.264 everywhere; VP9 and AV1 up to 2160p where the platform
decodes them.** YouTube's above-1080p ladder is VP9 (AV1 alongside on popular
uploads), so format selection asks the player whether this machine hardware-decodes
each codec (Windows: the decoder MFT — the Store extension or a vendor one — plus a
GPU with hardware decode; Quest: VP9 always, AV1 on Quest 3) and, where it does,
picks rungs up to 4K — SDR 8-bit ladders only. At equal height `avc1` wins, then
`av01` over `vp9` (better bitrate at 4K), so a VP9/AV1 rung is only chosen where it
offers more resolution than any available `avc1`; where neither decodes, the video
stays `avc1` capped at 1080p and audio uses `mp4a` when present, otherwise the WebM
`opus` fallback. Above ~360p YouTube serves video
and audio separately, so those resolve to a
[split stream](https://github.com/BasisVR/BasisMediaPlayer#split-stream-separate-video--audio)
the player syncs on one clock.

## Usage

Drop a YouTube/Twitch link into the `StreamUrl` field of a `BasisMediaPlayerStreaming`
component — or call `player.LoadUrl(pageUrl)` directly, which routes the same way.
That's it; resolution and loading are automatic.

Programmatic, if you need the resolver directly:

```csharp
// Resolve a page URL and load it into the player:
BasisYtDlpResolver.ResolveAndPlay(player, "https://www.youtube.com/watch?v=…");

// Or resolve without loading (e.g. to inspect / cache the result):
BasisMediaSource source = await BasisYtDlpResolver.ResolveSourceAsync(pageUrl);
```

## Requirements

- **`com.basis.mediaplayer`** (the player) and **`com.yewnyx.ytdlp`** (the in-process
  yt-dlp native plugin — an embedded CPython runtime + yt-dlp). This package compiles
  to nothing unless both are present (asmdef define constraints
  `BASIS_MEDIAPLAYER_EXISTS` + `YTDLP_EXISTS`).
- **Windows** today — the yt-dlp native plugin is Windows-first.
- **Expect a few seconds per page-URL load.** The *first* resolution also unpacks the
  bundled Python runtime (tens of MB) to persistent storage — a one-off, noticeably slow
  step that later loads skip. But every page-URL load still spends a few seconds resolving
  in-process (network round-trips plus YouTube's JS signature challenge), not just the
  first. Resolution runs off the main thread, and nothing is surfaced on the player during
  the gap, so a consuming UI should show its own loading state.

## Removing it

Remove the package and page-URL resolution goes away: the player still plays every
direct stream URL, but YouTube/Twitch links no longer resolve — loading one reports
that the resolver is needed, rather than failing silently. Nothing else changes.

## Trust

Resolved stream URLs pass the player's host trust gate (`BasisMediaPlayerSecurity`)
like any other URL. Interactive consent for the page URL itself is a UI-layer concern
and is not driven from here.

## Known gap

A direct HTTP stream with **no file extension** can't be told apart from a page URL, so
with this package installed it is sent to yt-dlp and fails, rather than loading
directly. Give direct HTTP streams a recognised extension, or use a transport scheme
(`rtsp`/`rtmp`). See the
[player README](https://github.com/BasisVR/BasisMediaPlayer#page-urls-optional-resolver-package).
