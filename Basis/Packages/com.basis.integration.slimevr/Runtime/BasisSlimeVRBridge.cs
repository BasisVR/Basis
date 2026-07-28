#if BASIS_FRAMEWORK_EXISTS
using System;
using System.Collections;
using System.Collections.Generic;
using Basis.Scripts.Audio;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using solarxr_protocol.datatypes;
using solarxr_protocol.rpc;
using UnityEngine;

namespace Basis.Integration.SlimeVR
{
    /// <summary>Actions that sample the user's current pose, so UI routes them through the pose countdown.</summary>
    public enum BasisSlimeVRPoseAction
    {
        YawReset,
        FullReset,
        MountingReset,
        RecalibrateFbt,
    }

    /// <summary>
    /// Runtime hub for the SlimeVR integration. Boots itself, keeps a background SolarXR client
    /// connected to any local SlimeVR server, and applies the reported body proportions to the
    /// Basis height system so users with SlimeVR never need to run a manual height calibration:
    /// eye height and arm span arrive already measured (and already refined by SlimeVR's own
    /// autobone / height calibration). Also exposes live tracker/battery state and SlimeVR's
    /// reset actions for other systems (HUDs, menus, bindings).
    /// </summary>
    public static class BasisSlimeVRBridge
    {
        public static bool IsConnected { get; private set; }
        public static bool HasBodyMetrics { get; private set; }
        public static BasisSlimeVRBodyMetrics LastBodyMetrics { get; private set; }
        public static SlimeVRSkeletonConfig LastSkeletonConfig { get; private set; }

        /// <summary>Latest tracker snapshots (physical devices first, then synthetic). Main-thread only.</summary>
        public static readonly List<SlimeVRTrackerSnapshot> Trackers = new List<SlimeVRTrackerSnapshot>();

        public static event Action<bool> OnConnectionChanged;
        public static event Action<BasisSlimeVRBodyMetrics> OnBodyMetricsChanged;
        public static event Action OnTrackersUpdated;

        private static BasisSolarXRClient _client;
        private static bool _hooked;
        private static bool _pendingSeatedApply;

        // ---- FBT offset freshness ----
        // SlimeVR's mounting orientation is a static per-tracker calibration value that only moves when
        // the user runs a mounting/full reset. Basis captures each tracker's bone offset RELATIVE to the
        // tracker's reported orientation, so a reset silently invalidates every FBT offset until the next
        // calibration. We snapshot the mounting orientations at each calibration and re-run
        // FullBodyCalibration when they drift, so a SlimeVR reset self-heals with no manual recalibration.
        private const float MountingChangeThresholdDegrees = 4f;
        private const float RecaptureCooldownSeconds = 6f;
        private static readonly Dictionary<BodyPart, Quaternion> _mountingBaseline = new Dictionary<BodyPart, Quaternion>();
        private static bool _hasMountingBaseline;
        private static float _lastRecaptureRealtime = float.NegativeInfinity;

        /// <summary>Largest mounting-orientation drift (degrees) from the last calibration across bound trackers.</summary>
        public static float MountingDriftDegrees { get; private set; }
        /// <summary>A mounting change was detected but not yet recaptured (seated, cooling down, or setting off).</summary>
        public static bool OffsetsStale { get; private set; }
        /// <summary>Why the last FBT offset recapture ran (for the debug window / status text).</summary>
        public static string LastRecaptureReason { get; private set; }
        /// <summary><see cref="Time.realtimeSinceStartup"/> at the last recapture.</summary>
        public static float LastRecaptureRealtime { get; private set; } = float.NegativeInfinity;

        private static readonly object _trackerSwapLock = new object();
        private static List<SlimeVRTrackerSnapshot> _incomingTrackers;
        private static bool _trackerFlushQueued;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // The SlimeVR server runs on the same machine, so only desktop platforms can reach it.
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

