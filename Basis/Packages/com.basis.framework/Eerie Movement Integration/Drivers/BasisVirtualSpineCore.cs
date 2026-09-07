using Basis.Scripts.Drivers;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
namespace Basis.IK
{
    [BurstCompile]
    public static class BasisVirtualSpineCore
    {
        private const float StanceRadiusFrac = 0.12f;
        private const float CrouchLeanAllowanceFrac = 0.70f;
        private const float HeadBaselineFollowRateRest = 2f;
        private const float HeadBaselineFollowRateGain = 250f;
        private const float CounterbalanceFollowFrac = 0.25f;
        private const float CounterbalanceLateralFollowFrac = 0.8f;
        private const float FootPendulumLeanFrac = 0.20f;
        private const float TorsoYawRelockSpeedDeg = 6f;
        private const float ReachUseCeiling = 0.97f;
        public struct SpineSolveParams
        {
            public float Dt, Scale, TrackingLiftY;
            public float4x4 ParentMatrix;
            public quaternion ParentRotation, EyeRot;
            public float3 HeadTargetPos;
            public quaternion HeadTargetRot;
            public float3 NeckTargetPos;
            public quaternion NeckTargetRot;
            public float3 ChestTargetPos;
            public quaternion ChestTargetRot;
            public float3 SpineTargetPos;
            public quaternion SpineTargetRot;
            public float3 HeadScaledOffset, NeckScaledOffset, ChestScaledOffset, SpineScaledOffset;
            public float ChestTposeY, SpineTposeY;
            public float3 TposeHips, LeftFootPos, RightFootPos;
            public byte LeftFootTracked, RightFootTracked;
            public float ChestPitchFrac, ChestRollFrac, SpinePitchFrac, SpineRollFrac, NeckRotationSpeed;
            public float ChestRotationSpeed, SpineRotationSpeed, HipsRotationSpeed;
            public float3 GazeSwingLever;
            public float TposeNeckMinusEyeY, GazeSwingRemoval, HipsForwardBias, NeckExtensionDamp, NeckFlexionDamp;
            public float TorsoYawDeadzoneDeg, TorsoYawBlendSpeed;
            public byte HipsFreeze, IsLocomoting;
            public float LenTotal, TChest, TSpine, StandingHipsLocalY, StandingHeadLocalY;
            public float3 EyePos, HipsAnchorOffsetLocal, HeadRestFromEyeLocal, YawPivotFromEyeLocal;
            public byte PostureModel;
            public float HipsCompressionStrength, HipsMaxDropMeters, HipsRestDropY;
            public float3 RestChordDir, ChestRestPerp, SpineRestPerp;
            public float ChestRestAlong, SpineRestAlong;
        }
        public struct SpineSolveState
        {
            public float3 HeadBaselineXZ;
            public byte HeadBaselineInitialized;
            public float StandingHeadRefY;
            public byte TorsoYawInitialized;
            public float TorsoYawAnchorDeg, PrevHeadYawDeg;
            public byte TorsoYawBroken;
            public float TorsoFollow;
        }
        [BurstCompile]
        public struct BasisVirtualSpineSolveJob : IJob
        {
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<BasisBoneSimState> States;
            public NativeArray<SpineSolveState> State;
            public SpineSolveParams P;
            public int IdxHead, IdxNeck, IdxChest, IdxSpine, IdxHips;
            public byte SkipHead, SkipNeck, SkipChest, SkipSpine, SkipHips;
            public void Execute()
            {
                BasisBoneSimState head = States[IdxHead];
                BasisBoneSimState neck = States[IdxNeck];
                BasisBoneSimState chest = States[IdxChest];
                BasisBoneSimState spine = States[IdxSpine];
                BasisBoneSimState hips = States[IdxHips];
                SpineSolveState s = State[0];

                float dt = P.Dt;
                bool freeze = P.HipsFreeze != 0;

                quaternion eyeRot = P.EyeRot;
                head.OutgoingRotation = eyeRot;

                quaternion neckCurrent = neck.OutgoingRotation;
                SmoothSlerpBurst(in neckCurrent, in eyeRot, P.NeckRotationSpeed, dt, out quaternion neckRot);
                neck.OutgoingRotation = neckRot;

                ComposePosition(in P.HeadTargetPos, in P.HeadTargetRot, in P.HeadScaledOffset, out float3 headPos);
                head.OutgoingPosition = headPos;
                ApplyWorldAndLastBurst(ref head, in P.ParentMatrix, in P.ParentRotation);

                float3 solveUp = new float3(0f, 1f, 0f);

                float3 neckPos0 = BasisNeckCueCore.Solve(P.NeckTargetPos, P.NeckTargetRot, P.NeckScaledOffset, solveUp, P.NeckExtensionDamp, P.NeckFlexionDamp);
                neck.OutgoingPosition = neckPos0;
                ApplyWorldAndLastBurst(ref neck, in P.ParentMatrix, in P.ParentRotation);

                float3 neckPosWorld = neck.OutgoingPosition;

                ExtractYawBurst(in eyeRot, out quaternion headYawFromEye);

                bool isLocomoting = P.IsLocomoting != 0;
                quaternion torsoYawTarget = ComputeTorsoYawTargetBurst(ref s, in headYawFromEye, P.TorsoYawDeadzoneDeg, P.TorsoYawBlendSpeed, isLocomoting, dt);

                float3 tposeHips = P.TposeHips;
                float biasScale = P.HipsForwardBias * P.Scale;

                float3 headPosWorld = head.OutgoingPosition;

                float3 eyePosDevice = P.EyePos;

                float standingHeadLifted = P.StandingHeadLocalY + P.TrackingLiftY;
                float headRestCandidate = math.min(headPosWorld.y, standingHeadLifted);
                s.StandingHeadRefY = s.HeadBaselineInitialized == 0 ? headRestCandidate : math.max(s.StandingHeadRefY, headRestCandidate);
                float stanceHeadDrop = math.max(0f, s.StandingHeadRefY - headPosWorld.y);

                bool feetSupported = P.LeftFootTracked != 0 && P.RightFootTracked != 0;

                float3 yawArm = feetSupported ? float3.zero : P.YawPivotFromEyeLocal * P.GazeSwingRemoval;

                float3 leashEyePos = eyePosDevice + math.mul(headYawFromEye, yawArm);
                if (P.GazeSwingRemoval > 0f)
                {
                    float3 gazeFwd = math.mul(eyeRot, new float3(0f, 0f, 1f));
                    float gazeHorizMag = math.sqrt(gazeFwd.x * gazeFwd.x + gazeFwd.z * gazeFwd.z);

                    float gazePitchDeg = math.degrees(math.atan2(-gazeFwd.y, gazeHorizMag));
                    YawDegrees(in headYawFromEye, out float gazeYawDeg);

                    BasisHeadPitchSwingCore.Solve(gazePitchDeg, gazeYawDeg, P.GazeSwingLever, P.GazeSwingRemoval, 1f, out UnityEngine.Vector3 swingOffset, out _);

                    leashEyePos -= (float3)swingOffset;
                }

                float3 desiredHipsXZ = ComputeRealisticHipsXZBurst(ref s, leashEyePos, dt, P.StandingHeadLocalY, stanceHeadDrop, in torsoYawTarget, P.LeftFootPos, P.RightFootPos, P.LeftFootTracked != 0, P.RightFootTracked != 0, out float3 supportXZ);
                float3 hipsArm = math.mul(torsoYawTarget, P.HipsAnchorOffsetLocal - yawArm);
                desiredHipsXZ += new float3(hipsArm.x, 0f, hipsArm.z);

                if (!feetSupported)
                {
                    float3 headRestArm = math.mul(torsoYawTarget, P.HeadRestFromEyeLocal - yawArm);
                    supportXZ += new float3(headRestArm.x, 0f, headRestArm.z);
                }

                float3 neckForHips = neckPosWorld;
                if (P.GazeSwingRemoval > 0f)
                {
                    float3 nodPivot = eyePosDevice - math.mul(eyeRot, P.GazeSwingLever);
                    float stableNeckY = nodPivot.y + P.TposeNeckMinusEyeY + P.GazeSwingLever.y;
                    neckForHips.y = math.lerp(neckPosWorld.y, stableNeckY, math.saturate(P.GazeSwingRemoval));

                    float reach = (P.LenTotal + math.distance(neckPosWorld, headPosWorld)) * ReachUseCeiling;
                    float2 spanXZ = new float2(headPosWorld.x - desiredHipsXZ.x, headPosWorld.z - desiredHipsXZ.z);
                    float horizSq = math.lengthsq(spanXZ);
                    if (reach * reach > horizSq)
                    {
                        float maxVertical = math.sqrt(reach * reach - horizSq);
                        float reachLimitNeckY = headPosWorld.y - maxVertical + (P.HipsRestDropY > 0f ? P.HipsRestDropY : P.LenTotal);
                        neckForHips.y = math.max(neckForHips.y, math.min(reachLimitNeckY, neckPosWorld.y));
                    }
                }

                ComputeHipsPosition( in neckForHips, in headPosWorld, in supportXZ, in solveUp, P.LenTotal, in torsoYawTarget, biasScale, in desiredHipsXZ, freeze, in tposeHips, P.StandingHipsLocalY, P.StandingHeadLocalY, P.TrackingLiftY, P.PostureModel != 0, P.HipsCompressionStrength, P.HipsMaxDropMeters, P.HipsRestDropY, out float3 hipsPos);

                quaternion hipsRotTarget = freeze ? quaternion.identity : torsoYawTarget;
                quaternion hipsCurrent = hips.OutgoingRotation;
                SmoothSlerpBurst(in hipsCurrent, in hipsRotTarget, P.HipsRotationSpeed, dt, out quaternion hipsSmoothed);
                ExtractYawBurst(in hipsSmoothed, out quaternion hipsYaw);

                hips.OutgoingRotation = hipsYaw;
                hips.OutgoingPosition = hipsPos;
                ApplyWorldAndLastBurst(ref hips, in P.ParentMatrix, in P.ParentRotation);

                ExtractYawBurst(in neckRot, out quaternion neckYaw);

                float3 hipsPosReadback = hips.OutgoingPosition;
                float3 neckPos = neck.OutgoingPosition;
                float3 neckToHips = hipsPosReadback - neckPos;

                if (math.lengthsq(neckToHips) < 1e-10f)
                {
                    ApplyPositionControlTorsoLock(ref chest, in P.ChestTargetRot, in P.ChestTargetPos, in P.ChestScaledOffset, P.ChestTposeY + P.TrackingLiftY, in P.ParentMatrix, in P.ParentRotation);
                    ApplyPositionControlTorsoLock(ref spine, in P.SpineTargetRot, in P.SpineTargetPos, in P.SpineScaledOffset, P.SpineTposeY + P.TrackingLiftY, in P.ParentMatrix, in P.ParentRotation);
                }
                else
                {
                    quaternion chainTopYaw = freeze ? neckYaw : torsoYawTarget;
                    ComputeChainPlacement( in neckPos, in hipsPosReadback, P.TChest, P.TSpine, in chainTopYaw, in hipsYaw, out float3 chestPos, out float3 spinePos, out quaternion chestYawTarget, out quaternion spineYawTarget);

                    quaternion chestTarget = ApplyPitchRollCascadeBurst(in chestYawTarget, in eyeRot, P.ChestPitchFrac, P.ChestRollFrac);
                    quaternion spineTarget = ApplyPitchRollCascadeBurst(in spineYawTarget, in eyeRot, P.SpinePitchFrac, P.SpineRollFrac);

                    quaternion chestCurrent = chest.OutgoingRotation;
                    quaternion spineCurrent = spine.OutgoingRotation;

                    SmoothSlerpBurst(in chestCurrent, in chestTarget, P.ChestRotationSpeed, dt, out quaternion chestSmoothed);
                    SmoothSlerpBurst(in spineCurrent, in spineTarget, P.SpineRotationSpeed, dt, out quaternion spineSmoothed);

                    chest.OutgoingRotation = chestSmoothed;
                    spine.OutgoingRotation = spineSmoothed;

                    PlaceOnTorsoChord(ref chest, in neckPos, in hipsPosReadback, in chestPos, in P.ChestScaledOffset, P.ChestRestAlong, in P.ChestRestPerp, in P.RestChordDir, in P.ParentMatrix, in P.ParentRotation);
                    PlaceOnTorsoChord(ref spine, in neckPos, in hipsPosReadback, in spinePos, in P.SpineScaledOffset, P.SpineRestAlong, in P.SpineRestPerp, in P.RestChordDir, in P.ParentMatrix, in P.ParentRotation);
                }

                if (SkipHead == 0) States[IdxHead] = head;
                if (SkipNeck == 0) States[IdxNeck] = neck;
                if (SkipChest == 0) States[IdxChest] = chest;
                if (SkipSpine == 0) States[IdxSpine] = spine;
                if (SkipHips == 0) States[IdxHips] = hips;
                State[0] = s;
            }
        }
        [BurstCompile]
        private static void ApplyWorldAndLastBurst(ref BasisBoneSimState st, in float4x4 parentMatrix, in quaternion parentRotation)
        {
            st.LastRunPosition = st.OutgoingPosition;
            st.LastRunRotation = st.OutgoingRotation;

            float4 p = math.mul(parentMatrix, new float4(st.OutgoingPosition, 1f));
            st.OutgoingWorldPosition = p.xyz;
            st.OutgoingWorldRotation = math.mul(parentRotation, st.OutgoingRotation);
        }
        [BurstCompile]
        private static void ApplyPositionControlTorsoLock(ref BasisBoneSimState st, in quaternion targetRot, in float3 targetPos, in float3 scaledOffset, float tposeY, in float4x4 parentMatrix, in quaternion parentRotation)
        {
            ExtractYawBurst(in targetRot, out quaternion yawOnly);
            float3 localOffset = scaledOffset;
            localOffset.y = 0f;
            ComposePosition(in targetPos, in yawOnly, in localOffset, out float3 desired);
            desired.y = tposeY;
            st.OutgoingPosition = desired;
            ApplyWorldAndLastBurst(ref st, in parentMatrix, in parentRotation);
        }
        // Untracked chest/spine ride the neck->hips chord: the bone sits at its authored fraction of the chord plus its
        // authored offset from that chord, swung from the rest chord direction to the current one, so the avatar's own
        // curve is reproduced at rest and follows the head vertically (crouch, jump, a player taller than the avatar's
        // rest head). Pinning the height to the T-pose instead put the chest below the lumbar joint whenever the head
        // rode more than one vertebra above rest, and the chest IK target then folded the spine toward it.
        [BurstCompile]
        private static void PlaceOnTorsoChord(ref BasisBoneSimState st, in float3 neckPos, in float3 hipsPos, in float3 chordPos, in float3 scaledOffset, float restAlong, in float3 restPerp, in float3 restChordDir, in float4x4 parentMatrix, in quaternion parentRotation)
        {
            quaternion rot = st.OutgoingRotation;
            ExtractYawBurst(in rot, out quaternion yawOnly);
            float3 chord = neckPos - hipsPos, desired;
            if (restAlong > 0f && math.lengthsq(chord) > 1e-10f && math.lengthsq(restChordDir) > 1e-10f)
            {
                float3 restDirWorld = math.mul(yawOnly, math.normalize(restChordDir)), chordDir = math.normalize(chord);
                FromToRotationBurst(in restDirWorld, in chordDir, out quaternion swing);
                desired = hipsPos + chord * restAlong + math.mul(swing, math.mul(yawOnly, restPerp));
            }
            else
            {
                float3 localOffset = scaledOffset;
                localOffset.y = 0f;
                ComposePosition(in chordPos, in yawOnly, in localOffset, out desired);
            }
            st.OutgoingPosition = desired;
            ApplyWorldAndLastBurst(ref st, in parentMatrix, in parentRotation);
        }
        [BurstCompile]
        public static void FromToRotationBurst(in float3 from, in float3 to, out quaternion result)
        {
            float3 a = math.normalizesafe(from), b = math.normalizesafe(to);
            float d = math.dot(a, b);
            if (d >= 1f - 1e-6f)
            {
                result = quaternion.identity;
                return;
            }
            float3 axis = math.cross(a, b);
            if (d <= -1f + 1e-6f || math.lengthsq(axis) < 1e-12f)
            {
                float3 ortho = math.abs(a.y) < 0.9f ? math.cross(a, new float3(0f, 1f, 0f)) : math.cross(a, new float3(1f, 0f, 0f));
                result = quaternion.AxisAngle(math.normalize(ortho), math.PI);
                return;
            }
            result = math.normalize(new quaternion(new float4(axis, 1f + d)));
        }
        private static quaternion ComputeTorsoYawTargetBurst(ref SpineSolveState s, in quaternion headYawOnly, float deadzoneDeg, float blendSpeed, bool moving, float dt)
        {
            YawDegrees(in headYawOnly, out float headYawDeg);

            if (s.TorsoYawInitialized == 0)
            {
                s.TorsoYawAnchorDeg = headYawDeg;
                s.PrevHeadYawDeg = headYawDeg;
                s.TorsoYawBroken = 0;
                s.TorsoFollow = 0f;
                s.TorsoYawInitialized = 1;
            }

            float headSpeedDeg = math.abs(DeltaAngleDeg(s.PrevHeadYawDeg, headYawDeg)) / math.max(dt, 1e-5f);
            s.PrevHeadYawDeg = headYawDeg;

            if (moving)
            {
                s.TorsoYawBroken = 1;
            }

            if (s.TorsoYawBroken == 0 && math.abs(DeltaAngleDeg(s.TorsoYawAnchorDeg, headYawDeg)) > math.max(0f, deadzoneDeg))
            {
                s.TorsoYawBroken = 1;
            }

            float targetFollow = s.TorsoYawBroken != 0 ? 1f : 0f;

            s.TorsoFollow = math.lerp(s.TorsoFollow, targetFollow, BasisSmoothingProfiles.FramerateIndependentAlpha(blendSpeed, dt));

            if (s.TorsoYawBroken != 0 && s.TorsoFollow >= 0.999f && headSpeedDeg <= TorsoYawRelockSpeedDeg)
            {
                s.TorsoYawBroken = 0;
                s.TorsoYawAnchorDeg = headYawDeg;
            }

            quaternion anchorYaw = quaternion.AxisAngle(new float3(0f, 1f, 0f), math.radians(s.TorsoYawAnchorDeg));
            return math.slerp(anchorYaw, headYawOnly, s.TorsoFollow);
        }
        private static float3 ComputeRealisticHipsXZBurst(ref SpineSolveState s, float3 headPosWorld, float dt, float standingHeadY, float headDrop, in quaternion torsoYaw, float3 leftFootPos, float3 rightFootPos, bool leftFootTracked, bool rightFootTracked, out float3 supportXZ)
        {
            float3 headXZ = new float3(headPosWorld.x, 0f, headPosWorld.z);

            if (s.HeadBaselineInitialized == 0)
            {
                s.HeadBaselineXZ = headXZ;
                s.HeadBaselineInitialized = 1;
            }
            else
            {
                float safeDt = math.max(dt, 1e-6f);

                float radius = math.max(StanceRadiusFrac * standingHeadY + CrouchLeanAllowanceFrac * headDrop, 1e-3f);

                float dist = math.length(headXZ - s.HeadBaselineXZ);
                float rate = HeadBaselineFollowRateRest + HeadBaselineFollowRateGain * (dist / radius);
                float alpha = 1f - math.exp(-rate * safeDt);
                s.HeadBaselineXZ = math.lerp(s.HeadBaselineXZ, headXZ, alpha);
            }

            if (leftFootTracked && rightFootTracked)
            {
                float3 feetMidXZ = new float3( (leftFootPos.x + rightFootPos.x) * 0.5f, 0f, (leftFootPos.z + rightFootPos.z) * 0.5f);
                supportXZ = feetMidXZ;
                return math.lerp(feetMidXZ, headXZ, FootPendulumLeanFrac);
            }

            supportXZ = s.HeadBaselineXZ;

            float3 dev = headXZ - s.HeadBaselineXZ;
            float3 fwd = math.mul(torsoYaw, new float3(0f, 0f, 1f));
            float3 right = math.mul(torsoYaw, new float3(1f, 0f, 0f));
            float devFwd = math.dot(dev, fwd);
            float devRight = math.dot(dev, right);
            return s.HeadBaselineXZ + fwd * (devFwd * CounterbalanceFollowFrac) + right * (devRight * CounterbalanceLateralFollowFrac);
        }
        private static quaternion ApplyPitchRollCascadeBurst(in quaternion yawBase, in quaternion eyeRot, float pitchFrac, float rollFrac)
        {
            if (pitchFrac <= 0f && rollFrac <= 0f)
            {
                return yawBase;
            }

            float3 headFwd = math.mul(eyeRot, new float3(0f, 0f, 1f));
            float3 headRight = math.mul(eyeRot, new float3(1f, 0f, 0f));

            float horizMag = math.sqrt(headFwd.x * headFwd.x + headFwd.z * headFwd.z);
            float pitchDeg = math.degrees(math.atan2(-headFwd.y, horizMag));

            float rollDeg = math.degrees(math.asin(math.clamp(-headRight.y, -1f, 1f)));

            quaternion swing = quaternion.EulerZXY(math.radians(new float3(pitchDeg * pitchFrac, 0f, rollDeg * rollFrac)));
            return math.mul(yawBase, swing);
        }
        private static float DeltaAngleDeg(float current, float target)
        {
            float delta = target - current;
            delta -= math.floor(delta / 360f) * 360f;
            if (delta > 180f) delta -= 360f;
            return delta;
        }
        [BurstCompile]
        private static void SmoothSlerpBurst(in quaternion current, in quaternion target, float speed, float dt, out quaternion result)
        {
            result = math.slerp(current, target, BasisSmoothingProfiles.FramerateIndependentAlpha(speed, dt));
        }
        [BurstCompile]
        public static void ExtractYawBurst(in quaternion rotation, out quaternion result)
        {
            float4 q = rotation.value;
            float lenSq = q.y * q.y + q.w * q.w;
            if (lenSq < 1e-12f)
            {
                result = quaternion.identity;
                return;
            }
            float inv = math.rsqrt(lenSq);
            result = new quaternion(0f, q.y * inv, 0f, q.w * inv);
        }
        [BurstCompile]
        private static void ComposePosition(in float3 basePos, in quaternion rot, in float3 localOffset, out float3 result)
        {
            result = basePos + math.mul(rot, localOffset);
        }
        [BurstCompile]
        internal static void ComputeHipsPosition( in float3 neckPos, in float3 headPos, in float3 supportXZ, in float3 solveUp, float lenTotal, in quaternion headYaw, float biasScale, in float3 desiredHipsXZ, bool freezeToTpose, in float3 tposeHips, float standingHipsLocalY, float standingHeadLocalY, float trackingLiftY, bool usePostureModel, float compressionStrength, float maxDrop, out float3 result)
        {
            ComputeHipsPosition(in neckPos, in headPos, in supportXZ, in solveUp, lenTotal, in headYaw, biasScale, in desiredHipsXZ, freezeToTpose, in tposeHips, standingHipsLocalY, standingHeadLocalY, trackingLiftY, usePostureModel, compressionStrength, maxDrop, 0f, out result);
        }
        [BurstCompile]
        internal static void ComputeHipsPosition( in float3 neckPos, in float3 headPos, in float3 supportXZ, in float3 solveUp, float lenTotal, in quaternion headYaw, float biasScale, in float3 desiredHipsXZ, bool freezeToTpose, in float3 tposeHips, float standingHipsLocalY, float standingHeadLocalY, float trackingLiftY, bool usePostureModel, float compressionStrength, float maxDrop, float restDropY, out float3 result)
        {
            float standingHipsY = standingHipsLocalY + trackingLiftY;
            float standingHeadY = standingHeadLocalY + trackingLiftY;

            float3 hipsBase = freezeToTpose ? tposeHips + new float3(0f, trackingLiftY, 0f) : neckPos - solveUp * (restDropY > 0f ? restDropY : lenTotal);
            quaternion biasYaw = freezeToTpose ? quaternion.identity : headYaw;
            float3 forwardBias = math.mul(biasYaw, new float3(0f, 0f, 1f)) * biasScale;

            if (freezeToTpose)
            {
                result = hipsBase + forwardBias;
                return;
            }

            float rigidY = hipsBase.y;
            float headDrop = standingHeadY - headPos.y;

            if (usePostureModel && standingHeadLocalY > 1e-3f && headDrop > 0f)
            {
                float3 headXZ = new float3(headPos.x, 0f, headPos.z);
                float lean = math.length(headXZ - new float3(supportXZ.x, 0f, supportXZ.z));

                float d = headDrop / standingHeadLocalY;
                float f = lean / standingHeadLocalY;

                float pelvisDrop = BasisPelvisPostureModel.PelvisDrop(d, f) * standingHeadLocalY;

                hipsBase.y = math.max(standingHipsY - pelvisDrop, rigidY);
            }
            else if (!usePostureModel)
            {
                float drop = standingHipsY - rigidY;
                if (drop > 0f && compressionStrength > 0f && maxDrop > 1e-4f)
                {
                    float softDrop = maxDrop * (1f - math.exp(-drop / maxDrop));
                    hipsBase.y = standingHipsY - math.lerp(drop, softDrop, math.saturate(compressionStrength));
                }
            }

            hipsBase.y = math.min(hipsBase.y, headPos.y - 0.15f * lenTotal);

            result = new float3(desiredHipsXZ.x, hipsBase.y, desiredHipsXZ.z) + forwardBias;
        }
        [BurstCompile]
        internal static void ComputeChainPlacement( in float3 neckPos, in float3 hipsPos, float tChest, float tSpine, in quaternion neckYaw, in quaternion hipsYaw, out float3 chestPos, out float3 spinePos, out quaternion chestYawTarget, out quaternion spineYawTarget)
        {
            chestPos = math.lerp(neckPos, hipsPos, tChest);
            spinePos = math.lerp(neckPos, hipsPos, tSpine);
            chestYawTarget = math.slerp(neckYaw, hipsYaw, tChest);
            spineYawTarget = math.slerp(neckYaw, hipsYaw, tSpine);
        }
        [BurstCompile]
        public static void YawDegrees(in quaternion yawOnly, out float result)
        {
            float3 f = math.mul(yawOnly, new float3(0f, 0f, 1f));
            result = math.degrees(math.atan2(f.x, f.z));
        }
    }
}
