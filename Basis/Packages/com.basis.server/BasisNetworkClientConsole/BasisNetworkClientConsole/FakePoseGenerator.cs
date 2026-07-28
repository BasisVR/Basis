using System;
using Basis.Network.Core.Compression;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkClientConsole
{
    /// <summary>
    /// Generates realistic human-like avatar pose data for fake clients.
    /// Produces a natural standing pose (arms at sides, relaxed fingers, natural spine)
    /// with subtle idle animation (breathing, swaying, head micro-movements).
    ///
    /// Replaces the old zeroed-byte-array approach which produced T-pose avatars.
    /// Uses the same smallest-three quaternion compression as the real client.
    /// </summary>
    public static class FakePoseGenerator
    {
        private const float Deg2Rad = MathF.PI / 180f;
        private const float TwoPi = MathF.PI * 2f;
        private const float InvSqrt2 = 0.70710678118f;
        private const int BoneCount = BasisBoneRotationCompression.SyncBoneCount; // 51

        // Base natural standing pose: 51 quaternions stored as flat float array.
        // Layout: [slot * 4 + 0] = x, [slot * 4 + 1] = y, [slot * 4 + 2] = z, [slot * 4 + 3] = w
        // These are T-pose-relative delta quaternions — identity means T-pose, non-identity means deviation.
        private static readonly float[] BasePose;

        static FakePoseGenerator()
        {
            BasePose = new float[BoneCount * 4];
            BuildNaturalStandingPose();
        }

        // ────────────────────────────────────────────────────────────
        //  Natural standing pose definition
        //
        //  BONE_WRITE_ORDER slot assignments:
        //   0:Spine  1:Chest  2:UpperChest  3:Neck  4:Head
        //   5:LUpperArm  6:RUpperArm  7:LUpperLeg  8:RUpperLeg
        //   9:LLowerArm  10:RLowerArm  11:LLowerLeg  12:RLowerLeg
        //  13:LShoulder  14:RShoulder  15:LHand  16:RHand  17:LFoot  18:RFoot
        //  19:LToes  20:RToes
        //  21-30: Finger proximal (L-Thumb,L-Index,L-Mid,L-Ring,L-Little, R-same)
        //  31-40: Finger intermediate
        //  41-50: Finger distal
        // ────────────────────────────────────────────────────────────

        private static void BuildNaturalStandingPose()
        {
            // Initialize all 51 bones to identity (T-pose)
            for (int i = 0; i < BoneCount; i++)
                SetQuat(i, 0f, 0f, 0f, 1f);

            // ── Spine chain: natural S-curve ──
            SetAxisAngle(0, 1, 0, 0, 5f);     // Spine: slight forward lean
            SetAxisAngle(1, 1, 0, 0, -3f);    // Chest: slight extension (compensate)
            SetAxisAngle(2, 1, 0, 0, 2f);     // UpperChest: slight forward
            SetAxisAngle(3, 1, 0, 0, 8f);     // Neck: forward tilt
            SetAxisAngle(4, 1, 0, 0, -3f);    // Head: slight back (eyes level)

            // ── Upper arms: down from T-pose ──
            // In the bone's local T-pose frame, -Z rotation swings the arm downward.
            // Both arms use the same local delta since they mirror structurally.
            SetAxisAngle(5, 0, 0, 1, -72f);   // Left upper arm: down ~72 degrees
            SetAxisAngle(6, 0, 0, 1, -72f);   // Right upper arm: down ~72 degrees

            // ── Upper legs: standing straight with tiny forward tilt ──
            SetAxisAngle(7, 1, 0, 0, 2f);
            SetAxisAngle(8, 1, 0, 0, 2f);

            // ── Lower arms: slight elbow bend ──
            SetAxisAngle(9, 0, 1, 0, 20f);    // Left elbow
            SetAxisAngle(10, 0, 1, 0, -20f);  // Right elbow (mirrored)

            // ── Lower legs: very slight knee bend ──
            SetAxisAngle(11, 1, 0, 0, 5f);
            SetAxisAngle(12, 1, 0, 0, 5f);

            // ── Shoulders: slight depression ──
            SetAxisAngle(13, 0, 0, 1, -3f);
            SetAxisAngle(14, 0, 0, 1, 3f);

            // ── Hands: slight natural wrist angle ──
            SetAxisAngle(15, 0, 0, 1, 5f);
            SetAxisAngle(16, 0, 0, 1, -5f);

            // ── Feet: slight dorsiflexion for standing ──
            SetAxisAngle(17, 1, 0, 0, -8f);
            SetAxisAngle(18, 1, 0, 0, -8f);

            // ── Toes: flat on ground (identity) ──
            // Slots 19-20 already identity

            // ── Fingers: relaxed/slightly curled ──
            // Proximal bones: ~20 degree curl (includes thumb which has different axis)
            for (int i = 21; i <= 30; i++)
                SetAxisAngle(i, 1, 0, 0, 20f);

            // Intermediate bones: ~30 degree curl
            for (int i = 31; i <= 40; i++)
                SetAxisAngle(i, 1, 0, 0, 30f);

            // Distal bones: ~15 degree curl
            for (int i = 41; i <= 50; i++)
                SetAxisAngle(i, 1, 0, 0, 15f);
        }

        // ────────────────────────────────────────────────────────────
        //  Bone rotation encoding (writes into the packet byte buffer)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes all 51 bone rotations (base pose + idle animation) into the packet buffer
        /// using smallest-three compression. Clears the rotation region before writing.
        /// </summary>
        /// <param name="dst">Packet byte array.</param>
        /// <param name="byteOffset">Start of the bone rotation region (after position bytes).</param>
        /// <param name="quality">Compression quality level.</param>
        /// <param name="timeSec">Elapsed time in seconds (for animation).</param>
        /// <param name="phase">Per-player phase offset (prevents synchronized animation).</param>
        public static void WriteBoneRotations(byte[] dst, int byteOffset, BitQuality quality, double timeSec, float phase)
        {
            byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);
            float[] ranges = BasisBoneRotationCompression.MAX_COMPONENT;

            // Clear the rotation region (WriteBits ORs into bytes, so must start clean)
            int rotBytes = BasisBoneRotationCompression.RotationBytes(quality);
            Array.Clear(dst, byteOffset, rotBytes);

            int bitPos = byteOffset << 3;

            for (int slot = 0; slot < BoneCount; slot++)
            {
                int bitsPerComp = bpc[slot];
                int totalBits = 2 + 3 * bitsPerComp;

                // Every slot animates every frame — a load-test sender must produce fresh
                // rotation bits per send like a real tracked human, not a frozen statue.
                int idx = slot * 4;
                float bx = BasePose[idx], by = BasePose[idx + 1], bz = BasePose[idx + 2], bw = BasePose[idx + 3];

                GetIdleDelta(slot, timeSec, phase, out float dx, out float dy, out float dz, out float dw);

                QuatMul(bx, by, bz, bw, dx, dy, dz, dw, out float rx, out float ry, out float rz, out float rw);
                Normalize(ref rx, ref ry, ref rz, ref rw);

                ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(rx, ry, rz, rw, bitsPerComp, ranges[slot]);

                BasisBoneRotationCompression.WriteBits(dst, bitPos, packed, totalBits);
                bitPos += totalBits;
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
        /// Writes an animated hips rotation into the 7-byte tail of the packet.
        /// </summary>
        public static void WriteCompressedHipsRotation(byte[] dst, int offset, double timeSec, float phase)
        {
            // Subtle body yaw sway + slight lateral tilt
            float yaw = 3f * MathF.Sin((float)(timeSec * 0.06 * TwoPi + phase * 1.7));
            float tilt = 1f * MathF.Sin((float)(timeSec * 0.04 * TwoPi + phase * 2.3));

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
        //  Idle animation
        //
        //  Each animated bone gets a small time-varying delta quaternion
        //  layered on top of the base pose. Frequencies are sub-1 Hz
        //  to produce slow, natural-looking motion at the 11 Hz send rate.
        //
        //  Breathing: ~0.25 Hz (15 breaths/min) on spine/chest
        //  Head look: ~0.08-0.15 Hz slow gaze drift
        //  Arm sway:  ~0.1 Hz subtle pendulum, L/R out of phase
        //  Weight shift: ~0.05 Hz leg loading alternation
        //  Grip:      ~0.07 Hz subtle finger tightening/relaxing
        // ────────────────────────────────────────────────────────────

        private static void GetIdleDelta(int slot, double t, float phase, out float dx, out float dy, out float dz, out float dw)
        {
            // Default: identity (no animation for this bone)
            dx = 0f; dy = 0f; dz = 0f; dw = 1f;

            float p = phase;

            switch (slot)
            {
                case 0: // Spine — breathing
                    AxisAngleToQuat(1, 0, 0, 1.5f * MathF.Sin((float)(t * 0.25 * TwoPi + p)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 1: // Chest — breathing
                    AxisAngleToQuat(1, 0, 0, 1.0f * MathF.Sin((float)(t * 0.25 * TwoPi + p)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 3: // Neck — slow gaze drift (yaw + pitch combined)
                {
                    float yaw = 3f * MathF.Sin((float)(t * 0.08 * TwoPi + p * 1.3));
                    float pitch = 1.5f * MathF.Sin((float)(t * 0.12 * TwoPi + p * 0.7));
                    AxisAngleToQuat(0, 1, 0, yaw, out float yx, out float yy, out float yz, out float yw);
                    AxisAngleToQuat(1, 0, 0, pitch, out float px, out float py, out float pz, out float pw);
                    QuatMul(yx, yy, yz, yw, px, py, pz, pw, out dx, out dy, out dz, out dw);
                    break;
                }

                case 4: // Head — micro-nod
                    AxisAngleToQuat(1, 0, 0, 1f * MathF.Sin((float)(t * 0.15 * TwoPi + p * 2.1)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 5: // Left upper arm — sway
                    AxisAngleToQuat(1, 0, 0, 2f * MathF.Sin((float)(t * 0.1 * TwoPi + p)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 6: // Right upper arm — sway (out of phase with left)
                    AxisAngleToQuat(1, 0, 0, 2f * MathF.Sin((float)(t * 0.1 * TwoPi + p + MathF.PI)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 7: // Left upper leg — weight shift
                    AxisAngleToQuat(0, 0, 1, 1f * MathF.Sin((float)(t * 0.05 * TwoPi + p)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 8: // Right upper leg — weight shift (opposite)
                    AxisAngleToQuat(0, 0, 1, -1f * MathF.Sin((float)(t * 0.05 * TwoPi + p)),
                        out dx, out dy, out dz, out dw);
                    break;

                default:
                {
                    // Every remaining slot oscillates continuously, with amplitude × frequency
                    // sized so the per-send angular step crosses that bone group's quantization
                    // step at the ~11 Hz send rate (coarser BPC ⇒ bigger, faster motion):
                    //   12-BPC body/limb bones need only ~0.04°/frame; 5-6-BPC extremities need
                    //   degrees per frame before their bits change at all.
                    float amplitude;
                    float frequency;
                    if (slot >= 41)      { amplitude = 14f; frequency = 0.50f; } // finger distal (5 BPC)
                    else if (slot >= 31) { amplitude = 12f; frequency = 0.50f; } // finger intermediate (5-6 BPC)
                    else if (slot >= 21) { amplitude = 12f; frequency = 0.45f; } // finger proximal (5-6 BPC)
                    else if (slot >= 19) { amplitude = 16f; frequency = 0.50f; } // toes (5 BPC, tight range)
                    else                 { amplitude = 2f;  frequency = 0.30f + 0.05f * (slot % 5); } // 12-BPC body/limbs

                    // Slot-seeded frequency jitter + phase spread so bones (and players) desync.
                    frequency *= 1f + 0.07f * (slot % 3);
                    float angle = amplitude * MathF.Sin((float)(t * frequency * TwoPi + p * 1.1f + slot * 0.61f));

                    // Cycle the rotation axis per slot so motion isn't uniformly single-axis.
                    switch (slot % 3)
                    {
                        case 0: AxisAngleToQuat(1, 0, 0, angle, out dx, out dy, out dz, out dw); break;
                        case 1: AxisAngleToQuat(0, 1, 0, angle, out dx, out dy, out dz, out dw); break;
                        default: AxisAngleToQuat(0, 0, 1, angle, out dx, out dy, out dz, out dw); break;
                    }
                    break;
                }
            }
        }

        // ────────────────────────────────────────────────────────────
        //  Quaternion math helpers (pure float, no Unity dependencies)
        // ────────────────────────────────────────────────────────────

        private static void SetQuat(int slot, float x, float y, float z, float w)
        {
            int idx = slot * 4;
            BasePose[idx] = x;
            BasePose[idx + 1] = y;
            BasePose[idx + 2] = z;
            BasePose[idx + 3] = w;
        }

        private static void SetAxisAngle(int slot, float ax, float ay, float az, float degrees)
        {
            AxisAngleToQuat(ax, ay, az, degrees, out float qx, out float qy, out float qz, out float qw);
            SetQuat(slot, qx, qy, qz, qw);
        }

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
