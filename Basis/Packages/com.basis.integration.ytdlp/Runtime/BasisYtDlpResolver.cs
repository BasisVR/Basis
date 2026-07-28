#if BASIS_MEDIAPLAYER_EXISTS && YTDLP_EXISTS
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YtDlp;

namespace Basis.Integration.YtDlp
{
    /// <summary>
    /// Bridges the in-process yt-dlp resolver (com.yewnyx.ytdlp) to BasisMediaPlayer:
    /// turns a page URL (YouTube, Twitch, …) into the stream(s) the OS-codec engine
    /// can open, then loads them. The core media player has no reference to this type,
    /// so removing this package removes the feature with nothing dangling.
    ///
    /// Sources that deliver high-res video and audio as separate streams (YouTube VOD
    /// above ~360p: H.264 video-only + AAC audio-only) resolve to a split
    /// BasisMediaSource (Uri + AudioUri); single muxed sources (Twitch / live YouTube
    /// HLS, progressive ~360p) resolve to a single Uri.
    /// </summary>
    public static class BasisYtDlpResolver
    {
        // yt-dlp's first init unpacks the bundled Python stdlib + yt-dlp (tens of MB)
        // to persistentDataPath. Share one bootstrap Task across all callers so a
        // burst of requests triggers exactly one extraction.
        private static Task initTask;
        private static readonly object initLock = new object();

        /// <summary>
        /// Resolves <paramref name="pageUrl"/> to its stream(s) and loads them into
        /// <paramref name="player"/>. URLs already naming a transport scheme the player
        /// opens directly (rtsp/rtmp) are passed through unresolved. Call from the main
        /// thread — the continuation resumes there via Unity's synchronization context,
        /// so the final load is main-thread safe.
        /// </summary>
        /// <param name="onError">Invoked on the main thread if resolution fails. The
        /// core player exposes no externally-raisable error entry point, so the caller
        /// owns failure reporting.</param>
        public static async void ResolveAndPlay(
            BasisMediaPlayer player,
            string pageUrl,
            Action<Exception> onError = null,
            CancellationToken cancellationToken = default)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (string.IsNullOrEmpty(pageUrl))
            {
                BasisDebug.LogWarning("BasisYtDlpResolver.ResolveAndPlay called with empty URL.", BasisDebug.LogTag.Video);
                return;
            }

            if (!NeedsResolution(pageUrl))
            {
                player.LoadUrl(pageUrl);
                return;
            }

            // Capture the player's load generation before the async resolve. If another
            // LoadUrl bumps it while yt-dlp runs, this resolve is stale — drop it rather than
            // overwrite the newer load (load A then B: A must not win if it finishes last).
            int loadGen = player.LoadGeneration;

            try
            {
                BasisMediaSource source = await ResolveSourceAsync(pageUrl, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (loadGen != player.LoadGeneration) return; // superseded by a newer load
                // OPEN QUESTION (REQUIREMENTS.md §Trust & consent): the resolved CDN host
                // (googlevideo, Twitch edge) differs from pageUrl, so LoadSource runs both
                // Uri and AudioUri through BasisMediaPlayerSecurity.IsUrlAllowed — public
                // https hosts pass; a host allowlist or page-URL-approval policy is the
                // deferred decision.
                // Hand back the captured generation so the player matches this to the
                // metadata seed its LoadUrl planted, rather than one a racing load left.
                player.LoadResolvedSource(source, loadGen);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Log the exception type, not ex.Message — yt-dlp/extractor messages embed the
                // raw page URL (and its tokens), which would defeat the redaction above.
                BasisDebug.LogError($"yt-dlp resolution failed for '{BasisMediaUrlRouter.Redact(pageUrl)}' ({ex.GetType().Name}).", BasisDebug.LogTag.Video);
                // Only report if this resolve still owns the player — a newer LoadUrl since
                // capture supersedes us, and its outcome must not be clobbered by our failure.
                if (loadGen == player.LoadGeneration) onError?.Invoke(ex);
            }
        }

