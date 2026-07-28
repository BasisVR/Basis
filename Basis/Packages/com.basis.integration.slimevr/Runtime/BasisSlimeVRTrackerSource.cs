#if BASIS_FRAMEWORK_EXISTS
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using solarxr_protocol.datatypes;
using UnityEngine;

namespace Basis.Integration.SlimeVR
{
    /// <summary>
    /// Sources SlimeVR's trackers straight from the SlimeVR server (SolarXR) as Basis input devices,
    /// for setups where the OpenVR/OpenXR runtime doesn't surface them. Gated by the TrackerSource
    /// setting: "auto" only fills the gap (creates a server device when no XR device already holds that
    /// body part), "force" always drives from the server and removes any runtime-provided duplicate.
    ///
    /// Every created device carries the same "human://..." serial an OpenVR-sourced SlimeVR tracker
    /// would, so role assignment and calibration run through the existing pipeline unchanged. Poses are
    /// placed into Basis playspace via an HMD-anchored transform (both frames are gravity-aligned, so the
    /// HMD alone fixes the alignment) — which is why this needs SlimeVR to have an HMD reference.
    /// </summary>
    public static class BasisSlimeVRTrackerSource
    {
        private const string SubSystem = nameof(BasisSlimeVRTrackerSource);
        private const string SerialPrefix = "human://";
        private const float ScanIntervalSeconds = 1f;

        // SolarXR body part -> the SteamVR-driver serial token BasisAnnouncedTrackerRoles already maps.
        private static readonly Dictionary<BodyPart, string> TokenByBodyPart = new Dictionary<BodyPart, string>
        {
            { BodyPart.WAIST, "WAIST" },
            { BodyPart.HIP, "WAIST" },
            { BodyPart.CHEST, "CHEST" },
            { BodyPart.UPPER_CHEST, "CHEST" },
            { BodyPart.LEFT_FOOT, "LEFT_FOOT" },
            { BodyPart.RIGHT_FOOT, "RIGHT_FOOT" },
            { BodyPart.LEFT_LOWER_LEG, "LEFT_KNEE" },
            { BodyPart.RIGHT_LOWER_LEG, "RIGHT_KNEE" },
            { BodyPart.LEFT_LOWER_ARM, "LEFT_ELBOW" },
            { BodyPart.RIGHT_LOWER_ARM, "RIGHT_ELBOW" },
        };

        private static readonly HashSet<BodyPart> _created = new HashSet<BodyPart>();
        private static readonly Dictionary<BodyPart, string> _desired = new Dictionary<BodyPart, string>();
        private static float _nextScanTime;
        private static bool _hooked;

        // Per-frame memoized HMD anchor so every device shares one solve.
        private static int _anchorFrame = -1;
        private static bool _anchorValid;
        private static BasisSlimeVRFrameAlign.HmdAnchor _anchor;

        /// <summary>True while at least one server-sourced tracker device exists (for status display).</summary>
        public static bool IsSourcing => _created.Count > 0;
        public static int SourcedCount => _created.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
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

        /// <summary>Whether the setting currently asks for any server sourcing.</summary>
        public static bool WantsPoseFeed()
        {
            string mode = BasisSlimeVRSettings.TrackerSource.RawValue;
            return mode == BasisSlimeVRSettings.TrackerSourceAuto || mode == BasisSlimeVRSettings.TrackerSourceForce;
        }

        private static void Scan()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextScanTime)
            {
                return;
            }
            _nextScanTime = now + ScanIntervalSeconds;

            string mode = BasisSlimeVRSettings.TrackerSource.RawValue;
            // No SlimeVR HMD in the feed means SlimeVR has no anchor for its solve (e.g. no SteamVR), so
            // its poses can't be placed — create nothing rather than phantom pose-less devices.
            bool active = WantsPoseFeed()
                && BasisSlimeVRBridge.IsConnected
                && BasisDeviceManagement.Instance != null
                && BasisLocalPlayer.Instance != null
                && TryGetSlimeHmd(out _, out _);
            if (!active)
            {
                RemoveAll();
                return;
            }

            bool force = mode == BasisSlimeVRSettings.TrackerSourceForce;

            CollectDesired(_desired);

            // Drop devices whose tracker is gone from the feed.
            _staleParts.Clear();
            foreach (BodyPart part in _created)
            {
                if (!_desired.ContainsKey(part))
                {
                    _staleParts.Add(part);
                }
            }
            for (int i = 0; i < _staleParts.Count; i++)
            {
                RemoveDevice(_staleParts[i]);
            }

