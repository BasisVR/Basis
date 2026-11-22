using Basis.Network.Core.Compression;
using Basis.Scripts.Common;
using Basis.Scripts.Networking.NetworkedAvatar;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Q = Basis.Network.Core.Compression.BasisGenericBoneRotation.Quat;
using G = Basis.Network.Core.Compression.BasisGenericBoneRotation;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Locks the RIG-NEUTRAL bone rotation space the avatar stream carries
    /// (<see cref="BasisGenericBoneRotation"/>).
    ///
    /// The stream used to carry each bone's T-pose-relative LOCAL delta, which is expressed in
    /// whatever local axes the modeller authored that bone with. One rig runs +X down the forearm,
    /// the next runs +Y down it: the same visible elbow bend is a different number on each, so the
    /// pose could only ever be replayed on the exact avatar that produced it. What goes on the
    /// wire now is the joint's rotation away from its own rest pose expressed in the CHARACTER's
    /// axes, which any rig can read using only its own rest data.
    ///
    /// What these guard, in order of how load-bearing they are:
    ///   - the wire value is literally the character-space rotation (the definition holds),
    ///   - a pose encoded on one rig replays correctly on a completely different rig,
    ///   - the OLD scheme fails that same test, so the change is doing real work,
    ///   - the remap is LOSSLESS: cross-rig error through the real codec equals the same-rig
    ///     quantiser error that was already there, which is what lets the bit budget, the
    ///     MAX_COMPONENT ranges and the packet size all stay exactly as they were,
    ///   - an identity rest frame reproduces the legacy scheme bit for bit, which is the
    ///     degradation path for a bone a rig does not have,
    ///   - the shared pure-C# math agrees with Unity.Mathematics, since the Burst jobs on both
    ///     ends use math.mul while the server and these tests use the portable implementation.
    /// </summary>
    public class BasisGenericBoneRotationTests
    {
        // ────────────────────────────────────────────────────────────
        //  Helpers
        // ────────────────────────────────────────────────────────────

        static Q ToQ(quaternion q) => new Q(q.value.x, q.value.y, q.value.z, q.value.w);
        static quaternion ToU(in Q q) => new quaternion(q.x, q.y, q.z, q.w);

        /// <summary>
        /// Geodesic angle via atan2 on the vector/scalar parts. The obvious 2*acos(|dot|) loses
        /// half its significant digits exactly where every assertion here lives (dot ≈ 1) and
        /// reports ~0.05° of pure conditioning noise as though it were error — which would either
        /// force uselessly loose tolerances or fail correct code.
        /// </summary>
        static float AngleDeg(quaternion q)
        {
            float4 v = math.normalize(q).value;
            return math.degrees(2f * math.atan2(math.length(v.xyz), math.abs(v.w)));
        }

        static float AngleBetween(quaternion a, quaternion b)
            => AngleDeg(math.mul(math.normalize(a), math.conjugate(math.normalize(b))));

        static float AngleBetween(in Q a, in Q b) => AngleBetween(ToU(a), ToU(b));

        static quaternion AxisAngle(float3 axis, float deg)
            => quaternion.AxisAngle(math.normalize(axis), math.radians(deg));

        uint _rng;
        void Seed(uint s) => _rng = s;
        float Next() { _rng = _rng * 1664525u + 1013904223u; return (_rng >> 8) / (float)(1 << 24) * 2f - 1f; }
        quaternion RndQ() => math.normalize(new quaternion(Next(), Next(), Next(), Next()));

        /// <summary>
        /// A synthetic humanoid rig: the two rest quantities calibration captures, per bone.
        /// F is the bone's rest rotation relative to the avatar ROOT (TposeFromRoot) and T is its
        /// rest rotation relative to its PARENT (TposeLocal). Two rigs differ in both, which is
        /// precisely the situation the old wire format could not survive.
        /// </summary>
        sealed class SyntheticRig
        {
            public readonly quaternion[] RestFrame = new quaternion[55];
            public readonly quaternion[] RestLocal = new quaternion[55];
        }

        SyntheticRig MakeRig(uint seed)
        {
            Seed(seed);
            var rig = new SyntheticRig();
            for (int i = 0; i < 55; i++)
            {
                rig.RestFrame[i] = RndQ();
                rig.RestLocal[i] = RndQ();
            }
            return rig;
        }

        /// <summary>Sender side, exactly as BasisNetworkAvatarCompressor does it.</summary>
        static quaternion Encode(SyntheticRig rig, int bone, quaternion currentLocal)
        {
            BasisGenericBoneRotationUtils.BuildEncodeOperators(rig.RestFrame[bone], rig.RestLocal[bone],
                out quaternion pre, out quaternion post);
            return math.mul(math.mul(pre, currentLocal), post);
        }

        /// <summary>Receiver side, exactly as ComputeSkeletonRotationsFromNetworkJob does it.</summary>
        static quaternion Decode(SyntheticRig rig, int bone, quaternion generic)
        {
            BasisGenericBoneRotationUtils.BuildDecodeOperators(rig.RestFrame[bone], rig.RestLocal[bone],
                out quaternion pre, out quaternion post);
            return math.mul(math.mul(pre, generic), post);
        }

        // ────────────────────────────────────────────────────────────
        //  1. The definition: the wire value IS the character-space rotation
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// A three-bone chain (root → parent → bone) built so the joint's physical rotation is
        /// known by construction. With the parent at rest, the value that goes on the wire must
        /// come out equal to that rotation — not similar to it, equal — because "rotation from
        /// rest, in character axes" is what the representation is DEFINED to be. Everything else
        /// in this file follows from this holding.
        /// </summary>
        [Test]
        public void GenericValue_IsTheCharacterSpaceRotation()
        {
            quaternion parentFrame = AxisAngle(new float3(0, 1, 0), 25f);
            quaternion boneFrame = math.mul(parentFrame, AxisAngle(new float3(1, 0, 0), 90f)); // +X down the bone
            quaternion restLocal = math.mul(math.conjugate(parentFrame), boneFrame);

            Seed(4242u);
            for (int i = 0; i < 500; i++)
            {
                quaternion characterRot = AxisAngle(new float3(Next(), Next(), Next()), Next() * 170f);

                // Turn the joint by characterRot about the ROOT's axes, parent left at rest.
                quaternion boneWorld = math.mul(characterRot, boneFrame);
                quaternion currentLocal = math.mul(math.conjugate(parentFrame), boneWorld);

                var g = G.ToGeneric(ToQ(boneFrame), ToQ(restLocal), ToQ(currentLocal));

                Assert.That(AngleBetween(ToU(g), characterRot), Is.LessThan(0.01f),
                    "the encoded value must be the joint's character-space rotation itself");
            }
        }

        // ────────────────────────────────────────────────────────────
        //  2. The point of the exercise: cross-rig replay
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Encode a pose on rig A, decode it on rig B, and read the character-space rotation back
        /// off B. B's joint must have turned by the same physical rotation A's did, even though
        /// the two rigs share no bone axes and no rest pose.
        /// </summary>
        [Test]
        public void Pose_TransfersBetweenRigsWithDifferentBoneAxes()
        {
            // Rig A: +X down the bone, parent yawed.
            quaternion parentA = AxisAngle(new float3(0, 1, 0), 25f);
            quaternion frameA = math.mul(parentA, AxisAngle(new float3(1, 0, 0), 90f));
            quaternion localA = math.mul(math.conjugate(parentA), frameA);

            // Rig B: a different axis entirely, rolled, and a parent frame with nothing in common.
            quaternion parentB = AxisAngle(new float3(0, 0, 1), -70f);
            quaternion frameB = math.mul(parentB, math.mul(AxisAngle(new float3(0, 1, 0), 130f), AxisAngle(new float3(1, 0, 0), 40f)));
            quaternion localB = math.mul(math.conjugate(parentB), frameB);

            Seed(99u);
            float worst = 0f;
            for (int i = 0; i < 1000; i++)
            {
                quaternion characterRot = RndQ();

                quaternion currentA = math.mul(math.conjugate(parentA), math.mul(characterRot, frameA));
                var g = G.ToGeneric(ToQ(frameA), ToQ(localA), ToQ(currentA));

                quaternion currentB = ToU(G.FromGeneric(ToQ(frameB), ToQ(localB), g));
                // Read B's joint rotation back out in character axes.
                quaternion recovered = math.mul(math.mul(parentB, currentB), math.conjugate(frameB));

                worst = math.max(worst, AngleBetween(recovered, characterRot));
            }

            Assert.That(worst, Is.LessThan(0.01f), $"cross-rig transfer drifted by {worst}°");
        }

        /// <summary>
        /// The same scenario through the OLD wire format. If this ever starts passing, either the
        /// two rigs stopped differing or the legacy scheme was secretly fine — both would mean the
        /// test above is proving nothing, so this is the guard on the guard.
        /// </summary>
        [Test]
        public void LegacyLocalDelta_DoesNotTransferBetweenRigs()
        {
            quaternion parentA = AxisAngle(new float3(0, 1, 0), 25f);
            quaternion frameA = math.mul(parentA, AxisAngle(new float3(1, 0, 0), 90f));
            quaternion restLocalA = math.mul(math.conjugate(parentA), frameA);

            quaternion parentB = AxisAngle(new float3(0, 0, 1), -70f);
            quaternion frameB = math.mul(parentB, math.mul(AxisAngle(new float3(0, 1, 0), 130f), AxisAngle(new float3(1, 0, 0), 40f)));
            quaternion restLocalB = math.mul(math.conjugate(parentB), frameB);

            Seed(99u);
            float worst = 0f;
            for (int i = 0; i < 1000; i++)
            {
                quaternion characterRot = RndQ();
                quaternion currentA = math.mul(math.conjugate(parentA), math.mul(characterRot, frameA));

                // Legacy: delta = conj(T_A) * C_A on the wire, C_B = T_B * delta on the far end.
                quaternion legacyDelta = math.mul(math.conjugate(restLocalA), currentA);
                quaternion currentB = math.mul(restLocalB, legacyDelta);
                quaternion recovered = math.mul(math.mul(parentB, currentB), math.conjugate(frameB));

                worst = math.max(worst, AngleBetween(recovered, characterRot));
            }

            Assert.That(worst, Is.GreaterThan(30f),
                "the legacy local-frame delta is expected to land the far rig's joint somewhere else entirely");
        }

        // ────────────────────────────────────────────────────────────
        //  3. Why the bit budget did not have to change
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Conjugation is an isometry of SO(3), so the generic value has exactly the rotation
        /// ANGLE the local delta had. That is the whole reason BPC_HIGH, MAX_COMPONENT, the
        /// deadband thresholds and the packet size all carried over untouched: only the axis
        /// changed frame, never the magnitude. If this breaks, every per-joint range in
        /// BasisBoneRotationCompression is silently mis-sized.
        /// </summary>
        [Test]
        public void GenericSpace_PreservesRotationAngle()
        {
            Seed(31337u);
            float worst = 0f;
            for (int i = 0; i < 5000; i++)
            {
                quaternion restFrame = RndQ();
                quaternion localDelta = RndQ();
                var g = G.LocalDeltaToGeneric(ToQ(restFrame), ToQ(localDelta));
                worst = math.max(worst, math.abs(AngleDeg(ToU(g)) - AngleDeg(localDelta)));
            }
            Assert.That(worst, Is.LessThan(0.01f), $"angle drifted by {worst}° under the remap");
        }

        /// <summary>
        /// End to end through the REAL smallest-three codec at High quality, per bone slot, with
        /// its real per-slot bit count and component range.
        ///
        /// The assertion is deliberately not "cross-rig error is small" — it is that the cross-rig
        /// error EQUALS the error a same-rig stream already suffers from quantisation alone. That
        /// is the precise claim: the remap contributes nothing, so the coarsest slots are no worse
        /// off than they were before the change.
        /// </summary>
        [Test]
        public void ThroughRealCodec_CrossRigErrorIsExactlyTheQuantiserError()
        {
            quaternion parentA = AxisAngle(new float3(0, 1, 0), 25f);
            quaternion frameA = math.mul(parentA, AxisAngle(new float3(1, 0, 0), 90f));
            quaternion restLocalA = math.mul(math.conjugate(parentA), frameA);

            quaternion parentB = AxisAngle(new float3(0, 0, 1), -70f);
            quaternion frameB = math.mul(parentB, math.mul(AxisAngle(new float3(0, 1, 0), 130f), AxisAngle(new float3(1, 0, 0), 40f)));
            quaternion restLocalB = math.mul(math.conjugate(parentB), frameB);

            byte[] bpc = BasisBoneRotationCompression.BPC_HIGH;
            float[] ranges = BasisBoneRotationCompression.MAX_COMPONENT;

            Seed(7u);
            float worstExcess = 0f;
            for (int slot = 0; slot < BasisBoneRotationCompression.SyncBoneCount; slot++)
            {
                // Stay inside the rotation range this slot's component budget is actually sized
                // for, so a finger distal is not asked to carry a 180° swing it was never given
                // the bits for.
                float maxHalf = math.degrees(math.asin(math.min(1f, ranges[slot])));
                for (int i = 0; i < 120; i++)
                {
                    float deg = (i / 119f) * (1.8f * maxHalf) - 0.9f * maxHalf;
                    quaternion characterRot = AxisAngle(new float3(Next(), Next(), Next()), deg);

                    quaternion currentA = math.mul(math.conjugate(parentA), math.mul(characterRot, frameA));
                    var g = G.ToGeneric(ToQ(frameA), ToQ(restLocalA), ToQ(currentA));

                    ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(
                        g.x, g.y, g.z, g.w, bpc[slot], ranges[slot]);
                    BasisBoneRotationCompression.DecodeSmallestThree(packed, bpc[slot],
                        out float qx, out float qy, out float qz, out float qw, ranges[slot]);
                    var quantised = new Q(qx, qy, qz, qw);

                    quaternion backOnA = ToU(G.FromGeneric(ToQ(frameA), ToQ(restLocalA), quantised));
                    quaternion backOnB = ToU(G.FromGeneric(ToQ(frameB), ToQ(restLocalB), quantised));

                    float errSame = AngleBetween(math.mul(math.mul(parentA, backOnA), math.conjugate(frameA)), characterRot);
                    float errCross = AngleBetween(math.mul(math.mul(parentB, backOnB), math.conjugate(frameB)), characterRot);

                    worstExcess = math.max(worstExcess, math.abs(errCross - errSame));
                }
            }

            Assert.That(worstExcess, Is.LessThan(0.005f),
                $"the remap added {worstExcess}° on top of the quantiser; it must add nothing");
        }

        // ────────────────────────────────────────────────────────────
        //  4. Production encode path, not a copy of it
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Drives the real Burst encode job over a full 51-bone pose on rig A, reads the packet
        /// back with the real bitstream decoder, and reconstructs the pose on rig B. This is the
        /// actual shipping sender — BasisBoneDeltaAndCompressJob — so the wire layout, the bit
        /// packing and the operator application are all exercised as they run in production.
        ///
        /// The invariant checked is that B's reconstructed local rotation carries the same generic
        /// value A sent: that IS "the same pose", stated in the only frame both rigs share.
        /// </summary>
        [Test]
        public void RealEncodeJob_RoundTripsAcrossRigs()
        {
            var rigA = MakeRig(1001u);
            var rigB = MakeRig(2002u);

            // Only the slots the wire still carries as explicit rotations. Slots 21..50 are the
            // finger joints, which v47 replaced with curl/splay channels — they are not encoded
            // through this path at all, so including them would assert a remap nobody performs.
            int boneCount = BasisBoneRotationCompression.WireBoneSlotCount;
            var quality = BasisAvatarBitPacking.BitQuality.High;
            int[] order = BasisBoneRotationCompression.BONE_WRITE_ORDER;
            float[] ranges = BasisBoneRotationCompression.MAX_COMPONENT;
            byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);

            // TempJob, not Temp: BasisBoneDeltaAndCompressJob is a real IJob and Run() still goes
            // through the job safety system, which rejects Temp-allocated containers.
            var current = new NativeArray<quaternion>(boneCount, Allocator.TempJob);
            var encodePre = new NativeArray<quaternion>(boneCount, Allocator.TempJob);
            var encodePost = new NativeArray<quaternion>(boneCount, Allocator.TempJob);
            var outDeltas = new NativeArray<quaternion>(boneCount, Allocator.TempJob);
            var bpcNative = new NativeArray<byte>(boneCount, Allocator.TempJob);
            var maxComp = new NativeArray<float>(boneCount, Allocator.TempJob);
            var packet = new NativeArray<byte>(BasisBoneRotationCompression.RotationBytes(quality), Allocator.TempJob);
            var decoded = new NativeArray<quaternion>(BasisBoneRotationCompression.SyncBoneCount, Allocator.TempJob);
            var fingersIn = new NativeArray<float2>(BasisBoneRotationCompression.FingerChannelCount, Allocator.TempJob);
            var fingersOut = new NativeArray<float2>(BasisBoneRotationCompression.FingerChannelCount, Allocator.TempJob);
            var dofNative = new NativeArray<byte>(boneCount, Allocator.TempJob);
            var axisANative = new NativeArray<byte>(boneCount, Allocator.TempJob);
            var axisBNative = new NativeArray<byte>(boneCount, Allocator.TempJob);
            var rangeANative = new NativeArray<float>(boneCount, Allocator.TempJob);
            var rangeBNative = new NativeArray<float>(boneCount, Allocator.TempJob);
            NativeArray<byte>.Copy(BasisBoneRotationCompression.BONE_DOF, dofNative, boneCount);
            NativeArray<byte>.Copy(BasisBoneRotationCompression.BONE_AXIS_A, axisANative, boneCount);
            NativeArray<byte>.Copy(BasisBoneRotationCompression.BONE_AXIS_B, axisBNative, boneCount);
            NativeArray<float>.Copy(BasisBoneRotationCompression.BONE_RANGE_A, rangeANative, boneCount);
            NativeArray<float>.Copy(BasisBoneRotationCompression.BONE_RANGE_B, rangeBNative, boneCount);

            try
            {
                Seed(555u);
                for (int slot = 0; slot < boneCount; slot++)
                {
                    int bone = order[slot];

                    // A pose this slot's budget can represent: rotate the rest local by an amount
                    // inside the slot's range, so the check isolates the remap from clipping.
                    float maxHalf = math.degrees(math.asin(math.min(1f, ranges[slot])));
                    quaternion joint = AxisAngle(new float3(Next(), Next(), Next()), Next() * 0.85f * maxHalf);
                    // Applied in rig A's own bone frame, i.e. an ordinary local-space pose.
                    current[slot] = math.mul(rigA.RestLocal[bone], joint);

                    BasisGenericBoneRotationUtils.BuildEncodeOperators(
                        rigA.RestFrame[bone], rigA.RestLocal[bone],
                        out quaternion pre, out quaternion post);
                    encodePre[slot] = pre;
                    encodePost[slot] = post;

                    bpcNative[slot] = bpc[slot];
                    maxComp[slot] = ranges[slot];
                }

                new BasisBoneDeltaAndCompressJob
                {
                    CurrentLocalRotations = current,
                    EncodePre = encodePre,
                    EncodePost = encodePost,
                    BitsPerComponent = bpcNative,
                    MaxComponent = maxComp,
                    OutputBuffer = packet,
                    RotationByteOffset = 0,
                    BoneCount = boneCount,
                    BoneDeltas = outDeltas,
                    FingerPercentages = fingersIn,
                    CurlBits = BasisBoneRotationCompression.CurlBits(quality),
                    SplayBits = BasisBoneRotationCompression.SplayBits(quality),
                    BoneDof = dofNative,
                    BoneAxisA = axisANative,
                    BoneAxisB = axisBNative,
                    BoneRangeA = rangeANative,
                    BoneRangeB = rangeBNative,
                    HingeBitCount = BasisBoneRotationCompression.HingeBits(quality),
                    TwistBitCount = BasisBoneRotationCompression.TwistBits(quality),
                    SingleAxisBitCount = BasisBoneRotationCompression.SingleAxisBits(quality),
                }.Run();

                byte[] wire = packet.ToArray();
                int offset = 0;
                BasisBoneRotationUtils.DecompressBoneRotations(wire, quality, ref decoded, ref fingersOut, ref offset);
                Assert.That(offset, Is.EqualTo(wire.Length), "decoder must consume exactly the rotation block");

                float worst = 0f;
                for (int slot = 0; slot < boneCount; slot++)
                {
                    int bone = order[slot];

                    // Rebuild on rig B, then re-encode with B's operators. Same generic value out
                    // means the pose survived the trip onto a foreign rig.
                    quaternion onB = Decode(rigB, bone, decoded[slot]);
                    quaternion reEncoded = Encode(rigB, bone, onB);

                    worst = math.max(worst, AngleBetween(reEncoded, decoded[slot]));
                }

                Assert.That(worst, Is.LessThan(0.01f), $"pose drifted {worst}° being replayed on another rig");
            }
            finally
            {
                current.Dispose(); encodePre.Dispose(); encodePost.Dispose(); outDeltas.Dispose();
                bpcNative.Dispose(); maxComp.Dispose(); packet.Dispose(); decoded.Dispose();
                fingersIn.Dispose(); fingersOut.Dispose();
                dofNative.Dispose(); axisANative.Dispose(); axisBNative.Dispose();
                rangeANative.Dispose(); rangeBNative.Dispose();
            }
        }

        /// <summary>
        /// The Burst encode job and the managed CompressBoneRotations must emit bit-identical
        /// packets — the round-trip test above cannot catch a divergence because it re-encodes
        /// with the same implementation it decoded from. Also asserts the restricted-DOF slots
        /// (v52: elbows/knees/shoulders/wrists/ankles as hinge+twist angles, toes as one angle)
        /// reconstruct anatomically-plausible poses within their quantization step.
        /// </summary>
        [Test]
        public void BurstEncode_BitMatchesManagedEncode_AndRestrictedJointsRoundTrip()
        {
            int boneCount = BasisBoneRotationCompression.WireBoneSlotCount;
            var quality = BasisAvatarBitPacking.BitQuality.High;

            var current = new NativeArray<quaternion>(boneCount, Allocator.TempJob);
            var identityOps = new NativeArray<quaternion>(boneCount, Allocator.TempJob);
            var outDeltas = new NativeArray<quaternion>(boneCount, Allocator.TempJob);
            var bpcNative = new NativeArray<byte>(boneCount, Allocator.TempJob);
            var maxComp = new NativeArray<float>(boneCount, Allocator.TempJob);
            var packet = new NativeArray<byte>(BasisBoneRotationCompression.RotationBytes(quality), Allocator.TempJob);
            var fingersIn = new NativeArray<float2>(BasisBoneRotationCompression.FingerChannelCount, Allocator.TempJob);
            var fingersOut = new NativeArray<float2>(BasisBoneRotationCompression.FingerChannelCount, Allocator.TempJob);
            var decoded = new NativeArray<quaternion>(BasisBoneRotationCompression.SyncBoneCount, Allocator.TempJob);
            var dofNative = new NativeArray<byte>(boneCount, Allocator.TempJob);
            var axisANative = new NativeArray<byte>(boneCount, Allocator.TempJob);
            var axisBNative = new NativeArray<byte>(boneCount, Allocator.TempJob);
            var rangeANative = new NativeArray<float>(boneCount, Allocator.TempJob);
            var rangeBNative = new NativeArray<float>(boneCount, Allocator.TempJob);
            NativeArray<byte>.Copy(BasisBoneRotationCompression.BONE_DOF, dofNative, boneCount);
            NativeArray<byte>.Copy(BasisBoneRotationCompression.BONE_AXIS_A, axisANative, boneCount);
            NativeArray<byte>.Copy(BasisBoneRotationCompression.BONE_AXIS_B, axisBNative, boneCount);
            NativeArray<float>.Copy(BasisBoneRotationCompression.BONE_RANGE_A, rangeANative, boneCount);
            NativeArray<float>.Copy(BasisBoneRotationCompression.BONE_RANGE_B, rangeBNative, boneCount);

            try
            {
                float3 Axis(int code) => new float3(code == 0 ? 1f : 0f, code == 1 ? 1f : 0f, code == 2 ? 1f : 0f);

                Seed(909u);
                byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);
                for (int slot = 0; slot < boneCount; slot++)
                {
                    identityOps[slot] = quaternion.identity;
                    bpcNative[slot] = bpc[slot];
                    maxComp[slot] = BasisBoneRotationCompression.MAX_COMPONENT[slot];

                    // Generic-space pose the slot's encoding can represent: restricted slots get an
                    // in-range two-axis rotation, full slots a bounded random rotation.
                    int dof = BasisBoneRotationCompression.BONE_DOF[slot];
                    if (dof == 3)
                    {
                        float maxHalf = math.degrees(math.asin(math.min(1f, maxComp[slot])));
                        current[slot] = AxisAngle(new float3(Next(), Next(), Next()), Next() * 0.85f * maxHalf);
                    }
                    else
                    {
                        quaternion hinge = quaternion.AxisAngle(
                            Axis(BasisBoneRotationCompression.BONE_AXIS_A[slot]),
                            Next() * 0.9f * BasisBoneRotationCompression.BONE_RANGE_A[slot]);
                        current[slot] = dof == 2
                            ? math.mul(hinge, quaternion.AxisAngle(
                                Axis(BasisBoneRotationCompression.BONE_AXIS_B[slot]),
                                Next() * 0.9f * BasisBoneRotationCompression.BONE_RANGE_B[slot]))
                            : hinge;
                    }
                }

                new BasisBoneDeltaAndCompressJob
                {
                    CurrentLocalRotations = current,
                    EncodePre = identityOps,
                    EncodePost = identityOps,
                    BitsPerComponent = bpcNative,
                    MaxComponent = maxComp,
                    OutputBuffer = packet,
                    RotationByteOffset = 0,
                    BoneCount = boneCount,
                    BoneDeltas = outDeltas,
                    FingerPercentages = fingersIn,
                    CurlBits = BasisBoneRotationCompression.CurlBits(quality),
                    SplayBits = BasisBoneRotationCompression.SplayBits(quality),
                    BoneDof = dofNative,
                    BoneAxisA = axisANative,
                    BoneAxisB = axisBNative,
                    BoneRangeA = rangeANative,
                    BoneRangeB = rangeBNative,
                    HingeBitCount = BasisBoneRotationCompression.HingeBits(quality),
                    TwistBitCount = BasisBoneRotationCompression.TwistBits(quality),
                    SingleAxisBitCount = BasisBoneRotationCompression.SingleAxisBits(quality),
                }.Run();

                byte[] burstWire = packet.ToArray();

                var managedWire = new byte[burstWire.Length];
                int managedOffset = 0;
                BasisBoneRotationUtils.CompressBoneRotations(outDeltas, fingersIn, quality, managedWire, ref managedOffset);

                // Field-by-field, allowing a single quantization step: the managed encoder runs
                // trig through double-precision Math while Burst uses float math, so a value that
                // lands within one ulp of a step boundary may legally round apart. Any real
                // divergence (wrong axis, sign, layout) is off by orders of magnitude more.
                var fieldOffsets = new int[BasisBoneRotationCompression.RotationFieldCount];
                BasisBoneRotationCompression.BuildRotationFieldOffsets(quality, fieldOffsets);
                int hingeBits = BasisBoneRotationCompression.HingeBits(quality);
                int twistBits = BasisBoneRotationCompression.TwistBits(quality);
                for (int slot = 0; slot < boneCount; slot++)
                {
                    int width = BasisBoneRotationCompression.BoneFieldWidth(quality, slot);
                    int posA = fieldOffsets[slot], posB = fieldOffsets[slot];
                    ulong fromBurst = BasisBoneRotationCompression.ReadBits(burstWire, ref posA, width);
                    ulong fromManaged = BasisBoneRotationCompression.ReadBits(managedWire, ref posB, width);
                    if (BasisBoneRotationCompression.BONE_DOF[slot] == 2)
                    {
                        long hB = (long)(fromBurst & ((1UL << hingeBits) - 1UL));
                        long hM = (long)(fromManaged & ((1UL << hingeBits) - 1UL));
                        long tB = (long)(fromBurst >> hingeBits), tM = (long)(fromManaged >> hingeBits);
                        Assert.That(math.abs(hB - hM), Is.LessThanOrEqualTo(1), $"slot {slot} hinge diverged between encoders");
                        Assert.That(math.abs(tB - tM), Is.LessThanOrEqualTo(1), $"slot {slot} twist diverged between encoders");
                    }
                    else if (BasisBoneRotationCompression.BONE_DOF[slot] == 1)
                    {
                        Assert.That(math.abs((long)fromBurst - (long)fromManaged), Is.LessThanOrEqualTo(1),
                            $"slot {slot} angle diverged between encoders");
                    }
                    else
                    {
                        Assert.That(fromManaged, Is.EqualTo(fromBurst),
                            $"slot {slot} smallest-three diverged between encoders");
                    }
                }

                int offset = 0;
                BasisBoneRotationUtils.DecompressBoneRotations(burstWire, quality, ref decoded, ref fingersOut, ref offset);

                for (int slot = 0; slot < boneCount; slot++)
                {
                    int dof = BasisBoneRotationCompression.BONE_DOF[slot];
                    // Half a quantization step per transmitted angle, in degrees, with slack.
                    float bound = dof == 3 ? 0.2f
                        : dof == 2 ? math.degrees(
                            BasisBoneRotationCompression.BONE_RANGE_A[slot] / ((1 << BasisBoneRotationCompression.HingeBits(quality)) - 1)
                            + BasisBoneRotationCompression.BONE_RANGE_B[slot] / ((1 << BasisBoneRotationCompression.TwistBits(quality)) - 1)) + 0.05f
                        : math.degrees(
                            BasisBoneRotationCompression.BONE_RANGE_A[slot] / ((1 << BasisBoneRotationCompression.SingleAxisBits(quality)) - 1)) + 0.05f;
                    Assert.That(AngleBetween(decoded[slot], current[slot]), Is.LessThan(bound),
                        $"slot {slot} (dof {dof}) reconstructed outside its quantization bound");
                }
            }
            finally
            {
                current.Dispose(); identityOps.Dispose(); outDeltas.Dispose(); bpcNative.Dispose();
                maxComp.Dispose(); packet.Dispose(); fingersIn.Dispose(); fingersOut.Dispose();
                decoded.Dispose(); dofNative.Dispose(); axisANative.Dispose(); axisBNative.Dispose();
                rangeANative.Dispose(); rangeBNative.Dispose();
            }
        }

        /// <summary>
        /// A bone left sitting at its rest pose must encode to identity, on every slot. The
        /// sender relies on this for bones the rig does not have (BuildJobArrays pre-fills those
        /// slots with the rest local), and idle suppression relies on a still avatar producing a
        /// byte-identical packet.
        /// </summary>
        [Test]
        public void RestPose_EncodesToIdentity()
        {
            var rig = MakeRig(4004u);
            foreach (int bone in BasisBoneRotationCompression.BONE_WRITE_ORDER)
            {
                quaternion g = Encode(rig, bone, rig.RestLocal[bone]);
                Assert.That(AngleDeg(g), Is.LessThan(0.01f), $"bone {bone} did not encode rest as identity");
            }
        }

        // ────────────────────────────────────────────────────────────
        //  5. Degradation path and shared-math agreement
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// With no rest frame (F = identity) the operators collapse to the legacy scheme exactly:
        /// encode becomes conj(T) * C and decode becomes T * g. That is the fallback every
        /// missing-bone and un-calibrated slot takes, so it must degrade to the PREVIOUS behaviour
        /// rather than to something arbitrary.
        /// </summary>
        [Test]
        public void IdentityRestFrame_ReproducesLegacyScheme()
        {
            Seed(8080u);
            for (int i = 0; i < 500; i++)
            {
                quaternion t = RndQ(), c = RndQ(), g = RndQ();

                BasisGenericBoneRotationUtils.BuildEncodeOperators(quaternion.identity, t,
                    out quaternion ePre, out quaternion ePost);
                Assert.That(AngleBetween(math.mul(math.mul(ePre, c), ePost), math.mul(math.conjugate(t), c)),
                    Is.LessThan(0.01f), "identity rest frame must encode as the legacy local delta");

                BasisGenericBoneRotationUtils.BuildDecodeOperators(quaternion.identity, t,
                    out quaternion dPre, out quaternion dPost);
                Assert.That(AngleBetween(math.mul(math.mul(dPre, g), dPost), math.mul(t, g)),
                    Is.LessThan(0.01f), "identity rest frame must decode as the legacy T-pose compose");
            }
        }

        /// <summary>
        /// A rig with no calibration data at all (RecordPoses stores absent bones as an all-zero
        /// BasisCalibratedCoords, which is not a rotation) must still yield finite operators. An
        /// un-normalised zero quaternion propagating into the bone stream would NaN a whole
        /// avatar, which the pose watchdogs then see as a hard failure rather than a missing bone.
        /// </summary>
        [Test]
        public void DegenerateRestData_ProducesFiniteOperators()
        {
            var zero = new quaternion(0f, 0f, 0f, 0f);
            BasisGenericBoneRotationUtils.BuildEncodeOperators(zero, zero, out quaternion pre, out quaternion post);
            quaternion g = math.mul(math.mul(pre, quaternion.identity), post);

            Assert.That(math.all(math.isfinite(pre.value)), Is.True, "encode pre was not finite");
            Assert.That(math.all(math.isfinite(post.value)), Is.True, "encode post was not finite");
            Assert.That(AngleDeg(g), Is.LessThan(0.01f), "degenerate rest data should behave as identity");

            BasisGenericBoneRotationUtils.BuildDecodeOperators(zero, zero, out pre, out post);
            Assert.That(math.all(math.isfinite(pre.value)), Is.True, "decode pre was not finite");
            Assert.That(math.all(math.isfinite(post.value)), Is.True, "decode post was not finite");
        }

        /// <summary>
        /// The portable implementation and Unity's must be the same product. The Burst jobs on
        /// both ends of the wire use math.mul; the server, the headless load-test client and these
        /// tests use BasisGenericBoneRotation.Mul. A convention mismatch between them would be
        /// invisible in any single-process test and would corrupt every pose crossing the server.
        /// </summary>
        [Test]
        public void PortableQuaternionMath_MatchesUnityMathematics()
        {
            Seed(6161u);
            for (int i = 0; i < 2000; i++)
            {
                quaternion a = RndQ(), b = RndQ();

                Assert.That(AngleBetween(ToU(G.Mul(ToQ(a), ToQ(b))), math.mul(a, b)), Is.LessThan(0.01f),
                    "Mul disagrees with math.mul");
                Assert.That(AngleBetween(ToU(G.Conjugate(ToQ(a))), math.conjugate(a)), Is.LessThan(0.01f),
                    "Conjugate disagrees with math.conjugate");
            }
        }

        /// <summary>
        /// The Unity wrapper must build the same operators the portable builder does — the sender
        /// goes through the wrapper while the server and these tests go through the core, and they
        /// have to agree on the constant folded into every bone.
        /// </summary>
        [Test]
        public void UnityWrapper_MatchesPortableOperatorBuilders()
        {
            Seed(1717u);
            for (int i = 0; i < 500; i++)
            {
                quaternion f = RndQ(), t = RndQ();

                BasisGenericBoneRotationUtils.BuildEncodeOperators(f, t, out quaternion uPre, out quaternion uPost);
                G.BuildEncodeOperators(ToQ(f), ToQ(t), out Q pPre, out Q pPost);
                Assert.That(AngleBetween(uPre, ToU(pPre)), Is.LessThan(0.01f));
                Assert.That(AngleBetween(uPost, ToU(pPost)), Is.LessThan(0.01f));

                BasisGenericBoneRotationUtils.BuildDecodeOperators(f, t, out uPre, out uPost);
                G.BuildDecodeOperators(ToQ(f), ToQ(t), out pPre, out pPost);
                Assert.That(AngleBetween(uPre, ToU(pPre)), Is.LessThan(0.01f));
                Assert.That(AngleBetween(uPost, ToU(pPost)), Is.LessThan(0.01f));
            }
        }

        /// <summary>
        /// Slot-order tables must agree with per-bone construction under BONE_WRITE_ORDER, and
        /// round-trip within a rig. A silent off-by-one in the slot mapping would put the left
        /// thumb's operator on the right little finger — a pose that looks nearly right and is
        /// completely wrong.
        /// </summary>
        [Test]
        public void SlotTables_MatchPerBoneBuildsAndRoundTrip()
        {
            var rig = MakeRig(2323u);
            int n = BasisBoneRotationCompression.SyncBoneCount;

            var restFrame = new Q[55];
            var restLocal = new Q[55];
            for (int i = 0; i < 55; i++)
            {
                restFrame[i] = ToQ(rig.RestFrame[i]);
                restLocal[i] = ToQ(rig.RestLocal[i]);
            }

            var ePre = new Q[n]; var ePost = new Q[n];
            var dPre = new Q[n]; var dPost = new Q[n];
            G.BuildEncodeOperatorTable(restFrame, restLocal, ePre, ePost);
            G.BuildDecodeOperatorTable(restFrame, restLocal, dPre, dPost);

            Seed(313u);
            for (int slot = 0; slot < n; slot++)
            {
                int bone = BasisBoneRotationCompression.BONE_WRITE_ORDER[slot];
                quaternion c = RndQ();

                var g = G.Apply(ePre[slot], ToQ(c), ePost[slot]);
                Assert.That(AngleBetween(g, G.ToGeneric(restFrame[bone], restLocal[bone], ToQ(c))),
                    Is.LessThan(0.01f), $"slot {slot} encode table does not match bone {bone}");

                var back = G.Apply(dPre[slot], g, dPost[slot]);
                Assert.That(AngleBetween(ToU(back), c), Is.LessThan(0.01f), $"slot {slot} did not round-trip");
            }
        }

        // ────────────────────────────────────────────────────────────
        //  5b. The character basis — rigs whose ROOTS are authored differently
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// The bug this basis exists to fix, reproduced end to end.
        ///
        /// TposeFromRoot divides out where the avatar is STANDING, but not the rotation an
        /// exporter baked between the animator transform and the skeleton — a Blender Z-up
        /// conversion node, an `Armature` child rotated −90° about X, a model authored facing −Z.
        /// All are legal humanoid rigs (Unity's Avatar constrains the rig definition, not the
        /// GameObject hierarchy above it) and each puts a different constant inside F.
        ///
        /// That constant cancels whenever both ends wear the same rig, which is why it stayed
        /// hidden: the ONLY time a pose is decoded on a foreign rig is the avatar-swap window,
        /// where the old avatar stays worn while the new one downloads and the far end is already
        /// streaming the new one. Reported as limbs twisting AND the whole avatar offsetting for a
        /// few frames, then snapping correct once the new avatar landed.
        ///
        /// Here rig B's root is turned 90° about Y relative to rig A's — the same character,
        /// exported facing a different way. Without the basis normalisation the retarget is wrong
        /// by that 90°; with it, the two agree.
        /// </summary>
        [Test]
        public void RigsWithDifferentRootFacing_StillTransfer()
        {
            // Same anatomical character; only the frame the rig was authored in differs.
            quaternion rootSkewB = AxisAngle(new float3(0, 1, 0), 90f);

            quaternion parentA = AxisAngle(new float3(0, 1, 0), 25f);
            quaternion boneA = math.mul(parentA, AxisAngle(new float3(1, 0, 0), 90f));

            // B's every root-space rest rotation carries the exporter's extra turn...
            quaternion parentB = math.mul(rootSkewB, math.mul(AxisAngle(new float3(0, 0, 1), -70f), AxisAngle(new float3(0, 1, 0), 15f)));
            quaternion boneB = math.mul(parentB, AxisAngle(new float3(0, 0, 1), 55f));

            // ...and so does its measured anatomical frame, which is what cancels it back out.
            quaternion basisA = BasisGenericBoneRotationUtils.GetCharacterBasis(
                new float3(0, 0, 1), new float3(0, 1, 0));
            quaternion basisB = BasisGenericBoneRotationUtils.GetCharacterBasis(
                math.mul(rootSkewB, new float3(0, 0, 1)), math.mul(rootSkewB, new float3(0, 1, 0)));

            quaternion restFrameA = BasisGenericBoneRotationUtils.NormalizeRestFrame(boneA, basisA);
            quaternion restFrameB = BasisGenericBoneRotationUtils.NormalizeRestFrame(boneB, basisB);
            quaternion restLocalA = math.mul(math.conjugate(parentA), boneA);
            quaternion restLocalB = math.mul(math.conjugate(parentB), boneB);

            BasisGenericBoneRotationUtils.BuildEncodeOperators(restFrameA, restLocalA, out quaternion ePre, out quaternion ePost);
            BasisGenericBoneRotationUtils.BuildDecodeOperators(restFrameB, restLocalB, out quaternion dPre, out quaternion dPost);

            Seed(24u);
            float worst = 0f, worstRaw = 0f;
            for (int i = 0; i < 500; i++)
            {
                // A rotation stated in ANATOMICAL axes — "raise the arm", not "turn about the
                // root's +X" — which is the only thing two differently-authored rigs can agree on.
                quaternion anatomical = RndQ();

                quaternion charA = math.mul(math.mul(basisA, anatomical), math.conjugate(basisA));
                quaternion currentA = math.mul(math.conjugate(parentA), math.mul(charA, boneA));

                quaternion g = math.mul(math.mul(ePre, currentA), ePost);
                quaternion currentB = math.mul(math.mul(dPre, g), dPost);

                // What B's joint actually did, expressed back in B's anatomical axes.
                quaternion charB = math.mul(math.mul(parentB, currentB), math.conjugate(boneB));
                quaternion recovered = math.mul(math.mul(math.conjugate(basisB), charB), basisB);
                worst = math.max(worst, AngleBetween(recovered, anatomical));

                // The same trip with RAW root-space rest frames — v46's behaviour.
                BasisGenericBoneRotationUtils.BuildEncodeOperators(boneA, restLocalA, out quaternion rPre, out quaternion rPost);
                BasisGenericBoneRotationUtils.BuildDecodeOperators(boneB, restLocalB, out quaternion rdPre, out quaternion rdPost);
                quaternion rawB = math.mul(math.mul(rdPre, math.mul(math.mul(rPre, currentA), rPost)), rdPost);
                quaternion rawChar = math.mul(math.mul(parentB, rawB), math.conjugate(boneB));
                quaternion rawRecovered = math.mul(math.mul(math.conjugate(basisB), rawChar), basisB);
                worstRaw = math.max(worstRaw, AngleBetween(rawRecovered, anatomical));
            }

            Assert.That(worst, Is.LessThan(0.01f),
                $"basis-normalised rest frames must cancel the root-facing difference; drifted {worst}°");
            Assert.That(worstRaw, Is.GreaterThan(30f),
                "raw root-space rest frames are expected to FAIL this — if they pass, the two rigs " +
                "no longer differ in root facing and this test is proving nothing");
        }

        /// <summary>
        /// Degenerate or unmeasurable T-pose geometry must fall back to identity, i.e. to the raw
        /// root frame. RecordPoses already defaults AvatarForwards/Upwards to +Z/+Y when
        /// TryComputeForwardUpFromTpose fails, and a basis of identity reproduces exactly the
        /// behaviour that existed before this normalisation — a known state, not a random one.
        /// </summary>
        [Test]
        public void CharacterBasis_FallsBackToIdentityOnUnusableGeometry()
        {
            Assert.That(AngleDeg(BasisGenericBoneRotationUtils.GetCharacterBasis(float3.zero, new float3(0, 1, 0))),
                Is.LessThan(0.01f), "zero forward must fall back to identity");
            Assert.That(AngleDeg(BasisGenericBoneRotationUtils.GetCharacterBasis(new float3(0, 0, 1), float3.zero)),
                Is.LessThan(0.01f), "zero up must fall back to identity");
            Assert.That(AngleDeg(BasisGenericBoneRotationUtils.GetCharacterBasis(new float3(0, 0, 1), new float3(0, 1, 0))),
                Is.LessThan(0.01f), "the canonical forward/up pair IS identity");

            // Colinear forward/up has no well-defined basis; it must not produce NaN.
            quaternion colinear = BasisGenericBoneRotationUtils.GetCharacterBasis(new float3(0, 1, 0), new float3(0, 1, 0));
            Assert.That(math.all(math.isfinite(colinear.value)), Is.True, "colinear axes produced a non-finite basis");
        }

        /// <summary>
        /// A basis of identity must leave the rest frame untouched, so every fallback path lands on
        /// the raw root frame rather than on some third behaviour.
        /// </summary>
        [Test]
        public void IdentityBasis_LeavesRestFrameUnchanged()
        {
            Seed(4141u);
            for (int i = 0; i < 300; i++)
            {
                quaternion f = RndQ();
                Assert.That(AngleBetween(BasisGenericBoneRotationUtils.NormalizeRestFrame(f, quaternion.identity), f),
                    Is.LessThan(0.01f));
            }
        }

        // ────────────────────────────────────────────────────────────
        //  6. Hips — carried in the packet tail, same space
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Hips is excluded from the bone block and rides in the packet tail, decoded by
        /// BulkCopyHipsAndDeriveJob rather than the skeleton compose. It is carried in the same
        /// rig-neutral space, so it needs the same treatment — and it is the one bone whose
        /// rest frame is near identity on a well-authored rig, which is exactly how a mistake here
        /// would hide on the avatars people test with and only show up on the ones they don't.
        /// </summary>
        [Test]
        public void HipsRotation_TransfersBetweenRigs()
        {
            // Hips sits directly under the root, so its parent frame is identity and F == its
            // root-relative rest rotation.
            quaternion frameA = AxisAngle(new float3(0, 1, 0), 12f);
            quaternion frameB = math.mul(AxisAngle(new float3(1, 0, 0), -90f), AxisAngle(new float3(0, 0, 1), 45f));

            BasisGenericBoneRotationUtils.BuildEncodeOperators(frameA, frameA, out quaternion ePre, out quaternion ePost);
            BasisGenericBoneRotationUtils.BuildDecodeOperators(frameB, frameB, out quaternion dPre, out quaternion dPost);

            Seed(1234u);
            float worst = 0f;
            for (int i = 0; i < 500; i++)
            {
                quaternion characterRot = RndQ();

                quaternion localA = math.mul(characterRot, frameA);       // parent (root) at rest
                quaternion g = math.mul(math.mul(ePre, localA), ePost);
                quaternion localB = math.mul(math.mul(dPre, g), dPost);
                quaternion recovered = math.mul(localB, math.conjugate(frameB));

                worst = math.max(worst, AngleBetween(recovered, characterRot));
            }

            Assert.That(worst, Is.LessThan(0.01f), $"hips rotation drifted {worst}° across rigs");
        }

        /// <summary>
        /// The two rest quantities calibration records for a rig whose skeleton hangs off the
        /// animator root through <paramref name="chain"/> (an <c>Armature</c> node, a Z-up
        /// conversion) and whose anatomy points along <paramref name="basis"/> in root space.
        /// </summary>
        static BasisTransformMapping RigMapping(quaternion chain, quaternion basis, quaternion hipsRestLocal)
        {
            BasisTransformMapping mapping = new BasisTransformMapping();
            mapping.TposeLocal[HumanBodyBones.Hips] = new BasisCalibratedCoords(Vector3.zero, hipsRestLocal);
            // F is what a T-pose capture reads back off the root, so the chain above the hips is
            // already inside it — which is the whole reason F and T differ.
            mapping.TposeFromRoot[HumanBodyBones.Hips] =
                new BasisCalibratedCoords(Vector3.zero, math.mul(chain, hipsRestLocal));
            mapping.AvatarForwards = math.mul(basis, new float3(0f, 0f, 1f));
            mapping.AvatarUpwards = math.mul(basis, new float3(0f, 1f, 0f));
            return mapping;
        }

        /// <summary>
        /// The root pose the remote pipeline derives is the hips' PARENT, and the animator root is
        /// not the anatomical frame either — so on some perfectly legal rigs it is not a facing at
        /// all. Nothing in the rendered avatar shows it (hips are applied in world space), which
        /// left the follow camera as the only consumer, framing those people from behind.
        /// </summary>
        [Test]
        public void DerivedRoot_ReadsAsFacingOnlyOnceTheRigConstantsAreDividedOut()
        {
            quaternion hipsRestLocal = AxisAngle(new float3(1, 0, 0), -8f);
            quaternion worldFacing = AxisAngle(new float3(0, 1, 0), 37f);
            quaternion flipped = AxisAngle(new float3(0, 1, 0), 180f);
            quaternion zUp = AxisAngle(new float3(1, 0, 0), -90f);

            (string Name, quaternion Chain, quaternion Basis)[] rigs =
            {
                ("hips straight off a +Z root", quaternion.identity, quaternion.identity),
                ("model authored facing -Z", quaternion.identity, flipped),
                ("Armature node rotated -90 about X", zUp, zUp),
                ("both at once", zUp, math.mul(zUp, flipped)),
            };

            foreach ((string Name, quaternion Chain, quaternion Basis) rig in rigs)
            {
                BasisTransformMapping mapping = RigMapping(rig.Chain, rig.Basis, hipsRestLocal);

                // What ApplyRootAndScaleJob writes: the received hips world pose with the live hips
                // local rotation divided back out, which lands on the hips' parent.
                quaternion derivedRoot = math.mul(worldFacing, rig.Chain);
                quaternion anatomical = math.mul(worldFacing, rig.Basis);

                quaternion corrected = math.mul(derivedRoot,
                    BasisGenericBoneRotationUtils.GetDerivedRootToCharacterBasis(mapping));

                Assert.That(AngleBetween(corrected, anatomical), Is.LessThan(0.01f),
                    $"{rig.Name}: corrected root must face where the avatar faces");

                if (AngleBetween(rig.Chain, rig.Basis) > 1f)
                {
                    Assert.That(AngleBetween(derivedRoot, anatomical), Is.GreaterThan(170f),
                        $"{rig.Name}: this rig is only interesting because the raw root is the wrong " +
                        "way round — if it stops being, the correction above is proving nothing");
                }
            }
        }

        /// <summary>
        /// Half a measurement must not move an avatar that reads correctly today: every incomplete
        /// case degrades to the raw root frame, which is the behaviour that was already there.
        /// </summary>
        [Test]
        public void DerivedRootCorrection_IsIdentityWithoutBothHipsRestRotations()
        {
            Assert.That(AngleDeg(BasisGenericBoneRotationUtils.GetDerivedRootToCharacterBasis(null)),
                Is.LessThan(0.01f), "no mapping at all");

            Assert.That(AngleDeg(BasisGenericBoneRotationUtils.GetDerivedRootToCharacterBasis(new BasisTransformMapping())),
                Is.LessThan(0.01f), "a mapping calibration has not filled in yet");

            // RecordPoses stores a bone the rig does not have as an all-zero coords pair.
            BasisTransformMapping absent = new BasisTransformMapping();
            absent.TposeLocal[HumanBodyBones.Hips] = default;
            absent.TposeFromRoot[HumanBodyBones.Hips] = default;
            Assert.That(AngleDeg(BasisGenericBoneRotationUtils.GetDerivedRootToCharacterBasis(absent)),
                Is.LessThan(0.01f), "an all-zero coords pair is not a rotation");
        }

        /// <summary>
        /// The wire format changed meaning without changing shape — same fields, same bits, same
        /// bytes — so a v45 peer and a v46 peer would happily parse each other's packets and both
        /// render nonsense. Only the version gate stops that, which makes the bump part of the
        /// change rather than bookkeeping alongside it.
        /// </summary>
        [Test]
        public void NetworkVersion_GatesTheFormatChange()
        {
            Assert.That(Basis.Network.Core.BasisNetworkVersion.ServerVersion, Is.GreaterThanOrEqualTo(46),
                "generic bone rotations are wire-incompatible with the local-delta format they replaced");
        }

        /// <summary>
        /// Packet size must be untouched. Moving to generic space is a pure change of axes and
        /// preserves rotation angle exactly (see <see cref="GenericSpace_PreservesRotationAngle"/>),
        /// so not one bit of budget had to move with it. If these numbers drift, someone
        /// rebalanced the bit tables on the assumption that the new representation needs more
        /// range than the old one did — it does not, and every bandwidth figure the reduction
        /// system is tuned against would be off.
        ///
        /// 163 is the pre-existing High rotation-block size: 15 body/limb slots at 12 bits per
        /// component plus the toe/finger tail = 1302 bits. (The class doc on
        /// BasisBoneRotationCompression still says 148, left over from before the v40 10→12 bit
        /// change; the code has been the source of truth since.)
        /// </summary>
        [Test]
        public void PacketSize_IsUnchangedByTheRepresentation()
        {
            // Conjugation is an isometry of SO(3), so the generic-space remap cannot alter any
            // bone's bit cost. v47 moved the total by dropping finger rotations, and v52 moved it
            // again by shipping restricted-DOF joints as angles — both are changes of CONTENT.
            // What this pins is the current explicit bone region so a drift is deliberate:
            // 9 x (2 + 3*12) three-DOF slots + 10 x (13 + 12) hinge/twist slots + 2 x 7 toe slots.
            int boneBits = 0;
            for (int slot = 0; slot < BasisBoneRotationCompression.WireBoneSlotCount; slot++)
                boneBits += BasisBoneRotationCompression.BoneFieldWidth(BasisAvatarBitPacking.BitQuality.High, slot);

            Assert.That(boneBits, Is.EqualTo(606), "explicit bone region must only move deliberately");
            Assert.That(BasisBoneRotationCompression.RotationBytes(BasisAvatarBitPacking.BitQuality.High),
                Is.EqualTo(94), "High rotation block: 606 bone bits + 140 finger bits = 746 = 94 bytes");
            Assert.That(BasisBoneRotationCompression.SyncBoneCount, Is.EqualTo(51));
        }

        // ---------- where the avatar's front is ----------

        /// <summary>
        /// A T-posed humanoid standing on the origin facing +Z, in the root-local frame
        /// <see cref="BasisTransformMapping.TposeFromRoot"/> stores. Bones the caller nulls out are
        /// dropped, which is how RecordPoses represents a rig that does not carry them.
        /// </summary>
        private static BasisTransformMapping TposedRig(bool toes = true, bool eyes = true, bool mirroredLabels = false,
            bool leaningHead = false, float kneeBend = float.NaN, bool singleToe = false, float headAhead = 0f)
        {
            BasisTransformMapping rig = new BasisTransformMapping();

            void Bone(HumanBodyBones bone, float x, float y, float z) =>
                rig.TposeFromRoot[bone] = new BasisCalibratedCoords
                {
                    position = new Vector3(x, y, z),
                    rotation = Quaternion.identity,
                };

            Bone(HumanBodyBones.Hips, 0f, 0.95f, 0f);
            Bone(HumanBodyBones.Spine, 0f, 1.05f, 0f);
            Bone(HumanBodyBones.Chest, 0f, 1.20f, 0f);
            Bone(HumanBodyBones.Neck, 0f, 1.45f, 0f);
            Bone(HumanBodyBones.Head, leaningHead ? 0.02f : 0f, 1.55f, (leaningHead ? 0.03f : 0f) + headAhead);

            float legSide = mirroredLabels ? -0.09f : 0.09f;
            Bone(HumanBodyBones.LeftUpperLeg, -legSide, 0.90f, 0f);
            Bone(HumanBodyBones.RightUpperLeg, legSide, 0.90f, 0f);
            Bone(HumanBodyBones.LeftFoot, -legSide, 0.08f, 0f);
            Bone(HumanBodyBones.RightFoot, legSide, 0.08f, 0f);

            if (!float.IsNaN(kneeBend))
            {
                Bone(HumanBodyBones.LeftLowerLeg, -legSide, 0.49f, kneeBend);
                Bone(HumanBodyBones.RightLowerLeg, legSide, 0.49f, kneeBend);
            }

            if (singleToe)
            {
                Bone(HumanBodyBones.LeftToes, -legSide, 0.03f, 0.12f);
            }

            if (toes)
            {
                Bone(HumanBodyBones.LeftToes, -legSide, 0.03f, 0.12f);
                Bone(HumanBodyBones.RightToes, legSide, 0.03f, 0.12f);
            }

            if (eyes)
            {
                Bone(HumanBodyBones.LeftEye, -0.03f, 1.60f, 0.08f);
                Bone(HumanBodyBones.RightEye, 0.03f, 1.60f, 0.08f);
            }

            return rig;
        }

        /// <summary>Turns a rig round inside its own root, the way an armature authored facing -Z sits.</summary>
        private static BasisTransformMapping TurnedAround(BasisTransformMapping rig)
        {
            BasisTransformMapping turned = new BasisTransformMapping();
            foreach (var pair in rig.TposeFromRoot)
            {
                turned.TposeFromRoot[pair.Key] = new BasisCalibratedCoords
                {
                    position = new Vector3(-pair.Value.position.x, pair.Value.position.y, -pair.Value.position.z),
                    rotation = pair.Value.rotation,
                };
            }
            return turned;
        }

        private static Vector3 Facing(BasisTransformMapping rig)
        {
            Assert.That(BasisTransformMapping.TryComputeForwardUpFromTpose(
                rig, out Vector3 forward, out Vector3 up, out Vector3 right), Is.True,
                "the rig carries hips, a spine and legs - the geometry is readable");
            Assert.That(up.y, Is.GreaterThan(0.99f), "up must come out of the spine");
            Assert.That(Vector3.Dot(forward, up), Is.EqualTo(0f).Within(1e-3f), "forward must be level");
            Assert.That(Vector3.Dot(right, up), Is.EqualTo(0f).Within(1e-3f), "right must be level");
            return forward;
        }

        [Test]
        public void ACanonicalRig_FacesItsOwnPlusZ()
        {
            Assert.That(Vector3.Dot(Facing(TposedRig()), Vector3.forward), Is.GreaterThan(0.99f));
        }

        [Test]
        public void ARigAuthoredTurnedAround_ReadsItsOwnFrontNotItsRootsFront()
        {
            Assert.That(Vector3.Dot(Facing(TurnedAround(TposedRig())), Vector3.back), Is.GreaterThan(0.99f));
        }

        /// <summary>
        /// The Left/Right humanoid bones are assignments a rigger makes, and a mirrored model gets
        /// them the wrong way round often enough to matter. Deriving the front from right x up used
        /// to hand those avatars a back-to-front basis, so the follow camera filmed them from
        /// behind: the toes are the measurement that does not care which leg is called which.
        /// </summary>
        [Test]
        public void AMirroredRigStillReadsItsFrontFromItsToes()
        {
            Assert.That(Vector3.Dot(Facing(TposedRig(mirroredLabels: true)), Vector3.forward), Is.GreaterThan(0.99f));
        }

        [Test]
        public void ARigWithNoToes_ReadsItsFrontFromItsEyes()
        {
            Assert.That(Vector3.Dot(Facing(TposedRig(toes: false)), Vector3.forward), Is.GreaterThan(0.99f));
            Assert.That(Vector3.Dot(Facing(TurnedAround(TposedRig(toes: false))), Vector3.back), Is.GreaterThan(0.99f));
        }

        /// <summary>
        /// Nothing anterior left to measure, so the legs are all there is. Correctly labelled they
        /// give the right answer; this is the one shape a mirrored rig cannot be rescued from.
        /// </summary>
        [Test]
        public void ARigWithNeitherToesNorEyes_FallsBackToTheLegs()
        {
            Assert.That(Vector3.Dot(Facing(TposedRig(toes: false, eyes: false)), Vector3.forward),
                Is.GreaterThan(0.99f));
        }

        /// <summary>
        /// A head bone is a couple of centimetres off the hips on plenty of rigs, and head minus
        /// hips is vertical: flatten it and what is left is that couple of centimetres of nothing,
        /// which used to be normalised into a heading and swung the follow camera round by tens of
        /// degrees. It is not a facing measurement and is no longer read as one.
        /// </summary>
        [Test]
        public void AnOffAxisHeadBone_IsNotMistakenForAHeading()
        {
            Assert.That(Vector3.Dot(Facing(TposedRig(toes: false, eyes: false, leaningHead: true)), Vector3.forward),
                Is.GreaterThan(0.99f));
        }

        [Test]
        public void AMirroredRigWithOneToe_ReadsItsFrontFromThatToe()
        {
            Assert.That(Vector3.Dot(Facing(TposedRig(toes: false, eyes: false, mirroredLabels: true, singleToe: true)), Vector3.forward), Is.GreaterThan(0.99f));
            Assert.That(Vector3.Dot(Facing(TurnedAround(TposedRig(toes: false, eyes: false, mirroredLabels: true, singleToe: true))), Vector3.back), Is.GreaterThan(0.99f));
        }

        [Test]
        public void AMirroredRigWithNeitherToesNorEyes_ReadsItsFrontFromItsKnees()
        {
            Assert.That(Vector3.Dot(Facing(TposedRig(toes: false, eyes: false, mirroredLabels: true, kneeBend: 0.02f)), Vector3.forward), Is.GreaterThan(0.99f));
            Assert.That(Vector3.Dot(Facing(TurnedAround(TposedRig(toes: false, eyes: false, mirroredLabels: true, kneeBend: 0.02f))), Vector3.back), Is.GreaterThan(0.99f));
        }

        [Test]
        public void AMirroredRigWithStraightKnees_ReadsItsFrontFromItsHeadOverItsChest()
        {
            Assert.That(Vector3.Dot(Facing(TposedRig(toes: false, eyes: false, mirroredLabels: true, kneeBend: 0f, headAhead: 0.04f)), Vector3.forward), Is.GreaterThan(0.99f));
            Assert.That(Vector3.Dot(Facing(TurnedAround(TposedRig(toes: false, eyes: false, mirroredLabels: true, kneeBend: 0f, headAhead: 0.04f))), Vector3.back), Is.GreaterThan(0.99f));
        }

        [Test]
        public void ALandmarkFreeRig_StillTakesTheLegsAsLabelled()
        {
            Assert.That(Vector3.Dot(Facing(TposedRig(toes: false, eyes: false, mirroredLabels: true, kneeBend: 0f)), Vector3.back), Is.GreaterThan(0.99f));
        }

        /// <summary>The basis the wire and the camera actually consume, built off that forward.</summary>
        [Test]
        public void TheCharacterBasis_TurnsARigsOwnFrontIntoPlusZ()
        {
            BasisTransformMapping rig = TurnedAround(TposedRig());
            Assert.That(BasisTransformMapping.TryComputeForwardUpFromTpose(
                rig, out Vector3 forward, out Vector3 up, out _), Is.True);

            quaternion basis = BasisGenericBoneRotationUtils.GetCharacterBasis(forward, up);
            Vector3 front = math.mul(basis, new float3(0f, 0f, 1f));

            Assert.That(Vector3.Dot(front, Vector3.back), Is.GreaterThan(0.99f),
                "the basis maps canonical +Z onto where this rig's front actually points");
        }
    }
}
