using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// On-disk format ("BASISDVS") for a recorded VR device stream, plus the pure codec and the pure
    /// pose-reconstruction math that <see cref="BasisDeviceStreamRecorder"/> writes and
    /// <see cref="BasisDeviceStreamPlayer"/> replays.
    ///
    /// WHY THIS EXISTS. The IK suite has ~810 tests, a 109-clip CMU mocap corpus and 64 editor sweeps,
    /// and one limitation none of them can lift: nothing is verified in a headset. Mocap cannot supply
    /// three things only a real session has —
    ///   1. real controller ROTATION CONVENTIONS (the corpus NOTICE.md says it outright: "A mocap hand
    ///      is not a controller... A VR controller's rotation is a GRIP convention"; 27 hand-rotation
    ///      features shipped on mocap evidence and were wrong),
    ///   2. real tracker noise, stand-off, slip and dropout,
    ///   3. real calibration residual — this user's actual avatar/body mismatch.
    /// A recorded device stream supplies all three, because it IS a real session. Once it replays
    /// deterministically the in-headset verification debt stops being permanent: the session becomes a
    /// fixture like any mocap clip.
    ///
    /// WHY THE CALIBRATION BLOCK IS MANDATORY. The same device stream against a different calibration is
    /// a DIFFERENT INPUT — scale, per-device offsets and player height all sit between the tracker pose
    /// and the solved bone. A recording without them is not reproducible, so the calibration block is
    /// written unconditionally and <see cref="BasisDeviceStreamPlayer"/> compares it against the live
    /// calibration before it will claim a faithful replay.
    ///
    /// WHY THERE IS NO QUANTISATION. "Compact" stops exactly where bit-exactness begins. Quantising the
    /// poses would break the round-trip equality that the determinism test asserts, and a lossy fixture
    /// is a fixture that drifts against the code it is meant to pin. Poses are raw IEEE-754 float32.
    /// Compactness is bought back losslessly with the optional <see cref="BasisDeviceStreamFlags.DeflateBody"/>
    /// body compression instead, which cannot change a single decoded bit.
    ///
    /// SIZE. Header is a few hundred bytes. Each frame costs 16 bytes plus 58 bytes per device, so an
    /// 8-device session is 480 B/frame = 43.2 KB/s at 90 Hz = 2.59 MB/min = 155 MB/hour UNCOMPRESSED.
    /// Deflate on smooth tracker data should cut that substantially; the ratio is NOT measured here and
    /// no claim is made about it.
    /// </summary>
    public static class BasisDeviceStreamFormat
    {
        /// <summary>File magic. Eight ASCII bytes so a corrupt/foreign file is rejected before any parse.</summary>
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("BASISDVS");

        /// <summary>
        /// Format version. BUMP THIS whenever the body layout changes in any way that an older reader
        /// would misparse. <see cref="Read"/> refuses a mismatch outright rather than guessing — these
        /// files are fixtures and will outlive the code that wrote them, so a silently misparsed
        /// recording is far worse than a refused one.
        /// </summary>
        public const uint Version = 1;

        /// <summary>Header is magic(8) + version(4) + flags(4) + bodyLength(4).</summary>
        public const int HeaderLengthBytes = 20;

        /// <summary>Per-frame fixed cost in the body: frame index(4) + time(8) + delta(4).</summary>
        public const int FrameOverheadBytes = 16;

        /// <summary>Per-frame per-device cost: flags(1) + role(1) + unscaled(28) + scaled(28).</summary>
        public const int SampleBytes = 58;

        /// <summary>
        /// Refuses to allocate for a frame/device count a stream of the given length could not possibly
        /// contain. Guards a corrupt or truncated file from turning into a multi-gigabyte allocation
        /// before it fails.
        /// </summary>
        private const int AbsurdCountCeiling = 1 << 26;

        /// <summary>
        /// Largest body a declared length may claim, in bytes. Flat 1 GiB rather than derived from
        /// AbsurdCountCeiling * SampleBytes: that product is 3.89e9, which does not fit in the int32 the
        /// length is read as, so comparing against it is DEAD CODE — the compiler says so (CS0652) and the
        /// guard silently never fires. 1 GiB is roughly 6.6 hours of an 8-device session uncompressed,
        /// which is far past any recording anyone should be replaying as a fixture.
        /// </summary>
        private const int MaximumBodyLengthBytes = 1 << 30;

        /// <summary>
        /// Serialises a recording. The body is built whole in memory first so its uncompressed length
        /// can be written into the header — that length is both the preallocation hint and the
        /// strongest corruption check the reader has.
        /// </summary>
        /// <param name="recording">Recording to serialise. Must be non-null and structurally consistent.</param>
        /// <param name="flags">Body options; <see cref="BasisDeviceStreamFlags.DeflateBody"/> is lossless.</param>
        public static byte[] Write(BasisDeviceStreamRecording recording, BasisDeviceStreamFlags flags = BasisDeviceStreamFlags.None)
        {
            if (recording == null)
            {
                throw new ArgumentNullException(nameof(recording));
            }
            recording.ValidateStructure();

            byte[] body;
            using (MemoryStream bodyStream = new MemoryStream(EstimateBodyBytes(recording)))
            {
                using (BinaryWriter writer = new BinaryWriter(bodyStream, new UTF8Encoding(false), true))
                {
                    WriteBody(writer, recording);
                }
                body = bodyStream.ToArray();
            }

            using (MemoryStream output = new MemoryStream(HeaderLengthBytes + body.Length))
            {
                using (BinaryWriter writer = new BinaryWriter(output, new UTF8Encoding(false), true))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    writer.Write((uint)flags);
                    writer.Write(body.Length);
                }

                if ((flags & BasisDeviceStreamFlags.DeflateBody) != 0)
                {
                    // Fully qualified: UnityEngine defines its own CompressionLevel and the two collide.
                    using (DeflateStream deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true))
                    {
                        deflate.Write(body, 0, body.Length);
                    }
                }
                else
                {
                    output.Write(body, 0, body.Length);
                }
                return output.ToArray();
            }
        }

        /// <summary>
        /// Deserialises a recording, or throws <see cref="BasisDeviceStreamFormatException"/> with a
        /// message that names what was expected and what was found. Every failure path here is loud on
        /// purpose: a fixture that half-loads is a test that lies.
        /// </summary>
        public static BasisDeviceStreamRecording Read(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }
            if (bytes.Length < HeaderLengthBytes)
            {
                throw new BasisDeviceStreamFormatException(
                    $"Device stream is {bytes.Length} bytes, too short to contain the {HeaderLengthBytes}-byte header.");
            }

            for (int Index = 0; Index < Magic.Length; Index++)
            {
                if (bytes[Index] != Magic[Index])
                {
                    throw new BasisDeviceStreamFormatException(
                        "Device stream does not start with the 'BASISDVS' magic — this is not a Basis device stream recording.");
                }
            }

            uint version = BitConverter.ToUInt32(bytes, 8);
            if (version != Version)
            {
                // The whole point of the version field. Refuse, name both numbers, and say what to do —
                // a recording is a fixture, and re-recording it needs a headset and a human in it.
                throw new BasisDeviceStreamFormatException(
                    $"Device stream format version {version} cannot be read by this build, which reads version {Version} only. " +
                    "Replay it with a build of the matching vintage, or re-record the session.");
            }

            BasisDeviceStreamFlags flags = (BasisDeviceStreamFlags)BitConverter.ToUInt32(bytes, 12);
            int bodyLength = BitConverter.ToInt32(bytes, 16);
            if (bodyLength < 0 || bodyLength > MaximumBodyLengthBytes)
            {
                throw new BasisDeviceStreamFormatException($"Device stream declares an impossible body length of {bodyLength} bytes.");
            }

            byte[] body;
            if ((flags & BasisDeviceStreamFlags.DeflateBody) != 0)
            {
                body = new byte[bodyLength];
                using (MemoryStream compressed = new MemoryStream(bytes, HeaderLengthBytes, bytes.Length - HeaderLengthBytes, false))
                using (DeflateStream deflate = new DeflateStream(compressed, CompressionMode.Decompress))
                {
                    int filled = 0;
                    while (filled < bodyLength)
                    {
                        int read = deflate.Read(body, filled, bodyLength - filled);
                        if (read <= 0)
                        {
                            throw new BasisDeviceStreamFormatException(
                                $"Device stream body is truncated: decompressed {filled} of a declared {bodyLength} bytes.");
                        }
                        filled += read;
                    }
                }
            }
            else
            {
                if (bytes.Length - HeaderLengthBytes < bodyLength)
                {
                    throw new BasisDeviceStreamFormatException(
                        $"Device stream body is truncated: {bytes.Length - HeaderLengthBytes} bytes present, {bodyLength} declared.");
                }
                body = new byte[bodyLength];
                Buffer.BlockCopy(bytes, HeaderLengthBytes, body, 0, bodyLength);
            }

            using (MemoryStream bodyStream = new MemoryStream(body, false))
            using (BinaryReader reader = new BinaryReader(bodyStream, new UTF8Encoding(false), true))
            {
                try
                {
                    return ReadBody(reader, bodyLength);
                }
                catch (EndOfStreamException e)
                {
                    throw new BasisDeviceStreamFormatException("Device stream body ended early — the file is truncated or corrupt.", e);
                }
            }
        }

        private static void WriteBody(BinaryWriter writer, BasisDeviceStreamRecording recording)
        {
            writer.Write(recording.SessionLabel ?? string.Empty);
            writer.Write(recording.ProducerVersion ?? string.Empty);
            writer.Write(recording.CapturedUtcTicks);
            writer.Write(recording.NominalHz);
            writer.Write(recording.WarmupFrames);

            WriteCalibration(writer, recording.Calibration);

            int deviceCount = recording.Devices.Count;
            writer.Write(deviceCount);
            for (int Index = 0; Index < deviceCount; Index++)
            {
                WriteDevice(writer, recording.Devices[Index]);
            }

            int frameCount = recording.Frames.Count;
            writer.Write(frameCount);
            for (int Frame = 0; Frame < frameCount; Frame++)
            {
                BasisDeviceStreamFrame frame = recording.Frames[Frame];
                writer.Write(frame.FrameIndex);
                writer.Write(frame.TimeSeconds);
                writer.Write(frame.DeltaTime);

                int baseOffset = Frame * deviceCount;
                for (int Device = 0; Device < deviceCount; Device++)
                {
                    WriteSample(writer, recording.Samples[baseOffset + Device]);
                }
            }
        }

        private static BasisDeviceStreamRecording ReadBody(BinaryReader reader, int bodyLength)
        {
            BasisDeviceStreamRecording recording = new BasisDeviceStreamRecording
            {
                SessionLabel = reader.ReadString(),
                ProducerVersion = reader.ReadString(),
                CapturedUtcTicks = reader.ReadInt64(),
                NominalHz = reader.ReadSingle(),
                WarmupFrames = reader.ReadInt32(),
            };

            recording.Calibration = ReadCalibration(reader);

            int deviceCount = reader.ReadInt32();
            RejectAbsurdCount(deviceCount, bodyLength, 1, "device");
            recording.Devices = new List<BasisDeviceStreamDevice>(deviceCount);
            for (int Index = 0; Index < deviceCount; Index++)
            {
                recording.Devices.Add(ReadDevice(reader));
            }

            int frameCount = reader.ReadInt32();
            RejectAbsurdCount(frameCount, bodyLength, FrameOverheadBytes + (deviceCount * SampleBytes), "frame");
            recording.Frames = new List<BasisDeviceStreamFrame>(frameCount);
            recording.Samples = new BasisDeviceStreamSample[(long)frameCount * deviceCount <= int.MaxValue
                ? frameCount * deviceCount
                : throw new BasisDeviceStreamFormatException("Device stream declares more samples than can be addressed.")];

            for (int Frame = 0; Frame < frameCount; Frame++)
            {
                recording.Frames.Add(new BasisDeviceStreamFrame
                {
                    FrameIndex = reader.ReadInt32(),
                    TimeSeconds = reader.ReadDouble(),
                    DeltaTime = reader.ReadSingle(),
                });

                int baseOffset = Frame * deviceCount;
                for (int Device = 0; Device < deviceCount; Device++)
                {
                    recording.Samples[baseOffset + Device] = ReadSample(reader);
                }
            }
            return recording;
        }

        // A count is impossible when the bytes it implies exceed what the body actually holds. Catches a
        // corrupt length before it becomes an OutOfMemoryException instead of a clear message.
        private static void RejectAbsurdCount(int count, int bodyLength, int bytesEach, string noun)
        {
            if (count < 0 || count > AbsurdCountCeiling || (bytesEach > 0 && (long)count * bytesEach > bodyLength))
            {
                throw new BasisDeviceStreamFormatException(
                    $"Device stream declares {count} {noun}(s), which cannot fit in its {bodyLength}-byte body — the file is corrupt.");
            }
        }

        private static void WriteCalibration(BinaryWriter writer, BasisDeviceStreamCalibration c)
        {
            writer.Write(c.DeviceScale);
            writer.Write(c.ScaledToMatchValue);
            writer.Write(c.AppliedUpScale);
            writer.Write(c.AvatarToPlayerRatioScaled);
            writer.Write(c.PlayerToAvatarRatioScaled);
            writer.Write(c.PlayerEyeHeight);
            writer.Write(c.AvatarEyeHeight);
            writer.Write(c.SelectedScaledPlayerHeight);
            writer.Write(c.SelectedScaledAvatarHeight);
            writer.Write(c.PlayerArmSpan);
            writer.Write(c.AvatarArmSpan);
            writer.Write(c.PlayerHipHeight);
            writer.Write(c.AvatarHipHeight);
            writer.Write(c.HeightModeGroundingOffset);
            writer.Write(c.Flags);
            WriteVector3(writer, c.OffsetCoordsPosition);
            WriteQuaternion(writer, c.OffsetCoordsRotation);
        }

        private static BasisDeviceStreamCalibration ReadCalibration(BinaryReader reader)
        {
            return new BasisDeviceStreamCalibration
            {
                DeviceScale = reader.ReadSingle(),
                ScaledToMatchValue = reader.ReadSingle(),
                AppliedUpScale = reader.ReadSingle(),
                AvatarToPlayerRatioScaled = reader.ReadSingle(),
                PlayerToAvatarRatioScaled = reader.ReadSingle(),
                PlayerEyeHeight = reader.ReadSingle(),
                AvatarEyeHeight = reader.ReadSingle(),
                SelectedScaledPlayerHeight = reader.ReadSingle(),
                SelectedScaledAvatarHeight = reader.ReadSingle(),
                PlayerArmSpan = reader.ReadSingle(),
                AvatarArmSpan = reader.ReadSingle(),
                PlayerHipHeight = reader.ReadSingle(),
                AvatarHipHeight = reader.ReadSingle(),
                HeightModeGroundingOffset = reader.ReadSingle(),
                Flags = reader.ReadByte(),
                OffsetCoordsPosition = ReadVector3(reader),
                OffsetCoordsRotation = ReadQuaternion(reader),
            };
        }

        private static void WriteDevice(BinaryWriter writer, BasisDeviceStreamDevice d)
        {
            writer.Write(d.UniqueDeviceIdentifier ?? string.Empty);
            writer.Write(d.CommonDeviceIdentifier ?? string.Empty);
            writer.Write(d.DeviceSerial ?? string.Empty);
            writer.Write(d.DeviceControllerType ?? string.Empty);
            writer.Write(d.SubSystemIdentifier ?? string.Empty);
            writer.Write(d.Role);
            writer.Write(d.TrackingHardware);
            writer.Write(d.Flags);
            writer.Write(d.CenterEyeVerticalOffset);
            WriteVector3(writer, d.CenterEyeOffset);
            WriteVector3(writer, d.ScaledControlPositionOffset);
            WriteVector3(writer, d.CalibratedUnscaledPosition);
            WriteQuaternion(writer, d.CalibratedUnscaledRotation);
            WriteVector3(writer, d.CalibratedUnscaledHeadPosition);
            WriteQuaternion(writer, d.CalibratedUnscaledHeadRotation);
            WriteVector3(writer, d.InverseOffsetPosition);
            WriteQuaternion(writer, d.InverseOffsetRotation);
        }

        private static BasisDeviceStreamDevice ReadDevice(BinaryReader reader)
        {
            return new BasisDeviceStreamDevice
            {
                UniqueDeviceIdentifier = reader.ReadString(),
                CommonDeviceIdentifier = reader.ReadString(),
                DeviceSerial = reader.ReadString(),
                DeviceControllerType = reader.ReadString(),
                SubSystemIdentifier = reader.ReadString(),
                Role = reader.ReadByte(),
                TrackingHardware = reader.ReadByte(),
                Flags = reader.ReadByte(),
                CenterEyeVerticalOffset = reader.ReadSingle(),
                CenterEyeOffset = ReadVector3(reader),
                ScaledControlPositionOffset = ReadVector3(reader),
                CalibratedUnscaledPosition = ReadVector3(reader),
                CalibratedUnscaledRotation = ReadQuaternion(reader),
                CalibratedUnscaledHeadPosition = ReadVector3(reader),
                CalibratedUnscaledHeadRotation = ReadQuaternion(reader),
                InverseOffsetPosition = ReadVector3(reader),
                InverseOffsetRotation = ReadQuaternion(reader),
            };
        }

        private static void WriteSample(BinaryWriter writer, BasisDeviceStreamSample s)
        {
            writer.Write(s.Flags);
            writer.Write(s.Role);
            WriteVector3(writer, s.UnscaledPosition);
            WriteQuaternion(writer, s.UnscaledRotation);
            WriteVector3(writer, s.ScaledPosition);
            WriteQuaternion(writer, s.ScaledRotation);
        }

        private static BasisDeviceStreamSample ReadSample(BinaryReader reader)
        {
            return new BasisDeviceStreamSample
            {
                Flags = reader.ReadByte(),
                Role = reader.ReadByte(),
                UnscaledPosition = ReadVector3(reader),
                UnscaledRotation = ReadQuaternion(reader),
                ScaledPosition = ReadVector3(reader),
                ScaledRotation = ReadQuaternion(reader),
            };
        }

        // Raw IEEE-754 float32 in and out — no formatting, no normalisation, no NaN laundering. A
        // tracker that emitted a NaN must read back as a NaN or the recording is not evidence.
        private static void WriteVector3(BinaryWriter writer, Vector3 v)
        {
            writer.Write(v.x);
            writer.Write(v.y);
            writer.Write(v.z);
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            return new Vector3(x, y, z);
        }

        private static void WriteQuaternion(BinaryWriter writer, Quaternion q)
        {
            writer.Write(q.x);
            writer.Write(q.y);
            writer.Write(q.z);
            writer.Write(q.w);
        }

        private static Quaternion ReadQuaternion(BinaryReader reader)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            float w = reader.ReadSingle();
            return new Quaternion(x, y, z, w);
        }

        private static int EstimateBodyBytes(BasisDeviceStreamRecording recording)
        {
            int perFrame = FrameOverheadBytes + (recording.Devices.Count * SampleBytes);
            return 512 + (recording.Devices.Count * 192) + (recording.Frames.Count * perFrame);
        }

        /// <summary>
        /// Inverts <see cref="Basis.Scripts.Device_Management.Devices.Simulation.BasisInputXRSimulate.LateDoPollData"/>'s
        /// forward map, so a simulated device fed this local pose republishes EXACTLY the recorded scaled pose.
        ///
        /// The simulated device computes, from its FollowMovement local pose:
        ///     ScaledDeviceCoord.position = OffsetCoords.position + (OffsetCoords.rotation * localPos)
        ///     ScaledDeviceCoord.rotation = OffsetCoords.rotation * localRot
        /// so the inverse is a single rigid un-transform. Crucially this is solved against the REPLAY-TIME
        /// OffsetCoords, not the recorded one: OffsetCoords is a process-global on BasisInput that the
        /// playspace mover and calibration both write, so it will not generally match the recording. Solving
        /// against the live value makes the replayed scaled pose immune to that drift, which is the whole
        /// reason the recorder stores the scaled pose as the primary signal rather than the raw one.
        ///
        /// This is also why <see cref="Basis.Scripts.Device_Management.Devices.Simulation.BasisInputXRSimulate.AccountForScale"/>
        /// must stay false on a replay device: it multiplies ScaledDeviceCoord by a second scale factor
        /// after this map, and there is no inverse for it here.
        /// </summary>
        public static void ComputeFollowLocalPose(
            Vector3 recordedScaledPosition,
            Quaternion recordedScaledRotation,
            Vector3 replayOffsetPosition,
            Quaternion replayOffsetRotation,
            out Vector3 localPosition,
            out Quaternion localRotation)
        {
            Quaternion inverseOffsetRotation = Quaternion.Inverse(replayOffsetRotation);
            localPosition = inverseOffsetRotation * (recordedScaledPosition - replayOffsetPosition);
            localRotation = inverseOffsetRotation * recordedScaledRotation;
        }

        /// <summary>
        /// The forward map the simulated device applies, exposed so a test can prove the reconstruction
        /// above actually round-trips through the real injection seam rather than merely looking correct.
        /// Kept byte-for-byte in the same operation order as BasisInputXRSimulate so the float error is
        /// the same float error.
        /// </summary>
        public static void ApplySimulatedDeviceForwardMap(
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 replayOffsetPosition,
            Quaternion replayOffsetRotation,
            out Vector3 scaledPosition,
            out Quaternion scaledRotation)
        {
            scaledPosition = replayOffsetPosition + (replayOffsetRotation * localPosition);
            scaledRotation = replayOffsetRotation * localRotation;
        }
    }

    /// <summary>Body-level options recorded in the header. Lossless only — nothing here may alter a decoded bit.</summary>
    [Flags]
    public enum BasisDeviceStreamFlags : uint
    {
        None = 0,

        /// <summary>
        /// Body is Deflate-compressed. Lossless, so decoded bytes are identical either way; only the
        /// FILE bytes differ. Nothing should ever assert on compressed bytes — the deflate encoder's
        /// output is a .NET implementation detail and may change between runtimes.
        /// </summary>
        DeflateBody = 1 << 0,
    }

    /// <summary>Thrown for every malformed, foreign or wrong-version device stream. Never swallowed.</summary>
    public class BasisDeviceStreamFormatException : Exception
    {
        public BasisDeviceStreamFormatException(string message) : base(message) { }
        public BasisDeviceStreamFormatException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The calibration the device stream was produced under. MANDATORY: the same poses interpreted under
    /// a different scale/offset/height are a different input, so a recording missing this is not a
    /// reproducible fixture. Mirrors the <c>BasisHeightDriver</c> statics plus the global
    /// <c>BasisInput.OffsetCoords</c>.
    /// </summary>
    public struct BasisDeviceStreamCalibration
    {
        public const byte FlagHasGenuinePlayerEyeHeight = 1 << 0;
        public const byte FlagHasUserCalibratedHeight = 1 << 1;

        public float DeviceScale;
        public float ScaledToMatchValue;
        public float AppliedUpScale;
        public float AvatarToPlayerRatioScaled;
        public float PlayerToAvatarRatioScaled;
        public float PlayerEyeHeight;
        public float AvatarEyeHeight;
        public float SelectedScaledPlayerHeight;
        public float SelectedScaledAvatarHeight;
        public float PlayerArmSpan;
        public float AvatarArmSpan;
        public float PlayerHipHeight;
        public float AvatarHipHeight;
        public float HeightModeGroundingOffset;
        public byte Flags;
        public Vector3 OffsetCoordsPosition;
        public Quaternion OffsetCoordsRotation;

        public bool HasGenuinePlayerEyeHeight => (Flags & FlagHasGenuinePlayerEyeHeight) != 0;
        public bool HasUserCalibratedHeight => (Flags & FlagHasUserCalibratedHeight) != 0;
    }

    /// <summary>
    /// Per-device identity and CALIBRATION RESIDUAL, captured once at the head of the recording.
    /// The residual fields are the third thing mocap cannot supply: this user's real avatar/body
    /// mismatch, as the tracker offsets that calibration actually solved for.
    /// </summary>
    public struct BasisDeviceStreamDevice
    {
        public const byte FlagHasRoleAssigned = 1 << 0;
        public const byte FlagIsCameraTracked = 1 << 1;
        public const byte FlagIsLinked = 1 << 2;
        public const byte FlagHasCalibratedOffsetSnapshot = 1 << 3;
        public const byte FlagUseInverseOffset = 1 << 4;

        public string UniqueDeviceIdentifier;
        public string CommonDeviceIdentifier;

        /// <summary>Hardware serial. Load-bearing for classification: SlimeVR encodes body part as "human://WAIST".</summary>
        public string DeviceSerial;

        /// <summary>Runtime controller-type/profile string. SteamVR encodes a tracker's assigned body role here.</summary>
        public string DeviceControllerType;

        public string SubSystemIdentifier;

        /// <summary><see cref="Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole"/> as a byte.</summary>
        public byte Role;

        /// <summary>
        /// <see cref="Basis.Scripts.Device_Management.BasisTrackingHardware"/> as a byte. Recorded because it
        /// selects the "Auto" smoothing preset — a replay that classified a lighthouse tracker as an IMU
        /// would be filtering the stream differently from the session that produced it.
        /// </summary>
        public byte TrackingHardware;

        public byte Flags;

        /// <summary>Stand-off from the tracked origin to center-eye. Non-zero on OpenVR HMDs only.</summary>
        public float CenterEyeVerticalOffset;
        public Vector3 CenterEyeOffset;
        public Vector3 ScaledControlPositionOffset;

        /// <summary>Scale-free calibration snapshot: where this tracker sat when calibration captured it.</summary>
        public Vector3 CalibratedUnscaledPosition;
        public Quaternion CalibratedUnscaledRotation;

        /// <summary>The head anchor THIS tracker's snapshot was captured against (each tracker pairs with its own).</summary>
        public Vector3 CalibratedUnscaledHeadPosition;
        public Quaternion CalibratedUnscaledHeadRotation;

        /// <summary>The solved inverse offset actually driving the bone — the calibration residual itself.</summary>
        public Vector3 InverseOffsetPosition;
        public Quaternion InverseOffsetRotation;

        public bool HasRoleAssigned => (Flags & FlagHasRoleAssigned) != 0;
        public bool IsCameraTracked => (Flags & FlagIsCameraTracked) != 0;
        public bool IsLinked => (Flags & FlagIsLinked) != 0;
        public bool HasCalibratedOffsetSnapshot => (Flags & FlagHasCalibratedOffsetSnapshot) != 0;
        public bool UseInverseOffset => (Flags & FlagUseInverseOffset) != 0;
    }

    /// <summary>One device's pose for one frame.</summary>
    public struct BasisDeviceStreamSample
    {
        /// <summary>
        /// Clear when the device was absent for this frame. A device that connects mid-session is present
        /// in the table from frame 0 with this clear until it appears, and a dropout clears it again —
        /// dropout is one of the three things mocap cannot supply, so it is modelled rather than smoothed.
        /// </summary>
        public const byte FlagConnected = 1 << 0;

        /// <summary>Role assignment is per-frame because calibration and reconnects reassign roles mid-session.</summary>
        public const byte FlagHasRoleAssigned = 1 << 1;

        public byte Flags;
        public byte Role;

        /// <summary>Pre-scale device pose. Secondary signal: what the constellation classifier and gizmos read.</summary>
        public Vector3 UnscaledPosition;
        public Quaternion UnscaledRotation;

        /// <summary>
        /// Post-scale, post-OffsetCoords device pose in player-root-local space. PRIMARY signal — this is
        /// what reaches the bone via SetIncoming, and what the replay reproduces exactly.
        /// </summary>
        public Vector3 ScaledPosition;
        public Quaternion ScaledRotation;

        public bool Connected => (Flags & FlagConnected) != 0;
        public bool HasRoleAssigned => (Flags & FlagHasRoleAssigned) != 0;
    }

    /// <summary>Per-frame timing. See <see cref="DeltaTime"/> for the framerate hazard this exists to pin.</summary>
    public struct BasisDeviceStreamFrame
    {
        /// <summary>Monotonic index from the start of the recording. Frame 0 is the first recorded frame.</summary>
        public int FrameIndex;

        /// <summary>Seconds since recording start. Double so a long session does not lose resolution.</summary>
        public double TimeSeconds;

        /// <summary>
        /// The timestep the pipeline actually ran this frame. RECORDED BECAUSE REPLAY RATE CHANGES THE
        /// ANSWER: this codebase has shipped real framerate-dependent blends — a saturate(dt*speed)
        /// smoother whose time constant tracked GPU speed, and a self-referential slerp that converged by
        /// frame count rather than elapsed time. Replaying at a rate other than the recorded one is
        /// therefore legitimately a different experiment, not a regression.
        /// </summary>
        public float DeltaTime;
    }

    /// <summary>
    /// A decoded device stream: header, mandatory calibration, the device table, and a rectangular
    /// frames x devices sample grid (row-major by frame, so one frame's samples are contiguous).
    /// </summary>
    public sealed class BasisDeviceStreamRecording
    {
        public string SessionLabel = string.Empty;

        /// <summary>Build/version string of whatever produced this. Free text; for triage only.</summary>
        public string ProducerVersion = string.Empty;

        public long CapturedUtcTicks;

        /// <summary>Nominal capture rate in Hz. Advisory — <see cref="BasisDeviceStreamFrame.DeltaTime"/> is the truth.</summary>
        public float NominalHz;

        /// <summary>
        /// Leading frames that exist only to converge filter state, and whose solved output must NOT be
        /// measured. This is the other half of the determinism story: the BONE POSE is never carried
        /// between frames, but FILTER STATE always is, so a replay that begins mid-stream — or that
        /// measures before the filters have caught up — produces a different, wrong answer. Model both or
        /// the temporal behaviour is fiction.
        /// </summary>
        public int WarmupFrames;

        public BasisDeviceStreamCalibration Calibration;
        public List<BasisDeviceStreamDevice> Devices = new List<BasisDeviceStreamDevice>();
        public List<BasisDeviceStreamFrame> Frames = new List<BasisDeviceStreamFrame>();

        /// <summary>Frames.Count * Devices.Count samples, row-major by frame.</summary>
        public BasisDeviceStreamSample[] Samples = Array.Empty<BasisDeviceStreamSample>();

        public int DeviceCount => Devices.Count;
        public int FrameCount => Frames.Count;

        /// <summary>Sample for one device on one frame.</summary>
        public BasisDeviceStreamSample SampleAt(int frameIndex, int deviceIndex)
        {
            return Samples[(frameIndex * Devices.Count) + deviceIndex];
        }

        /// <summary>
        /// Total elapsed seconds summed from the RECORDED timesteps rather than from the timestamps, so it
        /// reports the duration the pipeline believed it ran for.
        /// </summary>
        public double SummedDuration
        {
            get
            {
                double total = 0d;
                for (int Index = 0; Index < Frames.Count; Index++)
                {
                    total += Frames[Index].DeltaTime;
                }
                return total;
            }
        }

        /// <summary>
        /// Throws if the sample grid does not match the frame/device counts. Called before every write so
        /// a malformed recording is rejected at the point it was built, not at the point it is replayed
        /// weeks later.
        /// </summary>
        public void ValidateStructure()
        {
            if (Devices == null || Frames == null || Samples == null)
            {
                throw new BasisDeviceStreamFormatException("Device stream recording has a null device table, frame list or sample grid.");
            }
            int expected = Frames.Count * Devices.Count;
            if (Samples.Length != expected)
            {
                throw new BasisDeviceStreamFormatException(
                    $"Device stream recording holds {Samples.Length} samples but {Frames.Count} frames x {Devices.Count} devices needs {expected}.");
            }
        }
    }
}
