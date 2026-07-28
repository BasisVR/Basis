using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Developer-only recorder that turns a REAL headset session into permanent, replayable test data.
    ///
    /// This is the unified DEVICE-LEVEL recorder. The four recorders that already exist each capture one
    /// subsystem's output — <see cref="BasisCalibrationDebugRecorder"/> (calibration stages),
    /// <see cref="BasisArmIKRuntimeRecorder"/> (solved arm chain), <see cref="BasisPairingRotationRecorder"/>
    /// (linked-pair hip fusion) and BasisFootIKDiagnostics (foot plant/slide). All four record what the IK
    /// PRODUCED, in CSV, for a human to read. This one records what the IK was GIVEN, in binary, for a
    /// machine to replay. That makes it complementary rather than a replacement: none of the four is made
    /// redundant, but all four become reproducible, because their input can now be re-fed frame for frame.
    ///
    /// Gating follows <see cref="BasisCalibrationDebugRecorder"/>'s precedent exactly: the toggle is read
    /// ONCE at <see cref="Begin"/>, and while off every Sample call is a single bool test, so the
    /// instrumentation is free to leave wired in. Output lands under persistentDataPath/DeviceStream,
    /// matching the CalibrationDebug / ArmIKRuntime / PairingRotation convention.
    ///
    /// WHAT IS CAPTURED, and why each part is not optional:
    ///   - per frame, per device: role, unscaled + scaled pose, connected flag, and the frame's timestep;
    ///   - once per session: the full calibration block and every device's calibration residual.
    /// The calibration is mandatory because the same device stream against a different calibration is a
    /// different input. A recording without it would replay, look plausible, and mean nothing.
    /// </summary>
    public static class BasisDeviceStreamRecorder
    {
        /// <summary>
        /// Developer gate. FALSE BY DEFAULT and must stay that way — this writes megabytes per minute.
        ///
        /// This is currently a plain static rather than a settings binding because adding one requires
        /// editing BasisSettingsDefaults, which this change does not touch. The intended wiring is a
        /// sibling of DumpCalibrationCsv:
        ///     public static BasisSettingsBinding&lt;bool&gt; DumpDeviceStreamRecording =
        ///         new("devdumpdevicestream", new BasisPlatformDefault&lt;bool&gt;(false));
        /// read here in <see cref="Begin"/> the way DumpCalibrationCsv is read in
        /// BasisCalibrationDebugRecorder.Begin. Until then a developer sets this from a debug menu or the
        /// console before calling Begin.
        /// </summary>
        public static bool DeveloperEnabled;

        /// <summary>True only between a <see cref="Begin"/> that passed the gate and its <see cref="Flush"/>.</summary>
        public static bool Active { get; private set; }

        /// <summary>Folder recordings are written to, matching the other recorders' convention.</summary>
        public static string OutputDirectory => Path.Combine(Application.persistentDataPath, "DeviceStream");

        /// <summary>File extension. "Basis device stream".</summary>
        public const string FileExtension = ".bds";

        /// <summary>
        /// Priority on <see cref="BasisLocalPlayer.AfterSimulateOnLate"/>. Deliberately far above every
        /// other subscriber (the busiest sit in the 90-220 band) so the recorder samples LAST — after
        /// OnLatePollData has refreshed every device, after bone sim, IK and the animator. Sampling
        /// earlier would capture a half-updated frame.
        /// </summary>
        public const int SamplePriority = 900;

        /// <summary>
        /// Hard ceiling on captured frames, so a recorder left running cannot exhaust memory. At 90 Hz
        /// this is a little over 60 minutes; at 8 devices that is ~155 MB of sample grid, which is the
        /// real limit being defended.
        /// </summary>
        public const int MaxFrames = 330000;

        /// <summary>Leading frames marked as filter warm-up in the header. See BasisDeviceStreamRecording.WarmupFrames.</summary>
        public static int WarmupFrames = 30;

        private static readonly List<BasisDeviceStreamDevice> Devices = new List<BasisDeviceStreamDevice>(16);
        private static readonly Dictionary<string, int> DeviceIndexById = new Dictionary<string, int>(16, StringComparer.Ordinal);
        private static readonly List<BasisDeviceStreamFrame> Frames = new List<BasisDeviceStreamFrame>(4096);

        // Samples are appended flat as the frame is walked, and the device table can GROW mid-session when
        // a tracker connects. FrameSampleCounts remembers how wide each frame was so Flush can rebuild a
        // rectangular grid, back-filling the frames that predate a late-joining device as disconnected.
        private static readonly List<BasisDeviceStreamSample> FlatSamples = new List<BasisDeviceStreamSample>(65536);
        private static readonly List<int> FrameSampleCounts = new List<int>(4096);

        private static string _sessionLabel = string.Empty;
        private static int _requestedFrames;
        private static double _startTime;
        private static bool _hooked;
        private static string _lastWrittenPath = string.Empty;

        /// <summary>Path of the most recent successful <see cref="Flush"/>. Empty until one succeeds.</summary>
        public static string LastWrittenPath => _lastWrittenPath;

        /// <summary>Frames captured so far in the active session.</summary>
        public static int CapturedFrames => Frames.Count;

        /// <summary>
        /// Starts a capture. Reads the developer gate; when off, this and every later Sample is a no-op
        /// until the next Begin. Registers the per-frame hook itself, so a caller only needs this one call.
        /// </summary>
        /// <param name="sessionLabel">Identifier used in the file name (avatar or display name).</param>
        /// <param name="frames">Frames to capture before auto-flushing. Clamped to <see cref="MaxFrames"/>.</param>
        public static void Begin(string sessionLabel, int frames)
        {
            Active = DeveloperEnabled;
            if (!Active)
            {
                return;
            }

            Devices.Clear();
            DeviceIndexById.Clear();
            Frames.Clear();
            FlatSamples.Clear();
            FrameSampleCounts.Clear();

            _sessionLabel = string.IsNullOrEmpty(sessionLabel) ? "session" : sessionLabel;
            _requestedFrames = Mathf.Clamp(frames, 1, MaxFrames);
            _startTime = Time.timeAsDouble;

            if (!_hooked && BasisLocalPlayer.Instance != null)
            {
                BasisLocalPlayer.AfterSimulateOnLate.AddAction(SamplePriority, Sample);
                _hooked = true;
            }

            BasisDebug.Log(
                $"[DeviceStream] Begin '{_sessionLabel}' for {_requestedFrames} frames — move naturally, this is the session that becomes the fixture.",
                BasisDebug.LogTag.Input);
        }

        /// <summary>
        /// Captures one frame of every connected device. Called automatically from
        /// <see cref="BasisLocalPlayer.AfterSimulateOnLate"/>; single bool test when inactive.
        /// </summary>
        public static void Sample()
        {
            if (!Active)
            {
                return;
            }

            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management == null || management.AllInputDevices == null)
            {
                return;
            }

            // Every device already in the table starts this frame disconnected; the walk below turns back
            // on the ones that actually reported. That is how a dropout records as a dropout instead of a
            // silently held pose.
            int frameStart = FlatSamples.Count;
            for (int Index = 0; Index < Devices.Count; Index++)
            {
                FlatSamples.Add(default);
            }

            int deviceCount = management.AllInputDevices.Count;
            for (int Index = 0; Index < deviceCount; Index++)
            {
                BasisInput input = management.AllInputDevices[Index];
                if (input == null)
                {
                    continue;
                }

                int deviceIndex = ResolveDeviceIndex(input);
                if (deviceIndex < 0)
                {
                    continue;
                }

                // A device that first appears now widens the table; this frame's slice grows with it, and
                // Flush back-fills the earlier frames as disconnected.
                while (FlatSamples.Count < frameStart + Devices.Count)
                {
                    FlatSamples.Add(default);
                }

                FlatSamples[frameStart + deviceIndex] = BuildSample(input);
            }

            float deltaTime = Time.deltaTime;
            Frames.Add(new BasisDeviceStreamFrame
            {
                FrameIndex = Frames.Count,
                TimeSeconds = Time.timeAsDouble - _startTime,
                DeltaTime = deltaTime,
            });
            FrameSampleCounts.Add(FlatSamples.Count - frameStart);

            if (Frames.Count >= _requestedFrames || Frames.Count >= MaxFrames)
            {
                Flush();
            }
        }

        private static BasisDeviceStreamSample BuildSample(BasisInput input)
        {
            byte flags = BasisDeviceStreamSample.FlagConnected;
            byte role = 0;
            if (input.TryGetRole(out Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole resolved))
            {
                flags |= BasisDeviceStreamSample.FlagHasRoleAssigned;
                role = (byte)resolved;
            }

            return new BasisDeviceStreamSample
            {
                Flags = flags,
                Role = role,
                UnscaledPosition = input.UnscaledDeviceCoord.position,
                UnscaledRotation = input.UnscaledDeviceCoord.rotation,
                ScaledPosition = input.ScaledDeviceCoord.position,
                ScaledRotation = input.ScaledDeviceCoord.rotation,
            };
        }

        // Devices are keyed by UniqueDeviceIdentifier, which survives for the life of a session. A
        // reconnect that changes the identifier deliberately becomes a NEW row rather than silently
        // continuing the old one — the reconnect is part of what the session is evidence of.
        private static int ResolveDeviceIndex(BasisInput input)
        {
            string id = input.UniqueDeviceIdentifier;
            if (string.IsNullOrEmpty(id))
            {
                return -1;
            }
            if (DeviceIndexById.TryGetValue(id, out int existing))
            {
                return existing;
            }
            if (Devices.Count >= byte.MaxValue)
            {
                return -1;
            }

            int index = Devices.Count;
            Devices.Add(BuildDeviceRecord(input));
            DeviceIndexById[id] = index;
            return index;
        }

        private static BasisDeviceStreamDevice BuildDeviceRecord(BasisInput input)
        {
            byte flags = 0;
            byte role = 0;
            if (input.TryGetRole(out Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole resolved))
            {
                flags |= BasisDeviceStreamDevice.FlagHasRoleAssigned;
                role = (byte)resolved;
            }
            if (input.IsCameraTracked)
            {
                flags |= BasisDeviceStreamDevice.FlagIsCameraTracked;
            }
            if (input.IsLinked)
            {
                flags |= BasisDeviceStreamDevice.FlagIsLinked;
            }
            if (input.HasCalibratedOffsetSnapshot)
            {
                flags |= BasisDeviceStreamDevice.FlagHasCalibratedOffsetSnapshot;
            }

            // The calibration residual: the inverse offset actually driving this bone. This is the third
            // thing mocap cannot supply — the user's real avatar/body mismatch, as solved.
            Vector3 inverseOffsetPosition = Vector3.zero;
            Quaternion inverseOffsetRotation = Quaternion.identity;
            if (input.HasControl && input.Control != null)
            {
                if (input.Control.UseInverseOffset)
                {
                    flags |= BasisDeviceStreamDevice.FlagUseInverseOffset;
                }
                Basis.Scripts.Common.BasisCalibratedCoords inverse = input.Control.InverseOffsetFromBone;
                inverseOffsetPosition = inverse.position;
                inverseOffsetRotation = inverse.rotation;
            }

            return new BasisDeviceStreamDevice
            {
                UniqueDeviceIdentifier = input.UniqueDeviceIdentifier ?? string.Empty,
                CommonDeviceIdentifier = input.CommonDeviceIdentifier ?? string.Empty,
                DeviceSerial = input.DeviceSerial ?? string.Empty,
                DeviceControllerType = input.DeviceControllerType ?? string.Empty,
                SubSystemIdentifier = input.SubSystemIdentifier ?? string.Empty,
                Role = role,
                TrackingHardware = (byte)input.TrackingHardware,
                Flags = flags,
                CenterEyeVerticalOffset = input.CenterEyeVerticalOffset,
                CenterEyeOffset = input.CenterEyeOffset,
                ScaledControlPositionOffset = input.ScaledControlPositionOffset,
                CalibratedUnscaledPosition = input.CalibratedUnscaledPosition,
                CalibratedUnscaledRotation = input.CalibratedUnscaledRotation,
                CalibratedUnscaledHeadPosition = input.CalibratedUnscaledHeadPosition,
                CalibratedUnscaledHeadRotation = input.CalibratedUnscaledHeadRotation,
                InverseOffsetPosition = inverseOffsetPosition,
                InverseOffsetRotation = inverseOffsetRotation,
            };
        }

        /// <summary>
        /// Snapshots the live calibration. Public and static so the same block can be taken at replay time
        /// and diffed against the recorded one — see BasisDeviceStreamPlayer.DescribeCalibrationDrift.
        /// </summary>
        public static BasisDeviceStreamCalibration CaptureCalibration()
        {
            byte flags = 0;
            if (BasisHeightDriver.HasGenuinePlayerEyeHeight)
            {
                flags |= BasisDeviceStreamCalibration.FlagHasGenuinePlayerEyeHeight;
            }
            if (BasisHeightDriver.HasUserCalibratedHeight)
            {
                flags |= BasisDeviceStreamCalibration.FlagHasUserCalibratedHeight;
            }

            return new BasisDeviceStreamCalibration
            {
                DeviceScale = BasisHeightDriver.DeviceScale,
                ScaledToMatchValue = BasisHeightDriver.ScaledToMatchValue,
                AppliedUpScale = BasisHeightDriver.AppliedUpScale,
                AvatarToPlayerRatioScaled = BasisHeightDriver.AvatarToPlayerRatioScaled,
                PlayerToAvatarRatioScaled = BasisHeightDriver.PlayerToAvatarRatioScaled,
                PlayerEyeHeight = BasisHeightDriver.PlayerEyeHeight,
                AvatarEyeHeight = BasisHeightDriver.AvatarEyeHeight,
                SelectedScaledPlayerHeight = BasisHeightDriver.SelectedScaledPlayerHeight,
                SelectedScaledAvatarHeight = BasisHeightDriver.SelectedScaledAvatarHeight,
                PlayerArmSpan = BasisHeightDriver.PlayerArmSpan,
                AvatarArmSpan = BasisHeightDriver.AvatarArmSpan,
                PlayerHipHeight = BasisHeightDriver.PlayerHipHeight,
                AvatarHipHeight = BasisHeightDriver.AvatarHipHeight,
                HeightModeGroundingOffset = BasisHeightDriver.HeightModeGroundingOffset,
                Flags = flags,
                OffsetCoordsPosition = BasisInput.OffsetCoords.position,
                OffsetCoordsRotation = BasisInput.OffsetCoords.rotation,
            };
        }

        /// <summary>
        /// Rebuilds the ragged capture into the rectangular grid the format stores, attaches the
        /// calibration block, and returns the recording WITHOUT writing it. Exposed separately from
        /// <see cref="Flush"/> so a caller can inspect or re-encode a capture in memory.
        /// </summary>
        public static BasisDeviceStreamRecording BuildRecording()
        {
            int deviceCount = Devices.Count;
            int frameCount = Frames.Count;

            BasisDeviceStreamRecording recording = new BasisDeviceStreamRecording
            {
                SessionLabel = _sessionLabel,
                ProducerVersion = Application.version ?? string.Empty,
                CapturedUtcTicks = DateTime.UtcNow.Ticks,
                NominalHz = EstimateNominalHz(),
                WarmupFrames = Mathf.Clamp(WarmupFrames, 0, Mathf.Max(0, frameCount)),
                Calibration = CaptureCalibration(),
                Devices = new List<BasisDeviceStreamDevice>(Devices),
                Frames = new List<BasisDeviceStreamFrame>(Frames),
                Samples = new BasisDeviceStreamSample[frameCount * deviceCount],
            };

            // Rectangularise. A frame captured before a device joined simply has no entry for it, and the
            // default sample (Flags == 0) is exactly "not connected", which is the truth.
            int readCursor = 0;
            for (int Frame = 0; Frame < frameCount; Frame++)
            {
                int width = FrameSampleCounts[Frame];
                int writeBase = Frame * deviceCount;
                for (int Device = 0; Device < deviceCount; Device++)
                {
                    recording.Samples[writeBase + Device] = Device < width
                        ? FlatSamples[readCursor + Device]
                        : default;
                }
                readCursor += width;
            }

            recording.ValidateStructure();
            return recording;
        }

        // Frame-count over summed timestep. Reported as advisory only: DeltaTime per frame is the truth,
        // and a session with a hitch in it has no single meaningful rate.
        private static float EstimateNominalHz()
        {
            double total = 0d;
            for (int Index = 0; Index < Frames.Count; Index++)
            {
                total += Frames[Index].DeltaTime;
            }
            return total > 0d ? (float)(Frames.Count / total) : 0f;
        }

        /// <summary>
        /// Writes the capture and ends the session. No-op when the session was never enabled. Always
        /// clears state and unhooks, so the next Begin starts clean.
        /// </summary>
        /// <param name="flags">Body options; Deflate is lossless and recommended for long sessions.</param>
        public static string Flush(BasisDeviceStreamFlags flags = BasisDeviceStreamFlags.DeflateBody)
        {
            if (!Active)
            {
                return string.Empty;
            }

            string fullPath = string.Empty;
            try
            {
                if (Frames.Count > 0 && Devices.Count > 0)
                {
                    BasisDeviceStreamRecording recording = BuildRecording();
                    byte[] bytes = BasisDeviceStreamFormat.Write(recording, flags);

                    string directory = OutputDirectory;
                    Directory.CreateDirectory(directory);

                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                    fullPath = Path.Combine(directory, $"devicestream_{Sanitize(_sessionLabel)}_{stamp}{FileExtension}");
                    File.WriteAllBytes(fullPath, bytes);
                    _lastWrittenPath = fullPath;

                    BasisDebug.Log(
                        $"[DeviceStream] Wrote {recording.FrameCount} frames x {recording.DeviceCount} devices " +
                        $"({bytes.Length} bytes, {recording.SummedDuration:F2}s at ~{recording.NominalHz:F1} Hz) to {fullPath}",
                        BasisDebug.LogTag.Input);
                }
                else
                {
                    BasisDebug.LogWarning("[DeviceStream] Nothing captured — no frames or no devices.", BasisDebug.LogTag.Input);
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[DeviceStream] Failed to write recording: {e}");
            }
            finally
            {
                Stop();
            }
            return fullPath;
        }

        /// <summary>Ends the session and releases everything without writing. Safe to call at any time.</summary>
        public static void Stop()
        {
            if (_hooked)
            {
                BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(SamplePriority, Sample);
                _hooked = false;
            }
            Devices.Clear();
            DeviceIndexById.Clear();
            Frames.Clear();
            FlatSamples.Clear();
            FrameSampleCounts.Clear();
            Active = false;
        }

        private static string Sanitize(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }
    }
}