            if (!_hooked)
            {
                _hooked = true;
                Application.quitting += Shutdown;
                BasisSlimeVRSettings.Enable.OnChanged += OnEnableSettingChanged;
                BasisSlimeVRSettings.ApplyBodyMeasurements.OnChanged += OnApplySettingChanged;
                BasisSlimeVRSettings.Transport.OnChanged += OnTransportSettingChanged;
                BasisSlimeVRSettings.TrackerSource.OnChanged += OnTrackerSourceChanged;
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnPlayersHeightChanged;
                // Every calibration (menu, auto-bind, or our own recapture) becomes the new mounting
                // reference the drift check compares against.
                BasisAvatarIKStageCalibration.OnFullBodyCalibrated += SnapshotMountingBaseline;
            }

            if (BasisSlimeVRSettings.Enable.RawValue)
            {
                StartClient();
            }
        }

        private static void StartClient()
        {
            if (_client != null)
            {
                return;
            }
            _client = new BasisSolarXRClient
            {
                Transport = SelectedTransport,
                EnablePoseFeed = BasisSlimeVRTrackerSource.WantsPoseFeed(),
                Log = message => BasisDebug.Log(message, BasisDebug.LogTag.Device),
                LogError = message => BasisDebug.LogError(message, BasisDebug.LogTag.Device),
                ConnectionChanged = connected => RunOnMainThread(() => HandleConnectionChanged(connected)),
                SkeletonConfigReceived = config => RunOnMainThread(() => HandleSkeletonConfig(config)),
                TrackersReceived = QueueTrackers
            };
            _client.Start();
            BasisDebug.Log($"SlimeVR integration enabled, watching for a local SlimeVR server ({_client.Transport})", BasisDebug.LogTag.Device);
        }

        private static void StopClient()
        {
            if (_client == null)
            {
                return;
            }
            _client.Dispose();
            _client = null;
            if (IsConnected)
            {
                IsConnected = false;
                OnConnectionChanged?.Invoke(false);
            }
            Trackers.Clear();
            OnTrackersUpdated?.Invoke();
        }

        private static void Shutdown()
        {
            CancelPoseCountdown();
            StopClient();
        }

        // ---- Public actions (usable from menus, bindings, other systems) ----

        /// <summary>Re-request the skeleton config right now instead of waiting for the next poll.</summary>
        public static void RefreshBodyMeasurements() => _client?.RequestSkeletonConfig();

        /// <summary>SlimeVR yaw reset (straighten trackers), same as the SlimeVR GUI button.</summary>
        public static void TriggerYawReset() => _client?.RequestReset(ResetType.Yaw);

        /// <summary>SlimeVR full reset.</summary>
        public static void TriggerFullReset() => _client?.RequestReset(ResetType.Full);

        /// <summary>SlimeVR mounting reset.</summary>
        public static void TriggerMountingReset() => _client?.RequestReset(ResetType.Mounting);

        /// <summary>
        /// Re-runs the standard full-body calibration so every FBT tracker recaptures its bone offset
        /// against the current tracker orientations. Safe to call by hand (menu / debug button); the
        /// automatic mounting-drift path routes through here too. Roles pinned by the announced-role
        /// scanner rebind without scoring, so this is effectively a pure offset/rotation recapture.
        /// </summary>
        public static void RecaptureFbtOffsets(string reason)
        {
            _lastRecaptureRealtime = Time.realtimeSinceStartup;
            LastRecaptureRealtime = _lastRecaptureRealtime;
            LastRecaptureReason = reason;
            OffsetsStale = false;
            BasisDebug.Log($"Recapturing SlimeVR FBT offsets: {reason}", BasisDebug.LogTag.Device);
            // Fires OnFullBodyCalibrated -> SnapshotMountingBaseline, which resets the drift reference.
            BasisAvatarIKStageCalibration.FullBodyCalibration();
        }

        // ---- Pose countdowns ----
        // SlimeVR's resets and the FBT offset recapture all sample the user's current pose, so firing
        // them the instant a menu button is pressed captures a hand-on-the-menu stance. Buttons route
        // through this shared countdown so the user has a few seconds to get into pose first.

        /// <summary>Seconds a pose countdown waits before firing its action (user setting, whole seconds, 0 = instant).</summary>
        public static int PoseCountdownSeconds => Mathf.Max(0, Mathf.RoundToInt(BasisSlimeVRSettings.PoseCountdownSeconds.RawValue));

