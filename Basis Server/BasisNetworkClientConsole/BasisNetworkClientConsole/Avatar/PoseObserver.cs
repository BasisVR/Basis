using System;
using System.Collections.Concurrent;
using System.Threading;
using Basis.Network.Core.Compression;
using static SerializableBasis;

namespace Basis.Network
{
    /// <summary>
    /// Decodes the rotation region of avatar frames this client RECEIVES and reports the pose a
    /// remote would actually draw. The load tester writes that region by hand (see
    /// <see cref="BasisNetworkClientConsole.FakePoseGenerator"/>), so a wire-format change does not
    /// break its build - it just starts emitting a pose nobody can use. Counting frames proves
    /// delivery; this proves the frames carry a pose.
    ///
    /// Both downlink paths are covered: full keyframes on the per-quality channels, and the deltas
    /// between them (rebuilt against the keyframe baseline the same way the real client does), so a
    /// tier whose keyframes are fine but whose deltas are not still shows up. Per sender it reports
    /// the largest bone angle in the frame - a T-posing remote reads ~0 on every slot - how many
    /// slots sit at their rest rotation, and whether the bits moved since the last frame.
    /// Enable with BASIS_POSE_OBSERVE=1.
    /// </summary>
    public static class PoseObserver
    {
        public static bool Enabled;

        private const int LeftUpperArmSlot = 5;
        private const int RightUpperArmSlot = 6;

        /// <summary>A slot under this angle is sitting at its rest rotation, i.e. T-posed.</summary>
        private const float IdentityAngleDeg = 0.5f;

        private sealed class SenderState
        {
            public byte[] LastRegion;
            public byte[] Baseline;          // last full frame, for rebuilding deltas
            public byte BaselineSeq;
            public byte BaselineQuality;
            public bool HasBaseline;
            public byte[] DeltaScratch;
            public long Keyframes;
            public long Deltas;
            public long DeltasUnbaselined;
            public long Changed;
            public float PeakAngleDeg;
            public float LastAngleDeg;
            public int RestSlots;
            public byte LastQuality;
            public float LeftArmY;
            public float RightArmY;
        }

        private static readonly ConcurrentDictionary<ushort, SenderState> States = new();
        private static long sFramesDecoded;

        /// <summary>A full keyframe: measure it, and keep it as the baseline its deltas rebuild from.</summary>
        public static void ObserveKeyframe(ushort fromPlayer, byte sequence, LocalAvatarSyncMessage message)
        {
            if (!Enabled || message.array == null) return;

            var q = (BasisAvatarBitPacking.BitQuality)message.DataQualityLevel;
            if (!BasisAvatarBitPacking.IsValidQuality(q)) return;

            SenderState st = States.GetOrAdd(fromPlayer, static _ => new SenderState());
            lock (st)
            {
                if (!Measure(st, message.array, q)) return;
                st.Keyframes++;

                int size = BasisAvatarDeltaCompression.PayloadSize(q);
                if (message.array.Length >= size)
                {
                    if (st.Baseline == null || st.Baseline.Length != size) st.Baseline = new byte[size];
                    Buffer.BlockCopy(message.array, 0, st.Baseline, 0, size);
                    st.BaselineSeq = sequence;
                    st.BaselineQuality = message.DataQualityLevel;
                    st.HasBaseline = true;
                }
            }
            Interlocked.Increment(ref sFramesDecoded);
        }

        /// <summary>
        /// A downlink delta. Rebuilt against this sender's last keyframe exactly as the real client
        /// does, so a delta path that drops or corrupts the rotation region is visible here rather
        /// than only in a headset.
        /// </summary>
        public static void ObserveDelta(ushort fromPlayer, byte quality, byte baseSeq, byte[] buffer, int start, int bodyLen)
        {
            if (!Enabled) return;

            var q = (BasisAvatarBitPacking.BitQuality)quality;
            if (!BasisAvatarBitPacking.IsValidQuality(q)) return;

            SenderState st = States.GetOrAdd(fromPlayer, static _ => new SenderState());
            lock (st)
            {
                if (!st.HasBaseline || st.BaselineQuality != quality || st.BaselineSeq != baseSeq)
                {
                    st.DeltasUnbaselined++;
                    return;
                }

                int size = BasisAvatarDeltaCompression.PayloadSize(q);
                if (st.DeltaScratch == null || st.DeltaScratch.Length != size) st.DeltaScratch = new byte[size];
                if (!BasisAvatarDeltaCompression.TryApplyDelta(st.Baseline, buffer, start, bodyLen, q, st.DeltaScratch)) return;

                if (!Measure(st, st.DeltaScratch, q)) return;
                st.Deltas++;
            }
            Interlocked.Increment(ref sFramesDecoded);
        }

        /// <summary>
        /// Y of the arm direction after the rotation, for a rest direction of (sign, 0, 0). The
        /// full rotate-a-vector reduces to this one term when the input is a unit X axis.
        /// </summary>
        private static float ArmY(float x, float y, float z, float w, float sign)
            => 2f * sign * (z * w + x * y);

