using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Parses YouTube timedtext json3 payloads into caption cues. json3 delivers
// plain-text segments (no markup to strip — the caption overlay renders with
// rich text off) inside a flat event list; auto-generated (ASR) tracks add
// roll-up presentation events that must be flattened for a single-active-cue
// overlay: whitespace-only append events are dropped and each cue's end is
// clamped to the next cue's start.
//
// Never throws: any input that isn't well-formed json3 parses to an empty
// array.
public static class BasisJson3SubtitleParser
{
    public static BasisCaptionCue[] Parse(string json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<BasisCaptionCue>();

        J3Root root;
        try
        {
            root = JsonUtility.FromJson<J3Root>(json);
        }
        catch (Exception)
        {
            return Array.Empty<BasisCaptionCue>();
        }
        if (root?.events == null || root.events.Length == 0) return Array.Empty<BasisCaptionCue>();

        var cues = new List<BasisCaptionCue>(root.events.Length);
        var text = new StringBuilder();
        foreach (J3Event ev in root.events)
        {
            if (ev?.segs == null || ev.segs.Length == 0) continue;
            if (ev.tStartMs < 0 || ev.dDurationMs < 0) continue;

            text.Clear();
            foreach (J3Seg seg in ev.segs)
            {
                if (!string.IsNullOrEmpty(seg?.utf8)) text.Append(seg.utf8);
            }
            string cueText = text.ToString();
            if (string.IsNullOrWhiteSpace(cueText)) continue;

            long startUs = ev.tStartMs * 1000L;
            cues.Add(new BasisCaptionCue(cueText, startUs, startUs + ev.dDurationMs * 1000L));
        }
        if (cues.Count == 0) return Array.Empty<BasisCaptionCue>();

        cues.Sort((a, b) => a.StartUs.CompareTo(b.StartUs));

        // ASR events overlap the next cue by design (roll-up); clamp so at most
        // one cue is active at a time. Zero-length results are dropped.
        var result = new List<BasisCaptionCue>(cues.Count);
        for (int i = 0; i < cues.Count; i++)
        {
            BasisCaptionCue cue = cues[i];
            long endUs = cue.EndUs;
            if (i + 1 < cues.Count && endUs > cues[i + 1].StartUs) endUs = cues[i + 1].StartUs;
            if (endUs <= cue.StartUs) continue;
            result.Add(new BasisCaptionCue(cue.Text, cue.StartUs, endUs));
        }
        return result.ToArray();
    }

    // Field names match the json3 keys for JsonUtility; fields beyond these
    // (window ids, per-segment ASR confidence and offsets, append flags) are
    // ignored.
    [Serializable]
    private sealed class J3Root
    {
        public J3Event[] events;
    }

    [Serializable]
    private sealed class J3Event
    {
        public long tStartMs;
        public long dDurationMs;
        public J3Seg[] segs;
    }

    [Serializable]
    private sealed class J3Seg
    {
        public string utf8;
    }
}
