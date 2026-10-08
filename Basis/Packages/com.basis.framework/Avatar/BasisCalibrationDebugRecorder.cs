using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Developer-only diagnostic recorder for the local avatar calibration pipeline.
    /// When the "Dump Calibration CSV" developer toggle is on, every stage of calibration
    /// (spawn, T-pose, offset capture, post-zero) pushes the scales / positions / rotation
    /// eulers (plus raw quaternions) of the avatar root and each tracked bone into a flat
    /// table, which is flushed to a CSV under persistentDataPath/CalibrationDebug at the end
    /// of calibration. The gate is read once at <see cref="Begin"/>; when off, every Record
    /// call is a single bool check so it is free to leave the instrumentation in place.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisCalibrationDebugRecorder
    {
        /// <summary>True only between a <see cref="Begin"/> with the toggle on and its <see cref="Flush"/>.</summary>
        public static bool Enabled { get; private set; }

        /// <summary>Folder the calibration CSVs are written to (and read back by the editor window).</summary>
        public static string OutputDirectory => Path.Combine(Application.persistentDataPath, "CalibrationDebug");

        private struct Row
        {
            public string Stage;
            public string Label;
            public string Space;
            public Vector3 Position;
            public Vector3 Euler;
            public Quaternion Quaternion;
            public Vector3 Scale;
            public string Note;
        }

        private static readonly List<Row> Rows = new List<Row>(256);
        private static string _sessionLabel = string.Empty;

        /// <summary>Number of post-calibration frames the runtime sampler records.</summary>
        public const int RuntimeSampleFrames = 30;

        /// <summary>True while the per-frame runtime sampler is collecting frames.</summary>
        public static bool RuntimeActive { get; private set; }

        private static int _runtimeRemaining;
        private static int _runtimeFrameIndex;

        /// <summary>
        /// Starts a recording session. Reads the developer toggle; if off, the session stays
        /// disabled and all subsequent Record calls are no-ops until the next Begin.
        /// </summary>
        /// <param name="sessionLabel">Identifier (e.g. avatar/display name) used in the file name.</param>
        public static void Begin(string sessionLabel)
        {
            Enabled = Basis.BasisUI.BasisSettingsDefaults.DumpCalibrationCsv.RawValue;
            RuntimeActive = false;
            if (!Enabled)
            {
                return;
            }
            Rows.Clear();
            _sessionLabel = string.IsNullOrEmpty(sessionLabel) ? "avatar" : sessionLabel;
            BasisDebug.Log($"[CalibrationDebug] Begin session '{_sessionLabel}'", BasisDebug.LogTag.Avatar);
        }

        /// <summary>
        /// Records both the world-space and local-space pose (position, rotation euler, quaternion,
        /// scale) of a transform. Emits two rows so the same bone can be compared across spaces.
        /// </summary>
        public static void Bone(string stage, string label, Transform t, string note = "")
        {
            if (!Enabled || t == null)
            {
                return;
            }
            t.GetPositionAndRotation(out Vector3 wp, out Quaternion wr);
            Add(stage, label, "world", wp, wr, t.lossyScale, note);

            t.GetLocalPositionAndRotation(out Vector3 lp, out Quaternion lr);
            Add(stage, label, "local", lp, lr, t.localScale, note);
        }

        /// <summary>Records an explicit pose (position, rotation, scale) in the given space.</summary>
        public static void Pose(string stage, string label, string space, Vector3 position, Quaternion rotation, Vector3 scale, string note = "")
        {
            if (!Enabled)
            {
                return;
            }
            Add(stage, label, space, position, rotation, scale, note);
        }

        /// <summary>
        /// Records a free-text metadata row (e.g. the avatar name). Null-safe: null label/value are
        /// stored as empty strings. No-op when the session is disabled.
        /// </summary>
        public static void Meta(string label, string value)
        {
            if (!Enabled)
            {
                return;
            }
            Add("Meta", label ?? string.Empty, "meta", Vector3.zero, Quaternion.identity, Vector3.one, value ?? string.Empty);
        }

        /// <summary>Records a bare rotation (e.g. a calibrated offset quaternion). Position is zero, scale one.</summary>
        public static void Rotation(string stage, string label, string space, Quaternion rotation, string note = "")
        {
            if (!Enabled)
            {
                return;
            }
            Add(stage, label, space, Vector3.zero, rotation, Vector3.one, note);
        }

        /// <summary>
        /// Records one FB tracker's offset capture (issue #531). Emits, under stage "OffsetCapture"
        /// and label=role, the virtualized device/bone-local poses <see cref="BasisInput.CalculateOffset"/>
        /// now uses (ScaledDeviceCoord, OutGoing local, OutgoingWorld, TposeLocalScaled), the live Unity
        /// transform world pose the old path read, the player root, and BOTH the legacy world-space
        /// inverse offset and the virtualized local-space offset so the two can be compared pass-to-pass
        /// after a tracker is moved and recalibrated.
        /// </summary>
        public static void OffsetCapture(BasisInput input, BasisLocalBoneControl bone)
        {
            if (!Enabled || input == null || bone == null)
            {
                return;
            }
            string role = input.TryGetRole(out var resolved) ? resolved.ToString() : "(none)";
            string note = input.UniqueDeviceIdentifier;

            input.transform.GetPositionAndRotation(out Vector3 trackerWorldPos, out Quaternion trackerWorldRot);
            Add("OffsetCapture", role, "trackerWorld", trackerWorldPos, trackerWorldRot, Vector3.one, note);

            var scaled = input.ScaledDeviceCoord;
            Add("OffsetCapture", role, "trackerScaledLocal", scaled.position, scaled.rotation, Vector3.one, note);

            var unscaled = input.UnscaledDeviceCoord;
            Add("OffsetCapture", role, "trackerUnscaled", unscaled.position, unscaled.rotation, Vector3.one, note);

            var outLocal = bone.OutGoingData;
            Add("OffsetCapture", role, "boneOutLocal", outLocal.position, outLocal.rotation, Vector3.one, note);

            var outWorld = bone.OutgoingWorldData;
            Add("OffsetCapture", role, "boneOutWorld", outWorld.position, outWorld.rotation, Vector3.one, note);

            var tpose = bone.TposeLocalScaled;
            Add("OffsetCapture", role, "boneTposeScaled", tpose.position, tpose.rotation, Vector3.one, note);

            Vector3 referenceLocal = outLocal.position;
            var avatarDriver = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.LocalAvatarDriver : null;
            if (avatarDriver != null && avatarDriver.StoredRolesTransforms != null
                && input.TryGetRole(out var roleEnum)
                && avatarDriver.StoredRolesTransforms.TryGetValue(roleEnum, out var avatarBone)
                && avatarBone != null)
            {
                Add("OffsetCapture", role, "avatarBoneWorld", avatarBone.position, avatarBone.rotation, Vector3.one, note);
                referenceLocal = BasisLocalPlayer.localToWorldMatrix.inverse.MultiplyPoint3x4(avatarBone.position);
                Add("OffsetCapture", role, "avatarBoneRefLocal", referenceLocal, Quaternion.identity, Vector3.one, note);
            }

            Quaternion inverseTrackerWorldRotation = Quaternion.Inverse(trackerWorldRot);
            Vector3 worldOffsetPosition = inverseTrackerWorldRotation * (outWorld.position - trackerWorldPos);
            Quaternion worldOffsetRotation = inverseTrackerWorldRotation * outWorld.rotation;
            Add("OffsetCapture", role, "offsetWorldBased", worldOffsetPosition, worldOffsetRotation, Vector3.one, note);

            Quaternion inverseScaledRotation = Quaternion.Inverse(scaled.rotation);
            Vector3 virtualOffsetPosition = inverseScaledRotation * (referenceLocal - scaled.position);
            Quaternion virtualOffsetRotation = inverseScaledRotation * outLocal.rotation;
            Add("OffsetCapture", role, "offsetVirtualLocal", virtualOffsetPosition, virtualOffsetRotation, Vector3.one, note);

            Matrix4x4 localToWorld = BasisLocalPlayer.localToWorldMatrix;
            Add("OffsetCapture", role, "playerRoot", localToWorld.GetColumn(3), localToWorld.rotation, Vector3.one, note);
        }

        /// <summary>
        /// Starts a fresh session that the per-frame sampler fills for <see cref="RuntimeSampleFrames"/>
        /// frames after calibration, then auto-flushes. Reads the toggle; no-op when off. Writes a
        /// separate "runtime_*" CSV so the live head solve can be compared against the calibration capture.
        /// </summary>
        public static void RuntimeBegin(string sessionLabel)
        {
            Enabled = Basis.BasisUI.BasisSettingsDefaults.DumpCalibrationCsv.RawValue;
            RuntimeActive = false;
            if (!Enabled)
            {
                return;
            }
            Rows.Clear();
            _sessionLabel = "runtime_" + (string.IsNullOrEmpty(sessionLabel) ? "avatar" : sessionLabel);
            _runtimeRemaining = RuntimeSampleFrames;
            _runtimeFrameIndex = 0;
            RuntimeActive = true;
            BasisDebug.Log($"[CalibrationDebug] Runtime sampling {_runtimeRemaining} frames for '{sessionLabel}'", BasisDebug.LogTag.Avatar);
        }

        /// <summary>
        /// Records one bone's live solve for the current runtime frame: the IK target rotation, the
        /// calibrated offset, the predicted product (target * offset) and the observed bone pose.
        /// </summary>
        public static void RuntimeBone(string label, Quaternion target, Quaternion offset, Transform observed)
        {
            if (!Enabled || !RuntimeActive)
            {
                return;
            }
            string note = "frame " + _runtimeFrameIndex.ToString(CultureInfo.InvariantCulture);
            Add("Runtime", label, "target", Vector3.zero, target, Vector3.one, note);
            Add("Runtime", label, "offset", Vector3.zero, offset, Vector3.one, note);
            Add("Runtime", label, "predicted", Vector3.zero, target * offset, Vector3.one, note);
            Bone("Runtime", label, observed, note);
        }

        /// <summary>
        /// Closes the current runtime frame: records the live avatar roots, advances the frame
        /// counter, and flushes once <see cref="RuntimeSampleFrames"/> frames have been captured.
        /// </summary>
        public static void RuntimeEndFrame(Transform playerRoot, Transform animatorRoot)
        {
            if (!Enabled || !RuntimeActive)
            {
                return;
            }
            string note = "frame " + _runtimeFrameIndex.ToString(CultureInfo.InvariantCulture);
            Bone("Runtime", "PlayerRoot", playerRoot, note);
            Bone("Runtime", "AnimatorRoot", animatorRoot, note);
            _runtimeFrameIndex++;
            _runtimeRemaining--;
            if (_runtimeRemaining <= 0)
            {
                RuntimeActive = false;
                Flush();
            }
        }

        private static void Add(string stage, string label, string space, Vector3 position, Quaternion rotation, Vector3 scale, string note)
        {
            Rows.Add(new Row
            {
                Stage = stage,
                Label = label,
                Space = space,
                Position = position,
                Euler = rotation.eulerAngles,
                Quaternion = rotation,
                Scale = scale,
                Note = note ?? string.Empty,
            });
        }

        /// <summary>
        /// Writes all recorded rows to a CSV and ends the session. No-op when the session was
        /// never enabled or nothing was recorded. Always clears state so the next Begin starts clean.
        /// </summary>
        public static void Flush()
        {
            if (!Enabled)
            {
                return;
            }
            try
            {
                if (Rows.Count > 0)
                {
                    string directory = OutputDirectory;
                    Directory.CreateDirectory(directory);

                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                    string fileName = $"calib_{Sanitize(_sessionLabel)}_{stamp}.csv";
                    string fullPath = Path.Combine(directory, fileName);

                    File.WriteAllText(fullPath, Build());
                    BasisDebug.Log($"[CalibrationDebug] Wrote {Rows.Count} rows to {fullPath}", BasisDebug.LogTag.Avatar);
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[CalibrationDebug] Failed to write CSV: {e}");
            }
            finally
            {
                Rows.Clear();
                Enabled = false;
            }
        }

        private static string Build()
        {
            StringBuilder sb = new StringBuilder(Rows.Count * 96 + 256);
            sb.Append("index,stage,label,space,posX,posY,posZ,eulerX,eulerY,eulerZ,quatX,quatY,quatZ,quatW,scaleX,scaleY,scaleZ,note\n");
            for (int i = 0; i < Rows.Count; i++)
            {
                Row r = Rows[i];
                sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(Escape(r.Stage)).Append(',');
                sb.Append(Escape(r.Label)).Append(',');
                sb.Append(Escape(r.Space)).Append(',');
                Append(sb, r.Position.x); Append(sb, r.Position.y); Append(sb, r.Position.z);
                Append(sb, r.Euler.x); Append(sb, r.Euler.y); Append(sb, r.Euler.z);
                Append(sb, r.Quaternion.x); Append(sb, r.Quaternion.y); Append(sb, r.Quaternion.z); Append(sb, r.Quaternion.w);
                Append(sb, r.Scale.x); Append(sb, r.Scale.y); Append(sb, r.Scale.z);
                sb.Append(Escape(r.Note)).Append('\n');
            }
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, float value)
        {
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            if (value.IndexOfAny(new[] { ',', '"', '\n' }) < 0)
            {
                return value;
            }
            return "\"" + value.Replace("\"", "\"\"") + "\"";
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