        /// <summary>
        /// Resolves a page URL to a BasisMediaSource — a split video+audio pair when the
        /// source offers separate avc1/mp4a streams, otherwise a single muxed Uri —
        /// without loading it. Extraction runs on a thread-pool thread inside yt-dlp.
        /// </summary>
        public static async Task<BasisMediaSource> ResolveSourceAsync(string pageUrl, CancellationToken cancellationToken = default)
        {
            await EnsureInitAsync();
            VideoInfo info = await YtDlpApi.ExtractAsync(pageUrl, opts: null, cancellationToken: cancellationToken);
            BasisMediaSource source = SelectSource(info);
            if (source == null || string.IsNullOrEmpty(source.Uri))
                throw new YtDlpException($"yt-dlp returned no player-ingestible format for '{BasisMediaUrlRouter.Redact(pageUrl)}'.");
            // Carry display metadata on the source: the player keys its metadata on
            // the page URL (matching what networking syncs, not the per-client CDN
            // endpoint) and shows the real title. Everything here comes from the
            // extraction that just ran — no extra fetch.
            source.Metadata = new BasisMediaMetadata
            {
                SourceUrl = pageUrl,
                Title = info.Title,
                Uploader = info.Uploader,
                ThumbnailUrl = info.Thumbnail,
                Duration = info.Duration.HasValue && info.Duration.Value > 0
                    ? TimeSpan.FromSeconds(info.Duration.Value)
                    : (TimeSpan?)null,
                SubtitleTracks = BuildSubtitleTracks(info),
                Provider = "ytdlp",
            };
            return source;
        }

        /// <summary>
        /// Resolution-only convenience returning just the primary stream URL (the video
        /// leg of a split source, or the single muxed URL). Callers needing the audio
        /// leg use <see cref="ResolveSourceAsync"/>.
        /// </summary>
        public static async Task<string> ResolveAsync(string pageUrl, CancellationToken cancellationToken = default)
        {
            BasisMediaSource source = await ResolveSourceAsync(pageUrl, cancellationToken);
            return source.Uri;
        }

        private static Task EnsureInitAsync()
        {
            lock (initLock)
            {
                return initTask ??= DlpBootstrap.EnsureInitAsync();
            }
        }

        // Whether a URL should be resolved by yt-dlp, or is already something the player
        // opens directly. The directly-playable classification is owned by the player
        // (BasisMediaUrlRouter.IsDirectlyPlayable) so there's one source of truth; a page
        // URL (YouTube, Twitch, …) is anything that isn't directly playable. This only
        // steers — an unsupported page URL simply fails to resolve and surfaces via
        // ResolveAndPlay's onError.
        internal static bool NeedsResolution(string url)
            => !string.IsNullOrEmpty(url) && !BasisMediaUrlRouter.IsDirectlyPlayable(url);

        // Builds the BasisMediaSource from yt-dlp's format list. Prefers a split pair
        // (avc1 video-only + mp4a audio-only) when both exist — that's how YouTube
        // serves anything above ~360p, and the player presents the two in sync via
        // AudioUri. Falls back to a single muxed/HLS stream (Twitch, live YouTube,
        // progressive ~360p) otherwise. Assumes a split-capable player; emitting the
        // pair only against one is a version-define refinement.
        private static BasisMediaSource SelectSource(VideoInfo info)
        {
            if (info == null) return null;

            Format video = BestVideoOnly(info.Formats);
            Format audio = BestAudioOnly(info.Formats);
            if (video != null && audio != null)
                // A split pair is adaptive VOD (YouTube serves these only above ~360p),
                // delivered faster than real time — force on-demand pacing. Split is
                // preferred over muxed unconditionally by design: muxed is YouTube's
                // ≤720p progressive fallback, always lower quality than the split ladder.
                // The "avc1 wins at equal height" rule is a video-codec preference and
                // lives in BestVideoOnly's ranking (CodecRank), so the split already
                // carries avc1 at any height an avc1 video-only rung exists.
                return new BasisMediaSource { Uri = video.Url, AudioUri = audio.Url, Delivery = BasisMediaDelivery.OnDemand };

            Format muxed = BestMuxed(info.Formats);
            if (muxed != null) return new BasisMediaSource { Uri = muxed.Url, Delivery = DeliveryFor(info) };

            // Last resort: yt-dlp's top-level URL — but only if the player can open it
            // directly. An unvalidated DirectUrl can be an unsupported manifest/codec that
            // would bypass the avc1/mp4a filtering above, so reject it and let
            // ResolveSourceAsync fail loudly ("no player-ingestible format") instead.
            if (!string.IsNullOrEmpty(info.DirectUrl) && BasisMediaUrlRouter.IsDirectlyPlayable(info.DirectUrl))
                return new BasisMediaSource { Uri = info.DirectUrl, Delivery = DeliveryFor(info) };

            return null;
        }

