using UnityEngine;
using static RalivIKSpine;
namespace Basis.IK
{
    public partial struct BasisEerieMovement
    {
        public void SolveSpinePass()
        {
            BasisEerieMarkers.SpineHipsPlacement.Begin();

            if (plan.prone)
            {
                if (plan.hasHips && plan.hasHead)
                {
                    Vector3 up = playerUp, hipsPos = poseStream.GetPosition(handleHips);
                    Vector3 headPos = poseStream.GetPosition(handleHead), bodyFwd = headPos - hipsPos;
                    bodyFwd -= up * Vector3.Dot(bodyFwd, up);
                    Vector3 desiredFwd = targetRotationHips * Vector3.forward;
                    desiredFwd -= up * Vector3.Dot(desiredFwd, up);
                    if (bodyFwd.sqrMagnitude < sqrEpsilon || desiredFwd.sqrMagnitude < sqrEpsilon)
                    {
                        return;
                    }

                    float deltaYaw = Vector3.SignedAngle(bodyFwd, desiredFwd, up);
                    Quaternion swing = Quaternion.AngleAxis(deltaYaw, up);
                    Vector3 toTarget = targetPositionHead - headPos;
                    toTarget -= up * Vector3.Dot(toTarget, up);
                    poseStream.SetPosition(handleHips, headPos + swing * (hipsPos - headPos) + toTarget);
                    poseStream.SetRotation(handleHips, swing * poseStream.GetRotation(handleHips));
                }
            }
            else
            {
                if (!plan.hasSpineChain)
                {
                    return;
                }
                for (int Index = chainHeadToSpine.Length - 1; Index >= 0; Index--)
                {
                    poseStream.ResetToRest(chainHeadToSpine[Index]);
                }
                Vector3 headTargetPos = targetPositionHead, hipsTargetPos = targetPositionHips;
                Quaternion hipsTargetRot = targetRotationHips;
                Quaternion offsetHips = offsetRotationHips, hipDesired = hipsTargetRot * offsetHips;
                Vector3 up = playerUp;
                float crouchFade = 1f;
                if (plan.crouchOffset)
                {
                    hipsTargetPos = ApplyCrouchBodyOffset(headTargetPos, hipsTargetPos, hipDesired, up, crouchFade);
                }
                targetPositionHips = hipsTargetPos;
                if (plan.hasHips)
                {
                    poseStream.SetPosition(handleHips, hipsTargetPos);
                    poseStream.SetRotation(handleHips, hipDesired);
                }
            }
            BasisEerieMarkers.SpineHipsPlacement.End();
            if (plan.hasSpineChain && (plan.chestChain || plan.headChain))
            {
                BasisEerieMarkers.SpineSequentialIK.Begin();
                SolveRalivSpineIK(targetPositionHead, targetRotationHead * offsetRotationHead);
                BasisEerieMarkers.SpineSequentialIK.End();
            }
        }
        void SolveRalivSpineIK(Vector3 headTargetPosition, Quaternion headTargetRotation)
        {
            int count = chainHeadToSpine.Length;
            if (count < 2)
            {
                return;
            }
            if (SpineData.length <= epsilon || SpineData.positions.Length != count)
            {
                InitalizeRalivSpineIK();
            }
            if (SpineData.length <= epsilon || SpineData.positions.Length != count)
            {
                return;
            }
            SpineData.hipTargetPosition = plan.hasHips ? poseStream.GetPosition(handleHips) : SpineData.positions[0];
            SpineData.hipTargetRotation = plan.hasHips ? poseStream.GetRotation(handleHips) : SpineData.rotations[0];
            SpineData.headTargetPosition = headTargetPosition;
            SpineData.headTargetRotation = headTargetRotation;
            int chestIndex = count - 1 - plan.chestIdx;
            bool hasTrackedChest = plan.chestTracked && plan.chestChain && chestIndex > 0 && chestIndex < count - 1;
            SpineData.chestIndex = chestIndex;
            SpineData.chestTargetPosition = targetPositionChest;
            SpineData.chestTargetRotation = targetRotationChest * offsetRotationChest;
            SpineData.chestForward = Vector3.forward;
            SpineData.chestHintWeight = hasTrackedChest ? 1f : 0f;
            RalivIKSpine.SolveSpine(ref SpineData);
            BasisBoneHandle rootHandle = chainHeadToSpine[count - 1];
            poseStream.SetPosition(rootHandle, SpineData.positions[0]);
            for (int index = 0; index < count; index++)
            {
                BasisBoneHandle handle = chainHeadToSpine[count - 1 - index];
                poseStream.SetRotation(handle, SpineData.rotations[index]);
            }

            BasisBoneHandle headHandle = chainHeadToSpine[0];
            Vector3 headCorrection = headTargetPosition - poseStream.GetPosition(headHandle);
            if (headCorrection.sqrMagnitude > sqrEpsilon)
            {
                poseStream.SetPosition(rootHandle, poseStream.GetPosition(rootHandle) + headCorrection);
            }
        }
        public void InitalizeRalivSpineIK()
        {
            int count = chainHeadToSpine.Length;
            if (count < 2 || !poseStream.LocalPosition.IsCreated)
            {
                return;
            }
            SpineData = default;
            SpineData.positions.Length = count;
            SpineData.rotations.Length = count;
            SpineData.restPositions.Length = count;
            SpineData.restRotations.Length = count;
            SpineData.t.Length = count;
            SpineData.targetSpinePositions.Length = count;
            SpineData.hipTargetAlignedSpinePositions.Length = count;
            SpineData.headTargetAlignedSpinePositions.Length = count;

            float hiplessSpineLength = 0f;
            for (int index = 0; index < count; index++)
            {
                BasisBoneHandle handle = chainHeadToSpine[count - 1 - index];
                if (!poseStream.IsValid(handle))
                {
                    SpineData = default;
                    return;
                }
                poseStream.GetPositionAndRotation(handle, out Vector3 position, out Quaternion rotation);
                SpineData.positions[index] = position;
                SpineData.rotations[index] = rotation;
                SpineData.restPositions[index] = position;
                SpineData.restRotations[index] = rotation;

                // Match SpineTest: do not include the hips-to-first-spine-bone
                // segment in the interpolation length.
                if (index > 1)
                {
                    hiplessSpineLength += (SpineData.restPositions[index] - SpineData.restPositions[index - 1]).magnitude;
                }
                SpineData.t[index] = hiplessSpineLength;
            }

            if (hiplessSpineLength <= epsilon)
            {
                SpineData = default;
                return;
            }
            SpineData.length = hiplessSpineLength;
            for (int index = 0; index < count; index++)
            {
                SpineData.t[index] = SpineData.t[index] / hiplessSpineLength * 0.8f;
            }

            SpineData.hipTargetPosition = SpineData.positions[0];
            SpineData.hipTargetRotation = SpineData.rotations[0];
            SpineData.headTargetPosition = SpineData.positions[count - 1];
            SpineData.headTargetRotation = SpineData.rotations[count - 1];
            SpineData.chestTargetPosition = SpineData.positions[Mathf.Clamp(count - 1 - chainChestIdx, 0, count - 1)];
            SpineData.chestForward = Vector3.forward;
        }

