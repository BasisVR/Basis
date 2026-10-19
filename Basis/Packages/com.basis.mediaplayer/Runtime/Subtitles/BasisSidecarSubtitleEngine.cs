using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UrlSecurity = Basis.Scripts.Common.BasisUrlSecurity;

/// <summary>How a sidecar subtitle fetch ended.</summary>
enum BasisSubtitleLoad
{
    Loaded,
    Failed,
    /// <summary>A newer fetch or a clear overtook it: not a failure.</summary>
    Superseded,
}

// Fetches an out-of-band subtitle track and answers "which cue is active at
// this position" for the player's per-frame tick. Owned by BasisMediaPlayer;
// not a MonoBehaviour. The lookup is stateless against playback (a binary
// search over the sorted cue list every call), so seeks, stop then play and
// loops need no reset. Only the change-detection index persists between
// calls.
internal sealed class BasisSidecarSubtitleEngine
{
    // Subtitle payloads are a few KB (manual tracks) to a few hundred KB
    // (long ASR tracks); anything past this is not a subtitle file.
    private const long MaxPayloadBytes = 10 * 1024 * 1024;
    private const int FetchTimeoutSeconds = 15;

    private BasisCaptionCue[] cues;
    private int activeIndex = -1;
    private int generation;
    private UnityWebRequest activeRequest;

    // The engine vets its own media legs, but this fetch goes out over
    // UnityWebRequest and never reaches it. Returns null to allow, or a
    // reason to refuse. The literal check runs first because it needs no
    // network; the DNS check then closes the name-that-resolves-to-a-
    // private-address hole the literal check cannot see.
    private static async Task<string> ValidateUrlAsync(string url)
    {
        if (!UrlSecurity.IsHttpUrlAllowed(url, out string reason))
            return reason;
        return await UrlSecurity.ValidateResolvedHostAsync(url);
    }

    // Fetches and parses a track, replacing the held cue list on success.
    // Failed covers the gate refusing the URL, a network or HTTP error, and
    // an oversized or unparseable payload; Superseded means a newer
    // LoadTrackAsync or Clear overtook this one, which is not a failure and
    // nothing to report. The caller decides how to surface either. Redirects
    // are refused so a post-validation redirect can't reach a host the gate
    // never saw.
    public async Task<BasisSubtitleLoad> LoadTrackAsync(BasisSubtitleTrack track)
    {
        int loadGeneration = ++generation;
        activeRequest?.Abort();
        cues = null;
        activeIndex = -1;

        if (track == null || string.IsNullOrEmpty(track.Url)) return BasisSubtitleLoad.Failed;
        string refusal = await ValidateUrlAsync(track.Url);
        if (refusal != null)
        {
            BasisDebug.LogWarning($"[BasisMedia] subtitle track refused: {refusal}", BasisDebug.LogTag.Video);
            return BasisSubtitleLoad.Failed;
        }
        if (loadGeneration != generation) return BasisSubtitleLoad.Superseded;

        string payload;
        using (var request = UnityWebRequest.Get(track.Url))
        {
            request.redirectLimit = 0;
            request.timeout = FetchTimeoutSeconds;
            activeRequest = request;
            try
            {
                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                while (!op.isDone) await Task.Yield();
                if (loadGeneration != generation) return BasisSubtitleLoad.Superseded;
                if (request.result != UnityWebRequest.Result.Success) return BasisSubtitleLoad.Failed;
                byte[] data = request.downloadHandler.data;
                if (data == null || data.LongLength == 0 || data.LongLength > MaxPayloadBytes) return BasisSubtitleLoad.Failed;
                payload = request.downloadHandler.text;
            }
            finally
            {
                if (activeRequest == request) activeRequest = null;
            }
        }

        BasisCaptionCue[] parsed = await Task.Run(() => ParseTrack(track.Format, payload));
        if (loadGeneration != generation) return BasisSubtitleLoad.Superseded;
        if (parsed == null || parsed.Length == 0) return BasisSubtitleLoad.Failed;
        cues = parsed;
        return BasisSubtitleLoad.Loaded;
    }

    // Reports the active cue whenever it differs from the last report,
    // including active to none (cue.Text null, matching the in-band clear
    // convention).
    public bool TryGetCueChange(long positionUs, out BasisCaptionCue cue)
    {
        cue = default;
        BasisCaptionCue[] list = cues;
        if (list == null || list.Length == 0) return false;

        int index = FindActiveIndex(list, positionUs);
        if (index == activeIndex) return false;
        activeIndex = index;
        cue = index >= 0 ? list[index] : new BasisCaptionCue(null, 0, 0);
        return true;
    }

    // Forgets the last-reported cue so the next TryGetCueChange re-reports
    // whatever is active. Pair with a display clear: once the overlay has been
    // force-cleared, an unchanged active cue must be re-sent, not deduped.
    public void ResetCueTracking()
    {
        activeIndex = -1;
    }

    // Cancels any in-flight fetch and drops the held cues.
    public void Clear()
    {
        generation++;
        activeRequest?.Abort();
        activeRequest = null;
        cues = null;
        activeIndex = -1;
    }

    private static BasisCaptionCue[] ParseTrack(string format, string payload)
    {
        return format == "json3" ? BasisJson3SubtitleParser.Parse(payload) : Array.Empty<BasisCaptionCue>();
    }

    // Last cue with StartUs <= position that is still covering position, or -1.
    private static int FindActiveIndex(BasisCaptionCue[] list, long positionUs)
    {
        int lo = 0, hi = list.Length - 1, last = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (list[mid].StartUs <= positionUs) { last = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (last < 0 || positionUs >= list[last].EndUs) return -1;
        return last;
    }
}
