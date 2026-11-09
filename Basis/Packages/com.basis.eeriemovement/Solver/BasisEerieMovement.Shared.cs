using System.Runtime.CompilerServices;
using Basis.Scripts.Common;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.IK
{
    public partial struct BasisEerieMovement
    {
        public const int torsoLimb = 4, limbCount = 4;
        public static int SlotLimb(int slot)
        {
            switch (slot)
            {
                case 11: case 13: case 15: case 17: return 0;
                case 12: case 14: case 16: case 18: return 1;
                case 1: case 3: case 5: case 19: return 2;
                case 2: case 4: case 6: case 20: return 3;
                default: return torsoLimb;
            }
        }
        void CaptureSlots(NativeArray<Vector3> positions, NativeArray<Quaternion> rotations, int limb)
        {
            for (int i = 0; i < slotPositions.Length && i < positions.Length && i < rotations.Length; i++)
            {
                if ((plan.boundSlots & (1u << i)) == 0 || (limb >= 0 && SlotLimb(i) != limb))
                {
                    continue;
                }
                BasisBoneHandle handle = SlotHandle(i);
                poseStream.GetPositionAndRotation(handle, out Vector3 position, out Quaternion rotation);
                positions[i] = position;
                rotations[i] = rotation;
            }
        }
        bool TorsoOverridden()
        {
            for (int i = 0; i < slotWeights.Length; i++)
            {
                if (slotWeights[i] && SlotLimb(i) == torsoLimb && (plan.boundSlots & (1u << i)) != 0 && (i >= slotBlends.Length || slotBlends[i] > 0f))
                {
                    return true;
                }
            }
            return false;
        }
        void SyncUnoverridden(bool capture)
        {
            if (capture && unoverriddenPositions.IsCreated && unoverriddenRotations.IsCreated)
            {
                solvedPositions.CopyTo(unoverriddenPositions);
                solvedRotations.CopyTo(unoverriddenRotations);
            }
            if (armState.IsCreated && unoverriddenArmState.IsCreated && unoverriddenArmState.Length == armState.Length)
            {
                armState.CopyTo(unoverriddenArmState);
            }
        }
        BasisEerieAnchoredEffector Anchor(int limb)
        {
            switch (limb)
            {
                case 0: return anchorLeftHand;
                case 1: return anchorRightHand;
                case 2: return anchorLeftFoot;
                default: return anchorRightFoot;
            }
        }
        bool Anchored(int limb) => (anchoredLimbs & (1 << limb)) != 0 && Anchor(limb).anchorSlot >= 0;
        int AnchorDependency(int limb)
        {
            if (!Anchored(limb))
            {
                return -1;
            }
            int dep = SlotLimb(Anchor(limb).anchorSlot);
            return dep == torsoLimb || dep == limb ? -1 : dep;
        }
        void GetLimbTarget(int limb, out Vector3 position, out Quaternion rotation)
        {
            switch (limb)
            {
                case 0: position = targetPositionLeftHand; rotation = targetRotationLeftHand; return;
                case 1: position = targetPositionRightHand; rotation = targetRotationRightHand; return;
                case 2: position = targetPositionLeftLowerLeg; rotation = targetRotationLeftLowerLeg; return;
                default: position = targetPositionRightLowerLeg; rotation = targetRotationRightLowerLeg; return;
            }
        }
        void SetLimbTarget(int limb, in Vector3 position, in Quaternion rotation)
        {
            switch (limb)
            {
                case 0: targetPositionLeftHand = position; targetRotationLeftHand = rotation; return;
                case 1: targetPositionRightHand = position; targetRotationRightHand = rotation; return;
                case 2: targetPositionLeftLowerLeg = position; targetRotationLeftLowerLeg = rotation; return;
                default: targetPositionRightLowerLeg = position; targetRotationRightLowerLeg = rotation; return;
            }
        }
        Quaternion LimbTargetOffset(int limb)
        {
            switch (limb)
            {
                case 0: return offsetRotationLeftHand;
                case 1: return offsetRotationRightHand;
                case 2: return offsetRotationLeftFoot;
                default: return offsetRotationRightFoot;
            }
        }
        void HipsFrame(out Vector3 position, out Quaternion rotation)
        {
            if (plan.hasHips && poseStream.IsValid(handleHips))
            {
                poseStream.GetPositionAndRotation(handleHips, out position, out rotation);
                return;
            }
            position = Vector3.zero;
            rotation = Quaternion.identity;
        }
        void StoreAnchorCache()
        {
            if (!anchorCachePositions.IsCreated || !anchorCacheRotations.IsCreated)
            {
                return;
            }
            HipsFrame(out Vector3 hipsPosition, out Quaternion hipsRotation);
            Quaternion inverse = Quaternion.Inverse(hipsRotation);
            for (int i = 0; i < Count && i < anchorCachePositions.Length && i < anchorCacheRotations.Length; i++)
            {
                if ((plan.boundSlots & (1u << i)) == 0)
                {
                    continue;
                }
                poseStream.GetPositionAndRotation(SlotHandle(i), out Vector3 position, out Quaternion rotation);
                anchorCachePositions[i] = inverse * (position - hipsPosition);
                anchorCacheRotations[i] = inverse * rotation;
            }
        }
        void ResolveAnchoredTarget(int limb, int solved, in Vector3 basePosition, in Quaternion baseRotation)
        {
            BasisEerieAnchoredEffector a = Anchor(limb);
            BasisBoneHandle handle = SlotHandle(a.anchorSlot);
            if (!poseStream.IsValid(handle) || (plan.boundSlots & (1u << a.anchorSlot)) == 0)
            {
                return;
            }
            Vector3 anchorPosition;
            Quaternion anchorRotation;
            int dep = SlotLimb(a.anchorSlot);
            if (dep != torsoLimb && (solved & (1 << dep)) == 0 && anchorCacheValid && anchorCachePositions.IsCreated && anchorCacheRotations.IsCreated && a.anchorSlot < anchorCachePositions.Length)
            {
                HipsFrame(out Vector3 hipsPosition, out Quaternion hipsRotation);
                anchorPosition = hipsPosition + hipsRotation * anchorCachePositions[a.anchorSlot];
                anchorRotation = hipsRotation * anchorCacheRotations[a.anchorSlot];
            }
            else
            {
                poseStream.GetPositionAndRotation(handle, out anchorPosition, out anchorRotation);
            }
            Vector3 position = anchorPosition + anchorRotation * a.localPosition;
            Quaternion wanted = anchorRotation * a.localRotation * Quaternion.Inverse(LimbTargetOffset(limb));
            SetLimbTarget(limb, a.blendPosition ? Vector3.Lerp(basePosition, position, a.blend) : position, a.blendRotation ? Quaternion.Slerp(baseRotation, wanted, a.blend) : wanted);
        }
        void SolveLimbsInOrder(bool capture)
        {
            FixedList128Bytes<Vector3> basePositions = default;
            FixedList128Bytes<Quaternion> baseRotations = default;
            for (int limb = 0; limb < limbCount; limb++)
            {
                GetLimbTarget(limb, out Vector3 position, out Quaternion rotation);
                basePositions.Add(position);
                baseRotations.Add(rotation);
                if (limb < armCount && Anchored(limb)) ResolveAnchoredTarget(limb, 0, position, rotation);
            }
            BasisEerieMarkers.Shoulders.Begin();
            SolveShoulderPass();
            BasisEerieMarkers.Shoulders.End();
            BasisSwivelFrame armFrame = BuildArmFrame();
            int solved = 0;
            for (int step = 0; step < limbCount; step++)
            {
                int pick = -1, fallback = -1;
                for (int k = 0; k < limbCount; k++)
                {
                    int limb = k < 2 ? k + 2 : k - 2;
                    if ((solved & (1 << limb)) != 0)
                    {
                        continue;
                    }
                    if (fallback < 0) fallback = limb;
                    int dep = AnchorDependency(limb);
                    if (dep < 0 || (solved & (1 << dep)) != 0)
                    {
                        pick = limb;
                        break;
                    }
                }
                if (pick < 0) pick = fallback;
                if (Anchored(pick)) ResolveAnchoredTarget(pick, solved, basePositions[pick], baseRotations[pick]);
                if (pick < armCount)
                {
                    BasisEerieMarkers.Arms.Begin();
                    SolveArm(pick, armFrame);
                    BasisEerieMarkers.Arms.End();
                }
                else
                {
                    BasisEerieMarkers.Legs.Begin();
                    SolveLeg(pick - armCount);
                    BasisEerieMarkers.Legs.End();
                    BasisEerieMarkers.Toes.Begin();
                    SolveToe(pick == armCount);
                    BasisEerieMarkers.Toes.End();
                }
                if (capture) CaptureSlots(solvedPositions, solvedRotations, pick);
                BasisEerieMarkers.TrackerOverrides.Begin();
                ApplyTrackerOverrides(pick, capture);
                BasisEerieMarkers.TrackerOverrides.End();
                solved |= 1 << pick;
            }
        }
        void SolveUnoverriddenLimbs()
        {
            if (!unoverriddenPositions.IsCreated || !unoverriddenRotations.IsCreated)
            {
                return;
            }
            int n = poseStream.Count;
            bool hasScale = poseStream.LocalScale.IsCreated;
            NativeArray<float3> keptPosition = new NativeArray<float3>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<quaternion> keptRotation = new NativeArray<quaternion>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<float3> keptScale = new NativeArray<float3>(hasScale ? n : 0, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<float3>.Copy(poseStream.LocalPosition, keptPosition, n);
            NativeArray<quaternion>.Copy(poseStream.LocalRotation, keptRotation, n);
            if (hasScale) NativeArray<float3>.Copy(poseStream.LocalScale, keptScale, n);
            BasisEeriePlan keptPlan = plan;
            NativeArray<BasisArmState> keptArmState = armState;
            Vector3 lhp = targetPositionLeftHand, rhp = targetPositionRightHand, llp = targetPositionLeftLowerLeg, rlp = targetPositionRightLowerLeg;
            Quaternion lhr = targetRotationLeftHand, rhr = targetRotationRightHand, llr = targetRotationLeftLowerLeg, rlr = targetRotationRightLowerLeg;
            plan.leftArm = unoverriddenLeftArm;
            plan.rightArm = unoverriddenRightArm;
            plan.leftLeg = unoverriddenLeftLeg;
            plan.rightLeg = unoverriddenRightLeg;
            plan.hasLegState = plan.hasLegDiagnostics = false;
            plan.hasArmState = keptPlan.hasArmState && unoverriddenArmState.IsCreated && unoverriddenArmState.Length >= armCount;
            armState = unoverriddenArmState;
            targetPositionLeftHand = unoverriddenPositionLeftHand;
            targetPositionRightHand = unoverriddenPositionRightHand;
            targetPositionLeftLowerLeg = unoverriddenPositionLeftLowerLeg;
            targetPositionRightLowerLeg = unoverriddenPositionRightLowerLeg;
            targetRotationLeftHand = unoverriddenRotationLeftHand;
            targetRotationRightHand = unoverriddenRotationRightHand;
            targetRotationLeftLowerLeg = unoverriddenRotationLeftLowerLeg;
            targetRotationRightLowerLeg = unoverriddenRotationRightLowerLeg;
            SolveShoulderPass();
            SolveLegPass();
            SolveArmPass();
            SolveToePass();
            CaptureSlots(unoverriddenPositions, unoverriddenRotations, -1);
            plan = keptPlan;
            armState = keptArmState;
            targetPositionLeftHand = lhp;
            targetPositionRightHand = rhp;
            targetPositionLeftLowerLeg = llp;
            targetPositionRightLowerLeg = rlp;
            targetRotationLeftHand = lhr;
            targetRotationRightHand = rhr;
            targetRotationLeftLowerLeg = llr;
            targetRotationRightLowerLeg = rlr;
            NativeArray<float3>.Copy(keptPosition, poseStream.LocalPosition, n);
            NativeArray<quaternion>.Copy(keptRotation, poseStream.LocalRotation, n);
            if (hasScale) NativeArray<float3>.Copy(keptScale, poseStream.LocalScale, n);
            poseStream.InvalidateWorldCache();
            keptPosition.Dispose();
            keptRotation.Dispose();
            keptScale.Dispose();
        }
        void ApplyTrackerOverrides(int limb, bool capture)
        {
            for (int i = 0; i < slotPositions.Length; i++)
            {
                if (!slotWeights[i] || (plan.boundSlots & (1u << i)) == 0 || SlotLimb(i) != limb)
                {
                    continue;
                }
                float blend = i < slotBlends.Length ? slotBlends[i] : 1f;
                if (!(blend > 0f))
                {
                    continue;
                }
                BasisBoneHandle handle = SlotHandle(i);
                bool partial = blend < 1f && capture;
                if (poseStream.TranslationFree.IsCreated && poseStream.TranslationFree[handle.Index] != 0)
                {
                    poseStream.SetPosition(handle, partial ? Vector3.Lerp(solvedPositions[i], slotPositions[i], blend) : slotPositions[i]);
                }
                Quaternion rotation = slotRotations[i] * slotOffsets[i];
                poseStream.SetRotation(handle, partial ? Quaternion.Slerp(solvedRotations[i], rotation, blend) : rotation);
            }
        }
        void ResetToRest(BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip)
        {
            poseStream.ResetToRest(root);
            poseStream.ResetToRest(mid);
            poseStream.ResetToRest(tip);
        }
        public static Quaternion ClampRotation(Quaternion current, Quaternion reference, float maxAngleDeg)
        {
            float angle = Quaternion.Angle(reference, current);
            if (angle <= maxAngleDeg)
            {
                return current;
            }

            float t = maxAngleDeg / Mathf.Max(angle, epsilon);
            return Quaternion.Slerp(reference, current, t);
        }
        public static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float abSqr = Vector3.Dot(ab, ab);
            if (abSqr <= sqrEpsilon)
            {
                return a;
            }

            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / abSqr);
            return a + ab * t;
        }
        public static void SegmentSegmentClosestPoints(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s, out float t, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);

            if (a <= sqrEpsilon && e <= sqrEpsilon)
            {
                s = t = 0.0f; c1 = p1; c2 = p2; return;
            }
            if (a <= sqrEpsilon)
            {
                s = 0.0f; t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= sqrEpsilon)
                {
                    t = 0.0f; s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2), denom = a * e - b * b;

                    if (denom != 0.0f) s = Mathf.Clamp01((b * f - c * e) / denom);
                    else s = 0.0f;

                    t = (b * s + f) / e;
                    if (t < 0.0f) { t = 0.0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1.0f) { t = 1.0f; s = Mathf.Clamp01((b - c) / a); }
                }
            }

            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }
        public static Vector3 CapsuleCapsuleResolve(Vector3 p1, Vector3 q1, float r1, Vector3 p2, Vector3 q2, float r2, Vector3 playerUp)
        {
            SegmentSegmentClosestPoints(p1, q1, p2, q2, out _, out _, out var c1, out var c2);
            Vector3 n = c1 - c2;
            float dSqr = Vector3.Dot(n, n), rSum = r1 + r2;

            if (dSqr >= rSum * rSum) return Vector3.zero;

            Vector3 normal;
            if (dSqr > sqrEpsilon) normal = n / Mathf.Sqrt(dSqr);
            else
            {
                Vector3 axis = (q2 - p2);
                normal = Vector3.Normalize(Vector3.Cross(axis, playerUp));
                if (normal.sqrMagnitude < minMag)
                {
                    normal = Vector3.Normalize(Vector3.Cross(axis, Vector3.right));
                }

                if (normal.sqrMagnitude < minMag)
                {
                    normal = playerUp;
                }
            }

            float d = Mathf.Sqrt(Mathf.Max(dSqr, 0f)), penetration = (rSum - d);
            return normal * penetration;
        }
        public static Vector3 PushOutFromCapsule(Vector3 p, Vector3 a, Vector3 b, float radiusWithSkin, Vector3 playerUp)
        {
            Vector3 q = ClosestPointOnSegment(p, a, b), qp = p - q;
            float dSqr = Vector3.Dot(qp, qp);
            if (dSqr >= radiusWithSkin * radiusWithSkin) return p;
            float d = Mathf.Sqrt(Mathf.Max(dSqr, sqrEpsilon));
            Vector3 n = (d > 0f) ? (qp / d) : playerUp;
            return q + n * radiusWithSkin;
        }
        static float TwistDeg(Quaternion q, Vector3 axis)
        {
            float s = q.x * axis.x + q.y * axis.y + q.z * axis.z, c = q.w;
            if (c < 0f) { s = -s; c = -c; }
            if (!(s * s + c * c > 1e-8f))
            {
                return 0f;
            }

            return 2f * Mathf.Atan2(s, c) * Mathf.Rad2Deg;
        }
    }
}