        /// <summary>A pose countdown is currently running.</summary>
        public static bool HasPoseCountdown { get; private set; }

        /// <summary>The action the running countdown will fire. Only meaningful while <see cref="HasPoseCountdown"/>.</summary>
        public static BasisSlimeVRPoseAction PoseCountdownAction { get; private set; }

        /// <summary>Whole seconds left on the running countdown.</summary>
        public static int PoseCountdownSecondsRemaining { get; private set; }

        /// <summary>Fired each countdown second with the seconds remaining (starts at <see cref="PoseCountdownSeconds"/>).</summary>
        public static event Action<BasisSlimeVRPoseAction, int> OnPoseCountdownTick;

        /// <summary>Countdown over; true means the action fired, false means it was cancelled or replaced.</summary>
        public static event Action<BasisSlimeVRPoseAction, bool> OnPoseCountdownEnded;

        private static Coroutine _poseCountdownRoutine;
        private static string _poseRecaptureReason;

        /// <summary>
        /// Runs <paramref name="action"/> after <see cref="PoseCountdownSeconds"/> so the user can get
        /// into pose. Replaces any countdown already running. The countdown lives on
        /// <see cref="BasisDeviceManagement"/>, so it still fires if the menu that started it closes.
        /// </summary>
        public static void StartPoseCountdown(BasisSlimeVRPoseAction action, string recaptureReason = null)
        {
            CancelPoseCountdown();
            _poseRecaptureReason = recaptureReason;
            int seconds = PoseCountdownSeconds;
            if (seconds <= 0 || BasisDeviceManagement.Instance == null)
            {
                ExecutePoseAction(action);
                return;
            }
            HasPoseCountdown = true;
            PoseCountdownAction = action;
            PoseCountdownSecondsRemaining = seconds;
            _poseCountdownRoutine = BasisDeviceManagement.Instance.StartCoroutine(PoseCountdownRoutine(action, seconds));
        }

        /// <summary>Stops the running pose countdown without firing its action.</summary>
        public static void CancelPoseCountdown()
        {
            if (!HasPoseCountdown)
            {
                return;
            }
            if (_poseCountdownRoutine != null && BasisDeviceManagement.Instance != null)
            {
                BasisDeviceManagement.Instance.StopCoroutine(_poseCountdownRoutine);
            }
            _poseCountdownRoutine = null;
            HasPoseCountdown = false;
            OnPoseCountdownEnded?.Invoke(PoseCountdownAction, false);
        }

        private static IEnumerator PoseCountdownRoutine(BasisSlimeVRPoseAction action, int totalSeconds)
        {
            for (int seconds = totalSeconds; seconds > 0; seconds--)
            {
                PoseCountdownSecondsRemaining = seconds;
                OnPoseCountdownTick?.Invoke(action, seconds);
                PlayPoseCountdownSound(BasisUISoundEvent.CameraCountdownTick, BasisDeviceManagement.Instance.CameraCountdownTickSound);
                yield return new WaitForSecondsRealtime(1f);
            }
            _poseCountdownRoutine = null;
            HasPoseCountdown = false;
            PoseCountdownSecondsRemaining = 0;
            PlayPoseCountdownSound(BasisUISoundEvent.Press, BasisDeviceManagement.Instance.pressUI);
            OnPoseCountdownEnded?.Invoke(action, true);
            ExecutePoseAction(action);
        }

        private static void PlayPoseCountdownSound(BasisUISoundEvent soundEvent, AudioClip clip)
        {
            if (clip != null)
            {
                BasisUISounds.PlayAt(soundEvent, clip, BasisLocalCameraDriver.HeadPosition, SMModuleAudio.ActiveMenusVolume);
            }
        }

        private static void ExecutePoseAction(BasisSlimeVRPoseAction action)
        {
            switch (action)
            {
                case BasisSlimeVRPoseAction.YawReset:
                    TriggerYawReset();
                    break;
                case BasisSlimeVRPoseAction.FullReset:
                    TriggerFullReset();
                    break;
                case BasisSlimeVRPoseAction.MountingReset:
                    TriggerMountingReset();
                    break;
                case BasisSlimeVRPoseAction.RecalibrateFbt:
                    RecaptureFbtOffsets(_poseRecaptureReason ?? "manual (pose countdown)");
                    break;
            }
        }

