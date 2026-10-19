#if BASIS_MEDIAPLAYER_EXISTS && YTDLP_EXISTS
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.Integration.YtDlp.Core;
using UnityEngine;

namespace Basis.Integration.YtDlp
{
    /// <summary>
    /// Bridges the in-process yt-dlp resolver to the Rust-engine player: turns a
    /// page URL into the stream (or pair of streams) it can open, then opens them.
    /// Format selection is <see cref="YtDlpExtraction"/>'s, shared with the native
    /// player's adapter; this is the mapping onto <see cref="BasisResolvedMedia"/>
    /// and the player.
    ///
    /// The engine plays a video-only stream against a separate audio-only one, so
    /// adaptive ladders resolve to their real rungs rather than the muxed fallback.
    /// </summary>
    public static class BasisYtDlpResolver
    {
        /// <summary>
        /// Resolves <paramref name="pageUrl"/> and opens the result on
        /// <paramref name="player"/>. Call from the main thread — the continuation
        /// resumes there via Unity's synchronization context, so the open is
        /// main-thread safe.
        /// </summary>
        public static async void ResolveAndPlay(
            BasisMediaPlayer player,
            string pageUrl,
            Action<Exception> onError = null,
            CancellationToken cancellationToken = default)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (string.IsNullOrEmpty(pageUrl))
            {
                BasisDebug.LogWarning("[BasisMedia] yt-dlp resolve called with an empty URL.", BasisDebug.LogTag.Video);
                return;
            }

            if (!NeedsResolution(pageUrl))
            {
                player.Open(pageUrl, null);
                return;
            }

            // Capture the load generation before the async resolve. If another open
            // bumps it while yt-dlp runs, this resolve is stale — drop it rather than
            // overwrite the newer load (open A then B: A must not win if it finishes
            // last).
            int loadGeneration = player.LoadGeneration;

            try
            {
                BasisResolvedMedia media = await ResolveMediaAsync(pageUrl, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (loadGeneration != player.LoadGeneration) return; // superseded
                player.OpenResolved(media);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Log the exception type, not its message — yt-dlp and extractor
                // messages embed the raw page URL and its tokens, which would defeat
                // the redaction.
                BasisDebug.LogError(
                    $"[BasisMedia] yt-dlp resolution failed for '{BasisMediaUrlRouter.Redact(pageUrl)}' ({ex.GetType().Name}).",
                    BasisDebug.LogTag.Video);
                if (loadGeneration == player.LoadGeneration) onError?.Invoke(ex);
            }
        }

        /// <summary>
        /// Resolves a page URL to the streams and metadata to open, without opening
        /// anything. Extraction runs on a thread-pool thread inside yt-dlp.
        /// </summary>
        public static async Task<BasisResolvedMedia> ResolveMediaAsync(
            string pageUrl,
            CancellationToken cancellationToken = default)
        {
            YtDlpExtractionResult resolved = await YtDlpExtraction.ResolveAsync(
                pageUrl,
                vp9Decodes: HardwareDecodes("vp9"),
                av1Decodes: HardwareDecodes("av1"),
                isDirectlyPlayable: BasisMediaUrlRouter.IsDirectlyPlayable,
                cancellationToken: cancellationToken);

            return new BasisResolvedMedia
            {
                Url = resolved.Selection.Url,
                AudioUrl = resolved.Selection.AudioUrl,
                Liveness = LivenessFor(resolved.Selection.Delivery),
                // The URL the viewer asked for, not the per-client CDN endpoint it
                // resolved to: that is what shared playback synchronises.
                SourceUrl = pageUrl,
                Title = resolved.Title,
                Uploader = resolved.Uploader,
                ThumbnailUrl = resolved.ThumbnailUrl,
                Duration = resolved.Duration,
                SubtitleTracks = SubtitleTracksFor(resolved.SubtitleTracks),
                Provider = "ytdlp",
            };
        }

        /// <summary>
        /// Whether a URL needs yt-dlp, or is already something the player opens
        /// directly. The classification belongs to the player
        /// (<see cref="BasisMediaUrlRouter.IsDirectlyPlayable"/>) so there is one
        /// source of truth, and it differs per engine — this one demuxes containers
        /// the native player does not.
        /// </summary>
        internal static bool NeedsResolution(string url)
            => !string.IsNullOrEmpty(url) && !BasisMediaUrlRouter.IsDirectlyPlayable(url);

        /// <summary>
        /// Whether the engine reports a hardware route for a codec. The >1080p rungs
        /// are gated on this rather than on decoding the codec at all, because the
        /// engine refuses software routes above 1080p60 anyway — offering a 4K rung a
        /// software decoder would decline just turns a playable video into a failure.
        /// </summary>
        private static bool HardwareDecodes(string codec)
        {
            BmCapabilitySet caps = BasisMediaPlayer.EngineCapabilities;
            if (caps?.video == null) return false;
            for (int i = 0; i < caps.video.Length; i++)
            {
                BmVideoCap cap = caps.video[i];
                if (cap == null) continue;
                if (!string.Equals(cap.codec, codec, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(cap.route, "hardware", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static BmLiveness LivenessFor(YtDlpDelivery delivery)
        {
            switch (delivery)
            {
                case YtDlpDelivery.Live: return BmLiveness.Live;
                case YtDlpDelivery.OnDemand: return BmLiveness.Vod;
                default: return BmLiveness.Auto;
            }
        }

        private static List<BasisSubtitleTrack> SubtitleTracksFor(List<YtDlpSubtitleEntry> entries)
        {
            if (entries == null || entries.Count == 0) return null;
            var tracks = new List<BasisSubtitleTrack>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                YtDlpSubtitleEntry entry = entries[i];
                tracks.Add(new BasisSubtitleTrack
                {
                    Url = entry.Url,
                    Format = entry.Format,
                    Language = entry.Language,
                    Label = entry.Label,
                    IsAutoGenerated = entry.IsAutoGenerated,
                });
            }
            return tracks;
        }
    }

    /// <summary>
    /// Registers the yt-dlp resolver with the v2 router at startup, so any player
    /// URL steers page URLs through yt-dlp while directly-playable streams open
    /// unchanged. The player holds no reference to this package; removing the
    /// package removes the registration, with nothing dangling.
    /// </summary>
    internal static class BasisYtDlpInstaller
    {
        private static BasisYtDlpVideoResolver installed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (installed != null) return; // idempotent across domain reloads
            installed = new BasisYtDlpVideoResolver();
            BasisMediaUrlRouter.Register(installed);
        }
    }

    internal sealed class BasisYtDlpVideoResolver : IBasisVideoResolver
    {
        // yt-dlp is the generic last-resort resolver — it can attempt almost any page
        // URL, so it registers at the lowest priority and only sees URLs no
        // more-specific resolver claimed. CanResolve stays broad on purpose: there is
        // no cheap, non-blocking way to know which of yt-dlp's sites a URL belongs to
        // without running extraction, so the fallback takes any non-directly-playable
        // URL and surfaces failures via the async resolve path.
        public int Priority => int.MinValue;

        public bool CanResolve(string url) => BasisYtDlpResolver.NeedsResolution(url);

        public bool TryResolve(BasisMediaPlayer player, string url)
        {
            // We claim ownership (return true), so the open stops here and relies on us
            // to report failure.
            BasisYtDlpResolver.ResolveAndPlay(player, url, onError: player.ReportLoadError);
            return true;
        }
    }
}
#endif
