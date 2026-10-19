using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using UnityEngine;

/// <summary>
/// Editor/dev-build diagnostic for the "Invalid AABB" / "IsFinite(distanceForSort)" console
/// spam: those asserts name no object, and by the time they print, the first NaN has already
/// latched (the pose gather reads transforms back, jiggle integrates it), so every later frame
/// is corrupt and the true injector is unfindable. While armed this scans a cheap set every
/// frame (cameras, local avatar root) and every renderer's bounds on a slow cadence, then logs
/// the FIRST non-finite offender with its full ancestor chain — the deepest non-finite
/// ancestor is where the NaN entered — and disarms so the report stays readable.
///
/// The stage checkpoints are the sharper instrument: <see cref="Checkpoint"/> and
/// <see cref="CheckpointRemote"/> are called on both sides of every system in the frame that
/// writes transforms, so the first stage that trips names the writer rather than a victim.
///
/// Off unless <see cref="Enabled"/> is set — toggle it from Basis/Debug/Finite Watchdog.
/// Every entry point is [Conditional], so the calls themselves are stripped outside the
/// editor and development builds.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisFiniteWatchdog
{
    /// <summary>Master toggle, off by default; the event driver only ticks the scan while this is set.</summary>
    public static bool Enabled;

#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    static bool sDisarmed;
    static float sNextFullSweepTime;

    /// <summary>The renderer sweep walks every renderer's bounds, so it runs on this cadence, not per frame.</summary>
    public static float FullSweepIntervalSeconds = 2f;
    /// <summary>Include remote players in <see cref="CheckpointRemote"/>. Off makes every remote checkpoint free.</summary>
    public static bool ScanRemotePlayers = true;
    /// <summary>
    /// Remote avatars visited per <see cref="CheckpointRemote"/> call. The cursor carries across
    /// calls and frames, so a full lobby is still covered — just spread out, which keeps a
    /// 200-player instance from costing 200 skeleton walks per checkpoint. 0 scans everyone.
    /// </summary>
    public static int RemotePlayersPerCheckpoint = 4;
    /// <summary>
    /// Ignore values that were ALREADY bad when the watchdog started looking. An avatar that is
    /// mid-explosion has permanently dead bones, and without this every arm re-reports the first of
    /// them at the first checkpoint that runs — which names a victim, never a writer. The event
    /// worth catching is a value that was finite and just became bad.
    /// </summary>
    public static bool IgnorePreexisting = true;
    /// <summary>
    /// Frames after arming during which bad values are recorded into the ignore set instead of
    /// reported. Needs to outlast one full remote round-robin sweep, so the default is generous.
    /// </summary>
    public static int PrimeFrames = 30;
    /// <summary>Beyond this a value is reported even when finite — culling breaks on absurd magnitudes too.</summary>
    const float k_AbsurdMagnitude = 1e12f;
    /// <summary>Avatar swaps leave dead roots in the per-player skeleton cache; drop the lot once it grows past this.</summary>
    const int k_RemoteCacheCap = 512;
    /// <summary>Destroyed bones linger as ignore-set keys; drop the lot rather than walk it looking for them.</summary>
    const int k_IgnoreCap = 4096;

    /// <summary>True once a report has fired; re-arm from the debug window to hunt again.</summary>
    public static bool Disarmed => sDisarmed;
    /// <summary>The full text of the last report, for the debug window.</summary>
    public static string LastReport { get; private set; } = string.Empty;
    /// <summary>The last stage that scanned clean — the writer sits between it and the stage that tripped.</summary>
    public static string LastCleanStage { get; private set; } = string.Empty;
    /// <summary>Checkpoint calls that ran on the previous frame, so the window can show the coverage in place.</summary>
    public static int CheckpointsLastFrame { get; private set; }
    /// <summary>Transforms read on the previous frame — this is what arming the watchdog actually costs.</summary>
    public static int TransformsScannedLastFrame { get; private set; }
    /// <summary>True while still recording pre-existing damage rather than reporting it.</summary>
    public static bool Priming => IgnorePreexisting && Time.frameCount < sPrimeUntilFrame;
    /// <summary>Frames left in the prime window, for the debug window.</summary>
    public static int PrimeFramesRemaining => Mathf.Max(0, sPrimeUntilFrame - Time.frameCount);
    /// <summary>How much already-broken state is being deliberately skipped.</summary>
    public static int IgnoredCount => sIgnoredTransforms.Count + sIgnoredValues.Count;

    static int sCheckpointCount;
    static int sScannedCount;
    static int sPrimeUntilFrame;
    static bool sWasEnabled;
    static readonly HashSet<Transform> sIgnoredTransforms = new HashSet<Transform>();
    static readonly HashSet<string> sIgnoredValues = new HashSet<string>();

    public static void Rearm()
    {
        sDisarmed = false;
        LastReport = string.Empty;
        LastCleanStage = string.Empty;
        sIgnoredTransforms.Clear();
        sIgnoredValues.Clear();
        BeginPrime();
    }

    static void BeginPrime()
    {
        sWasEnabled = true;
        sPrimeUntilFrame = Time.frameCount + Mathf.Max(1, PrimeFrames);
    }

    /// <summary>
    /// Opens the prime window on the frame the watchdog is switched on. Checkpoints run ahead of
    /// <see cref="Tick"/> in the frame, so the edge cannot be detected there alone.
    /// </summary>
    static void EnsurePrimeWindow()
    {
        if (!sWasEnabled)
        {
            BeginPrime();
        }
    }

    /// <summary>
    /// False when this transform was already bad before the watchdog was watching — the caller
    /// keeps scanning rather than reporting, so a genuinely fresh failure elsewhere still lands.
    /// </summary>
    static bool ShouldReport(Transform offender)
    {
        if (!IgnorePreexisting || offender == null)
        {
            return true;
        }
        if (sIgnoredTransforms.Contains(offender))
        {
            return false;
        }
        if (Time.frameCount < sPrimeUntilFrame)
        {
            if (sIgnoredTransforms.Count > k_IgnoreCap)
            {
                sIgnoredTransforms.Clear();
            }
            sIgnoredTransforms.Add(offender);
            return false;
        }
        return true;
    }

    /// <summary>Same rule for the non-transform checks, keyed by the label the caller composed.</summary>
    static bool ShouldReportValue(string what)
    {
        if (!IgnorePreexisting || what == null)
        {
            return true;
        }
        if (sIgnoredValues.Contains(what))
        {
            return false;
        }
        if (Time.frameCount < sPrimeUntilFrame)
        {
            if (sIgnoredValues.Count > k_IgnoreCap)
            {
                sIgnoredValues.Clear();
            }
            sIgnoredValues.Add(what);
            return false;
        }
        return true;
    }

    /// <summary>
    /// The lead line of every report. With no stage having scanned clean since arming, the value
    /// predates the watchdog and the stage that found it is not the stage that wrote it — saying so
    /// is the difference between a lead and a wild goose chase.
    /// </summary>
    static string DescribeWindow(string stage)
    {
        if (string.IsNullOrEmpty(LastCleanStage))
        {
            return $"STAGE '{stage}' — NO checkpoint has scanned clean since the watchdog was armed, so this value was ALREADY bad before it started watching. This names a VICTIM, not the writer. Re-arm (the prime window ignores damage that is already there) and reproduce from a clean avatar to catch the write itself.";
        }
        return $"STAGE '{stage}' — whatever ran between '{LastCleanStage}' and this checkpoint wrote it.";
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void Tick()
    {
        if (!Enabled)
        {
            sWasEnabled = false;
            return;
        }
        EnsurePrimeWindow();
        if (sDisarmed)
        {
            return;
        }
        CheckpointsLastFrame = sCheckpointCount;
        TransformsScannedLastFrame = sScannedCount;
        sCheckpointCount = 0;
        sScannedCount = 0;
        try
        {
            if (!IsSane(BasisLocalCameraDriver.Position))
            {
                Report("BasisLocalCameraDriver.Position (camera static)", null, BasisLocalCameraDriver.Position);
                return;
            }

            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera cam = cameras[i];
                if (cam != null && !IsSane(cam.transform.position))
                {
                    Report($"Camera '{cam.name}' transform", cam.transform, cam.transform.position);
                    return;
                }
            }

            if (BasisLocalPlayer.PlayerReady && BasisLocalPlayer.Instance != null)
            {
                var avatar = BasisLocalPlayer.Instance.BasisAvatar;
                if (avatar != null && avatar.transform != null && !IsSane(avatar.transform.position))
                {
                    Report("local avatar root", avatar.transform, avatar.transform.position);
                    return;
                }
            }

            if (Time.unscaledTime < sNextFullSweepTime)
            {
                return;
            }
            sNextFullSweepTime = Time.unscaledTime + Mathf.Max(0.25f, FullSweepIntervalSeconds);

            Renderer[] renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
            Renderer first = null;
            int offenderCount = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || !r.enabled)
                {
                    continue;
                }
                Bounds b = r.bounds;
                if (!IsSane(b.center) || !IsSane(b.extents))
                {
                    offenderCount++;
                    if (first == null)
                    {
                        first = r;
                    }
                }
            }
            if (first != null)
            {
                Bounds bounds = first.bounds;
                var detail = new StringBuilder(512);
                detail.Append($"bounds center={bounds.center} extents={bounds.extents}");
                if (first is SkinnedMeshRenderer skinned)
                {
                    DescribeSkinned(skinned, detail);
                }
                Report($"{first.GetType().Name} '{first.name}' ({offenderCount} non-finite renderer(s) this sweep). {detail}", first.transform, bounds.center);
            }
        }
        catch (System.Exception e)
        {
            sDisarmed = true;
            BasisDebug.LogError($"[FiniteWatchdog] scan threw and disarmed: {e}", BasisDebug.LogTag.Core);
        }
    }

    static bool IsSane(Vector3 v)
    {
        return float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z)
            && Mathf.Abs(v.x) < k_AbsurdMagnitude && Mathf.Abs(v.y) < k_AbsurdMagnitude && Mathf.Abs(v.z) < k_AbsurdMagnitude;
    }

    /// <summary>
    /// A zero-length quaternion is entirely finite, so a component-wise IsFinite check calls it
    /// healthy — but Unity normalizes the rotation when it composes the matrix, so it yields a NaN
    /// world matrix for the transform and every descendant. Degeneracy has to be checked too, or
    /// the scan reports the victims and calls the writer's own bone "ok".
    /// </summary>
    static bool IsSaneRotation(Quaternion q)
    {
        float lengthSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        return float.IsFinite(lengthSq) && lengthSq > 1e-8f;
    }

    static string DescribeRotation(Quaternion q)
    {
        float lengthSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        return $"({q.x:F4},{q.y:F4},{q.z:F4},{q.w:F4}) lengthSq={lengthSq:F6}";
    }

    /// <summary>
    /// Stage-tagged scan of the LOCAL transform values the local player owns — the avatar
    /// skeleton, the player/playspace chain above it, and the camera chain. A non-finite world
    /// position is inherited, so it only ever names a victim; a bad LOCAL value is always a direct
    /// write. Call it either side of each system that poses bones and the first stage that trips
    /// names the writer.
    /// </summary>
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void Checkpoint(string stage)
    {
        if (!Enabled || sDisarmed)
        {
            return;
        }
        EnsurePrimeWindow();
        sCheckpointCount++;
        try
        {
            if (!ScanLocal(stage))
            {
                LastCleanStage = stage;
            }
        }
        catch (System.Exception e)
        {
            sDisarmed = true;
            BasisDebug.LogError($"[FiniteWatchdog] checkpoint '{stage}' threw and disarmed: {e}", BasisDebug.LogTag.Core);
        }
    }

    /// <summary>
    /// The same stage-tagged local-TRS scan, run over remote players instead: their avatar
    /// skeletons plus the mouth marker and nameplate, which are job-positioned scene roots of
    /// their own. Remote avatars share slot-indexed job state with the local one (the bone
    /// pipeline and jiggle both index parallel arrays), so a corrupt remote is both a first-class
    /// symptom and the usual way corruption reaches the local avatar — a local-only scan reports
    /// the wrong end of that. Budgeted by <see cref="RemotePlayersPerCheckpoint"/>.
    /// </summary>
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void CheckpointRemote(string stage)
    {
        if (!Enabled || sDisarmed || !ScanRemotePlayers)
        {
            return;
        }
        EnsurePrimeWindow();
        sCheckpointCount++;
        try
        {
            if (!ScanRemote(stage))
            {
                LastCleanStage = stage;
            }
        }
        catch (System.Exception e)
        {
            sDisarmed = true;
            BasisDebug.LogError($"[FiniteWatchdog] remote checkpoint '{stage}' threw and disarmed: {e}", BasisDebug.LogTag.Core);
        }
    }

    /// <summary>
    /// Scans one named remote player instead of the round-robin slice. For the install paths —
    /// calibration, far-LOD swap, bone-job (re)registration — where the player that was just
    /// written is known and is the only one worth looking at.
    /// </summary>
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void CheckpointRemote(string stage, BasisRemotePlayer remote)
    {
        if (!Enabled || sDisarmed || !ScanRemotePlayers || remote == null || remote.IsDestroyed)
        {
            return;
        }
        EnsurePrimeWindow();
        sCheckpointCount++;
        try
        {
            if (!ScanRemotePlayer(stage, remote))
            {
                LastCleanStage = stage;
            }
        }
        catch (System.Exception e)
        {
            sDisarmed = true;
            BasisDebug.LogError($"[FiniteWatchdog] remote checkpoint '{stage}' threw and disarmed: {e}", BasisDebug.LogTag.Core);
        }
    }

    /// <summary>
    /// Non-transform predicate for callers that hold the value in native/job state the watchdog
    /// cannot reach on its own (filter slots, pose streams, bone-control data). Test with this and
    /// only build the label for <see cref="ReportValue"/> on failure — an interpolated string per
    /// slot per frame is the whole cost otherwise.
    /// </summary>
    public static bool IsNonFinite(Vector3 v) => !IsSane(v);

    /// <summary>Rotation counterpart — a zero-length quaternion is finite but still degenerate.</summary>
    public static bool IsNonFinite(Quaternion q) => !IsSaneRotation(q);

    /// <summary>Reports a bad value that lives outside the transform hierarchy, then disarms.</summary>
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void ReportValue(string stage, string what, string detail)
    {
        if (!Enabled || sDisarmed || !ShouldReportValue(what))
        {
            return;
        }
        sDisarmed = true;
        var sb = new StringBuilder(512);
        sb.AppendLine($"[FiniteWatchdog] {DescribeWindow(stage)} Non-finite value OUTSIDE the transform hierarchy. Watchdog disarmed.");
        sb.AppendLine($"  {what} = {detail}");
        sb.Append("  This value never reached a transform, so the transform scans would have named a victim instead of the writer.");
        LastReport = sb.ToString();
        BasisDebug.LogError(LastReport, BasisDebug.LogTag.Core);
    }

    /// <summary>
    /// Scans the local bone controls' pose data — the IK's own inputs, which live in a native store
    /// rather than on any transform. Splits the three stages apart (incoming device/virtual pose,
    /// the chain job's outgoing local pose, and the world pose the rig driver actually samples), so
    /// a bad head target is attributed to the device, the chain solve, or neither.
    /// </summary>
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void CheckpointBoneControls(string stage)
    {
        if (!Enabled || sDisarmed)
        {
            return;
        }
        EnsurePrimeWindow();
        sCheckpointCount++;
        try
        {
            if (!ScanBoneControls(stage))
            {
                LastCleanStage = stage;
            }
        }
        catch (System.Exception e)
        {
            sDisarmed = true;
            BasisDebug.LogError($"[FiniteWatchdog] bone-control checkpoint '{stage}' threw and disarmed: {e}", BasisDebug.LogTag.Core);
        }
    }

    static bool ScanBoneControls(string stage)
    {
        if (!BasisLocalPlayer.PlayerReady || BasisLocalPlayer.Instance == null)
        {
            return false;
        }
        var driver = BasisLocalPlayer.Instance.LocalBoneDriver;
        if (driver == null || !driver.HasControls || driver.Controls == null)
        {
            return false;
        }
        var controls = driver.Controls;
        for (int i = 0; i < controls.Length; i++)
        {
            var control = controls[i];
            if (control == null)
            {
                continue;
            }
            sScannedCount++;

            var incoming = control.IncomingData;
            if (!IsSane(incoming.position) || !IsSaneRotation(incoming.rotation))
            {
                ReportValue(stage, $"bone control '{control.Role}' IncomingData (device / virtual pose)",
                    $"position={incoming.position} rotation={DescribeRotation(incoming.rotation)}");
                return true;
            }

            var outgoing = control.OutGoingData;
            if (!IsSane(outgoing.position) || !IsSaneRotation(outgoing.rotation))
            {
                ReportValue(stage, $"bone control '{control.Role}' OutGoingData (chain job local output)",
                    $"position={outgoing.position} rotation={DescribeRotation(outgoing.rotation)}");
                return true;
            }

            var world = control.OutgoingWorldData;
            if (!IsSane(world.position) || !IsSaneRotation(world.rotation))
            {
                ReportValue(stage, $"bone control '{control.Role}' OutgoingWorldData (the IK's raw target)",
                    $"position={world.position} rotation={DescribeRotation(world.rotation)}");
                return true;
            }
        }
        return false;
    }

    static Transform[] sCheckpointTransforms;
    static Transform sCheckpointRoot;
    static readonly Dictionary<Transform, Transform[]> sRemoteTransforms = new Dictionary<Transform, Transform[]>();
    static int sRemoteCursor;
    static int sCacheBuildFrame = -1;
    static int sCacheBuildsThisFrame;

    /// <summary>
    /// Caps skeleton-cache rebuilds per frame. A cache miss costs a
    /// <c>GetComponentsInChildren</c> over a whole avatar, and the checkpoints run dozens of times
    /// a frame — so an avatar-swap burst (turning around reloads every far LOD at once) would
    /// otherwise turn every one of those calls into a hierarchy walk plus an allocation. A skipped
    /// player is simply visited on a later frame; the round-robin cursor already spreads coverage.
    /// </summary>
    static bool CanBuildCache()
    {
        int frame = Time.frameCount;
        if (frame != sCacheBuildFrame)
        {
            sCacheBuildFrame = frame;
            sCacheBuildsThisFrame = 0;
        }
        if (sCacheBuildsThisFrame >= 2)
        {
            return false;
        }
        sCacheBuildsThisFrame++;
        return true;
    }

    static bool ScanLocal(string stage)
    {
        if (!BasisLocalPlayer.PlayerReady || BasisLocalPlayer.Instance == null)
        {
            return false;
        }
        var avatar = BasisLocalPlayer.Instance.BasisAvatar;
        if (avatar == null || avatar.transform == null)
        {
            return false;
        }
        Transform root = avatar.transform;
        if (!ReferenceEquals(root, sCheckpointRoot) || sCheckpointTransforms == null)
        {
            if (!CanBuildCache())
            {
                return false;
            }
            sCheckpointRoot = root;
            sCheckpointTransforms = root.GetComponentsInChildren<Transform>(true);
        }

        if (ScanChainUp(stage, "local player chain", null, root.parent))
        {
            return true;
        }

        bool sawDestroyed = false;
        if (ScanSet(stage, "local avatar", null, sCheckpointTransforms, ref sawDestroyed))
        {
            return true;
        }
        if (sawDestroyed)
        {
            sCheckpointTransforms = null;
            sCheckpointRoot = null;
        }

        Camera cam = BasisLocalCameraDriver.CameraInstance;
        if (cam != null && ScanChainUp(stage, "local camera chain", null, cam.transform))
        {
            return true;
        }
        return false;
    }

    static bool ScanRemote(string stage)
    {
        int count = BasisNetworkPlayers.ReceiverCount;
        var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        if (snapshot == null || count <= 0)
        {
            return false;
        }
        if (count > snapshot.Length)
        {
            count = snapshot.Length;
        }

        int budget = RemotePlayersPerCheckpoint <= 0 ? count : Mathf.Min(count, RemotePlayersPerCheckpoint);
        for (int scanned = 0; scanned < budget; scanned++)
        {
            if (sRemoteCursor >= count)
            {
                sRemoteCursor = 0;
            }
            var receiver = snapshot[sRemoteCursor];
            sRemoteCursor++;
            if (receiver == null)
            {
                continue;
            }
            BasisRemotePlayer remote = receiver.RemotePlayer;
            if (remote == null || remote.IsDestroyed)
            {
                continue;
            }
            if (ScanRemotePlayer(stage, remote))
            {
                return true;
            }
        }
        return false;
    }

    static bool ScanRemotePlayer(string stage, BasisRemotePlayer remote)
    {
        string name = string.IsNullOrEmpty(remote.SafeDisplayName) ? remote.UUID : remote.SafeDisplayName;

        Transform mouth = remote.MouthTransform;
        if (mouth != null && ScanChainUp(stage, "remote mouth marker", name, mouth))
        {
            return true;
        }

        Transform plate = remote.NamePlateTransformProvider?.Invoke();
        if (plate != null && ScanChainUp(stage, "remote nameplate", name, plate))
        {
            return true;
        }

        var avatar = remote.BasisAvatar;
        if (avatar == null || avatar.transform == null)
        {
            return false;
        }
        Transform root = avatar.transform;
        if (!sRemoteTransforms.TryGetValue(root, out Transform[] set) || set == null)
        {
            if (!CanBuildCache())
            {
                return false;
            }
            if (sRemoteTransforms.Count > k_RemoteCacheCap)
            {
                sRemoteTransforms.Clear();
            }
            set = root.GetComponentsInChildren<Transform>(true);
            sRemoteTransforms[root] = set;
        }

        bool sawDestroyed = false;
        bool tripped = ScanSet(stage, "remote avatar", name, set, ref sawDestroyed);
        if (sawDestroyed)
        {
            sRemoteTransforms.Remove(root);
        }
        return tripped;
    }

    static bool ScanSet(string stage, string ownerKind, string ownerName, Transform[] set, ref bool sawDestroyed)
    {
        for (int i = 0; i < set.Length; i++)
        {
            Transform t = set[i];
            if (t == null)
            {
                sawDestroyed = true;
                continue;
            }
            sScannedCount++;
            t.GetLocalPositionAndRotation(out Vector3 localPosition, out Quaternion localRotation);
            Vector3 localScale = t.localScale;
            if (IsSane(localPosition) && IsSane(localScale) && IsSaneRotation(localRotation))
            {
                continue;
            }
            if (!ShouldReport(t))
            {
                continue;
            }
            ReportCheckpoint(stage, ownerKind, ownerName, t, localPosition, localRotation, localScale);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Walks a transform and its ancestors. The avatar skeleton scan starts at the avatar root, so
    /// anything above it — the player capsule, the playspace, a seat the player is parented to —
    /// is invisible to it, and a NaN written there shows up as every bone's world position going
    /// bad while every local value reads clean.
    /// </summary>
    static bool ScanChainUp(string stage, string ownerKind, string ownerName, Transform from)
    {
        for (Transform walk = from; walk != null; walk = walk.parent)
        {
            sScannedCount++;
            walk.GetLocalPositionAndRotation(out Vector3 localPosition, out Quaternion localRotation);
            Vector3 localScale = walk.localScale;
            if (IsSane(localPosition) && IsSane(localScale) && IsSaneRotation(localRotation))
            {
                continue;
            }
            if (!ShouldReport(walk))
            {
                continue;
            }
            ReportCheckpoint(stage, ownerKind, ownerName, walk, localPosition, localRotation, localScale);
            return true;
        }
        return false;
    }

    static void ReportCheckpoint(string stage, string ownerKind, string ownerName, Transform offender, Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
    {
        sDisarmed = true;
        var sb = new StringBuilder(768);
        sb.AppendLine($"[FiniteWatchdog] {DescribeWindow(stage)} First bad LOCAL transform value. Watchdog disarmed.");
        sb.AppendLine($"  owner: {ownerKind}{(string.IsNullOrEmpty(ownerName) ? string.Empty : $" '{ownerName}'")}");
        sb.AppendLine($"  transform '{offender.name}' localPos={localPosition} localRot={DescribeRotation(localRotation)} localScale={localScale}");
        sb.Append("  path: ");
        for (Transform walk = offender; walk != null; walk = walk.parent)
        {
            sb.Append('/').Append(walk.name);
        }
        sb.AppendLine();
        sb.AppendLine("  ancestors (deepest BAD is the true origin; a degenerate rotation NaNs every descendant):");
        for (Transform walk = offender.parent; walk != null; walk = walk.parent)
        {
            Vector3 lp = walk.localPosition;
            Quaternion lr = walk.localRotation;
            Vector3 ls = walk.localScale;
            bool ok = IsSane(lp) && IsSaneRotation(lr) && IsSane(ls);
            sb.AppendLine($"    {(ok ? "ok " : "BAD")} '{walk.name}' localPos={lp} localRot={DescribeRotation(lr)} localScale={ls}");
        }
        LastReport = sb.ToString();
        BasisDebug.LogError(LastReport, BasisDebug.LogTag.Core);
    }

    /// <summary>
    /// NaN world bounds with a finite root bone + finite ancestors means the bounds came from
    /// the skinned vertex path — the culprit is a skeleton bone outside the ancestor chain, a
    /// blendshape weight, or the mesh data itself. Enumerate exactly which, so the report names
    /// the system that wrote it (jiggle/finger/eye bone names vs viseme/blink weight indices).
    /// </summary>
    static void DescribeSkinned(SkinnedMeshRenderer skinned, StringBuilder detail)
    {
        Transform rootBone = skinned.rootBone;
        detail.Append(rootBone != null
            ? $" rootBone='{rootBone.name}' rootBonePos={rootBone.position} rootBoneScale={rootBone.lossyScale}"
            : " rootBone=<null>");
        detail.Append($" localBounds center={skinned.localBounds.center} extents={skinned.localBounds.extents}");
        detail.Append($" updateWhenOffscreen={skinned.updateWhenOffscreen}");

        Transform[] bones = skinned.bones;
        int badBones = 0;
        for (int i = 0; i < bones.Length; i++)
        {
            Transform bone = bones[i];
            if (bone == null)
            {
                detail.Append($"\n  BONE[{i}] <destroyed>");
                badBones++;
                continue;
            }
            Vector3 wp = bone.position;
            Vector3 ls = bone.localScale;
            Quaternion lr = bone.localRotation;
            bool ok = IsSane(wp) && IsSane(ls) && IsSaneRotation(lr);
            if (!ok && badBones < 12)
            {
                detail.Append($"\n  BONE[{i}] BAD '{bone.name}' worldPos={wp} localRot={DescribeRotation(lr)} localScale={ls}");
                for (Transform walk = bone.parent; walk != null; walk = walk.parent)
                {
                    Vector3 plp = walk.localPosition;
                    Quaternion plr = walk.localRotation;
                    bool pOk = IsSane(plp) && IsSaneRotation(plr) && IsSane(walk.localScale);
                    if (!pOk)
                    {
                        detail.Append($" ← parent BAD '{walk.name}' localPos={plp} localRot={DescribeRotation(plr)}");
                    }
                }
            }
            if (!ok)
            {
                badBones++;
            }
        }
        detail.Append($"\n  skeleton: {badBones}/{bones.Length} bones non-finite or destroyed");

        Mesh mesh = skinned.sharedMesh;
        if (mesh != null && mesh.blendShapeCount > 0)
        {
            int badWeights = 0;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                float w = skinned.GetBlendShapeWeight(i);
                if (!float.IsFinite(w) || Mathf.Abs(w) > k_AbsurdMagnitude)
                {
                    if (badWeights < 8)
                    {
                        detail.Append($"\n  BLENDSHAPE[{i}] BAD '{mesh.GetBlendShapeName(i)}' weight={w}");
                    }
                    badWeights++;
                }
            }
            detail.Append($"\n  blendshapes: {badWeights}/{mesh.blendShapeCount} weights non-finite");
        }
        detail.Append("\n  (0 bad bones AND 0 bad weights with NaN bounds ⇒ the shared mesh's own vertex data is NaN)");
    }

    static void Report(string what, Transform offender, Vector3 value)
    {
        if (!ShouldReportValue(what))
        {
            return;
        }
        sDisarmed = true;
        var sb = new StringBuilder(512);
        sb.AppendLine($"[FiniteWatchdog] FIRST non-finite/absurd value detected: {what} = {value}. Watchdog disarmed — everything after this frame is downstream corruption, THIS is the injection site.");
        if (!string.IsNullOrEmpty(LastCleanStage))
        {
            sb.AppendLine($"Last clean stage checkpoint: '{LastCleanStage}'.");
        }
        if (offender != null)
        {
            sb.AppendLine("Ancestor chain (deepest non-finite ancestor is where the NaN entered):");
            for (Transform walk = offender; walk != null; walk = walk.parent)
            {
                Vector3 lp = walk.localPosition;
                Quaternion lr = walk.localRotation;
                Vector3 ls = walk.localScale;
                bool ok = IsSane(lp) && IsSaneRotation(lr) && IsSane(ls);
                sb.AppendLine($"  {(ok ? "ok " : "BAD")} '{walk.name}' localPos={lp} localRot={DescribeRotation(lr)} localScale={ls}");
            }
        }
        LastReport = sb.ToString();
        BasisDebug.LogError(LastReport, BasisDebug.LogTag.Core);
    }
#else
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void Tick()
    {
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void Checkpoint(string stage)
    {
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void CheckpointRemote(string stage)
    {
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void CheckpointRemote(string stage, Basis.Scripts.BasisSdk.Players.BasisRemotePlayer remote)
    {
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void CheckpointBoneControls(string stage)
    {
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
    public static void ReportValue(string stage, string what, string detail)
    {
    }

    // Callers test with these inside [Conditional] method bodies, which still compile in release —
    // so unlike the reporting entry points these cannot live behind the #if.
    public static bool IsNonFinite(Vector3 v) => false;

    public static bool IsNonFinite(Quaternion q) => false;
#endif
}
