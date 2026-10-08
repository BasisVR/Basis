using Unity.Scripting.LifecycleManagement;
#if BASIS_FRAMEWORK_EXISTS
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Integration.MetaBodyTracking
{
    /// <summary>
    /// Surfaces the headset's solved body joints as Basis input devices. Gated by the TrackerSource
    /// setting: "auto" only fills the gap (a body part a physical tracker already holds is left
    /// alone), "force" always drives from the headset and removes the runtime-provided duplicate.
    ///
    /// Every created device carries a "metabody://..." serial, so role assignment and calibration run
    /// through the existing announced-tracker pipeline unchanged — the offsets, bend hints and
    /// rotation calibration are captured by the same full-body pass a physical tracker gets.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisMetaBodyTrackerSource
    {
        public const string SubSystem = nameof(BasisMetaBodyTrackerSource);
        public const string SerialPrefix = "metabody://";

        private const string CommonDeviceIdentifier = "metabody_tracker";
        private const float ScanIntervalSeconds = 1f;

        /// <summary>
        /// How long the body pose may stay inactive before the devices are torn down. Losing the body
        /// for a moment (arms out of view, a hand on the headset) is normal and the joints come back
        /// where they were, so the devices ride it out rather than dropping the rig and recalibrating.
        /// </summary>
        private const float InactiveGraceSeconds = 5f;

        private readonly struct BodyPart
        {
            public readonly BasisMetaBodyJoint Joint;
            public readonly BasisBoneTrackedRole Role;
            public readonly string Token;
            public readonly bool NeedsFullBody;

            public BodyPart(BasisMetaBodyJoint joint, BasisBoneTrackedRole role, string token, bool needsFullBody)
            {
                Joint = joint;
                Role = role;
                Token = token;
                NeedsFullBody = needsFullBody;
            }
        }

        /// <summary>
        /// The joints worth exposing. Head and hands are deliberately absent — the HMD and controllers
        /// own those bones and their real poses beat any solve. The elbows and knees ride the arm and
        /// leg segment ends, which are Basis's LowerArm/LowerLeg tracker roles.
        /// </summary>
        [NoAutoStaticsCleanup] private static readonly BodyPart[] Parts =
        {
            new BodyPart(BasisMetaBodyJoint.Hips, BasisBoneTrackedRole.Hips, "WAIST", false),
            new BodyPart(BasisMetaBodyJoint.Chest, BasisBoneTrackedRole.Chest, "CHEST", false),
            new BodyPart(BasisMetaBodyJoint.LeftArmLower, BasisBoneTrackedRole.LeftLowerArm, "LEFT_ELBOW", false),
            new BodyPart(BasisMetaBodyJoint.RightArmLower, BasisBoneTrackedRole.RightLowerArm, "RIGHT_ELBOW", false),
            new BodyPart(BasisMetaBodyJoint.LeftLowerLeg, BasisBoneTrackedRole.LeftLowerLeg, "LEFT_KNEE", true),
            new BodyPart(BasisMetaBodyJoint.RightLowerLeg, BasisBoneTrackedRole.RightLowerLeg, "RIGHT_KNEE", true),
            new BodyPart(BasisMetaBodyJoint.LeftFootAnkle, BasisBoneTrackedRole.LeftFoot, "LEFT_FOOT", true),
            new BodyPart(BasisMetaBodyJoint.RightFootAnkle, BasisBoneTrackedRole.RightFoot, "RIGHT_FOOT", true),
        };

        [NoAutoStaticsCleanup] private static readonly Dictionary<string, BasisBoneTrackedRole> RolesBySerialToken =
            new Dictionary<string, BasisBoneTrackedRole>(System.StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<BasisMetaBodyJoint> _created = new HashSet<BasisMetaBodyJoint>();
        private static readonly List<BasisMetaBodyJoint> _stale = new List<BasisMetaBodyJoint>();
        private static float _nextScanTime;
        private static float _bodyLostAt = -1f;
        private static bool _hooked;

        /// <summary>True while at least one headset-sourced tracker device exists (for status display).</summary>
        public static bool IsSourcing => _created.Count > 0;
        public static int SourcedCount => _created.Count;

        static BasisMetaBodyTrackerSource()
        {
            for (int Index = 0; Index < Parts.Length; Index++)
            {
                RolesBySerialToken[Parts[Index].Token] = Parts[Index].Role;
            }
        }

        /// <summary>Maps one of our serial tokens to its Basis role, for the announced-role source.</summary>
        public static bool TryGetRoleForSerialToken(string token, out BasisBoneTrackedRole role)
        {
            return RolesBySerialToken.TryGetValue(token, out role);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_hooked)
            {
                return;
            }
            _hooked = true;
            BasisDeviceManagement.OnDeviceManagementLoop += Scan;
        }

        /// <summary>Whether the setting currently asks for any headset sourcing.</summary>
        public static bool WantsPoseFeed()
        {
            string mode = BasisMetaBodyTrackingSettings.TrackerSource.RawValue;
            return mode == BasisMetaBodyTrackingSettings.TrackerSourceAuto || mode == BasisMetaBodyTrackingSettings.TrackerSourceForce;
        }

        private static void Scan()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextScanTime)
            {
                return;
            }
            _nextScanTime = now + ScanIntervalSeconds;

            TrackBodyActivity(now);

            string mode = BasisMetaBodyTrackingSettings.TrackerSource.RawValue;
            bool active = WantsPoseFeed()
                && BasisMetaBodyTrackingFeature.IsSupported
                && BasisMetaBodyTrackingFeature.ActiveJointSet != BasisMetaBodyJointSet.None
                && HasRecentBody(now)
                && BasisDeviceManagement.Instance != null
                && BasisLocalPlayer.Instance != null;
            if (!active)
            {
                RemoveAll();
                return;
            }

            BasisMetaBodyTrackingFeature.ApplyHeightOverride();

            bool force = mode == BasisMetaBodyTrackingSettings.TrackerSourceForce;
            bool fullBody = BasisMetaBodyTrackingFeature.ActiveJointSet == BasisMetaBodyJointSet.FullBody;

            for (int Index = 0; Index < Parts.Length; Index++)
            {
                BodyPart part = Parts[Index];
                bool wanted = part.NeedsFullBody
                    ? fullBody && BasisMetaBodyTrackingSettings.TrackLegs.RawValue
                    : BasisMetaBodyTrackingSettings.TrackUpperBody.RawValue;
                if (!wanted)
                {
                    RemoveDevice(part.Joint);
                    continue;
                }

                BasisInput holder = FindOtherHolder(part.Role);
                if (holder != null)
                {
                    if (!force)
                    {
                        RemoveDevice(part.Joint);
                        continue;
                    }
                    BasisDeviceManagement.Instance.RemoveDevicesFrom(holder.SubSystemIdentifier, holder.UniqueDeviceIdentifier);
                }

                if (!_created.Contains(part.Joint))
                {
                    CreateDevice(part);
                }
            }
        }

        /// <summary>
        /// Keeps the timestamps the grace period is measured against. Locating happens on the devices'
        /// poll, so the very first scan runs before any locate has been made.
        /// </summary>
        private static void TrackBodyActivity(float now)
        {
            BasisMetaBodyTrackingFeature.EnsureLocated();
            if (BasisMetaBodyTrackingFeature.IsBodyActive)
            {
                _bodyLostAt = -1f;
            }
            else if (_bodyLostAt < 0f)
            {
                _bodyLostAt = now;
            }
        }

        private static bool HasRecentBody(float now)
        {
            if (BasisMetaBodyTrackingFeature.IsBodyActive)
            {
                return true;
            }
            return _bodyLostAt >= 0f && now - _bodyLostAt < InactiveGraceSeconds && _created.Count > 0;
        }

        private static void CreateDevice(BodyPart part)
        {
            string uniqueId = UniqueId(part.Joint);
            var go = new GameObject(uniqueId)
            {
                transform = { parent = BasisLocalPlayer.Instance.transform }
            };
            var input = go.AddComponent<BasisMetaBodyTrackerInput>();
            input.ClassName = nameof(BasisMetaBodyTrackerInput);
            input.Initialize(uniqueId, CommonDeviceIdentifier, SubSystem, SerialPrefix + part.Token, part.Joint);
            BasisDeviceManagement.Instance.TryAdd(input);
            _created.Add(part.Joint);
            BasisDebug.Log($"Meta body tracking: sourcing {part.Role} from the headset ({SerialPrefix + part.Token})", BasisDebug.LogTag.Device);
        }

        private static void RemoveDevice(BasisMetaBodyJoint joint)
        {
            if (!_created.Contains(joint))
            {
                return;
            }
            BasisDeviceManagement.Instance?.RemoveDevicesFrom(SubSystem, UniqueId(joint));
            _created.Remove(joint);
        }

        private static void RemoveAll()
        {
            if (_created.Count == 0)
            {
                return;
            }
            _stale.Clear();
            _stale.AddRange(_created);
            for (int Index = 0; Index < _stale.Count; Index++)
            {
                RemoveDevice(_stale[Index]);
            }
        }

        private static string UniqueId(BasisMetaBodyJoint joint) => SubSystem + "|" + joint;

        /// <summary>Finds a device from another backend that already holds this body part.</summary>
        private static BasisInput FindOtherHolder(BasisBoneTrackedRole role)
        {
            var devices = BasisDeviceManagement.Instance.AllInputDevices;
            for (int Index = 0; Index < devices.Count; Index++)
            {
                BasisInput input = devices[Index];
                if (input == null || input.SubSystemIdentifier == SubSystem)
                {
                    continue;
                }
                if (input.TryGetRole(out BasisBoneTrackedRole held) && held == role)
                {
                    return input;
                }
            }
            return null;
        }
    }
}
#endif