        // How many subtitle rows a single video may contribute. Real track lists are a
        // handful; this only bounds a hostile/degenerate extraction.
        private const int MaxSubtitleTracks = 32;

        // Builds the sidecar subtitle track list from the extraction's caption dictionaries
        // (lang code -> per-format entries). Everything is already in the info dict — no
        // extra fetch. Selection rules:
        //   - VOD only: live/upcoming streams get none (their caption URLs don't apply).
        //   - json3 entries only — the format the player parses. This also naturally
        //     excludes pseudo-tracks like "live_chat" (ext "json").
        //   - Manual (uploader) tracks in the video's original language or the viewer's
        //     system language; when none match, all manual tracks (uploader-curated,
        //     rarely more than a handful — better listed than hidden).
        //   - Auto (ASR) tracks only for the original language and the viewer's system
        //     language, skipping languages a manual track already covers — YouTube
        //     offers 100+ auto-translations per video, which would drown the picker.
        // Returns null when nothing qualifies, so the metadata stays "no tracks".
        private static List<BasisSubtitleTrack> BuildSubtitleTracks(VideoInfo info)
        {
            if (info == null || info.IsLive == true) return null;
            if (info.LiveStatus == "is_live" || info.LiveStatus == "is_upcoming") return null;

            // The original language: the info dict's language field when present, else
            // derived from the ASR track yt-dlp marks with an "-orig" suffix (the
            // language field is null for plenty of videos that still carry captions).
            string originalLang = info.Language;
            string origAsrKey = null;
            if (info.AutomaticCaptions != null)
            {
                foreach (KeyValuePair<string, List<SubtitleTrack>> pair in info.AutomaticCaptions)
                {
                    if (!pair.Key.EndsWith("-orig", StringComparison.OrdinalIgnoreCase)) continue;
                    origAsrKey = pair.Key;
                    if (string.IsNullOrEmpty(originalLang))
                        originalLang = pair.Key.Substring(0, pair.Key.Length - "-orig".Length);
                    break;
                }
            }
            string systemLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

            var tracks = new List<BasisSubtitleTrack>();

            // Manual tracks first. Two passes: languages matching original/system, then
            // — only if that found nothing — every manual track.
            if (info.Subtitles != null && info.Subtitles.Count > 0)
            {
                AddManualTracks(tracks, info.Subtitles, originalLang, systemLang, matchedOnly: true);
                if (tracks.Count == 0)
                    AddManualTracks(tracks, info.Subtitles, originalLang, systemLang, matchedOnly: false);
            }

            // Auto tracks: original-language ASR, then the system language if distinct;
            // both skipped when a manual track already covers the language.
            if (info.AutomaticCaptions != null && info.AutomaticCaptions.Count > 0)
            {
                string origKey = origAsrKey ?? FindKeyByLanguage(info.AutomaticCaptions, originalLang);
                if (origKey != null) AddAutoTrack(tracks, info.AutomaticCaptions, origKey);
                if (!SameLanguage(systemLang, origKey))
                {
                    string systemKey = FindKeyByLanguage(info.AutomaticCaptions, systemLang);
                    if (systemKey != null) AddAutoTrack(tracks, info.AutomaticCaptions, systemKey);
                }
            }

            return tracks.Count > 0 ? tracks : null;
        }

