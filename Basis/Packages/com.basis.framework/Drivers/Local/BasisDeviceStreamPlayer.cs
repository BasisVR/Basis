using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Replays a recorded headset session through the live IK pipeline, using the SAME injection seam
    /// <see cref="BasisIKPlaybackDriver"/> uses for AnimationClips: simulated devices created through
    /// <see cref="BasisSimulateXR"/>, driven by writing their FollowMovement local pose each frame.
    /// The only difference is the source — recorded device data instead of a sampled clip — which is
    /// exactly the difference that matters, because the recording carries the three things a clip cannot:
    /// real controller grip-convention rotations, real tracker noise/stand-off/slip/dropout, and the
    /// user's real calibration residual.
    ///
    /// DETERMINISM — READ BEFORE TRUSTING A REPLAY.
    /// Two hazards in this codebase decide whether a replay reproduces its session, and both are modelled
    /// here rather than papered over:
    ///
    ///   1. FILTER STATE IS CARRIED ACROSS FRAMES EVEN THOUGH BONE POSE IS NOT. Replaying from anywhere
    ///      but the beginning, or skipping a frame, leaves the filters in a state the recording never
    ///      passed through, and the answer changes. So this player advances EXACTLY ONE recorded frame per
    ///      tick, always from frame 0, and <see cref="Seek"/> refuses. See <see cref="LockstepFrames"/>.
    ///
    ///   2. FRAMERATE-DEPENDENT BLENDS ARE REAL HERE. A saturate(dt*speed) smoother whose time constant
    ///      tracked GPU speed, and a self-referential slerp that converged by frame count rather than
    ///      elapsed time, were both shipped bugs. A replay at a rate other than the recorded one is
    ///      therefore a DIFFERENT EXPERIMENT. The recorded timestep is available as
    ///      <see cref="CurrentFrameDeltaTime"/>, but this player CANNOT currently force the pipeline to
    ///      use it — the solve reads BasisEventDriver.DeltaTime, which is assigned from Time.deltaTime and
    ///      cannot be overridden without editing that file. <see cref="ReplayRateDriftRatio"/> measures how
    ///      far the replay has diverged so a caller can refuse to believe a mismatched run. THIS IS THE
    ///      LARGEST KNOWN GAP IN THE CAPABILITY; it is a one-field change in BasisEventDriver to close.
    ///
    /// CALIBRATION. The same device stream under a different calibration is a different input. The player
    /// compares the recorded calibration block against the live one and logs the drift at Play();
    /// <see cref="DescribeCalibrationDrift"/> returns it for a caller that wants to fail rather than warn.
    /// </summary>
    public class BasisDeviceStreamPlayer : MonoBehaviour
    {
        [Header("Recording")]
        [Tooltip("Absolute path to a .bds device stream. Loaded on Play() when no recording is set.")]
        public string RecordingPath = string.Empty;

        [Header("Playback State")]
        public bool IsPlaying;

        [Tooltip("Index of the recorded frame that will be pushed next.")]
        public int CurrentFrame;

        [Tooltip("Restart from frame 0 at the end. Filter state is NOT reset, so loop 2 differs from loop 1.")]
        public bool Loop;

        [Header("Determinism")]
        [Tooltip(
            "One recorded frame per tick, always starting at 0. This is the ONLY mode in which a replay " +
            "is a regression tool: it is the mode that keeps carried filter state on the same trajectory " +
            "the recording produced. Turning it off wall-clock-samples the stream and is not reproducible.")]
        public bool LockstepFrames = true;

        [Tooltip("Log a warning when the live calibration differs from the recording's. Off = silent, not safe.")]
        public bool WarnOnCalibrationDrift = true;

        [Header("Tracked Roles")]
        [Tooltip(
            "Roles to drop from the replay. Empty replays the session as recorded. Suppressing a role is a " +
            "legitimate experiment (replay a 6-point session as 3-point to exercise the fallback) but it is " +
            "a DIFFERENT experiment — the result is no longer what the headset produced.")]
        public List<BasisBoneTrackedRole> SuppressedRoles = new List<BasisBoneTrackedRole>();

        /// <summary>The decoded recording. Null until <see cref="Load"/> or <see cref="SetRecording"/>.</summary>
        public BasisDeviceStreamRecording Recording { get; private set; }

        /// <summary>
        /// The timestep the recording ran at for the frame just pushed. This is the value a dt override
        /// hook would feed the pipeline; today it is reporting only. Zero before the first push.
        /// </summary>
        public float CurrentFrameDeltaTime { get; private set; }

        /// <summary>
        /// Live dt divided by the recorded dt for the frame just pushed. 1.0 means the replay is running at
        /// the rate that produced the recording; anything else means framerate-dependent blends are being
        /// integrated differently and the solved pose may legitimately differ. NaN before the first push.
        /// </summary>
        public float ReplayRateDriftRatio { get; private set; } = float.NaN;

        /// <summary>True once the replay has pushed every recorded frame at least once.</summary>
        public bool Completed { get; private set; }

        private struct ReplayTarget
        {
            public int DeviceIndex;
            public BasisBoneTrackedRole Role;
            public BasisInputXRSimulate Device;
        }

        private readonly List<ReplayTarget> _targets = new List<ReplayTarget>(16);
        private bool _initialized;

        /// <summary>
        /// Decodes a recording from disk. Throws <see cref="BasisDeviceStreamFormatException"/> on a bad
        /// magic, a version this build cannot read, or a truncated body — all loud, none recoverable.
        /// </summary>
        public void Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Recording = BasisDeviceStreamFormat.Read(bytes);
            RecordingPath = path;
            CurrentFrame = 0;
            Completed = false;
            BasisDebug.Log(
                $"[DeviceStream] Loaded '{Recording.SessionLabel}': {Recording.FrameCount} frames x {Recording.DeviceCount} devices, " +
                $"{Recording.SummedDuration:F2}s at ~{Recording.NominalHz:F1} Hz, {Recording.WarmupFrames} warm-up frames.",
                BasisDebug.LogTag.IK);
        }

        /// <summary>Injects an already-decoded recording (a synthesised fixture, or one held in memory).</summary>
        public void SetRecording(BasisDeviceStreamRecording recording)
        {
            Recording = recording;
            CurrentFrame = 0;
            Completed = false;
        }

        /// <summary>
        /// Always refuses. Seeking is not merely unimplemented, it is unsound: filter state is carried
        /// across frames while bone pose is not, so arriving at frame N without having played frames
        /// 0..N-1 puts the solve in a state the session never occupied. Play from the start or not at all.
        /// </summary>
        public void Seek(int frame)
        {
            BasisDebug.LogError(
                $"[DeviceStream] Refusing to seek to frame {frame}. A device stream replay is only valid " +
                "played from frame 0: filter state is carried across frames even though bone pose is not, " +
                "so a seek lands the solve in a state the recorded session never passed through.",
                BasisDebug.LogTag.IK);
        }

        /// <summary>Starts (or restarts) the replay from frame 0, creating simulated devices as needed.</summary>
        public void Play()
        {
            if (Recording == null)
            {
                if (string.IsNullOrEmpty(RecordingPath))
                {
                    BasisDebug.LogError("[DeviceStream] No recording assigned and no RecordingPath set.", BasisDebug.LogTag.IK);
                    return;
                }
                Load(RecordingPath);
            }

            if (Recording.FrameCount == 0 || Recording.DeviceCount == 0)
            {
                BasisDebug.LogError("[DeviceStream] Recording is empty — nothing to replay.", BasisDebug.LogTag.IK);
                return;
            }

            if (WarnOnCalibrationDrift)
            {
                string drift = DescribeCalibrationDrift();
                if (!string.IsNullOrEmpty(drift))
                {
                    BasisDebug.LogWarning(
                        "[DeviceStream] Live calibration differs from the recording's. The same device stream " +
                        "under a different calibration is a DIFFERENT INPUT, so this replay does not reproduce " +
                        "the recorded session:\n" + drift,
                        BasisDebug.LogTag.IK);
                }
            }

            if (!_initialized)
            {
                Initialize();
                if (!_initialized)
                {
                    return;
                }
            }

            CurrentFrame = 0;
            Completed = false;
            IsPlaying = true;
        }

        /// <summary>Pauses without resetting position. Filter state keeps whatever it holds.</summary>
        public void Pause()
        {
            IsPlaying = false;
        }

        /// <summary>Stops the replay and tears down the simulated devices.</summary>
        public void Stop()
        {
            IsPlaying = false;
            CurrentFrame = 0;
            Cleanup();
        }

        private void Update()
        {
            if (!IsPlaying || Recording == null)
            {
                return;
            }
            AdvanceAndPush();
        }

        /// <summary>
        /// Pushes exactly one recorded frame into the simulated devices and advances. Public so a harness
        /// can drive the replay in explicit lockstep rather than relying on MonoBehaviour update order —
        /// which is the only way to get a reproducible tick sequence, since nothing here guarantees this
        /// component's Update runs before the device poll in BasisEventDriver.
        /// </summary>
        public void AdvanceAndPush()
        {
            if (Recording == null || !_initialized)
            {
                return;
            }
            if (CurrentFrame >= Recording.FrameCount)
            {
                Completed = true;
                if (!Loop)
                {
                    IsPlaying = false;
                    return;
                }
                // Loop restarts the STREAM, not the pipeline. Filters keep the state the last frame left
                // them in, so a looped replay's second pass is not a repeat of its first.
                CurrentFrame = 0;
            }

            BasisDeviceStreamFrame frame = Recording.Frames[CurrentFrame];
            CurrentFrameDeltaTime = frame.DeltaTime;
            ReplayRateDriftRatio = frame.DeltaTime > 0f ? Time.deltaTime / frame.DeltaTime : float.NaN;

            PushFrame(CurrentFrame);
            CurrentFrame++;
        }

        /// <summary>
        /// Writes one recorded frame's poses onto the simulated devices' FollowMovement transforms. Pure
        /// with respect to the recording: the same frame index always produces the same local poses for a
        /// given live OffsetCoords, which is what makes two replays of one stream identical.
        /// </summary>
        public void PushFrame(int frameIndex)
        {
            Vector3 offsetPosition = BasisInput.OffsetCoords.position;
            Quaternion offsetRotation = BasisInput.OffsetCoords.rotation;

            for (int Index = 0; Index < _targets.Count; Index++)
            {
                ReplayTarget target = _targets[Index];
                if (target.Device == null || target.Device.FollowMovement == null)
                {
                    continue;
                }

                BasisDeviceStreamSample sample = Recording.SampleAt(frameIndex, target.DeviceIndex);
                if (!sample.Connected)
                {
                    // A dropout: the device reported nothing this frame, so the replay holds the last pose
                    // exactly as the live pipeline would when a tracker stops updating. Recorded as a
                    // dropout, replayed as a dropout.
                    continue;
                }

                BasisDeviceStreamFormat.ComputeFollowLocalPose(
                    sample.ScaledPosition, sample.ScaledRotation,
                    offsetPosition, offsetRotation,
                    out Vector3 localPosition, out Quaternion localRotation);

                target.Device.FollowMovement.SetLocalPositionAndRotation(localPosition, localRotation);
            }
        }

        private void Initialize()
        {
            BasisLocalPlayer localPlayer = BasisLocalPlayer.Instance;
            if (localPlayer == null)
            {
                BasisDebug.LogError("[DeviceStream] No local player available.", BasisDebug.LogTag.IK);
                return;
            }

            BasisSimulateXR simulator = ResolveSimulator();
            if (simulator == null)
            {
                BasisDebug.LogError("[DeviceStream] Could not resolve or create a BasisSimulateXR provider.", BasisDebug.LogTag.IK);
                return;
            }

            _targets.Clear();
            for (int Index = 0; Index < Recording.DeviceCount; Index++)
            {
                BasisDeviceStreamDevice device = Recording.Devices[Index];
                if (!device.HasRoleAssigned)
                {
                    continue;
                }

                BasisBoneTrackedRole role = (BasisBoneTrackedRole)device.Role;
                if (SuppressedRoles != null && SuppressedRoles.Contains(role))
                {
                    BasisDebug.Log($"[DeviceStream] Suppressing recorded role {role} by request.", BasisDebug.LogTag.IK);
                    continue;
                }

                // Prior art's rule, kept: never fight a real device for a role. A replay run with hardware
                // still attached would otherwise produce a pose that is neither the recording nor the live
                // session.
                if (BasisDeviceManagement.Instance.FindDevice(out BasisInput _, role))
                {
                    BasisDebug.Log($"[DeviceStream] Skipping {role} — a real device already holds it.", BasisDebug.LogTag.IK);
                    continue;
                }

                BasisInputXRSimulate simulated = simulator.CreatePhysicalTrackedDevice(
                    UniqueID: $"DeviceStreamReplay_{role}_{Index}",
                    UnUniqueID: string.IsNullOrEmpty(device.CommonDeviceIdentifier) ? "DeviceStreamReplay" : device.CommonDeviceIdentifier,
                    Role: role,
                    hasrole: true,
                    subSystems: "BasisDeviceStreamReplay");

                // AccountForScale applies a SECOND scale factor after the forward map that
                // ComputeFollowLocalPose inverts. Leaving it on would silently rescale every replayed pose.
                simulated.AccountForScale = false;

                // The recording already contains the session's real tracker noise. Adding synthetic jitter
                // on top would destroy the one property that makes this data worth more than mocap.
                simulated.AddSomeRandomizedInput = false;

                // Carry the recorded hardware class through: it selects the "Auto" smoothing preset, so a
                // replay that classified a lighthouse tracker as an IMU would filter the stream differently
                // from the session that produced it.
                simulated.TrackingHardware = (BasisTrackingHardware)device.TrackingHardware;

                _targets.Add(new ReplayTarget
                {
                    DeviceIndex = Index,
                    Role = role,
                    Device = simulated,
                });

                BasisDebug.Log($"[DeviceStream] Created replay device for {role} (recorded as '{device.UniqueDeviceIdentifier}').", BasisDebug.LogTag.IK);
            }

            if (_targets.Count == 0)
            {
                BasisDebug.LogError("[DeviceStream] Recording produced no replayable devices.", BasisDebug.LogTag.IK);
                return;
            }

            Basis.Scripts.Avatar.BasisAvatarIKStageCalibration.FullBodyCalibration();
            _initialized = true;
            BasisDebug.Log($"[DeviceStream] Initialized {_targets.Count} replay devices.", BasisDebug.LogTag.IK);
        }

        private static BasisSimulateXR ResolveSimulator()
        {
            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management == null)
            {
                return null;
            }

            for (int Index = 0; Index < management.BaseTypes.Length; Index++)
            {
                if (management.BaseTypes[Index] is BasisSimulateXR existing)
                {
                    return existing;
                }
            }

            GameObject host = new GameObject("DeviceStreamReplay_SimulateXR");
            host.hideFlags = HideFlags.HideAndDontSave;
            BasisSimulateXR created = host.AddComponent<BasisSimulateXR>();
            BasisBaseTypeManagement[] baseTypes = management.BaseTypes;
            Array.Resize(ref baseTypes, baseTypes.Length + 1);
            baseTypes[^1] = created;
            management.BaseTypes = baseTypes;
            return created;
        }

        /// <summary>
        /// Returns a human-readable list of every calibration field that differs between the recording and
        /// the live session, or an empty string when they agree. Empty is the ONLY state in which a replay
        /// reproduces its session — every entry in this list is a reason the solved pose may differ for
        /// reasons that have nothing to do with the code under test.
        /// </summary>
        public string DescribeCalibrationDrift()
        {
            if (Recording == null)
            {
                return string.Empty;
            }
            return DescribeCalibrationDrift(Recording.Calibration, BasisDeviceStreamRecorder.CaptureCalibration());
        }

        /// <summary>
        /// Pure comparison of two calibration blocks. Static and dependency-free so a test can pin the
        /// drift reporting without a live player.
        /// </summary>
        public static string DescribeCalibrationDrift(BasisDeviceStreamCalibration recorded, BasisDeviceStreamCalibration live)
        {
            StringBuilder sb = new StringBuilder();
            Compare(sb, "DeviceScale", recorded.DeviceScale, live.DeviceScale);
            Compare(sb, "ScaledToMatchValue", recorded.ScaledToMatchValue, live.ScaledToMatchValue);
            Compare(sb, "AppliedUpScale", recorded.AppliedUpScale, live.AppliedUpScale);
            Compare(sb, "AvatarToPlayerRatioScaled", recorded.AvatarToPlayerRatioScaled, live.AvatarToPlayerRatioScaled);
            Compare(sb, "PlayerToAvatarRatioScaled", recorded.PlayerToAvatarRatioScaled, live.PlayerToAvatarRatioScaled);
            Compare(sb, "PlayerEyeHeight", recorded.PlayerEyeHeight, live.PlayerEyeHeight);
            Compare(sb, "AvatarEyeHeight", recorded.AvatarEyeHeight, live.AvatarEyeHeight);
            Compare(sb, "SelectedScaledPlayerHeight", recorded.SelectedScaledPlayerHeight, live.SelectedScaledPlayerHeight);
            Compare(sb, "SelectedScaledAvatarHeight", recorded.SelectedScaledAvatarHeight, live.SelectedScaledAvatarHeight);
            Compare(sb, "PlayerArmSpan", recorded.PlayerArmSpan, live.PlayerArmSpan);
            Compare(sb, "AvatarArmSpan", recorded.AvatarArmSpan, live.AvatarArmSpan);
            Compare(sb, "PlayerHipHeight", recorded.PlayerHipHeight, live.PlayerHipHeight);
            Compare(sb, "AvatarHipHeight", recorded.AvatarHipHeight, live.AvatarHipHeight);
            Compare(sb, "HeightModeGroundingOffset", recorded.HeightModeGroundingOffset, live.HeightModeGroundingOffset);
            if (recorded.Flags != live.Flags)
            {
                sb.Append("  CalibrationFlags: recorded ").Append(recorded.Flags).Append(" -> live ").Append(live.Flags).Append('\n');
            }
            return sb.ToString();
        }

        // Exact inequality on purpose. These are the numbers that decide what the device stream MEANS, and
        // a tolerance here would quietly bless a replay that is measuring a different body.
        private static void Compare(StringBuilder sb, string label, float recorded, float live)
        {
            if (recorded.Equals(live))
            {
                return;
            }
            sb.Append("  ").Append(label)
              .Append(": recorded ").Append(recorded.ToString("R", CultureInfo.InvariantCulture))
              .Append(" -> live ").Append(live.ToString("R", CultureInfo.InvariantCulture))
              .Append('\n');
        }

        private void Cleanup()
        {
            for (int Index = 0; Index < _targets.Count; Index++)
            {
                BasisInputXRSimulate device = _targets[Index].Device;
                if (device == null)
                {
                    continue;
                }
                device.UnAssignTracker();
                BasisDeviceManagement.Instance.AllInputDevices.Remove(device);
                if (device.gameObject != null)
                {
                    Destroy(device.gameObject);
                }
            }
            _targets.Clear();
            _initialized = false;
        }

        private void OnDestroy()
        {
            Cleanup();
        }
    }
}
