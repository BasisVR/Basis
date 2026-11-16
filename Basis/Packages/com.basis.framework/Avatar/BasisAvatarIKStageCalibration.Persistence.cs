using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Basis.Scripts.Avatar
{
    public static partial class BasisAvatarIKStageCalibration
    {
        /// <summary>
        /// Remembers the full-body calibration between sessions. Basis already restores a tracker's role and
        /// offsets when it reconnects mid-session (<see cref="BasisDeviceManagement.TryRestoreCachedRole"/>);
        /// this saves the same data plus the body-wide calibration state after every
        /// <see cref="FullBodyCalibration"/>, and on the next launch feeds each saved tracker back through that
        /// reconnect path once the local avatar is ready. A fresh calibration always replaces the save.
        /// </summary>
        [AutoStaticsCleanup]
        public static partial class Persistence
        {
            public const string FileName = "fbtCalibration.json";
            public const int CurrentVersion = 1;
            public const int ReferenceCount = 9;
            public const float ReadyTimeoutSeconds = 60f;

            [Serializable]
            public class SavedTracker
            {
                public string Subsystem = string.Empty;
                public string Serial = string.Empty;
                public string UniqueId = string.Empty;
                public BasisBoneTrackedRole Role;
                public Vector3 OffsetPosition;
                public Quaternion OffsetRotation = Quaternion.identity;
                public bool HasSnapshot;
                public Vector3 Position;
                public Quaternion Rotation = Quaternion.identity;
                public Vector3 HeadPosition;
                public Quaternion HeadRotation = Quaternion.identity;
                public bool HasHeadFromEye;
                public Vector3 HeadFromEye;
            }

            [Serializable]
            public struct RoleVector
            {
                public BasisBoneTrackedRole Role;
                public Vector3 Value;
            }

            [Serializable]
            public struct RoleRotation
            {
                public BasisBoneTrackedRole Role;
                public Quaternion Value;
            }

            [Serializable]
            public class SavedCalibration
            {
                public int Version = CurrentVersion;
                public long SavedAtUtcTicks;
                public List<SavedTracker> Trackers = new List<SavedTracker>();
                public bool HasReference;
                // Fixed order: Head, Hips, Chest, LeftFoot, RightFoot, LeftToe, RightToe, LeftShoulder, RightShoulder.
                public Quaternion[] References = new Quaternion[0];
                public bool HasHeadSnapshot;
                public Vector3 HeadPosition;
                public Quaternion HeadRotation = Quaternion.identity;
                public List<RoleVector> HintBias = new List<RoleVector>();
                public List<RoleRotation> LimbToBone = new List<RoleRotation>();
                public List<RoleRotation> LimbToBody = new List<RoleRotation>();
                public bool HasHip;
                public float EyeToHipDrop;
                public float HipHeight;
            }

            public static event Action OnChanged;

            public static string filePath;
            public static string FilePath => filePath ??= Path.Combine(Application.persistentDataPath, FileName);

            public static SavedCalibration saved;
            public static bool loaded;
            public static readonly List<SavedTracker> pending = new List<SavedTracker>();
            public static bool sessionCalibrated;
            public static bool globalsApplied;
            public static bool restoring;
            public static bool waitingForReady;
            public static bool finishScheduled;
            public static int restoredCount;

            public static bool HasSaved
            {
                get
                {
                    EnsureLoaded();
                    return saved != null;
                }
            }

            /// <summary>"4 trackers, 16 Nov 21:40", or empty when nothing is saved.</summary>
            public static string SavedSummary
            {
                get
                {
                    EnsureLoaded();
                    if (saved == null)
                    {
                        return string.Empty;
                    }
                    string when = new DateTime(saved.SavedAtUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
                    return BasisLocalization.Get("settings.bodyTracking.rememberCalibration.summary", saved.Trackers.Count, when);
                }
            }

            #region Save

            /// <summary>Called at the end of a successful <see cref="FullBodyCalibration"/>.</summary>
            public static void SaveAfterCalibration()
            {
                EnsureLoaded();
                if (restoring)
                {
                    return;
                }
                // A fresh calibration always wins over whatever was still waiting to be restored.
                sessionCalibrated = true;
                pending.Clear();

                if (!BasisSettingsDefaults.RememberFullBodyCalibration.RawValue)
                {
                    return;
                }

                SavedCalibration data = new SavedCalibration { SavedAtUtcTicks = DateTime.UtcNow.Ticks };
                BasisDeviceManagement dm = BasisDeviceManagement.Instance;
                if (dm != null)
                {
                    BasisObservableList<BasisInput> devices = dm.AllInputDevices;
                    int count = devices.Count;
                    for (int Index = 0; Index < count; Index++)
                    {
                        BasisInput input = devices[Index];
                        if (input == null || !input.hasRoleAssigned || !input.HasControl || input.Control == null)
                        {
                            continue;
                        }
                        if (!input.TryGetRole(out BasisBoneTrackedRole role) || !BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(role))
                        {
                            continue;
                        }
                        BasisCalibratedCoords offset = input.Control.InverseOffsetFromBone;
                        data.Trackers.Add(new SavedTracker
                        {
                            Subsystem = input.SubSystemIdentifier ?? string.Empty,
                            Serial = input.DeviceSerial ?? string.Empty,
                            UniqueId = input.UniqueDeviceIdentifier ?? string.Empty,
                            Role = role,
                            OffsetPosition = offset.position,
                            OffsetRotation = offset.rotation,
                            HasSnapshot = input.HasCalibratedOffsetSnapshot,
                            Position = input.CalibratedUnscaledPosition,
                            Rotation = input.CalibratedUnscaledRotation,
                            HeadPosition = input.CalibratedUnscaledHeadPosition,
                            HeadRotation = input.CalibratedUnscaledHeadRotation,
                            HasHeadFromEye = input.HasCalibratedHeadFromEye,
                            HeadFromEye = input.CalibratedUnscaledHeadFromEye,
                        });
                    }
                }

                // Calibrated without trackers: a saved calibration would be stale.
                if (data.Trackers.Count == 0)
                {
                    if (saved != null)
                    {
                        BasisDebug.Log("Full-body calibration has no trackers, forgetting the saved one.", BasisDebug.LogTag.Device);
                    }
                    Forget();
                    return;
                }

                data.HasReference = HasCalibrationReference;
                data.References = new[]
                {
                    s_refHead, s_refHips, s_refChest, s_refLeftFoot, s_refRightFoot,
                    s_refLeftToe, s_refRightToe, s_refLeftShoulder, s_refRightShoulder,
                };
                data.HasHeadSnapshot = HasCalibrationHeadSnapshot;
                data.HeadPosition = s_calibHeadUnscaledPos;
                data.HeadRotation = s_calibHeadUnscaledRot;
                foreach (KeyValuePair<BasisBoneTrackedRole, Vector3> pair in BasisHintBiasStore.LocalOffset)
                {
                    data.HintBias.Add(new RoleVector { Role = pair.Key, Value = pair.Value });
                }
                foreach (KeyValuePair<BasisBoneTrackedRole, Quaternion> pair in BasisLimbRollStore.TrackerToBone)
                {
                    data.LimbToBone.Add(new RoleRotation { Role = pair.Key, Value = pair.Value });
                }
                foreach (KeyValuePair<BasisBoneTrackedRole, Quaternion> pair in BasisLimbRollStore.TrackerToBody)
                {
                    data.LimbToBody.Add(new RoleRotation { Role = pair.Key, Value = pair.Value });
                }
                data.HasHip = BasisHeightDriver.HasGenuinePlayerHipHeight;
                data.EyeToHipDrop = BasisHeightDriver.PlayerEyeToHipDrop;
                data.HipHeight = BasisHeightDriver.PlayerHipHeight;

                try
                {
                    WriteAllTextAtomic(FilePath, JsonUtility.ToJson(data, true));
                }
                catch (Exception e)
                {
                    BasisDebug.LogWarning($"Could not save full-body calibration: {e.Message}", BasisDebug.LogTag.Device);
                    return;
                }
                saved = data;
                BasisDebug.Log($"Saved full-body calibration: {DescribeTrackers(data)}", BasisDebug.LogTag.Device);
                OnChanged?.Invoke();
            }

            /// <summary>Deletes the saved calibration. The current session's calibration is left alone.</summary>
            public static void Forget()
            {
                EnsureLoaded();
                bool had = saved != null;
                saved = null;
                pending.Clear();
                try
                {
                    if (File.Exists(FilePath))
                    {
                        File.Delete(FilePath);
                    }
                }
                catch (Exception e)
                {
                    BasisDebug.LogWarning($"Could not delete the saved full-body calibration: {e.Message}", BasisDebug.LogTag.Device);
                }
                if (had)
                {
                    OnChanged?.Invoke();
                }
            }

            public static void WriteAllTextAtomic(string path, string contents)
            {
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, contents);
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }

            #endregion

            #region Load

            public static void EnsureLoaded()
            {
                if (loaded)
                {
                    return;
                }
                loaded = true;
                saved = TryRead();
                pending.Clear();
                if (saved != null)
                {
                    pending.AddRange(saved.Trackers);
                    BasisDebug.Log($"Loaded saved full-body calibration: {DescribeTrackers(saved)}", BasisDebug.LogTag.Device);
                }
            }

            public static SavedCalibration TryRead()
            {
                try
                {
                    if (!File.Exists(FilePath))
                    {
                        return null;
                    }
                    SavedCalibration data = JsonUtility.FromJson<SavedCalibration>(File.ReadAllText(FilePath));
                    if (data == null || data.Version != CurrentVersion || data.Trackers == null || data.Trackers.Count == 0)
                    {
                        BasisDebug.LogWarning("Ignoring the saved full-body calibration: unknown version or no trackers.", BasisDebug.LogTag.Device);
                        return null;
                    }
                    for (int Index = 0; Index < data.Trackers.Count; Index++)
                    {
                        SavedTracker tracker = data.Trackers[Index];
                        if (tracker == null || string.IsNullOrEmpty(tracker.Subsystem) || string.IsNullOrEmpty(tracker.UniqueId)
                            || !BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(tracker.Role))
                        {
                            BasisDebug.LogWarning("Ignoring the saved full-body calibration: a tracker entry is malformed.", BasisDebug.LogTag.Device);
                            return null;
                        }
                        tracker.OffsetRotation = Sanitize(tracker.OffsetRotation);
                        tracker.Rotation = Sanitize(tracker.Rotation);
                        tracker.HeadRotation = Sanitize(tracker.HeadRotation);
                    }
                    if (data.References == null || data.References.Length != ReferenceCount)
                    {
                        data.HasReference = false;
                        data.References = new Quaternion[0];
                    }
                    for (int Index = 0; Index < data.References.Length; Index++)
                    {
                        data.References[Index] = Sanitize(data.References[Index]);
                    }
                    data.HeadRotation = Sanitize(data.HeadRotation);
                    data.HintBias ??= new List<RoleVector>();
                    data.LimbToBone ??= new List<RoleRotation>();
                    data.LimbToBody ??= new List<RoleRotation>();
                    return data;
                }
                catch (Exception e)
                {
                    BasisDebug.LogWarning($"Ignoring the saved full-body calibration: {e.Message}", BasisDebug.LogTag.Device);
                    return null;
                }
            }

            // A hand-edited or truncated file can leave an all-zero quaternion, which Unity treats as invalid.
            public static Quaternion Sanitize(Quaternion q)
            {
                float sqr = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                if (!(sqr > 1e-8f) || float.IsInfinity(sqr))
                {
                    return Quaternion.identity;
                }
                return q.normalized;
            }

            #endregion

            #region Restore

            /// <summary>Called from <see cref="BasisDeviceManagement.TryAdd"/> once the device is in the list.</summary>
            public static void OnDeviceAdded(BasisInput input)
            {
                if (input == null)
                {
                    return;
                }
                EnsureLoaded();
                TryRestorePending();
            }

            /// <summary>Called when the local avatar finishes loading (<see cref="BasisLocalAvatarDriver.CalibrationComplete"/>).</summary>
            public static void OnLocalAvatarReady()
            {
                EnsureLoaded();
                TryRestorePending();
            }

            public static bool CanRestore()
            {
                return saved != null
                    && pending.Count > 0
                    && !sessionCalibrated
                    && BasisSettingsDefaults.RememberFullBodyCalibration.RawValue
                    && BasisSettingsDefaults.EnableFBT.RawValue
                    && BasisDeviceManagement.Instance != null
                    && BasisDeviceManagement.IsCurrentModeVR();
            }

            public static bool AvatarReady()
            {
                BasisLocalPlayer player = BasisLocalPlayer.Instance;
                return player != null
                    && player.LocalRigDriver != null
                    && player.LocalRigDriver.IKDataReady
                    && BasisLocalAvatarDriver.HasTposeBoneSnapshot
                    && BasisLocalBoneDriver.HeadControl != null;
            }

            public static void TryRestorePending()
            {
                if (!CanRestore())
                {
                    return;
                }
                if (AvatarReady())
                {
                    MatchAndRestore();
                }
                else if (!waitingForReady)
                {
                    // Devices usually connect before the avatar exists; finish once it does.
                    waitingForReady = true;
                    BasisDeviceManagement.Instance.StartCoroutine(WaitForAvatarThenRestore());
                }
            }

            public static IEnumerator WaitForAvatarThenRestore()
            {
                float giveUpAt = Time.unscaledTime + ReadyTimeoutSeconds;
                while (!AvatarReady() && Time.unscaledTime < giveUpAt)
                {
                    yield return null;
                }
                waitingForReady = false;
                if (AvatarReady() && CanRestore())
                {
                    MatchAndRestore();
                }
            }

            public static void MatchAndRestore()
            {
                BasisDeviceManagement dm = BasisDeviceManagement.Instance;
                BasisObservableList<BasisInput> devices = dm.AllInputDevices;

                // Trackers that could take a saved calibration, grouped by what identifies them across launches.
                Dictionary<string, List<(BasisInput input, int order)>> free = new Dictionary<string, List<(BasisInput, int)>>();
                int count = devices.Count;
                for (int Index = 0; Index < count; Index++)
                {
                    BasisInput input = devices[Index];
                    // Already holding a role means it's the headset, a controller, or a tracker in use.
                    if (input == null || input.IgnoresDevice || input.IsHandDevice || input.hasRoleAssigned)
                    {
                        continue;
                    }
                    string key = KeyOf(input.SubSystemIdentifier, input.DeviceSerial, input.UniqueDeviceIdentifier);
                    if (!free.TryGetValue(key, out List<(BasisInput, int)> list))
                    {
                        free[key] = list = new List<(BasisInput, int)>();
                    }
                    list.Add((input, Index));
                }

                Dictionary<string, List<SavedTracker>> savedByKey = new Dictionary<string, List<SavedTracker>>();
                for (int Index = 0; Index < pending.Count; Index++)
                {
                    SavedTracker tracker = pending[Index];
                    string key = KeyOf(tracker.Subsystem, tracker.Serial, tracker.UniqueId);
                    if (!savedByKey.TryGetValue(key, out List<SavedTracker> list))
                    {
                        savedByKey[key] = list = new List<SavedTracker>();
                    }
                    list.Add(tracker);
                }

                List<(BasisInput input, SavedTracker tracker)> pairs = new List<(BasisInput, SavedTracker)>();
                foreach (KeyValuePair<string, List<SavedTracker>> group in savedByKey)
                {
                    if (!free.TryGetValue(group.Key, out List<(BasisInput input, int order)> connected))
                    {
                        continue;
                    }
                    List<SavedTracker> wanted = group.Value;
                    // Virtual trackers can share a serial and id (both elbows are ".../Estimated/Elbow") and are
                    // told apart by creation order. Pair them only once all have connected, so a late one can't
                    // shift the order.
                    if (wanted.Count > 1 && connected.Count < wanted.Count)
                    {
                        continue;
                    }
                    wanted.Sort((a, b) => SessionIndexOf(a.UniqueId).CompareTo(SessionIndexOf(b.UniqueId)));
                    connected.Sort((a, b) =>
                    {
                        int byIndex = SessionIndexOf(a.input.UniqueDeviceIdentifier).CompareTo(SessionIndexOf(b.input.UniqueDeviceIdentifier));
                        return byIndex != 0 ? byIndex : a.order.CompareTo(b.order);
                    });
                    for (int Index = 0; Index < wanted.Count && Index < connected.Count; Index++)
                    {
                        pairs.Add((connected[Index].input, wanted[Index]));
                    }
                }
                if (pairs.Count == 0)
                {
                    return;
                }

                if (!globalsApplied)
                {
                    ApplyBodyWideState(saved);
                    globalsApplied = true;
                }

                for (int Index = 0; Index < pairs.Count; Index++)
                {
                    (BasisInput input, SavedTracker tracker) = pairs[Index];
                    pending.Remove(tracker);
                    // The reconnect cache is keyed by this session's id, which differs from the saved one on OpenVR.
                    BasisStoredPreviousDevice previous = new BasisStoredPreviousDevice
                    {
                        trackedRole = tracker.Role,
                        hasRoleAssigned = true,
                        SubSystemIdentifier = input.SubSystemIdentifier,
                        UniqueDeviceIdentifier = input.UniqueDeviceIdentifier,
                        InverseOffsetFromBone = new BasisCalibratedCoords(tracker.OffsetPosition, tracker.OffsetRotation),
                        HasCalibratedOffsetSnapshot = tracker.HasSnapshot,
                        CalibratedUnscaledPosition = tracker.Position,
                        CalibratedUnscaledRotation = tracker.Rotation,
                        CalibratedUnscaledHeadPosition = tracker.HeadPosition,
                        CalibratedUnscaledHeadRotation = tracker.HeadRotation,
                        HasCalibratedHeadFromEye = tracker.HasHeadFromEye,
                        CalibratedUnscaledHeadFromEye = tracker.HeadFromEye,
                    };
                    if (!dm.CheckBeforeOverride(previous))
                    {
                        BasisDebug.LogWarning($"Saved calibration for {tracker.Role} skipped: another device already has that role.", BasisDebug.LogTag.Device);
                        continue;
                    }
                    dm.PreviouslyConnectedDevices.RemoveAll(p => p != null && p.UniqueDeviceIdentifier == input.UniqueDeviceIdentifier && p.SubSystemIdentifier == input.SubSystemIdentifier);
                    dm.PreviouslyConnectedDevices.Add(previous);
                    // Same path as a mid-session reconnect: role, offset, snapshot, reprojection.
                    dm.TryRestoreCachedRole(input);
                    restoredCount++;
                    BasisDebug.Log($"Restoring saved calibration: {input.UniqueDeviceIdentifier} -> {tracker.Role}", BasisDebug.LogTag.Device);
                }

                if (!finishScheduled)
                {
                    finishScheduled = true;
                    dm.StartCoroutine(FinishRestoreAfterDevices());
                }
            }

            public static void ApplyBodyWideState(SavedCalibration data)
            {
                if (data.HasReference && data.References.Length == ReferenceCount)
                {
                    s_refHead = data.References[0];
                    s_refHips = data.References[1];
                    s_refChest = data.References[2];
                    s_refLeftFoot = data.References[3];
                    s_refRightFoot = data.References[4];
                    s_refLeftToe = data.References[5];
                    s_refRightToe = data.References[6];
                    s_refLeftShoulder = data.References[7];
                    s_refRightShoulder = data.References[8];
                    HasCalibrationReference = true;
                }
                if (data.HasHeadSnapshot)
                {
                    s_calibHeadUnscaledPos = data.HeadPosition;
                    s_calibHeadUnscaledRot = data.HeadRotation;
                    HasCalibrationHeadSnapshot = true;
                }

                BasisHintBiasStore.LocalOffset.Clear();
                for (int Index = 0; Index < data.HintBias.Count; Index++)
                {
                    BasisHintBiasStore.LocalOffset[data.HintBias[Index].Role] = data.HintBias[Index].Value;
                }
                BasisLimbRollStore.Clear();
                for (int Index = 0; Index < data.LimbToBone.Count; Index++)
                {
                    BasisLimbRollStore.TrackerToBone[data.LimbToBone[Index].Role] = Sanitize(data.LimbToBone[Index].Value);
                }
                for (int Index = 0; Index < data.LimbToBody.Count; Index++)
                {
                    BasisLimbRollStore.TrackerToBody[data.LimbToBody[Index].Role] = Sanitize(data.LimbToBody[Index].Value);
                }

                // Hip height is measured standing; it can't be re-measured while seated. The eye height is
                // remembered by the height settings, so the hip follows it as a drop from the eye.
                if (data.HasHip && data.EyeToHipDrop > 0f && !SMModuleSitStand.IsSteatedMode)
                {
                    BasisHeightDriver.PlayerEyeToHipDrop = data.EyeToHipDrop;
                    BasisHeightDriver.PlayerHipHeight = BasisHeightDriver.HasGenuinePlayerEyeHeight
                        ? BasisHeightDriver.PlayerEyeHeight - data.EyeToHipDrop
                        : data.HipHeight;
                    BasisHeightDriver.HasGenuinePlayerHipHeight = true;
                }
            }

            public static IEnumerator FinishRestoreAfterDevices()
            {
                // RestoreInversetOffsets applies each role at the end of the frame it was started in.
                yield return null;
                yield return null;
                finishScheduled = false;

                BasisLocalPlayer player = BasisLocalPlayer.Instance;
                if (player == null || sessionCalibrated)
                {
                    yield break;
                }
                try
                {
                    ApplyCalibrationToCurrentAvatar();
                    if (player.LocalAnimatorDriver != null)
                    {
                        player.LocalAnimatorDriver.AssignHipsFBTracker();
                    }
                    if (restoredCount > 0)
                    {
                        HasFBIKTrackers = true;
                    }
                    BasisHeightDriver.ApplyScaleAndHeight();
                    if (player.LocalBoneDriver != null)
                    {
                        player.LocalBoneDriver.RebuildCalibrationSpheres();
                    }
                    restoring = true;
                    try
                    {
                        OnFullBodyCalibrated?.Invoke();
                    }
                    finally
                    {
                        restoring = false;
                    }
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"Finishing the full-body calibration restore failed: {e}", BasisDebug.LogTag.Device);
                    yield break;
                }

                int total = saved != null ? saved.Trackers.Count : restoredCount;
                string message = pending.Count == 0
                    ? BasisLocalization.Get("notifications.fbtCalibrationRestored.description", restoredCount, total)
                    : BasisLocalization.Get("notifications.fbtCalibrationRestored.waiting", restoredCount, total, pending.Count);
                BasisDebug.Log(message, BasisDebug.LogTag.Device);
                BasisNotificationCenter.LogResolved(
                    BasisLocalization.Get("notifications.fbtCalibrationRestored.title"),
                    message,
                    AddressableAssets.Sprites.Calibrate,
                    BasisNotificationStatus.Accepted);
            }

            #endregion

            // Serial plus the id without OpenVR's leading "<device index>|", which changes between launches.
            public static string KeyOf(string subsystem, string serial, string uniqueId)
            {
                uniqueId ??= string.Empty;
                int bar = uniqueId.IndexOf('|');
                string id = bar > 0 && int.TryParse(uniqueId.Substring(0, bar), out _) ? uniqueId.Substring(bar + 1) : uniqueId;
                return (subsystem ?? string.Empty) + "/" + (serial ?? string.Empty) + "#" + id;
            }

            public static int SessionIndexOf(string uniqueId)
            {
                if (string.IsNullOrEmpty(uniqueId))
                {
                    return int.MaxValue;
                }
                int bar = uniqueId.IndexOf('|');
                return bar > 0 && int.TryParse(uniqueId.Substring(0, bar), out int index) ? index : int.MaxValue;
            }

            public static string DescribeTrackers(SavedCalibration data)
            {
                StringBuilder builder = new StringBuilder();
                for (int Index = 0; Index < data.Trackers.Count; Index++)
                {
                    SavedTracker tracker = data.Trackers[Index];
                    if (Index > 0)
                    {
                        builder.Append(", ");
                    }
                    builder.Append(tracker.Role).Append('=');
                    builder.Append(string.IsNullOrEmpty(tracker.Serial) ? tracker.UniqueId : "serial " + tracker.Serial);
                }
                return builder.ToString();
            }
        }
    }
}