        private static void AddManualTracks(
            List<BasisSubtitleTrack> tracks,
            Dictionary<string, List<SubtitleTrack>> subtitles,
            string originalLang, string systemLang, bool matchedOnly)
        {
            foreach (KeyValuePair<string, List<SubtitleTrack>> pair in subtitles)
            {
                if (tracks.Count >= MaxSubtitleTracks) return;
                if (matchedOnly && !SameLanguage(pair.Key, originalLang) && !SameLanguage(pair.Key, systemLang)) continue;
                SubtitleTrack json3 = FindJson3(pair.Value);
                if (json3 == null) continue;
                tracks.Add(new BasisSubtitleTrack
                {
                    Url = json3.Url,
                    Format = "json3",
                    Language = pair.Key,
                    Label = !string.IsNullOrEmpty(json3.Name) ? json3.Name : pair.Key,
                    IsAutoGenerated = false,
                });
            }
        }

        private static void AddAutoTrack(
            List<BasisSubtitleTrack> tracks,
            Dictionary<string, List<SubtitleTrack>> autoCaptions,
            string key)
        {
            if (tracks.Count >= MaxSubtitleTracks) return;
            foreach (BasisSubtitleTrack existing in tracks)
            {
                if (SameLanguage(existing.Language, key)) return;
            }
            if (!autoCaptions.TryGetValue(key, out List<SubtitleTrack> entries)) return;
            SubtitleTrack json3 = FindJson3(entries);
            if (json3 == null) return;

            // "English (Original)" -> "English (auto)" rather than stacking suffixes.
            string name = !string.IsNullOrEmpty(json3.Name) ? json3.Name : key;
            const string origSuffix = " (Original)";
            if (name.EndsWith(origSuffix, StringComparison.Ordinal))
                name = name.Substring(0, name.Length - origSuffix.Length);

            tracks.Add(new BasisSubtitleTrack
            {
                Url = json3.Url,
                Format = "json3",
                Language = key,
                Label = $"{name} (auto)",
                IsAutoGenerated = true,
            });
        }

        // Finds the dictionary key for a language: exact match first, then any key
        // sharing the primary subtag (a Chinese system locale is "zh" while YouTube's
        // auto tracks key as "zh-Hans"/"zh-Hant"). "-orig" keys are skipped here — the
        // original-language pass handles those.
        private static string FindKeyByLanguage(Dictionary<string, List<SubtitleTrack>> dict, string lang)
        {
            if (string.IsNullOrEmpty(lang)) return null;
            if (dict.ContainsKey(lang)) return lang;
            foreach (KeyValuePair<string, List<SubtitleTrack>> pair in dict)
            {
                if (pair.Key.EndsWith("-orig", StringComparison.OrdinalIgnoreCase)) continue;
                if (SameLanguage(pair.Key, lang)) return pair.Key;
            }
            return null;
        }

        private static SubtitleTrack FindJson3(List<SubtitleTrack> entries)
        {
            if (entries == null) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                SubtitleTrack t = entries[i];
                if (t != null && t.Ext == "json3" && !string.IsNullOrEmpty(t.Url)) return t;
            }
            return null;
        }

        // Language-code comparison on the primary subtag: "en-GB", "en-orig" and "en"
        // are all the same language for track-list purposes.
        private static bool SameLanguage(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string pa = PrimarySubtag(a), pb = PrimarySubtag(b);
            return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
        }

        private static string PrimarySubtag(string code)
        {
            int dash = code.IndexOf('-');
            return dash > 0 ? code.Substring(0, dash) : code;
        }

        // Maps yt-dlp's live-status metadata onto the engine's delivery hint, so the
        // live-vs-VOD clock is chosen at open instead of sniffed from the byte stream.
        // Absent/unknown status falls back to Auto (the engine detects a seekable,
        // finite body as VOD and an open-ended stream as live). The split avc1+mp4a
        // path doesn't consult this — that pairing is only ever adaptive VOD.
        private static BasisMediaDelivery DeliveryFor(VideoInfo info)
        {
            if (info.IsLive == true) return BasisMediaDelivery.Live;
            switch (info.LiveStatus)
            {
                case "is_live":
                case "is_upcoming": return BasisMediaDelivery.Live;
                case "was_live":
                case "post_live":   // ended broadcast, VOD still processing — watched as a recording
                case "not_live":    return BasisMediaDelivery.OnDemand;
                default:            return BasisMediaDelivery.Auto;
            }
        }

