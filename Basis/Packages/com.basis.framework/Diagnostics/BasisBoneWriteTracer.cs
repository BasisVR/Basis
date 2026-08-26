using System.Collections.Generic;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;

/// <summary>
/// Names the system that writes a bone, on a rig where every writer goes through an indexed
/// transform array and a job -- so the write has no managed call site to breakpoint and no stack to
/// read.
///
/// The trick is to stop asking who called what and read the Transform itself at every stage
/// boundary of the frame. BasisEventDriver already brackets each system that poses bones with a
/// <see cref="BasisFiniteWatchdog"/> checkpoint; this hangs off the same lines. A watched bone
/// whose local TRS differs from the previous checkpoint was written by whatever ran between the
/// two, whether that was a job, a main-thread write, the Animator retarget or PhysX. Nothing can
/// hide from it, because it observes the result rather than the caller.
///
/// It watches the ancestor chain as well, because the hardest version of this question has no
/// writer at all. A bone whose local rotation never changes still swings if a parent turns, and a
/// pose stream that stores locals propagates that for free -- so a search for the system writing
/// the bone comes back empty and correct. Those frames report as "dragged by" and name the nearest
/// ancestor that actually moved.
///
/// <see cref="HoldEnabled"/> is the other half: from <see cref="HoldFromStage"/> to the end of the
/// frame the watched bones are put back to the value they held at that stage. That both freezes
/// them so a solver can be developed against a bone nothing else is fighting for, and confirms the
/// attribution -- a stage that has to be overridden every frame is the writer.
/// </summary>
public static class BasisBoneWriteTracer
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    /// <summary>Master switch. Off costs one static bool read per checkpoint.</summary>
    public static bool Enabled;

    /// <summary>Watch the four BasisLocalPose slots instead of the arm twist bones.</summary>
    public static bool WatchPoseSlotsInsteadOfTwist;

    /// <summary>Log the per-frame breakdown every frame instead of only when it changes.</summary>
    public static bool LogEveryFrame;

    /// <summary>Include localPosition drift, not just rotation. Off by default -- a twist bone that
    /// moves is nearly always being rotated, and position noise from a scale pass is a distraction.</summary>
    public static bool TrackPosition;

    /// <summary>Rotations closer than this are treated as unwritten. Zero means bit-exact, which is
    /// what the pose pipeline's own write-skip uses: a system that re-writes a value it did not
    /// change produces bit-identical floats, so an epsilon buys nothing and hides small writes.</summary>
    public static float ReportThresholdDegrees;

    /// <summary>Put the watched bones back to their <see cref="HoldFromStage"/> value for the rest
    /// of the frame.</summary>
    public static bool HoldEnabled;

    /// <summary>The stage whose value the hold restores. Defaults to the IK join, so the hold
    /// preserves the solver's own output.</summary>
    public static string HoldFromStage = "PostLocalPlayerFinish (IK join + camera)";

    public static string LastReport { get; private set; } = string.Empty;

    public static int WritesLastFrame { get; private set; }

    /// <summary>How far up the parent chain drag is attributed. Deep enough to reach the hips from
    /// a twist bone on any rig that puts a roll bone under an arm.</summary>
    const int MaxAncestors = 12;

    sealed class Watched
    {
        public Transform Target;
        public string Label;
        public Quaternion LastRotation;
        public Vector3 LastPosition;
        public Quaternion LastWorldRotation;
        public Vector3 LastWorldPosition;
        public Quaternion HoldRotation;
        public Vector3 HoldPosition;
        public bool Seeded;

        /// <summary>Ancestors, nearest first. Their locals are the only thing that can move this
        /// bone without writing it.</summary>
        public Transform[] Ancestors;
        public Quaternion[] AncestorLast;
        public Vector3[] AncestorLastPosition;

        public readonly List<Write> Writes = new List<Write>();
    }

    struct Write
    {
        public string Stage;
        public float Degrees;
        public float WorldDegrees;
        public float Millimetres;
        public bool Overridden;

        /// <summary>Set when the bone's own local never changed and a parent turned instead.</summary>
        public string DraggedBy;
        public float DraggedByDegrees;
    }

    static readonly List<Watched> sWatched = new List<Watched>();
    static readonly Dictionary<string, int> sStageTotals = new Dictionary<string, int>();
    static readonly StringBuilder sBuilder = new StringBuilder();
    static int sFrame = -1;
    static bool sHoldArmed;
    static string sLastSignature = string.Empty;
    static readonly Transform[] sBoundTwist = new Transform[4];
    static readonly Transform[] sBoundSlots = new Transform[4];

    /// <summary>
    /// Watches the four arm twist bones of the local avatar. Safe to call every frame -- it rebinds
    /// itself when the mapping points somewhere else, which is what an avatar swap looks like from
    /// here.
    /// </summary>
    public static void WatchLocalArmTwist()
    {
        Basis.Scripts.Common.BasisTransformMapping mapping = BasisLocalAvatarDriver.Mapping;
        if (mapping == null)
        {
            return;
        }
        if (sWatched.Count > 0
            && sBoundTwist[0] == mapping.leftUpperArmTwist
            && sBoundTwist[1] == mapping.leftLowerArmTwist
            && sBoundTwist[2] == mapping.RightUpperArmTwist
            && sBoundTwist[3] == mapping.RightLowerArmTwist)
        {
            return;
        }
        Clear();
        sBoundTwist[0] = mapping.leftUpperArmTwist;
        sBoundTwist[1] = mapping.leftLowerArmTwist;
        sBoundTwist[2] = mapping.RightUpperArmTwist;
        sBoundTwist[3] = mapping.RightLowerArmTwist;
        Watch(mapping.leftUpperArmTwist, "LeftUpperArmTwist");
        Watch(mapping.leftLowerArmTwist, "LeftLowerArmTwist");
        Watch(mapping.RightUpperArmTwist, "RightUpperArmTwist");
        Watch(mapping.RightLowerArmTwist, "RightLowerArmTwist");
        if (sWatched.Count == 0)
        {
            BasisDebug.LogError("[BoneWriteTracer] the local mapping has no arm twist bones -- nothing to watch. "
                + "AutoDetectReferences finds them by name off the upper/lower arm; if this avatar names them "
                + "something FindTwistBone does not match, the IK never had a handle on them at all and whatever "
                + "the Animator leaves there is all they ever get.", BasisDebug.LogTag.IK);
        }
    }

    /// <summary>
    /// Watches the four transforms <see cref="BasisLocalPose"/> caches. When ValidateHits reports a
    /// stale slot, this is what says which span of the frame moved it and which ancestor carried it
    /// -- the two things the stale report itself cannot know, because it only ever sees the reader.
    /// </summary>
    public static void WatchLocalPoseSlots()
    {
        Basis.Scripts.Common.BasisTransformMapping mapping = BasisLocalAvatarDriver.Mapping;
        BasisLocalPlayer player = BasisLocalPlayer.Instance;
        if (mapping == null || player == null)
        {
            return;
        }
        if (sWatched.Count > 0
            && sBoundSlots[0] == player.transform
            && sBoundSlots[1] == mapping.AnimatorRoot
            && sBoundSlots[2] == mapping.Hips
            && sBoundSlots[3] == mapping.head)
        {
            return;
        }
        Clear();
        sBoundSlots[0] = player.transform;
        sBoundSlots[1] = mapping.AnimatorRoot;
        sBoundSlots[2] = mapping.Hips;
        sBoundSlots[3] = mapping.head;
        Watch(player.transform, "PlayerRoot");
        Watch(mapping.AnimatorRoot, "AvatarRoot");
        Watch(mapping.Hips, "Hips");
        Watch(mapping.head, "Head");
        TrackPosition = true;   // a playspace move or a rescale is a translation, not a rotation
    }


    /// <summary>
    /// Runs a multicast callback one subscriber at a time, sampling the watched transforms around
    /// each, and names the subscriber that moved one.
    ///
    /// From the outside a multicast delegate is a single stack frame and a single Invoke, so a
    /// stale-cache report fired from inside the chain can only ever name the READER. Splitting the
    /// invocation list is the only way to tell the members apart, and the ordering it reveals is
    /// the point: a subscriber that moves a transform an EARLIER subscriber already cached is a
    /// bug the chain cannot see from either end.
    ///
    /// Falls back to a plain invoke when disarmed, so the call site is safe to leave in place.
    /// </summary>
    public static void InvokeTraced<T>(System.Action<T> chain, T argument, string label)
    {
        if (chain == null)
        {
            return;
        }
        if (!Enabled || sWatched.Count == 0)
        {
            chain(argument);
            return;
        }

        System.Delegate[] members = chain.GetInvocationList();
        BasisDebug.Log($"[BoneWriteTracer] {label}: {members.Length} subscriber(s)", BasisDebug.LogTag.IK);
        for (int i = 0; i < members.Length; i++)
        {
            var one = (System.Action<T>)members[i];
            SnapshotProbe();
            try
            {
                one(argument);
            }
            catch (System.Exception e)
            {
                BasisDebug.LogError($"[BoneWriteTracer] {label} subscriber {Describe(one)} threw: {e}", BasisDebug.LogTag.IK);
            }
            ReportProbeDelta(label, i, Describe(one));
        }
    }

    static string Describe(System.Delegate d)
    {
        System.Reflection.MethodInfo m = d.Method;
        string declaring = m.DeclaringType != null ? m.DeclaringType.Name : "?";
        var owner = d.Target as Object;
        return owner != null ? $"{declaring}.{m.Name} on '{owner.name}'" : $"{declaring}.{m.Name}";
    }

    static Vector3[] sProbePosition = System.Array.Empty<Vector3>();
    static Quaternion[] sProbeRotation = System.Array.Empty<Quaternion>();

    static void SnapshotProbe()
    {
        if (sProbePosition.Length != sWatched.Count)
        {
            sProbePosition = new Vector3[sWatched.Count];
            sProbeRotation = new Quaternion[sWatched.Count];
        }
        for (int i = 0; i < sWatched.Count; i++)
        {
            Transform target = sWatched[i].Target;
            if (target != null)
            {
                target.GetPositionAndRotation(out sProbePosition[i], out sProbeRotation[i]);
            }
        }
    }

    static void ReportProbeDelta(string label, int index, string subscriber)
    {
        for (int i = 0; i < sWatched.Count; i++)
        {
            Watched w = sWatched[i];
            if (w.Target == null)
            {
                continue;
            }
            w.Target.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            if (!Differs(position, sProbePosition[i]) && !Differs(rotation, sProbeRotation[i]))
            {
                continue;
            }
            float millimetres = (position - sProbePosition[i]).magnitude * 1000f;
            float degrees = Quaternion.Angle(sProbeRotation[i], rotation);
            BasisDebug.LogError(
                $"[BoneWriteTracer] {label} subscriber #{index} {subscriber} MOVED {w.Label} "
                + $"by {millimetres:F3} mm / {degrees:F3} deg. Any earlier subscriber that cached "
                + $"{w.Label} read a pre-move value; this writer needs a BasisLocalPose.InvalidateAll().",
                BasisDebug.LogTag.IK);
        }
    }

    public static void Watch(Transform target, string label)
    {
        if (target == null)
        {
            return;
        }
        var ancestors = new List<Transform>();
        for (Transform walk = target.parent; walk != null && ancestors.Count < MaxAncestors; walk = walk.parent)
        {
            ancestors.Add(walk);
        }
        sWatched.Add(new Watched
        {
            Target = target,
            Label = label,
            Ancestors = ancestors.ToArray(),
            AncestorLast = new Quaternion[ancestors.Count],
            AncestorLastPosition = new Vector3[ancestors.Count],
        });
    }

    public static void Clear()
    {
        sWatched.Clear();
        sStageTotals.Clear();
        sLastSignature = string.Empty;
        for (int i = 0; i < sBoundTwist.Length; i++)
        {
            sBoundTwist[i] = null;
            sBoundSlots[i] = null;
        }
    }

    /// <summary>
    /// Reads every watched bone and attributes any change to the span since the previous call.
    /// Placed on the same lines as the watchdog's checkpoints, so the stage names already describe
    /// the system that ran.
    /// </summary>
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public static void Checkpoint(string stage)
    {
        if (!Enabled || sWatched.Count == 0)
        {
            return;
        }
        int frame = Time.frameCount;
        if (frame != sFrame)
        {
            Flush();
            sFrame = frame;
            sHoldArmed = false;
        }

        bool holdStage = HoldEnabled && stage == HoldFromStage;
        for (int i = 0; i < sWatched.Count; i++)
        {
            Watched w = sWatched[i];
            Transform target = w.Target;
            if (target == null)
            {
                continue;
            }
            target.GetLocalPositionAndRotation(out Vector3 position, out Quaternion rotation);
            target.GetPositionAndRotation(out Vector3 worldPosition, out Quaternion worldRotation);

            if (!w.Seeded)
            {
                w.Seeded = true;
                w.LastRotation = rotation;
                w.LastPosition = position;
                w.LastWorldRotation = worldRotation;
                w.LastWorldPosition = worldPosition;
                SampleAncestors(w);
                continue;
            }

            bool rotated = Differs(rotation, w.LastRotation);
            bool worldRotated = Differs(worldRotation, w.LastWorldRotation);
            bool moved = TrackPosition && Differs(position, w.LastPosition);
            bool worldMoved = TrackPosition && Differs(worldPosition, w.LastWorldPosition);

            if (rotated || moved || worldRotated || worldMoved)
            {
                float degrees = Quaternion.Angle(w.LastRotation, rotation);
                float worldDegrees = Quaternion.Angle(w.LastWorldRotation, worldRotation);
                float worldMillimetres = (worldPosition - w.LastWorldPosition).magnitude * 1000f;
                if (Mathf.Max(degrees, worldDegrees) >= ReportThresholdDegrees || moved || worldMoved)
                {
                    // A bone nothing wrote, whose world rotation moved anyway, was carried by a
                    // parent -- the case that looks like a phantom writer from the bone's own side.
                    string draggedBy = null;
                    float draggedByDegrees = 0f;
                    if (!rotated && !moved && (worldRotated || worldMoved))
                    {
                        FindDragSource(w, out draggedBy, out draggedByDegrees);
                    }
                    w.Writes.Add(new Write
                    {
                        Stage = stage,
                        Degrees = degrees,
                        WorldDegrees = worldDegrees,
                        Millimetres = worldMillimetres,
                        Overridden = sHoldArmed,
                        DraggedBy = draggedBy,
                        DraggedByDegrees = draggedByDegrees,
                    });
                    WritesLastFrame++;
                    sStageTotals.TryGetValue(stage, out int total);
                    sStageTotals[stage] = total + 1;
                }
            }
            w.LastWorldRotation = worldRotation;
            w.LastWorldPosition = worldPosition;
            SampleAncestors(w);

            if (sHoldArmed)
            {
                // Restore before caching, so the next span is measured against the held value and
                // reports the next writer's own delta rather than the sum of the two.
                if (TrackPosition)
                {
                    target.SetLocalPositionAndRotation(w.HoldPosition, w.HoldRotation);
                    w.LastPosition = w.HoldPosition;
                }
                else
                {
                    target.localRotation = w.HoldRotation;
                    w.LastPosition = position;
                }
                w.LastRotation = w.HoldRotation;
                // Restoring the local changes the world too, and a parent may have turned under
                // it -- so re-read rather than assuming the hold put the world back where it was.
                target.GetPositionAndRotation(out Vector3 heldWorldPos, out Quaternion heldWorldRot);
                w.LastWorldRotation = heldWorldRot;
                w.LastWorldPosition = heldWorldPos;
            }
            else
            {
                w.LastRotation = rotation;
                w.LastPosition = position;
            }

            if (holdStage)
            {
                w.HoldRotation = w.LastRotation;
                w.HoldPosition = w.LastPosition;
            }
        }

        if (holdStage)
        {
            sHoldArmed = true;
        }
    }

    /// <summary>
    /// Bit-exact, matching the pose pipeline's own write-skip. A system that re-writes a value it
    /// did not change produces bit-identical floats, so this reports every real write and no
    /// re-write, and an epsilon would only hide the small ones.
    /// </summary>
    static bool Differs(Quaternion a, Quaternion b)
        => a.x != b.x || a.y != b.y || a.z != b.z || a.w != b.w;

    static bool Differs(Vector3 a, Vector3 b) => a.x != b.x || a.y != b.y || a.z != b.z;

    static void SampleAncestors(Watched w)
    {
        for (int i = 0; i < w.Ancestors.Length; i++)
        {
            Transform ancestor = w.Ancestors[i];
            if (ancestor != null)
            {
                ancestor.GetLocalPositionAndRotation(out Vector3 local, out Quaternion rotation);
                w.AncestorLast[i] = rotation;
                w.AncestorLastPosition[i] = local;
            }
        }
    }

    /// <summary>
    /// Names the nearest ancestor whose own local rotation changed over the same span. That is the
    /// bone the write actually landed on; the watched bone just hangs off it.
    /// </summary>
    static void FindDragSource(Watched w, out string source, out float degrees)
    {
        for (int i = 0; i < w.Ancestors.Length; i++)
        {
            Transform ancestor = w.Ancestors[i];
            if (ancestor == null)
            {
                continue;
            }
            ancestor.GetLocalPositionAndRotation(out Vector3 localPosition, out Quaternion localRotation);
            if (Differs(localRotation, w.AncestorLast[i]) || Differs(localPosition, w.AncestorLastPosition[i]))
            {
                source = ancestor.name;
                degrees = Quaternion.Angle(w.AncestorLast[i], localRotation);
                return;
            }
        }
        // Every ancestor local held, so the chain was moved from above the watched depth -- the
        // avatar root, the playspace, or the player capsule.
        source = "(above the watched chain -- avatar root / playspace / capsule)";
        degrees = 0f;
    }

    static void Flush()
    {
        if (WritesLastFrame == 0)
        {
            ClearFrame();
            return;
        }

        sBuilder.Clear();
        sBuilder.Append("[BoneWriteTracer] frame ").Append(sFrame).AppendLine();
        for (int i = 0; i < sWatched.Count; i++)
        {
            Watched w = sWatched[i];
            if (w.Writes.Count == 0)
            {
                sBuilder.Append("  ").Append(w.Label).AppendLine(": held all frame");
                continue;
            }
            sBuilder.Append("  ").Append(w.Label).Append(": ").Append(w.Writes.Count).AppendLine(" write(s)");
            for (int n = 0; n < w.Writes.Count; n++)
            {
                Write write = w.Writes[n];
                sBuilder.Append("      ").Append(write.Overridden ? "[held] " : "       ").Append(write.Stage);
                if (write.DraggedBy != null)
                {
                    sBuilder.Append("  world ").Append(write.WorldDegrees.ToString("F3")).Append(" deg ")
                        .Append(write.Millimetres.ToString("F3"))
                        .Append(" mm, local unchanged -- DRAGGED BY ").Append(write.DraggedBy);
                    if (write.DraggedByDegrees > 0f)
                    {
                        sBuilder.Append(" (").Append(write.DraggedByDegrees.ToString("F3")).Append(" deg)");
                    }
                }
                else
                {
                    sBuilder.Append("  local ").Append(write.Degrees.ToString("F3"))
                        .Append(" deg, world ").Append(write.WorldDegrees.ToString("F3")).Append(" deg");
                    if (TrackPosition && write.Millimetres > 0f)
                    {
                        sBuilder.Append(" ").Append(write.Millimetres.ToString("F3")).Append(" mm");
                    }
                }
                sBuilder.AppendLine();
            }
        }

        LastReport = sBuilder.ToString();
        string signature = BuildSignature();
        if (LogEveryFrame || signature != sLastSignature)
        {
            sLastSignature = signature;
            BasisDebug.Log(LastReport, BasisDebug.LogTag.IK);
        }
        ClearFrame();
    }

    /// <summary>
    /// The set of stages that wrote, ignoring magnitudes -- so a steady writer logs once instead of
    /// every frame, and a stage appearing or dropping out is what breaks through.
    /// </summary>
    static string BuildSignature()
    {
        sBuilder.Clear();
        for (int i = 0; i < sWatched.Count; i++)
        {
            Watched w = sWatched[i];
            sBuilder.Append(w.Label).Append('=');
            for (int n = 0; n < w.Writes.Count; n++)
            {
                Write write = w.Writes[n];
                sBuilder.Append(write.Stage).Append(write.DraggedBy != null ? "<-" : "=")
                    .Append(write.DraggedBy).Append(',');
            }
            sBuilder.Append(';');
        }
        return sBuilder.ToString();
    }

    static void ClearFrame()
    {
        WritesLastFrame = 0;
        for (int i = 0; i < sWatched.Count; i++)
        {
            sWatched[i].Writes.Clear();
        }
    }

    /// <summary>Every stage that has ever written a watched bone, with its frame count.</summary>
    public static IReadOnlyDictionary<string, int> StageTotals => sStageTotals;
#else
    public static bool Enabled;
    public static bool WatchPoseSlotsInsteadOfTwist;
    public static bool LogEveryFrame;
    public static bool TrackPosition;
    public static float ReportThresholdDegrees;
    public static bool HoldEnabled;
    public static string HoldFromStage = "PostLocalPlayerFinish (IK join + camera)";
    public static string LastReport => string.Empty;
    public static int WritesLastFrame => 0;

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public static void WatchLocalArmTwist()
    {
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public static void WatchLocalPoseSlots()
    {
    }

    /// <summary>Plain invoke in release -- NOT [Conditional], the call site depends on the result.</summary>
    public static void InvokeTraced<T>(System.Action<T> chain, T argument, string label) => chain?.Invoke(argument);

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public static void Watch(Transform target, string label)
    {
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public static void Clear()
    {
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public static void Checkpoint(string stage)
    {
    }
#endif
}