        /// <summary>Decodes one payload's rotation region into this sender's running statistics.</summary>
        private static bool Measure(SenderState st, byte[] payload, BasisAvatarBitPacking.BitQuality q)
        {
            int rotBase = BasisAvatarBitPacking.WritePosition;
            int rotBytes = BasisBoneRotationCompression.RotationBytes(q);
            if (payload.Length < rotBase + rotBytes) return false;

            byte[] bpc = BasisBoneRotationCompression.GetBpcTable(q);
            float[] ranges = BasisBoneRotationCompression.MAX_COMPONENT;
            int bitPos = rotBase << 3;
            float maxAngle = 0f;
            int restSlots = 0;

            for (int slot = 0; slot < BasisBoneRotationCompression.WireBoneSlotCount; slot++)
            {
                int width = BasisBoneRotationCompression.BoneFieldWidth(q, slot);
                ulong packed = BasisBoneRotationCompression.ReadBits(payload, ref bitPos, width);
                float x, y, z, w;
                if (BasisBoneRotationCompression.BONE_DOF[slot] == 3)
                    BasisBoneRotationCompression.DecodeSmallestThree(packed, bpc[slot], out x, out y, out z, out w, ranges[slot]);
                else
                    BasisBoneRotationCompression.DecodeRestricted(packed, slot, q, out x, out y, out z, out w);

                float len = MathF.Sqrt(x * x + y * y + z * z + w * w);
                if (len > 1e-8f) { x /= len; y /= len; z /= len; w /= len; }
                float angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(w), 0f, 1f)) * (180f / MathF.PI);
                if (angle > maxAngle) maxAngle = angle;
                if (angle < IdentityAngleDeg) restSlots++;

                // Upper arms, reported as the symptom rather than as an angle. The wire value is
                // rotation-from-rest in the anatomical frame, so applying it to the arm's rest
                // direction there (-X for the left arm, +X for the right) gives where the arm
                // ends up: Y below zero is down at the side, above zero is raised. The two must
                // agree — one of each is the left/right mirroring being wrong.
                if (slot == LeftUpperArmSlot) st.LeftArmY = ArmY(x, y, z, w, -1f);
                else if (slot == RightUpperArmSlot) st.RightArmY = ArmY(x, y, z, w, 1f);
            }

            bool changed = true;
            if (st.LastRegion != null && st.LastRegion.Length == rotBytes)
            {
                changed = false;
                for (int i = 0; i < rotBytes; i++)
                {
                    if (st.LastRegion[i] != payload[rotBase + i]) { changed = true; break; }
                }
            }
            else
            {
                st.LastRegion = new byte[rotBytes];
            }
            Buffer.BlockCopy(payload, rotBase, st.LastRegion, 0, rotBytes);

            if (changed) st.Changed++;
            st.LastAngleDeg = maxAngle;
            if (maxAngle > st.PeakAngleDeg) st.PeakAngleDeg = maxAngle;
            st.RestSlots = restSlots;
            st.LastQuality = (byte)q;
            return true;
        }

        public static string Summary()
        {
            long frames = Interlocked.Read(ref sFramesDecoded);
            if (States.IsEmpty) return $"[PoseObserver] frames={frames} senders=0";

            int tposing = 0;
            int frozen = 0;
            float worst = float.MaxValue;
            float best = 0f;
            long unbaselined = 0;
            int armsMismatched = 0;
            int armsRaised = 0;
            foreach (var kv in States)
            {
                SenderState st = kv.Value;
                lock (st)
                {
                    long seen = st.Keyframes + st.Deltas;
                    if (st.PeakAngleDeg < IdentityAngleDeg) tposing++;
                    if (seen > 1 && st.Changed <= 1) frozen++;
                    if (st.PeakAngleDeg < worst) worst = st.PeakAngleDeg;
                    if (st.PeakAngleDeg > best) best = st.PeakAngleDeg;
                    unbaselined += st.DeltasUnbaselined;
                    if (st.LeftArmY * st.RightArmY < 0f) armsMismatched++;
                    else if (st.LeftArmY > 0f) armsRaised++;
                }
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"[PoseObserver] frames={frames} senders={States.Count} tposing={tposing} frozen={frozen} ")
              .Append($"unbaselinedDeltas={unbaselined} armsMismatched={armsMismatched} armsRaised={armsRaised} ")
              .Append($"peakBoneAngle: worstSender={worst:F2} bestSender={best:F2}");

            // The senders worth naming are the flattest ones - those are the avatars that would
            // read as T-posing - not whichever three the dictionary happens to hand back first.
            var flattest = new System.Collections.Generic.List<(ushort id, SenderState st, float peak)>(States.Count);
            foreach (var kv in States) flattest.Add((kv.Key, kv.Value, kv.Value.PeakAngleDeg));
            flattest.Sort(static (a, b) => a.peak.CompareTo(b.peak));

            int shown = System.Math.Min(3, flattest.Count);
            for (int i = 0; i < shown; i++)
            {
                SenderState st = flattest[i].st;
                lock (st)
                {
                    sb.Append($" | p{flattest[i].id}: q{st.LastQuality} kf={st.Keyframes} delta={st.Deltas} changed={st.Changed} ")
                      .Append($"last={st.LastAngleDeg:F2}deg peak={st.PeakAngleDeg:F2}deg atRest={st.RestSlots}/{BasisBoneRotationCompression.WireBoneSlotCount} ")
                      .Append($"armY L={st.LeftArmY:F2} R={st.RightArmY:F2}");
                }
            }
            return sb.ToString();
        }
    }
}