        // Best direct video-only stream. avc1 is always eligible up to 1080p; where
        // the platform hardware-decodes VP9 or AV1 (probed once, cached), those
        // ladders are eligible up to 2160p — which is what unlocks >1080p on
        // YouTube, whose above-1080p rungs are VP9 in video-only WebM (AV1 appears
        // alongside on popular uploads). Ranking is height first, then codec at
        // equal height — avc1 over av01 (decode cost / hardware breadth), av01 over
        // vp9 (better bitrate at 4K; both hardware-decoded wherever their probes
        // passed) — then bitrate. A VP9/AV1 rung only wins where it offers more
        // height than every eligible avc1 rung, so on the usual complete avc1
        // ladder selection at or below 1080p is unchanged.
        private static Format BestVideoOnly(List<Format> formats)
        {
            if (formats == null) return null;
            bool vp9Decodes = BasisNativeVideoSource.IsVideoCodecSupported(BasisVideoCodec.VP9);
            bool av1Decodes = BasisNativeVideoSource.IsVideoCodecSupported(BasisVideoCodec.AV1);
            Format best = null;
            for (int i = 0; i < formats.Count; i++)
            {
                Format f = formats[i];
                if (f == null || string.IsNullOrEmpty(f.Url)) continue;
                if (!HasCodec(f.VCodec) || HasCodec(f.ACodec)) continue;         // video-only
                int heightCap;
                if (StartsWithCI(f.VCodec, "avc1")) heightCap = 1080;
                // VP9/AV1 additionally require a known height: an unknown-resolution
                // rung could be 8K, past what the probe vouched for. (avc1 keeps
                // its long-standing unknown-height tolerance.)
                else if (vp9Decodes && IsSdrVp9(f) && (f.Height ?? 0) > 0) heightCap = 2160;
                else if (av1Decodes && IsSdrAv1(f) && (f.Height ?? 0) > 0) heightCap = 2160;
                else continue;
                if ((f.Height ?? 0) > heightCap) continue;
                if (!IsDirectByteStream(f.Protocol)) continue;
                if (best == null || VideoBetter(f, best)) best = f;
            }
            return best;
        }

