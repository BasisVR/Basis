using System;
using Basis.Network.Core.Compression;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkClientConsole
{
    /// <summary>
    /// Generates human-like avatar pose data for fake clients: a relaxed standing pose that varies
    /// per client, with idle motion layered on top (breathing, weight shift, gaze drift, gesture).
    ///
    /// Everything on the wire is in the rig-neutral GENERIC space (see BasisGenericBoneRotation):
    /// a bone's rotation from its own rest pose, expressed in the avatar's anatomical frame —
    /// <b>X = character right, Y = up, Z = forward</b> — which is the same frame on every rig.
    /// Identity therefore means "this bone is exactly at T-pose", and a pose built entirely from
    /// small angles renders as a T-pose no matter how correctly it is encoded.
    ///
    /// Two consequences the pose table below is built around:
    ///  * The frame is shared by both sides, not mirrored per limb, so a left/right pair needs
    ///    OPPOSITE signs on its Y and Z components and the SAME sign on X. Only the left slot is
    ///    described here; <see cref="MirrorSign"/> derives the right one. Writing both by hand is
    ///    what previously left the crowd with one arm raised and one arm at its side.
    ///  * Angles have to be large enough to read as a pose. Every joint under about ten degrees is
    ///    a T-pose to anyone looking at it.
    /// </summary>
    public static class FakePoseGenerator
    {
        private const float Deg2Rad = MathF.PI / 180f;
        private const float TwoPi = MathF.PI * 2f;
        private const float InvSqrt2 = 0.70710678118f;

        /// <summary>
        /// Slots the wire still carries as explicit rotations. Since v47 the thirty finger joints
        /// are no longer rotations at all — they are ten curl/splay channels, written by
        /// <see cref="WriteFingerChannels"/>. Walking all 51 BPC entries as if they were bones (as
        /// this generator used to) emits the pre-v47 1302-bit stream into a 896-bit region and
        /// overruns the packet's own tail.
        /// </summary>
        private const int WireBoneCount = BasisBoneRotationCompression.WireBoneSlotCount; // 21
        private const int FingerCount = BasisBoneRotationCompression.FingerChannelCount;  // 10

        private const int AxisX = 0, AxisY = 1, AxisZ = 2;

        /// <summary>First slot of the mirrored left/right pairs; 0..4 are the central chain.</summary>
        private const int FirstPairedSlot = 5;

        // Pose table, flat per (slot, axis). Only the LEFT slot of a pair is populated; a right slot
        // reads its partner's entry through SourceSlot and flips sign through MirrorSign.
        //   Base   — the resting angle in degrees.
        //   Spread — half-width of the per-client offset around Base, so a crowd is a crowd.
        //   Anim   — idle amplitude in degrees, at AnimHz.
        // Sized so the per-send angular step clears the joint's quantization step at the ~11 Hz
        // send cadence: 12-BPC body/limb slots need ~0.04 deg/frame, the 7-bit toes need ~1 deg.
        private static readonly float[] Base = new float[WireBoneCount * 3];
        private static readonly float[] Spread = new float[WireBoneCount * 3];
        private static readonly float[] Anim = new float[WireBoneCount * 3];
        private static readonly float[] AnimHz = new float[WireBoneCount * 3];

        // Rotation-field bit offsets per quality, built once — a load-test sender runs this per
        // client per send, so it must not allocate.
        private static readonly int[][] FieldOffsetsByQuality = BuildFieldOffsets();

        static FakePoseGenerator()
        {
            BuildPoseTable();
        }

        private static int[][] BuildFieldOffsets()
        {
            var all = new int[4][];
            for (int q = 0; q < 4; q++)
            {
                all[q] = new int[BasisBoneRotationCompression.RotationFieldCount];
                BasisBoneRotationCompression.BuildRotationFieldOffsets((BitQuality)q, all[q]);
            }
            return all;
        }

        private static int[] FieldOffsets(BitQuality quality) => FieldOffsetsByQuality[(int)quality];

        // ────────────────────────────────────────────────────────────
        //  Standing pose definition
        //
        //  BONE_WRITE_ORDER slot assignments:
        //   0:Spine  1:Chest  2:UpperChest  3:Neck  4:Head
        //   5:LUpperArm  6:RUpperArm  7:LUpperLeg  8:RUpperLeg
        //   9:LLowerArm  10:RLowerArm  11:LLowerLeg  12:RLowerLeg
        //  13:LShoulder  14:RShoulder  15:LHand  16:RHand  17:LFoot  18:RFoot
        //  19:LToes  20:RToes
        //  Left slots are odd from 5 up, right slots even — so a right slot's source is slot - 1.
        //  Fingers are not slots here — ten curl/splay channels follow the bone block instead,
        //  ordered L thumb→little then R thumb→little.
        //
        //  Restricted slots (BONE_DOF < 3) only carry their anatomical axes, so their entries stay
        //  on BONE_AXIS_A / BONE_AXIS_B; content on a dropped axis would encode to silence.
        // ────────────────────────────────────────────────────────────

        private static void BuildPoseTable()
        {
            // Spine chain: an S-curve that leans, twists and sways rather than standing to attention.
            Set(0, AxisX, 5f, 7f, 2.0f, 0.25f);    // Spine: forward lean + breathing
            Set(0, AxisY, 0f, 9f, 2.0f, 0.13f);    // torso twist
            Set(0, AxisZ, 0f, 5f, 1.5f, 0.11f);    // side lean
            Set(1, AxisX, -4f, 5f, 1.5f, 0.25f);   // Chest: counter-extension + breathing
            Set(1, AxisY, 0f, 7f, 2.0f, 0.15f);
            Set(1, AxisZ, 0f, 4f, 1.5f, 0.12f);
            Set(2, AxisX, 3f, 4f, 1.5f, 0.24f);    // UpperChest
            Set(2, AxisY, 0f, 6f, 2.0f, 0.17f);
            Set(2, AxisZ, 0f, 3f, 1.5f, 0.14f);
            Set(3, AxisX, 7f, 9f, 2.5f, 0.14f);    // Neck
            Set(3, AxisY, 0f, 14f, 4.0f, 0.10f);
            Set(3, AxisZ, 0f, 6f, 2.0f, 0.12f);
            Set(4, AxisX, -4f, 11f, 3.0f, 0.16f);  // Head: nod
            Set(4, AxisY, 0f, 20f, 7.0f, 0.09f);   // looking around
            Set(4, AxisZ, 0f, 7f, 2.5f, 0.13f);    // head tilt

            // Left upper arm: down at the side. The left arm rests along -X, and +Z carries -X
            // toward -Y, so a POSITIVE angle is the one that lowers it; the right slot's -Z comes
            // from the mirror, which is the whole point of deriving it rather than writing it.
            Set(5, AxisZ, 68f, 14f, 5.0f, 0.10f);  // arm down, spread = how far out it hangs
            Set(5, AxisX, 0f, 14f, 4.0f, 0.09f);   // fore/aft swing
            Set(5, AxisY, 0f, 12f, 4.0f, 0.11f);   // humeral rotation

            // Left upper leg: standing, weight shifting between the two.
            Set(7, AxisX, 3f, 9f, 2.5f, 0.10f);
            Set(7, AxisY, 0f, 6f, 2.0f, 0.10f);
            Set(7, AxisZ, -2f, 5f, 2.0f, 0.10f);   // stance width

            // Left lower arm (2-DOF: Y elbow flexion, X forearm pronation).
            Set(9, AxisY, 28f, 22f, 9.0f, 0.15f);
            Set(9, AxisX, 0f, 26f, 11.0f, 0.12f);

            // Left lower leg (2-DOF: X knee flexion, Y tibial twist).
            Set(11, AxisX, 7f, 9f, 3.0f, 0.11f);
            Set(11, AxisY, 0f, 7f, 2.5f, 0.10f);

            // Left shoulder (2-DOF: Z clavicle up/down, Y front/back).
            Set(13, AxisZ, 4f, 6f, 2.5f, 0.12f);
            Set(13, AxisY, 0f, 6f, 2.5f, 0.11f);

            // Left hand (2-DOF: Z wrist flexion, Y radial/ulnar deviation).
            Set(15, AxisZ, 6f, 20f, 9.0f, 0.18f);
            Set(15, AxisY, 0f, 13f, 6.0f, 0.15f);

            // Left foot (2-DOF: X dorsi/plantar flexion, Y in/out).
            Set(17, AxisX, -7f, 9f, 3.0f, 0.12f);
            Set(17, AxisY, 0f, 7f, 2.5f, 0.11f);

            // Left toes (1-DOF, X curl). Seven bits over +/-60 degrees is a ~0.94 degree step, so
            // this one needs real amplitude and rate or it quantizes into a frozen slot.
            Set(19, AxisX, 0f, 12f, 9.0f, 0.50f);
        }

        private static void Set(int slot, int axis, float baseDeg, float spreadDeg, float animDeg, float animHz)
        {
            int i = slot * 3 + axis;
            Base[i] = baseDeg;
            Spread[i] = spreadDeg;
            Anim[i] = animDeg;
            AnimHz[i] = animHz;
        }

        /// <summary>Right slots read the left partner's entry; central slots read their own.</summary>
        private static int SourceSlot(int slot)
            => slot >= FirstPairedSlot && (slot & 1) == 0 ? slot - 1 : slot;

        /// <summary>
        /// Reflecting a rotation across the body's sagittal plane keeps its X component and negates
        /// Y and Z, so a right slot mirrors its partner on those two axes and copies it on X.
        /// </summary>
        private static float MirrorSign(int slot, int axis)
            => slot >= FirstPairedSlot && (slot & 1) == 0 && axis != AxisX ? -1f : 1f;

        // ────────────────────────────────────────────────────────────
        //  Bone rotation encoding (writes into the packet byte buffer)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes the whole rotation region — the explicit bone rotations (standing pose + per-client
        /// spread + idle animation) followed by the ten finger curl/splay channels. Clears the
        /// region before writing, since WriteBits ORs into bytes.
        /// </summary>
        /// <param name="dst">Packet byte array.</param>
        /// <param name="byteOffset">Start of the rotation region (after position bytes).</param>
        /// <param name="quality">Compression quality level.</param>
        /// <param name="timeSec">Elapsed time in seconds (for animation).</param>
        /// <param name="phase">Per-player phase offset (prevents synchronized animation).</param>
        /// <param name="poseSeed">Per-player pose seed (spreads the resting pose across the crowd).</param>
        public static void WriteBoneRotations(byte[] dst, int byteOffset, BitQuality quality, double timeSec, float phase, int poseSeed)
        {
            byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);
            float[] ranges = BasisBoneRotationCompression.MAX_COMPONENT;

            // Clear the rotation region (WriteBits ORs into bytes, so must start clean)
            int rotBytes = BasisBoneRotationCompression.RotationBytes(quality);
            Array.Clear(dst, byteOffset, rotBytes);

            // Field starts come from the codec rather than a running counter, so this generator
            // cannot drift out of step with the wire layout the way it did across v47.
            int[] offsets = FieldOffsets(quality);
            int baseBit = byteOffset << 3;

            for (int slot = 0; slot < WireBoneCount; slot++)
            {
                // Every slot animates every frame — a load-test sender must produce fresh
                // rotation bits per send like a real tracked human, not a frozen statue.
                int dof = BasisBoneRotationCompression.BONE_DOF[slot];
                int totalBits = BasisBoneRotationCompression.BoneFieldWidth(quality, slot);
                ulong packed;

                if (dof == 3)
                {
                    float ax = SlotAngle(slot, AxisX, timeSec, phase, poseSeed);
                    float ay = SlotAngle(slot, AxisY, timeSec, phase, poseSeed);
                    float az = SlotAngle(slot, AxisZ, timeSec, phase, poseSeed);

                    AxisAngleToQuat(1, 0, 0, ax, out float qx, out float qy, out float qz, out float qw);
                    AxisAngleToQuat(0, 1, 0, ay, out float rx, out float ry, out float rz, out float rw);
                    QuatMul(qx, qy, qz, qw, rx, ry, rz, rw, out qx, out qy, out qz, out qw);
                    AxisAngleToQuat(0, 0, 1, az, out rx, out ry, out rz, out rw);
                    QuatMul(qx, qy, qz, qw, rx, ry, rz, rw, out qx, out qy, out qz, out qw);
                    Normalize(ref qx, ref qy, ref qz, ref qw);

                    packed = BasisBoneRotationCompression.EncodeSmallestThree(qx, qy, qz, qw, bpc[slot], ranges[slot]);
                }
                else
                {
                    // Composed as R_axisA(a) * R_axisB(b) — exactly the form ExtractHingeTwist
                    // factorizes — so the angles survive the round trip instead of having their
                    // off-axis content projected away.
                    int axisA = BasisBoneRotationCompression.BONE_AXIS_A[slot];
                    float angleA = ClampRad(SlotAngle(slot, axisA, timeSec, phase, poseSeed) * Deg2Rad,
                        BasisBoneRotationCompression.BONE_RANGE_A[slot]);
                    float angleB = 0f;
                    if (dof == 2)
                    {
                        int axisB = BasisBoneRotationCompression.BONE_AXIS_B[slot];
                        angleB = ClampRad(SlotAngle(slot, axisB, timeSec, phase, poseSeed) * Deg2Rad,
                            BasisBoneRotationCompression.BONE_RANGE_B[slot]);
                    }

                    BasisBoneRotationCompression.ComposeHingeTwist(
                        axisA, angleA, BasisBoneRotationCompression.BONE_AXIS_B[slot], angleB,
                        out float qx, out float qy, out float qz, out float qw);

                    packed = BasisBoneRotationCompression.EncodeRestricted(qx, qy, qz, qw, slot, quality);
                }

                BasisBoneRotationCompression.WriteBits(dst, baseBit + offsets[slot], packed, totalBits);
            }

            WriteFingerChannels(dst, baseBit, offsets, quality, timeSec, phase, poseSeed);
        }

        /// <summary>
        /// One joint angle in degrees: the resting pose, this client's offset from it, and the idle
        /// term — all taken from the LEFT slot of the pair and sign-flipped onto the right one.
        /// The offset and the animation phase are drawn per side, so the two halves of a body are
        /// decorrelated without either leaving its anatomical range.
        /// </summary>
        private static float SlotAngle(int slot, int axis, double timeSec, float phase, int poseSeed)
        {
            int src = SourceSlot(slot) * 3 + axis;
            float hz = AnimHz[src];
            if (hz <= 0f && Base[src] == 0f && Spread[src] == 0f) return 0f;

            float offset = Spread[src] * Rand(slot, axis, poseSeed);
            float wobble = Anim[src] * MathF.Sin((float)(timeSec * hz * TwoPi) + phase + slot * 0.61f + axis * 2.09f);
            return MirrorSign(slot, axis) * (Base[src] + offset + wobble);
        }

        /// <summary>Deterministic per-client, per-joint value in [-1, 1]. No allocation, no RNG state.</summary>
        private static float Rand(int slot, int axis, int poseSeed)
        {
            uint h = (uint)(poseSeed * 374761393 + slot * 668265263 + axis * 2246822519);
            h ^= h >> 13;
            h *= 1274126177u;
            h ^= h >> 16;
            return h * (2f / uint.MaxValue) - 1f;
        }

        private static float ClampRad(float value, float halfRange)
            => value < -halfRange ? -halfRange : (value > halfRange ? halfRange : value);

        /// <summary>
        /// Writes the ten finger channels: one curl and one splay scalar per finger in [-1, 1],
        /// ordered L thumb→little then R thumb→little, packed [curl][splay] exactly as
        /// BasisBoneDeltaAndCompressJob does on the real client.
        ///
        /// Amplitudes and rates are sized so both scalars cross their quantization step every send
        /// at the ~11 Hz load-test cadence (High curl is 8 bits ⇒ 0.0078/step, splay 6 ⇒ 0.032),
        /// so a fake hand keeps producing fresh bits instead of deadbanding into silence.
        /// </summary>
        private static void WriteFingerChannels(byte[] dst, int baseBit, int[] offsets, BitQuality quality, double timeSec, float phase, int poseSeed)
        {
            int curlBits = BasisBoneRotationCompression.CurlBits(quality);
            int splayBits = BasisBoneRotationCompression.SplayBits(quality);

            for (int finger = 0; finger < FingerCount; finger++)
            {
                // Per-finger phase spread so a hand ripples rather than clenching as one block.
                float fp = phase * 1.1f + finger * 0.73f;

                // Resting grip varies per client; it then tightens and releases around that.
                float rest = 0.30f + 0.25f * Rand(WireBoneCount + finger, 0, poseSeed);
                float curl = rest + 0.35f * MathF.Sin((float)(timeSec * 0.50 * TwoPi + fp));
                float splay = 0.25f * MathF.Sin((float)(timeSec * 0.37 * TwoPi + fp * 1.4f))
                            + 0.20f * Rand(WireBoneCount + finger, 1, poseSeed);

                ulong qCurl = BasisBoneRotationCompression.EncodeSignedUnit(curl, curlBits);
                ulong qSplay = BasisBoneRotationCompression.EncodeSignedUnit(splay, splayBits);

                int field = WireBoneCount + finger;
                BasisBoneRotationCompression.WriteBits(dst, baseBit + offsets[field],
                    qCurl | (qSplay << curlBits), curlBits + splayBits);
            }
        }

        // ────────────────────────────────────────────────────────────
        //  Hips (body) rotation — 7-byte compressed quaternion tail
        //
        //  Format matches WriteCompressedQuaternionToBytes on the Unity side:
        //   [1 byte: largest component index]
        //   [2 bytes: ushort comp a]
        //   [2 bytes: ushort comp b]
        //   [2 bytes: ushort comp c]
        //  Each component quantized from [-InvSqrt2, +InvSqrt2] to [0, 65535].
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes an animated hips rotation into the 7-byte tail of the packet. Each client faces a
        /// different way — a crowd that all faces the same direction is not a crowd.
        /// </summary>
        public static void WriteCompressedHipsRotation(byte[] dst, int offset, double timeSec, float phase, int poseSeed)
        {
            float yaw = 180f * Rand(WireBoneCount + FingerCount, 0, poseSeed)
                      + 4f * MathF.Sin((float)(timeSec * 0.06 * TwoPi + phase * 1.7));
            float tilt = 1.5f * MathF.Sin((float)(timeSec * 0.04 * TwoPi + phase * 2.3));

            AxisAngleToQuat(0, 1, 0, yaw, out float yx, out float yy, out float yz, out float yw);
            AxisAngleToQuat(0, 0, 1, tilt, out float tx, out float ty, out float tz, out float tw);
            QuatMul(yx, yy, yz, yw, tx, ty, tz, tw, out float qx, out float qy, out float qz, out float qw);
            Normalize(ref qx, ref qy, ref qz, ref qw);

            WriteCompressedQuat(dst, offset, qx, qy, qz, qw);
        }

        private static void WriteCompressedQuat(byte[] dst, int offset, float qx, float qy, float qz, float qw)
        {
            // Find largest absolute component
            float ax = MathF.Abs(qx), ay = MathF.Abs(qy), az = MathF.Abs(qz), aw = MathF.Abs(qw);
            int largest = 0;
            float max = ax;
            if (ay > max) { largest = 1; max = ay; }
            if (az > max) { largest = 2; max = az; }
            if (aw > max) { largest = 3; }

            // Ensure largest component is positive (double-cover equivalence)
            float sign = largest switch { 0 => qx, 1 => qy, 2 => qz, _ => qw };
            if (sign < 0f) { qx = -qx; qy = -qy; qz = -qz; qw = -qw; }

            // Extract three smallest components
            float a, b, c;
            switch (largest)
            {
                case 0: a = qy; b = qz; c = qw; break;
                case 1: a = qx; b = qz; c = qw; break;
                case 2: a = qx; b = qy; c = qw; break;
                default: a = qx; b = qy; c = qz; break;
            }

            ushort qa = QuantizeSmall(a);
            ushort qb = QuantizeSmall(b);
            ushort qc = QuantizeSmall(c);

            dst[offset] = (byte)largest;
            dst[offset + 1] = (byte)qa;
            dst[offset + 2] = (byte)(qa >> 8);
            dst[offset + 3] = (byte)qb;
            dst[offset + 4] = (byte)(qb >> 8);
            dst[offset + 5] = (byte)qc;
            dst[offset + 6] = (byte)(qc >> 8);
        }

        // ────────────────────────────────────────────────────────────
        //  Quaternion math helpers (pure float, no Unity dependencies)
        // ────────────────────────────────────────────────────────────

        private static void AxisAngleToQuat(float ax, float ay, float az, float degrees, out float qx, out float qy, out float qz, out float qw)
        {
            float half = degrees * Deg2Rad * 0.5f;
            float s = MathF.Sin(half);
            float c = MathF.Cos(half);
            float len = MathF.Sqrt(ax * ax + ay * ay + az * az);
            if (len > 0.0001f)
            {
                float inv = 1f / len;
                ax *= inv; ay *= inv; az *= inv;
            }
            qx = ax * s;
            qy = ay * s;
            qz = az * s;
            qw = c;
        }

        /// <summary>Hamilton product: result = a * b</summary>
        private static void QuatMul(float ax, float ay, float az, float aw,
                                     float bx, float by, float bz, float bw,
                                     out float rx, out float ry, out float rz, out float rw)
        {
            rw = aw * bw - ax * bx - ay * by - az * bz;
            rx = aw * bx + ax * bw + ay * bz - az * by;
            ry = aw * by - ax * bz + ay * bw + az * bx;
            rz = aw * bz + ax * by - ay * bx + az * bw;
        }

        private static void Normalize(ref float x, ref float y, ref float z, ref float w)
        {
            float len = MathF.Sqrt(x * x + y * y + z * z + w * w);
            if (len > 1e-8f)
            {
                float inv = 1f / len;
                x *= inv; y *= inv; z *= inv; w *= inv;
            }
            else
            {
                x = 0f; y = 0f; z = 0f; w = 1f;
            }
        }

        private static ushort QuantizeSmall(float v)
        {
            if (v < -InvSqrt2) v = -InvSqrt2;
            if (v > InvSqrt2) v = InvSqrt2;
            float t = (v + InvSqrt2) / (2f * InvSqrt2);
            int qi = (int)MathF.Round(t * 65535f);
            if (qi < 0) qi = 0;
            if (qi > 65535) qi = 65535;
            return (ushort)qi;
        }
    }
}
