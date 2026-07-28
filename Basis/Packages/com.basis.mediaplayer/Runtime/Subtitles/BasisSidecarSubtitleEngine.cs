using System;
using System.Threading.Tasks;
using UnityEngine.Networking;

// Fetches an out-of-band subtitle track and answers "which cue is active at
// this position" for the player's per-frame tick. Owned by BasisMediaPlayer;
// not a MonoBehaviour. The lookup is stateless against playback (a binary
// search over the sorted cue list every call), so seeks, stop→play and loops
// need no reset — only the change-detection index persists between calls.
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

    public bool HasCues => cues != null && cues.Length > 0;

    // Fetches and parses a track, replacing the held cue list on success.
    // Returns false on any failure (security block, network/HTTP error,
    // oversized or unparseable payload) or when superseded by a newer
    // LoadTrackAsync/Clear; the caller decides how to surface it. The URL
    // passes the same gate as the media legs, with redirects refused so a
    // post-validation redirect can't reach a host that never passed DNS
    // validation.
    public async Task<bool> LoadTrackAsync(BasisSubtitleTrack track)
    {
        int loadGeneration = ++generation;
        activeRequest?.Abort();
        cues = null;
        activeIndex = -1;

        if (track == null || string.IsNullOrEmpty(track.Url)) return false;
        if (!BasisMediaPlayerSecurity.IsUrlAllowed(track.Url, out _)) return false;
        string dnsReason = await BasisMediaPlayerSecurity.ValidateResolvedHostAsync(track.Url);
        if (dnsReason != null || loadGeneration != generation) return false;

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
                if (loadGeneration != generation) return false;
                if (request.result != UnityWebRequest.Result.Success) return false;
                byte[] data = request.downloadHandler.data;
                if (data == null || data.LongLength == 0 || data.LongLength > MaxPayloadBytes) return false;
                payload = request.downloadHandler.text;
            }
            finally
            {
                if (activeRequest == request) activeRequest = null;
            }
        }

        BasisCaptionCue[] parsed = ParseTrack(track.Format, payload);
        if (loadGeneration != generation) return false;
        if (parsed == null || parsed.Length == 0) return false;
        cues = parsed;
        return true;
    }

    // Reports the active cue whenever it differs from the last report,
    // including active → none (cue.Text null, matching the in-band clear
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