            foreach (KeyValuePair<BodyPart, string> entry in _desired)
            {
                string serial = SerialPrefix + entry.Value;
                BasisInput xrHolder = FindXrHolder(serial);
                if (xrHolder != null)
                {
                    if (!force)
                    {
                        // The runtime already provides this body part; stay out of its way.
                        if (_created.Contains(entry.Key))
                        {
                            RemoveDevice(entry.Key);
                        }
                        continue;
                    }
                    // Force: take the body part over from the runtime device.
                    BasisDeviceManagement.Instance.RemoveDevicesFrom(xrHolder.SubSystemIdentifier, xrHolder.UniqueDeviceIdentifier);
                }
                if (!_created.Contains(entry.Key))
                {
                    CreateDevice(entry.Key, serial);
                }
            }
        }

        private static readonly List<BodyPart> _staleParts = new List<BodyPart>();

        private static void CollectDesired(Dictionary<BodyPart, string> desired)
        {
            desired.Clear();
            List<SlimeVRTrackerSnapshot> trackers = BasisSlimeVRBridge.Trackers;
            for (int i = 0; i < trackers.Count; i++)
            {
                SlimeVRTrackerSnapshot t = trackers[i];
                if (!t.HasPosition || !TokenByBodyPart.TryGetValue(t.BodyPart, out string token))
                {
                    continue;
                }
                // One device per body part; a real (non-synthetic) tracker wins over a computed one.
                if (!desired.ContainsKey(t.BodyPart) || !t.IsSynthetic)
                {
                    desired[t.BodyPart] = token;
                }
            }
        }

        private static void CreateDevice(BodyPart part, string serial)
        {
            var go = new GameObject(UniqueId(part))
            {
                transform = { parent = BasisLocalPlayer.Instance.transform }
            };
            var input = go.AddComponent<BasisSlimeVRTrackerInput>();
            input.ClassName = nameof(BasisSlimeVRTrackerInput);
            input.Initialize(UniqueId(part), "slimevr_tracker", SubSystem, serial, part);
            BasisDeviceManagement.Instance.TryAdd(input);
            _created.Add(part);
            BasisDebug.Log($"SlimeVR: sourcing {part} from the server ({serial})", BasisDebug.LogTag.Device);
        }

        private static void RemoveDevice(BodyPart part)
        {
            BasisDeviceManagement.Instance?.RemoveDevicesFrom(SubSystem, UniqueId(part));
            _created.Remove(part);
        }

        private static void RemoveAll()
        {
            if (_created.Count == 0)
            {
                return;
            }
            _staleParts.Clear();
            _staleParts.AddRange(_created);
            for (int i = 0; i < _staleParts.Count; i++)
            {
                RemoveDevice(_staleParts[i]);
            }
        }

        private static string UniqueId(BodyPart part) => SubSystem + "|" + part;

        private static BasisInput FindXrHolder(string serial)
        {
            var devices = BasisDeviceManagement.Instance.AllInputDevices;
            for (int i = 0; i < devices.Count; i++)
            {
                BasisInput input = devices[i];
                if (input != null
                    && input.SubSystemIdentifier != SubSystem
                    && string.Equals(input.DeviceSerial, serial, System.StringComparison.OrdinalIgnoreCase))
                {
                    return input;
                }
            }
            return null;
        }

        // ---- Per-frame pose resolution (called by each device's RenderPollData) ----

        public static bool TryGetTrackerPose(BodyPart part, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            EnsureAnchor();
            if (!_anchorValid)
            {
                return false;
            }
            if (!TryGetSlimePose(part, out SlimeVRVec3 slimePos, out bool hasRot, out SlimeVRQuat slimeRot))
            {
                return false;
            }
            position = _anchor.ApplyPosition(BasisSlimeVRFrameAlign.FlipHandedness(slimePos));
            rotation = hasRot
                ? _anchor.ApplyRotation(BasisSlimeVRFrameAlign.FlipHandedness(slimeRot))
                : _anchor.ApplyRotation(Quaternion.identity);
            return true;
        }

        private static void EnsureAnchor()
        {
            if (_anchorFrame == Time.frameCount)
            {
                return;
            }
            _anchorFrame = Time.frameCount;
            _anchorValid = false;

            if (!TryGetBasisHmdPose(out Vector3 basisPos, out Quaternion basisRot))
            {
                return;
            }
            if (!TryGetSlimeHmd(out SlimeVRVec3 slimePos, out SlimeVRQuat slimeRot))
            {
                return;
            }
            _anchor = BasisSlimeVRFrameAlign.BuildHmdAnchor(basisPos, basisRot, slimePos, slimeRot);
            _anchorValid = _anchor.Valid;
        }

        private static bool TryGetBasisHmdPose(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (BasisDeviceManagement.Instance == null)
            {
                return false;
            }
            var devices = BasisDeviceManagement.Instance.AllInputDevices;
            for (int i = 0; i < devices.Count; i++)
            {
                BasisInput input = devices[i];
                if (input == null || !input.TryGetRole(out BasisBoneTrackedRole role))
                {
                    continue;
                }
                if (role == BasisBoneTrackedRole.CenterEye || role == BasisBoneTrackedRole.Head)
                {
                    position = input.UnscaledDeviceCoord.position;
                    rotation = input.UnscaledDeviceCoord.rotation;
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetSlimeHmd(out SlimeVRVec3 position, out SlimeVRQuat rotation)
        {
            position = default;
            rotation = default;
            List<SlimeVRTrackerSnapshot> trackers = BasisSlimeVRBridge.Trackers;
            for (int i = 0; i < trackers.Count; i++)
            {
                SlimeVRTrackerSnapshot t = trackers[i];
                if (t.IsHmd && t.HasPosition && t.HasRotation)
                {
                    position = t.Position;
                    rotation = t.Rotation;
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetSlimePose(BodyPart part, out SlimeVRVec3 position, out bool hasRotation, out SlimeVRQuat rotation)
        {
            position = default;
            rotation = default;
            hasRotation = false;
            List<SlimeVRTrackerSnapshot> trackers = BasisSlimeVRBridge.Trackers;
            bool found = false;
            for (int i = 0; i < trackers.Count; i++)
            {
                SlimeVRTrackerSnapshot t = trackers[i];
                if (t.BodyPart != part || !t.HasPosition)
                {
                    continue;
                }
                position = t.Position;
                hasRotation = t.HasRotation;
                rotation = t.Rotation;
                found = true;
                if (!t.IsSynthetic)
                {
                    break; // prefer a real tracker over a computed one
                }
            }
            return found;
        }
    }
}
#endif