        // A VP9 rung the engine can take: profile 0 only — bare "vp9" (yt-dlp signals
        // profile 2/10-bit as the distinct "vp9.2") or dotted "vp09.00.*" (the profile
        // lives in the first dotted field) — since profile 0 is what the decode probe
        // answers for. The carriage must be a WebM or MP4 byte stream, and SDR only:
        // the renderer has no tone mapping. HDR uploads still play via their parallel
        // SDR ladder.
        private static bool IsSdrVp9(Format f)
        {
            if (!string.Equals(f.VCodec, "vp9", StringComparison.OrdinalIgnoreCase) &&
                !StartsWithCI(f.VCodec, "vp09.00")) return false;
            if (!string.Equals(f.Ext, "webm", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(f.Ext, "mp4", StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(f.DynamicRange, "SDR", StringComparison.OrdinalIgnoreCase);
        }

        // An AV1 rung the engine can take: Main profile, 8-bit only — the codec
        // string is "av01.<profile>.<level+tier>.<bitdepth>[...]" (e.g.
        // "av01.0.12M.08"), so require profile "0" and bit-depth field "08"
        // alongside the SDR tag; the probe answers for 8-bit Profile-0 hardware
        // decode and the renderer has no tone mapping. HDR/10-bit uploads still
        // play via their parallel SDR ladders. Carriage must be an MP4 or WebM
        // byte stream (what yt-dlp serves av01 in).
        private static bool IsSdrAv1(Format f)
        {
            if (!StartsWithCI(f.VCodec, "av01.")) return false;
            string[] parts = f.VCodec.Split('.');
            if (parts.Length < 4 || parts[1] != "0" || parts[3] != "08") return false;
            if (!string.Equals(f.Ext, "mp4", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(f.Ext, "webm", StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(f.DynamicRange, "SDR", StringComparison.OrdinalIgnoreCase);
        }

        // Audio-only, highest bitrate, direct byte URL. AAC (mp4a) is in-box on
        // every platform and stays the default leg; Opus (WebM, itags 249/250/251)
        // is accepted only as a fallback for uploads that carry no AAC audio-only
        // format, so the reliable split path never regresses. Opus decode is
        // available everywhere the player runs (Android MediaCodec, Windows
        // runtime-loaded libopus), so no probe gates it.
        private static Format BestAudioOnly(List<Format> formats)
        {
            if (formats == null) return null;
            Format bestAac = null, bestOpus = null;
            for (int i = 0; i < formats.Count; i++)
            {
                Format f = formats[i];
                if (f == null || string.IsNullOrEmpty(f.Url)) continue;
                if (!HasCodec(f.ACodec) || HasCodec(f.VCodec)) continue;         // audio-only
                if (!IsDirectByteStream(f.Protocol)) continue;
                if (StartsWithCI(f.ACodec, "mp4a"))
                {
                    if (bestAac == null || Bitrate(f.AudioBitrate, f) > Bitrate(bestAac.AudioBitrate, bestAac)) bestAac = f;
                }
                else if (string.Equals(f.ACodec, "opus", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(f.Ext, "webm", StringComparison.OrdinalIgnoreCase))
                {
                    if (bestOpus == null || Bitrate(f.AudioBitrate, f) > Bitrate(bestOpus.AudioBitrate, bestOpus)) bestOpus = f;
                }
            }
            return bestAac ?? bestOpus;
        }

        // Single stream carrying both tracks: progressive avc1+mp4a (e.g. itag 18) or an
        // HLS variant. HLS manifests are allowed here — the player ingests them as one
        // stream — so this path covers Twitch / live YouTube.
        private static Format BestMuxed(List<Format> formats)
        {
            if (formats == null) return null;
            Format best = null;
            for (int i = 0; i < formats.Count; i++)
            {
                Format f = formats[i];
                if (f == null || string.IsNullOrEmpty(f.Url)) continue;
                if (!HasCodec(f.VCodec) || !HasCodec(f.ACodec)) continue;        // muxed
                if (!StartsWithCI(f.VCodec, "avc1")) continue;                   // H.264 only
                if (!StartsWithCI(f.ACodec, "mp4a")) continue;                   // AAC only
                if (!IsIngestibleProtocol(f.Protocol)) continue;                 // byte stream or HLS
                if ((f.Height ?? 0) > 1080) continue;
                if (best == null || VideoBetter(f, best)) best = f;
            }
            return best;
        }

        private static bool VideoBetter(Format a, Format b)
        {
            int ha = a.Height ?? 0, hb = b.Height ?? 0;
            if (ha != hb) return ha > hb;
            int ca = CodecRank(a.VCodec), cb = CodecRank(b.VCodec);
            if (ca != cb) return ca > cb;
            return Bitrate(a.VideoBitrate, a) > Bitrate(b.VideoBitrate, b);
        }

        // Codec preference at equal height: avc1 first (decode cost / hardware
        // breadth), then av01 over vp9 (better bitrate at 4K; both only reach
        // here where their hardware-decode probes passed).
        private static int CodecRank(string vcodec)
            => StartsWithCI(vcodec, "avc1") ? 2 : StartsWithCI(vcodec, "av01") ? 1 : 0;

        private static double Bitrate(double? primary, Format f) => primary ?? f.TotalBitrate ?? 0;

        private static bool HasCodec(string codec) => !string.IsNullOrEmpty(codec) && codec != "none";

        private static bool StartsWithCI(string s, string prefix)
            => s != null && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        // Direct byte/range URL the engine fetches over WinHTTP. Skip DASH-manifest
        // protocols the demuxers don't consume as a single byte stream; HLS is handled
        // separately in BestMuxed (the player has its own HLS source).
        private static bool IsDirectByteStream(string protocol)
            => string.IsNullOrEmpty(protocol) || protocol == "https" || protocol == "http";

        // Protocols the player can ingest as a single stream: a direct byte stream
        // (http/https) or HLS (the player has its own HLS source). Excludes DASH /
        // fragment manifests the demuxers can't open. Used for the muxed path, which
        // covers both progressive (http) and HLS (Twitch / live) sources.
        private static bool IsIngestibleProtocol(string protocol)
            => IsDirectByteStream(protocol) || protocol == "m3u8" || protocol == "m3u8_native";
    }
}
#endif