        /// <summary>Per-part mounting drift from the last calibration (degrees), or 0 if unknown.</summary>
        public static float GetMountingDriftDegrees(BodyPart part)
        {
            if (!_hasMountingBaseline || !_mountingBaseline.TryGetValue(part, out Quaternion baseline))
            {
                return 0f;
            }
            for (int i = 0; i < Trackers.Count; i++)
            {
                SlimeVRTrackerSnapshot t = Trackers[i];
                if (t.BodyPart == part && t.TryGetEffectiveMounting(out SlimeVRQuat mounting))
                {
                    return RawQuatAngleDegrees(baseline, ToQuat(mounting));
                }
            }
            return 0f;
        }

        // ---- Worker-to-main-thread plumbing ----

        private static void RunOnMainThread(Action action)
        {
            BasisDeviceManagement.mainThreadActions.Enqueue(action);
        }

        private static void QueueTrackers(List<SlimeVRTrackerSnapshot> trackers)
        {
            // Latest-wins swap with a single queued flush so a stalled main thread never
            // accumulates datafeed updates.
            lock (_trackerSwapLock)
            {
                _incomingTrackers = trackers;
                if (_trackerFlushQueued)
                {
                    return;
                }
                _trackerFlushQueued = true;
            }
            RunOnMainThread(FlushTrackers);
        }

        private static void FlushTrackers()
        {
            List<SlimeVRTrackerSnapshot> latest;
            lock (_trackerSwapLock)
            {
                latest = _incomingTrackers;
                _incomingTrackers = null;
                _trackerFlushQueued = false;
            }
            if (latest == null)
            {
                return;
            }
            Trackers.Clear();
            Trackers.AddRange(latest);
            OnTrackersUpdated?.Invoke();
            EvaluateMountingFreshness();
        }

        private static void HandleConnectionChanged(bool connected)
        {
            IsConnected = connected;
            if (!connected)
            {
                Trackers.Clear();
                OnTrackersUpdated?.Invoke();
            }
            OnConnectionChanged?.Invoke(connected);
        }

        private static void HandleSkeletonConfig(SlimeVRSkeletonConfig config)
        {
            LastSkeletonConfig = config;
            LastBodyMetrics = BasisSlimeVRBodyMetrics.Derive(config);
            HasBodyMetrics = true;
            OnBodyMetricsChanged?.Invoke(LastBodyMetrics);
            TryApplyBodyMeasurements(LastBodyMetrics);
        }

        // ---- FBT offset freshness ----

        /// <summary>
        /// Records each bound tracker's current mounting orientation as the reference the live offsets
        /// were captured against. Runs after every FullBodyCalibration, so the drift check only fires
        /// on a genuine post-calibration change.
        /// </summary>
        private static void SnapshotMountingBaseline()
        {
            _mountingBaseline.Clear();
            for (int i = 0; i < Trackers.Count; i++)
            {
                SlimeVRTrackerSnapshot t = Trackers[i];
                if (t.IsSynthetic || t.IsHmd)
                {
                    continue;
                }
                if (t.TryGetEffectiveMounting(out SlimeVRQuat mounting))
                {
                    _mountingBaseline[t.BodyPart] = ToQuat(mounting);
                }
            }
            _hasMountingBaseline = true;
            MountingDriftDegrees = 0f;
            OffsetsStale = false;
        }

