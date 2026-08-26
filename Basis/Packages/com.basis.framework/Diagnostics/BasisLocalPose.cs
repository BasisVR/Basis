using System.Runtime.CompilerServices;
using UnityEngine;

public static class BasisLocalPose
{
    [System.Flags]
    enum Field : byte { None = 0, Position = 1, Rotation = 2, LossyScale = 4, LocalToWorld = 8 }

    struct Entry
    {
        public Transform T;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 LossyScale;
        public Matrix4x4 LocalToWorld;
        public Field Valid;
        public uint Version;
    }

    static readonly Entry[] sEntries = new Entry[(int)BasisPoseSlot.Count];
    static uint sVersion = 1;

    public static int BoundCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < sEntries.Length; i++) if (sEntries[i].T != null) n++;
            return n;
        }
    }

    public static Transform Bound(BasisPoseSlot slot) => sEntries[(int)slot].T;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginFrame() => sVersion++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InvalidateAll() => sVersion++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void NotifyWrite(Transform t)
    {
        for (int i = 0; i < sEntries.Length; i++)
        {
            if (ReferenceEquals(sEntries[i].T, t))
            {
                // PlayerRoot and AvatarRoot are ancestors of Hips and Head, so a write to either
                // moves those two as well and clearing one slot would leave the descendants
                // holding world poses from before it. Slot order encodes that: 0 and 1 are roots.
                if (i <= (int)BasisPoseSlot.AvatarRoot)
                {
                    sVersion++;
                }
                else
                {
                    sEntries[i].Valid = Field.None;
                }
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Prepare(ref Entry e, Transform t)
    {
        if (!ReferenceEquals(e.T, t))
        {
            e.T = t;
            e.Valid = Field.None;
            e.Version = sVersion;
        }
        else if (e.Version != sVersion)
        {
            e.Valid = Field.None;
            e.Version = sVersion;
        }
        return t != null;
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    public static bool ValidateHits;

    public static int Hits { get; private set; }
    public static int Misses { get; private set; }
    public static int StaleHits { get; private set; }
    public static string LastStaleSite { get; private set; } = string.Empty;

    static readonly System.Collections.Generic.HashSet<string> sReportedStale = new System.Collections.Generic.HashSet<string>();

    public static void ResetStats()
    {
        Hits = 0; Misses = 0; StaleHits = 0;
        LastStaleSite = string.Empty;
        sReportedStale.Clear();
    }

    static void ReportStale(string file, int line, BasisPoseSlot slot, string field, string cached, string live)
    {
        StaleHits++;
        string site = $"{file}:{line} [{slot}.{field}]";
        LastStaleSite = site;
        if (!sReportedStale.Add(site)) return;
        Debug.LogError(
            $"[BasisLocalPose] STALE CACHE at {site}\n" +
            $"  cached: {cached}\n  live:   {live}\n" +
            $"Something moved {slot} without BasisLocalPose.InvalidateAll(). Add one at that writer.");
    }
#else
    public static void ResetStats() { }
#endif

    // ── Cached reads ────────────────────────────────────────────────────────────────────────────

    public static Vector3 GetPosition(BasisPoseSlot slot, Transform t,
        [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        ref Entry e = ref sEntries[(int)slot];
        if (!Prepare(ref e, t)) return Vector3.zero;

        if ((e.Valid & Field.Position) != 0)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Hits++;
            if (ValidateHits)
            {
                Vector3 live = t.position;
                if (live != e.Position) ReportStale(file, line, slot, "Position", e.Position.ToString("F6"), live.ToString("F6"));
            }
#endif
            return e.Position;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Misses++;
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetPosition);
#endif
        e.Position = t.position;
        e.Valid |= Field.Position;
        return e.Position;
    }

    public static Quaternion GetRotation(BasisPoseSlot slot, Transform t,
        [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        ref Entry e = ref sEntries[(int)slot];
        if (!Prepare(ref e, t)) return Quaternion.identity;

        if ((e.Valid & Field.Rotation) != 0)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Hits++;
            if (ValidateHits)
            {
                Quaternion live = t.rotation;
                if (live != e.Rotation) ReportStale(file, line, slot, "Rotation", e.Rotation.ToString("F6"), live.ToString("F6"));
            }
#endif
            return e.Rotation;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Misses++;
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetRotation);
#endif
        e.Rotation = t.rotation;
        e.Valid |= Field.Rotation;
        return e.Rotation;
    }

    public static void GetPose(BasisPoseSlot slot, Transform t, out Vector3 position, out Quaternion rotation,
        [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        ref Entry e = ref sEntries[(int)slot];
        if (!Prepare(ref e, t)) { position = Vector3.zero; rotation = Quaternion.identity; return; }

        const Field both = Field.Position | Field.Rotation;
        if ((e.Valid & both) == both)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Hits++;
            if (ValidateHits)
            {
                t.GetPositionAndRotation(out Vector3 lp, out Quaternion lr);
                if (lp != e.Position) ReportStale(file, line, slot, "Position", e.Position.ToString("F6"), lp.ToString("F6"));
                if (lr != e.Rotation) ReportStale(file, line, slot, "Rotation", e.Rotation.ToString("F6"), lr.ToString("F6"));
            }
#endif
            position = e.Position;
            rotation = e.Rotation;
            return;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Misses++;
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetPose);
#endif
        t.GetPositionAndRotation(out e.Position, out e.Rotation);
        e.Valid |= both;
        position = e.Position;
        rotation = e.Rotation;
    }

    public static Vector3 GetLossyScale(BasisPoseSlot slot, Transform t,
        [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        ref Entry e = ref sEntries[(int)slot];
        if (!Prepare(ref e, t)) return Vector3.one;

        if ((e.Valid & Field.LossyScale) != 0)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Hits++;
            if (ValidateHits)
            {
                Vector3 live = t.lossyScale;
                if (live != e.LossyScale) ReportStale(file, line, slot, "LossyScale", e.LossyScale.ToString("F6"), live.ToString("F6"));
            }
#endif
            return e.LossyScale;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Misses++;
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetLossyScale);
#endif
        e.LossyScale = t.lossyScale;
        e.Valid |= Field.LossyScale;
        return e.LossyScale;
    }

    public static Matrix4x4 GetLocalToWorld(BasisPoseSlot slot, Transform t,
        [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        ref Entry e = ref sEntries[(int)slot];
        if (!Prepare(ref e, t)) return Matrix4x4.identity;

        if ((e.Valid & Field.LocalToWorld) != 0)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Hits++;
            if (ValidateHits)
            {
                Matrix4x4 live = t.localToWorldMatrix;
                if (live != e.LocalToWorld) ReportStale(file, line, slot, "LocalToWorld", e.LocalToWorld.ToString(), live.ToString());
            }
#endif
            return e.LocalToWorld;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Misses++;
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetLocalToWorld);
#endif
        e.LocalToWorld = t.localToWorldMatrix;
        e.Valid |= Field.LocalToWorld;
        return e.LocalToWorld;
    }
}