        void RescaleRalivSpineIK(float scale)
        {
            int count = SpineData.restPositions.Length;
            if (count >= 2 && SpineData.positions.Length == count && scale > 0f && !float.IsNaN(scale) && !float.IsInfinity(scale))
            {
                Vector3 restRoot = SpineData.restPositions[0];
                Vector3 workingRoot = SpineData.positions[0];
                for (int index = 1; index < count; index++)
                {
                    SpineData.restPositions[index] = restRoot + (SpineData.restPositions[index] - restRoot) * scale;
                    SpineData.positions[index] = workingRoot + (SpineData.positions[index] - workingRoot) * scale;
                }
                SpineData.length *= scale;
            }
        }
        Vector3 ApplyCrouchBodyOffset(Vector3 headTargetPos, Vector3 hipsPos, Quaternion hipsRot, Vector3 playerUpDir, float fade)
        {
            BasisCrouchOffsetInput input;
            input.HeadTargetPos = headTargetPos;
            input.HipsPos = hipsPos;
            input.HipsRot = hipsRot;
            input.Bind = offsetRotationHips;
            input.PlayerUp = playerUpDir;
            input.Factor = moveBodyBackWhenCrouching;
            input.RestDist = minHeadSpineHeight;
            input.CrouchDepth = crouchDepth;
            input.StandingHeadHeight = standingHeadHeight;
            input.Fade = fade;
            BasisCrouchOffsetCore.Solve(input, out BasisCrouchOffsetResult result);
            return result.HipsPos;
        }
    }
}