        /// <summary>
        /// Compares each tracker's live mounting orientation against the last-calibration baseline and,
        /// when it has drifted past the threshold, re-runs full-body calibration to recapture offsets.
        /// Runs on every datafeed flush and on standing up. No-op until a calibration has established a
        /// baseline and SlimeVR trackers are actually driving the rig.
        /// </summary>
        private static void EvaluateMountingFreshness()
        {
            if (!BasisSlimeVRSettings.RecalibrateOnMountingChange.RawValue
                || !IsConnected
                || !_hasMountingBaseline
                || !BasisAvatarIKStageCalibration.HasFBIKTrackers)
            {
                OffsetsStale = false;
                MountingDriftDegrees = 0f;
                return;
            }

            float maxDrift = 0f;
            for (int i = 0; i < Trackers.Count; i++)
            {
                SlimeVRTrackerSnapshot t = Trackers[i];
                if (t.IsSynthetic || t.IsHmd || !t.TryGetEffectiveMounting(out SlimeVRQuat mounting))
                {
                    continue;
                }
                Quaternion live = ToQuat(mounting);
                if (!_mountingBaseline.TryGetValue(t.BodyPart, out Quaternion baseline))
                {
                    // A tracker absent at the last calibration has no captured offset to invalidate;
                    // adopt its current orientation as its own baseline (announced-role binding will
                    // recalibrate if it actually takes a role).
                    _mountingBaseline[t.BodyPart] = live;
                    continue;
                }
                float drift = RawQuatAngleDegrees(baseline, live);
                if (drift > maxDrift)
                {
                    maxDrift = drift;
                }
            }

            MountingDriftDegrees = maxDrift;
            if (maxDrift <= MountingChangeThresholdDegrees)
            {
                OffsetsStale = false;
                return;
            }

            OffsetsStale = true;

            // Seated defers (recapture needs the standing pose; OnPlayersHeightChanged re-checks on
            // stand). The cooldown stops a still-settling reset from firing a burst of recaptures.
            if (SMModuleSitStand.IsSteatedMode
                || Time.realtimeSinceStartup - _lastRecaptureRealtime < RecaptureCooldownSeconds)
            {
                return;
            }

            RecaptureFbtOffsets($"SlimeVR mounting changed ({maxDrift:F0}°)");
        }

        private static Quaternion ToQuat(SlimeVRQuat q) => new Quaternion(q.X, q.Y, q.Z, q.W);

        // Geodesic angle between two SlimeVR quaternions, compared in SlimeVR's own frame. Both operands
        // are SlimeVR-space so no handedness conversion is needed — a same-frame delta is all the
        // mounting-drift check requires.
        private static float RawQuatAngleDegrees(Quaternion a, Quaternion b)
        {
            float dot = Mathf.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
            if (dot > 1f)
            {
                dot = 1f;
            }
            return 2f * Mathf.Acos(dot) * Mathf.Rad2Deg;
        }

        // ---- Applying measurements to the Basis height system ----

        private static void OnEnableSettingChanged(bool enabled)
        {
            if (enabled)
            {
                StartClient();
            }
            else
            {
                StopClient();
            }
        }

        private static void OnApplySettingChanged(bool apply)
        {
            if (apply && HasBodyMetrics)
            {
                TryApplyBodyMeasurements(LastBodyMetrics);
            }
        }

        private static SolarXRTransportKind SelectedTransport =>
            string.Equals(BasisSlimeVRSettings.Transport.RawValue, BasisSlimeVRSettings.TransportPipe, StringComparison.OrdinalIgnoreCase)
                ? SolarXRTransportKind.Pipe
                : SolarXRTransportKind.WebSocket;

        private static void OnTransportSettingChanged(string _)
        {
            if (_client == null)
            {
                return;
            }
            StopClient();
            StartClient();
        }

        private static void OnTrackerSourceChanged(string _)
        {
            // Toggling server sourcing changes whether the datafeed carries poses (and its rate), so the
            // client reconnects with the right feed. BasisSlimeVRTrackerSource reconciles devices on its
            // own loop from BasisSlimeVRTrackerSource.WantsPoseFeed().
            if (_client == null)
            {
                return;
            }
            StopClient();
            StartClient();
        }

        private static void OnPlayersHeightChanged(BasisHeightDriver.HeightModeChange mode)
        {
            // A seated stint blocks applying (seated mode swaps in a virtual standing eye height);
            // catch up as soon as the player is standing again.
            if (mode == BasisHeightDriver.HeightModeChange.OnSitStandChanged
                && !SMModuleSitStand.IsSteatedMode
                && _pendingSeatedApply
                && HasBodyMetrics)
            {
                _pendingSeatedApply = false;
                TryApplyBodyMeasurements(LastBodyMetrics);
            }

            // A mounting change detected while seated defers its recapture (it needs the standing
            // pose); re-check the moment the player stands rather than waiting for the next flush.
            if (mode == BasisHeightDriver.HeightModeChange.OnSitStandChanged && !SMModuleSitStand.IsSteatedMode)
            {
                EvaluateMountingFreshness();
            }
        }

