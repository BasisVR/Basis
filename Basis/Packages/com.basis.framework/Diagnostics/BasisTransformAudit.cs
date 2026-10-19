using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Counts what goes through <see cref="BasisTransformAccess"/>, keyed by call site.
///
/// The point is coverage, not precision: a Transform read that does not route through the funnel is
/// invisible here, so a site missing from this list means either it does not run or it was never
/// migrated. Treat the list as the map of what is known, and the profiler as the check on what is not.
///
/// Off by default. Recording costs a dictionary lookup per operation, which is far more than the
/// Transform read it is measuring — this is a hunting tool, not something to leave armed. The whole
/// class compiles out of player builds.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisTransformAudit
{
    /// <summary>Master toggle, off by default. Armed from Basis/Debug/Transform Access.</summary>
    public static bool Enabled;

#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    /// <summary>One row per (file, line) that has performed at least one operation while armed.</summary>
    public sealed class Site
    {
        public string File;
        public int Line;
        public readonly int[] Ops = new int[(int)BasisTransformOp.Count];
        /// <summary>Operations recorded so far in the current frame.</summary>
        public int ThisFrame;
        /// <summary>Operations recorded in the last completed frame — what the window sorts on.</summary>
        public int LastFrame;
        /// <summary>Worst single frame seen since arming; catches sites that spike rather than sit high.</summary>
        public int PeakFrame;
        public long Total;

        public string ShortFile
        {
            get
            {
                if (string.IsNullOrEmpty(File)) return "<unknown>";
                int slash = File.LastIndexOfAny(k_Separators);
                return slash >= 0 ? File.Substring(slash + 1) : File;
            }
        }
    }

    [NoAutoStaticsCleanup] static readonly char[] k_Separators = { '/', '\\' };

    readonly struct SiteKey : System.IEquatable<SiteKey>
    {
        public readonly string File;
        public readonly int Line;
        public SiteKey(string file, int line) { File = file; Line = line; }
        public bool Equals(SiteKey other) => Line == other.Line && string.Equals(File, other.File, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is SiteKey k && Equals(k);
        public override int GetHashCode() => (File?.GetHashCode() ?? 0) * 397 ^ Line;
    }

    static readonly Dictionary<SiteKey, Site> sSites = new Dictionary<SiteKey, Site>();
    static readonly List<Site> sOrdered = new List<Site>();
    [NoAutoStaticsCleanup] static readonly int[] sOpsThisFrame = new int[(int)BasisTransformOp.Count];
    [NoAutoStaticsCleanup] static readonly int[] sOpsLastFrame = new int[(int)BasisTransformOp.Count];
    static int sFrame = -1;
    static int sCallsThisFrame;
    static bool sWasEnabled;

    /// <summary>Operations funnelled in the last completed frame.</summary>
    public static int CallsLastFrame { get; private set; }
    /// <summary>Highest single-frame total since arming.</summary>
    public static int PeakCallsPerFrame { get; private set; }
    /// <summary>Frames observed since arming — divides the totals into a per-frame average.</summary>
    public static int FramesObserved { get; private set; }
    /// <summary>Distinct call sites seen since arming.</summary>
    public static int SiteCount => sOrdered.Count;

    /// <summary>Live view of the recorded sites. Do not mutate; sort a copy.</summary>
    public static IReadOnlyList<Site> Sites => sOrdered;

    /// <summary>Per-op totals for the last completed frame, indexed by <see cref="BasisTransformOp"/>.</summary>
    public static IReadOnlyList<int> OpsLastFrame => sOpsLastFrame;

    /// <summary>
    /// Called from every <see cref="BasisTransformAccess"/> accessor. The frame rollover is detected
    /// here rather than driven from the event driver so that arming the audit needs no wiring — and so
    /// the counters stay correct no matter which phase of the frame a site runs in.
    /// </summary>
    public static void Record(string file, int line, BasisTransformOp op)
    {
        if (!Enabled)
        {
            // First call after disarming: publish the partial frame so the window does not keep
            // showing counts that are no longer being updated.
            if (sWasEnabled) { sWasEnabled = false; RollFrame(); }
            return;
        }
        sWasEnabled = true;

        int frame = Time.frameCount;
        if (frame != sFrame)
        {
            if (sFrame >= 0) RollFrame();
            sFrame = frame;
            FramesObserved++;
        }

        var key = new SiteKey(file, line);
        if (!sSites.TryGetValue(key, out Site site))
        {
            site = new Site { File = file, Line = line };
            sSites.Add(key, site);
            sOrdered.Add(site);
        }

        site.Ops[(int)op]++;
        site.ThisFrame++;
        site.Total++;
        sOpsThisFrame[(int)op]++;
        sCallsThisFrame++;
    }

    static void RollFrame()
    {
        for (int i = 0; i < sOrdered.Count; i++)
        {
            Site s = sOrdered[i];
            s.LastFrame = s.ThisFrame;
            if (s.ThisFrame > s.PeakFrame) s.PeakFrame = s.ThisFrame;
            s.ThisFrame = 0;
        }
        for (int i = 0; i < sOpsThisFrame.Length; i++)
        {
            sOpsLastFrame[i] = sOpsThisFrame[i];
            sOpsThisFrame[i] = 0;
        }
        CallsLastFrame = sCallsThisFrame;
        if (sCallsThisFrame > PeakCallsPerFrame) PeakCallsPerFrame = sCallsThisFrame;
        sCallsThisFrame = 0;
    }

    /// <summary>Drop every recorded site and start counting again.</summary>
    public static void Reset()
    {
        sSites.Clear();
        sOrdered.Clear();
        for (int i = 0; i < sOpsThisFrame.Length; i++) { sOpsThisFrame[i] = 0; sOpsLastFrame[i] = 0; }
        sFrame = -1;
        sCallsThisFrame = 0;
        CallsLastFrame = 0;
        PeakCallsPerFrame = 0;
        FramesObserved = 0;
        BasisLocalPose.ResetStats();
    }
#else
    /// <summary>Stripped outside the editor and development builds.</summary>
    public static void Record(string file, int line, BasisTransformOp op) { }
    public static void Reset() { }
#endif
}
