using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// Bypasses the manual tracker calibration for trackers that already announce their body part.
    /// The generic convention is built in: SteamVR encodes any tracker's user-assigned role,
    /// whatever the brand, in its controller type ("vive_tracker_waist", ...; TrustSteamVRRoles,
    /// default off since most setups leave those roles unset or stale). Integration packages
    /// register more specific announcement conventions via <see cref="RegisterSource"/> (e.g. the
    /// SlimeVR package's "human://WAIST"-style serials); sources run first and can also suppress
    /// a device from the generic path. When such trackers appear this forces the matching Basis
    /// role through a runtime tracker-role override and runs the standard FullBodyCalibration
    /// pass once the set has settled — the classifier binds the forced roles without scoring
    /// while the pass still captures the tracker-to-bone offsets, per-effector rotation
    /// calibration and bend hints, exactly like a menu-triggered calibration. Trackers announcing
    /// nothing keep going through the normal geometric classification in the same pass.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisAnnouncedTrackerRoles
    {
        private const string ControllerTypePrefix = "vive_tracker_";
        private const float ScanIntervalSeconds = 2f;
        private const float CalibrationCooldownSeconds = 10f;

        /// <summary>How a registered source answers for a device.</summary>
        public enum RoleClaim
        {
            /// <summary>Not this source's device; ask the next convention.</summary>
            NotMine,
            /// <summary>
            /// This source's device, but it must not auto-bind (its setting is off or the token
            /// is unknown) — also keeps it away from the generic SteamVR-role path.
            /// </summary>
            Suppress,
            /// <summary>This source's device; bind it to the returned role.</summary>
            Mapped,
        }

        /// <summary>A package-registered announcement convention (e.g. SlimeVR's serials).</summary>
        public delegate RoleClaim RoleSource(BasisInput input, out BasisBoneTrackedRole role);

        // Main-thread only: RuntimeInitializeOnLoad-time registration, device-management-loop reads.
        private static readonly List<RoleSource> _sources = new List<RoleSource>();

        /// <summary>
        /// Add an announcement convention. Sources run before the built-in SteamVR-role path,
        /// in registration order.
        /// </summary>
        public static void RegisterSource(RoleSource source)
        {
            if (source == null || _sources.Contains(source))
            {
                return;
            }
            _sources.Add(source);
        }

        public static void UnregisterSource(RoleSource source)
        {
            _sources.Remove(source);
        }

        /// <summary>
        /// SteamVR controller-type role token to Basis role, the generic path for any tracker
        /// whose role was assigned in SteamVR settings. handed/camera/keyboard are intentionally
        /// absent (not body parts), and so are wrist/ankle: Basis's LowerArm/LowerLeg roles expect
        /// elbow/knee placement, so those are safer left to the geometric classifier.
        /// </summary>
        private static readonly Dictionary<string, BasisBoneTrackedRole> RolesByControllerType =
            new Dictionary<string, BasisBoneTrackedRole>(StringComparer.OrdinalIgnoreCase)
        {
            { "waist", BasisBoneTrackedRole.Hips },
            { "chest", BasisBoneTrackedRole.Chest },
            { "left_foot", BasisBoneTrackedRole.LeftFoot },
            { "right_foot", BasisBoneTrackedRole.RightFoot },
            { "left_knee", BasisBoneTrackedRole.LeftLowerLeg },
            { "right_knee", BasisBoneTrackedRole.RightLowerLeg },
            { "left_elbow", BasisBoneTrackedRole.LeftLowerArm },
            { "right_elbow", BasisBoneTrackedRole.RightLowerArm },
            { "left_shoulder", BasisBoneTrackedRole.LeftShoulder },
            { "right_shoulder", BasisBoneTrackedRole.RightShoulder },
        };

        private static readonly HashSet<string> _ownedOverrideIds = new HashSet<string>();
        private static readonly List<string> _staleOverrideIds = new List<string>();
        private static float _nextScanTime;
        private static float _pendingSince = -1f;
        private static float _lastCalibrationTime = float.NegativeInfinity;
        private static bool _hooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // The built-in SteamVR-role path only means anything on desktop, but a registered source
            // can announce anywhere — standalone headsets announce their own solved body parts — so
            // Android runs the scan too and simply never matches the SteamVR convention.
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.Android:
                    break;
                default:
                    return;
            }

            if (_hooked)
            {
                return;
            }
            _hooked = true;
            BasisDeviceManagement.OnDeviceManagementLoop += Scan;
        }

        private static void Scan()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextScanTime)
            {
                return;
            }
            _nextScanTime = now + ScanIntervalSeconds;

            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management == null
                || BasisLocalPlayer.Instance == null
                || BasisLocalPlayer.Instance.LocalAvatarDriver == null)
            {
                return;
            }

            bool anyNeedsRole = false;
            _staleOverrideIds.Clear();
            _staleOverrideIds.AddRange(_ownedOverrideIds);

            int count = management.AllInputDevices.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisInput input = management.AllInputDevices[Index];
                if (input == null || input.IsLinked || !TryMapInput(input, out BasisBoneTrackedRole role))
                {
                    continue;
                }

                BasisTrackerRoleOverride.SetRuntimeOverride(input.UniqueDeviceIdentifier, role);
                _ownedOverrideIds.Add(input.UniqueDeviceIdentifier);
                _staleOverrideIds.Remove(input.UniqueDeviceIdentifier);

                // Only count trackers the calibration settings would actually let take the role,
                // otherwise a role disabled in Tracker Settings would re-trigger forever.
                bool roleEnabled = Basis.BasisUI.BasisSettingsDefaults.EnableFBT.RawValue
                    && Basis.BasisUI.BasisSettingsDefaults.IsRoleEnabledForCalibration(role);
                if (roleEnabled && (!input.TryGetRole(out BasisBoneTrackedRole current) || current != role))
                {
                    anyNeedsRole = true;
                }
            }

            // Ids that no longer resolve to a live announcing tracker (SteamVR restarts hand out
            // new device indices, and either toggle can flip off) must not linger — a reused
            // index could inherit the wrong role.
            for (int Index = 0; Index < _staleOverrideIds.Count; Index++)
            {
                BasisTrackerRoleOverride.ClearRuntimeOverride(_staleOverrideIds[Index]);
                _ownedOverrideIds.Remove(_staleOverrideIds[Index]);
            }

            if (!anyNeedsRole)
            {
                _pendingSince = -1f;
                return;
            }

            // First sighting arms the pass; firing on the next scan gives the rest of the tracker
            // set a scan interval to arrive so one calibration covers them all.
            if (_pendingSince < 0f)
            {
                _pendingSince = now;
                return;
            }
            if (now - _lastCalibrationTime < CalibrationCooldownSeconds)
            {
                return;
            }

            _pendingSince = -1f;
            _lastCalibrationTime = now;
            BasisDebug.Log("Trackers announced their body parts; running automatic full-body calibration", BasisDebug.LogTag.Device);
            BasisAvatarIKStageCalibration.FullBodyCalibration();
        }

        private static bool TryMapInput(BasisInput input, out BasisBoneTrackedRole role)
        {
            if (input.IgnoresPose || input.HasRoleOverride)
            {
                role = BasisBoneTrackedRole.CenterEye;
                return false;
            }
            // Hand-tracking devices are excluded from full-body calibration everywhere, so they must
            // not announce a body part either — a stale SteamVR role on one would otherwise both bind
            // it and trigger the automatic calibration pass below.
            if (BasisIgnoredCalibrationTrackers.ShouldIgnore(input))
            {
                role = BasisBoneTrackedRole.CenterEye;
                return false;
            }

            // Package sources first: they announce more specific conventions, and a Suppress
            // claim must win over the generic SteamVR-role path (e.g. SlimeVR trackers carry a
            // SteamVR role too, so falling through would re-bind them behind their own toggle).
            for (int Index = 0; Index < _sources.Count; Index++)
            {
                switch (_sources[Index](input, out role))
                {
                    case RoleClaim.Mapped:
                        return true;
                    case RoleClaim.Suppress:
                        return false;
                }
            }

            string controllerType = input.DeviceControllerType;
            if (Basis.BasisUI.BasisSettingsDefaults.TrustSteamVRRoles.RawValue
                && !string.IsNullOrEmpty(controllerType) && controllerType.StartsWith(ControllerTypePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return RolesByControllerType.TryGetValue(controllerType.Substring(ControllerTypePrefix.Length).Trim(), out role);
            }

            role = BasisBoneTrackedRole.CenterEye;
            return false;
        }
    }
}