        private static void TryApplyBodyMeasurements(BasisSlimeVRBodyMetrics metrics)
        {
            if (!BasisSlimeVRSettings.ApplyBodyMeasurements.RawValue)
            {
                return;
            }
            if (!metrics.HasEyeHeight && !metrics.HasArmSpan)
            {
                return;
            }
            if (SMModuleSitStand.IsSteatedMode)
            {
                _pendingSeatedApply = true;
                return;
            }

            // SlimeVR's user height is the raw HMD height above the SteamVR floor — the same
            // device origin the live capture samples — so the eye-mode denominator still needs
            // the backend's device-origin->eye lift. CalculatePlayerEyeHeight is the only other
            // writer of that lift, and a genuine SlimeVR height suppresses that capture on avatar
            // loads; without this refresh the applied scale would depend on whether a manual
            // calibration happened to run first this session.
            var headInput = BasisLocalCameraDriver.Instance?.BasisLockToInput?.BasisInput;
            if (headInput != null)
            {
                BasisHeightDriver.PlayerCenterEyeVerticalOffset = headInput.CenterEyeVerticalOffset;
            }

            bool changed = false;

            float eyeHeight = metrics.EyeHeightMeters;
            bool eyePlausible = eyeHeight >= BasisHeightDriver.MinPlausibleBodyMeasure
                && eyeHeight <= BasisHeightDriver.MaxPlausibleBodyMeasure;
            if (eyePlausible
                && (Mathf.Abs(BasisHeightDriver.PlayerEyeHeight - eyeHeight) > 0.005f
                    || !BasisHeightDriver.HasGenuinePlayerEyeHeight
                    || !BasisHeightDriver.HasUserCalibratedHeight))
            {
                BasisHeightDriver.PlayerEyeHeight = eyeHeight;
                Basis.BasisUI.BasisSettingsDefaults.SavedPlayerEyeHeight.SetValue(eyeHeight);
                changed = true;
            }

            float armSpan = metrics.ControllerSpanMeters;
            if (Basis.BasisUI.BasisSettingsDefaults.FBIKArmHeightRatioEnabled.RawValue)
            {
                // The arm-height-ratio override owns the span (CapturePlayerHeight derives it
                // from the eye height); keep that invariant against the just-applied eye instead
                // of stomping the override with the measured controller span every config poll.
                armSpan = BasisHeightDriver.PlayerEyeHeight
                    * Mathf.Max(0.1f, Basis.BasisUI.BasisSettingsDefaults.FBIKArmHeightRatio.RawValue);
            }
            bool spanPlausible = armSpan >= BasisHeightDriver.MinPlausibleBodyMeasure
                && armSpan <= BasisHeightDriver.MaxPlausibleBodyMeasure;
            if (spanPlausible && Mathf.Abs(BasisHeightDriver.PlayerArmSpan - armSpan) > 0.005f)
            {
                BasisHeightDriver.PlayerArmSpan = armSpan;
                Basis.BasisUI.BasisSettingsDefaults.SavedPlayerArmSpan.SetValue(armSpan);
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            // The measurements come from SlimeVR's calibrated skeleton, so they count as a real,
            // user-calibrated body size: the live auto-scale estimator must not fight them and
            // avatar loads must reuse them instead of re-polling a stance-dependent HMD sample.
            BasisHeightDriver.HasGenuinePlayerEyeHeight = true;
            BasisHeightDriver.HasUserCalibratedHeight = true;

            if (BasisLocalPlayer.Instance != null)
            {
                BasisHeightDriver.ApplyScaleAndHeight();
            }

            BasisDebug.Log($"Applied SlimeVR body measurements: {metrics}", BasisDebug.LogTag.Device);
        }
    }
}
#endif
