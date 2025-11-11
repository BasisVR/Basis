using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Driver class which takes control of the <see cref="BasisLocalPlayer"/>'s
    /// hips and legs in order to fit them onto a <see cref="BasisSeat"/>.
    /// See also <see cref="BasisLocalVirtualSpineDriver.HipsFreezeToTpose"/>.
    /// </summary>
    [System.Serializable]
    public class BasisLocalSeatDriver
    {
        [System.NonSerialized] public BasisLocalPlayer LocalPlayer;

        private BasisSeat _seat;
        public bool IsSeated { get { return _seat != null; } }

        /// <summary>
        /// Initialize the driver with the owning local player.
        /// </summary>
        public void Initialize(BasisLocalPlayer localPlayer)
        {
            // This could also be accomplished via BasisLocalPlayer.Instance,
            // but passing it in directly makes this class more self-contained.
            LocalPlayer = localPlayer;
        }

        /// <summary>
        /// Sit the local player at the provided seat. This will snap the player to the seat,
        /// including overriding the leg transforms to correctly bend around the seat.
        /// </summary>
        public void Sit(BasisSeat seat)
        {
            if (LocalPlayer == null || seat == null) return;
            // If already seated (using a seat from a seat), stand first.
            if (_seat != null)
            {
                Stand();
            }
            _seat = seat;
            // Disable character movement and add a movement lock so other systems respect being seated.
            BasisLocalVirtualSpineDriver.HipsFreezeToTpose = true;
            LocalPlayer.LocalCharacterDriver.IsEnabled = false;
            LocalPlayer.LocalCharacterDriver.MovementLock.Add(nameof(BasisLocalSeatDriver));
            _setAllOverrideUsages(true);
            LocalPlayer.OnPreSimulateBones += OnSimulate;
            OnSimulate();
        }

        /// <summary>
        /// Releases the player from the seat, re-enabling movement and disabling leg overrides.
        /// </summary>
        public void Stand()
        {
            if (LocalPlayer == null || _seat == null) return;
            BasisLocalVirtualSpineDriver.HipsFreezeToTpose = false;
            LocalPlayer.OnPreSimulateBones -= OnSimulate;
            LocalPlayer.LocalCharacterDriver.MovementLock.Remove(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalCharacterDriver.IsEnabled = true;
            _setAllOverrideUsages(false);
            LocalPlayer.transform.rotation = Quaternion.identity;
            LocalPlayer.AvatarTransform.rotation = Quaternion.identity;
            LocalPlayer.LocalAnimatorDriver.HandleTeleport();
            _seat = null;
        }

        /// <summary>
        /// Every frame, fit the local player to the seat as best as possible.
        /// While BasisSeat contains information calculatable just from the seat,
        /// this function incoporates the data from the player's avatar as well.
        /// </summary>
        private void OnSimulate()
        {
            if (_seat == null) return;
            if (LocalPlayer.LocalCharacterDriver.MovementVector.SqrMagnitude() > 0.25f)
            {
                Stand();
                return;
            }
            Vector3 leftLowerLegOffset = BasisLocalBoneDriver.LeftFootControl.TposeLocalScaled.position - BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.position;
            Vector3 rightLowerLegOffset = BasisLocalBoneDriver.RightFootControl.TposeLocalScaled.position - BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.position;
            Vector3 leftUpperLegOffset = BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.position - BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.position;
            Vector3 rightUpperLegOffset = BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.position - BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.position;
            // Note: This algorithm assumes that the left and right legs are symmetrical.
            // This should be the case on 99.999% of avatars, and solving otherwise is too complex.
            float upperLegLength = leftUpperLegOffset.magnitude;
            float lowerLegLength = leftLowerLegOffset.magnitude;
            float footThickness = Mathf.Max(BasisLocalBoneDriver.LeftFootControl.TposeLocalScaled.position.y, BasisLocalBoneDriver.LeftToeControl.TposeLocalScaled.position.y);
            // TODO: These could be supplied by avatars, or calculated from them, in the future.
            // For example, a character with a big butt should have upperLegBackThickness increased.
            // For now, just estimate these values based on the total leg length.
            float totalLegLength = upperLegLength + lowerLegLength;
            float spineBackThickness = totalLegLength * 0.14f;
            float upperLegBackRadius = totalLegLength * 0.14f;
            float upperLegKneeRadius = totalLegLength * 0.08f;
            float lowerLegKneeRadius = totalLegLength * 0.10f;
            float lowerLegFootRadius = totalLegLength * 0.06f;
            // Calculate targeting information for the seat in the object's local space.
            Vector3 targetFoot = _seat.Foot + (_seat.LowerLegPerp * lowerLegFootRadius) - (_seat.LowerLegDir * footThickness);
            // The IK position for the knees is more complicated. It needs to be adjusted by both the upper leg and
            // lower leg offsets. For perpendicular upper and lower legs, this would be trivial, you just add the
            // vectors together. Otherwise some trigonometry is required. The knee offset can be found by adding the
            // upper leg offset with the upper leg direction multiplied by an adjustment scalar, sliding it forward.
            Vector3 targetKnee = _seat.Knee + (_seat.UpperLegPerp * upperLegKneeRadius) + (_seat.UpperLegDir * _getAdjustmentScalar(
                _seat.LegAngleDegrees, lowerLegKneeRadius, upperLegKneeRadius, upperLegLength));
            // targetBack needs to be similarly adjusted, but with the spine, and fitting within instead of around the seat.
            Vector3 targetBack = _seat.Back + (_seat.UpperLegPerp * upperLegBackRadius) + (_seat.UpperLegDir * _getAdjustmentScalar(
                180.0f - (float)_seat.SpineAngleDegrees, spineBackThickness, upperLegBackRadius, upperLegLength));
            // Calculate the desired upper leg rotations based on the thickness of the legs.
            // Positive numbers here mean the back of the leg near the hips is thicker than near the knee.
            float upperLegAngleVsSeatRadians = Mathf.Asin((upperLegBackRadius - upperLegKneeRadius) / upperLegLength);
            float upperLegAngleVsSpineRadians = upperLegAngleVsSeatRadians + Mathf.Deg2Rad * (float)_seat.SpineAngleDegrees;
            // This code assumes that the hips have a rest T-pose with the local +Y axis pointing up.
            Vector3 targetUpperLegDirRelToHips = new Vector3(0.0f, Mathf.Cos(upperLegAngleVsSpineRadians), Mathf.Sin(upperLegAngleVsSpineRadians));
            Quaternion desiredLeftUpperLegRot = _alignAroundLocalX(
                BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.rotation,
                leftUpperLegOffset,
                targetUpperLegDirRelToHips
            );
            Quaternion desiredRightUpperLegRot = _alignAroundLocalX(
                BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.rotation,
                rightUpperLegOffset,
                targetUpperLegDirRelToHips
            );
            // Adjust the upper leg target points to account for the character's legs being shorter or longer than the seat's ideal leg lengths.
            float upperLegHorizontalTravelRatio = Vector3.Dot(_seat.UpperLegDir,
                    _seat.SpineRotation * desiredLeftUpperLegRot * Vector3.down);
            float availableUpperLegHorizontalTravel = Vector3.Distance(targetKnee - _seat.UpperLegPerp * upperLegKneeRadius, targetBack - _seat.UpperLegPerp * upperLegBackRadius);
            float characterUpperLegHorizontalTravel = upperLegLength * upperLegHorizontalTravelRatio;
            if (characterUpperLegHorizontalTravel < availableUpperLegHorizontalTravel)
            {
                // Characters with shorter upper legs than the seat need to have their back moved forward (closer to the knee).
                targetBack += _seat.UpperLegDir * (availableUpperLegHorizontalTravel - characterUpperLegHorizontalTravel);
            }
            else
            {
                // Characters with longer upper legs than the seat need to have their knee moved forward (to make room).
                targetKnee += _seat.UpperLegDir * (characterUpperLegHorizontalTravel - availableUpperLegHorizontalTravel);
            }
            // Calculate the desired lower leg rotations based on the thickness of the legs.
            float lowerLegAngleVsSeatRadians = Mathf.Asin((lowerLegKneeRadius - lowerLegFootRadius) / lowerLegLength);
            float lowerLegAngleVsSpineRadians = lowerLegAngleVsSeatRadians - Mathf.Deg2Rad * ((float)_seat.SpineAngleDegrees + _seat.LegAngleDegrees);
            Vector3 targetLowerLegDirRelToHips = new Vector3(0.0f, Mathf.Cos(lowerLegAngleVsSpineRadians), -Mathf.Sin(lowerLegAngleVsSpineRadians));
            Quaternion desiredLeftLowerLegRot = _alignAroundLocalX(
                BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.rotation,
                leftLowerLegOffset,
                targetLowerLegDirRelToHips
            );
            Quaternion desiredRightLowerLegRot = _alignAroundLocalX(
                BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.rotation,
                rightLowerLegOffset,
                targetLowerLegDirRelToHips
            );
            // Adjust the lower leg target points to account for the character's legs being shorter or longer than the seat's ideal leg lengths.
            float lowerLegVerticalTravelRatio = Vector3.Dot(_seat.LowerLegDir,
                    _seat.SpineRotation * desiredLeftLowerLegRot * Vector3.down);
            float availableLowerLegVerticalTravel = Vector3.Distance(targetFoot + _seat.LowerLegDir * lowerLegFootRadius, targetKnee + _seat.LowerLegDir * lowerLegKneeRadius);
            float characterLowerLegVerticalTravel = lowerLegLength * lowerLegVerticalTravelRatio;
            Vector3 lowerLegUpwardAdjustment = _seat.LowerLegDir * (availableLowerLegVerticalTravel - characterLowerLegVerticalTravel);
            if (characterLowerLegVerticalTravel < availableLowerLegVerticalTravel)
            {
                // Characters with shorter lower legs than the seat need to have their foot moved up (closer to the knee).
                targetFoot += _seat.LowerLegDir * (characterLowerLegVerticalTravel - availableLowerLegVerticalTravel);
            }
            else
            {
                // Characters with longer lower legs than the seat need to have their knee moved up (to make room).
                targetKnee += _seat.LowerLegDir * (availableLowerLegVerticalTravel - characterLowerLegVerticalTravel);
                if (characterUpperLegHorizontalTravel > availableUpperLegHorizontalTravel)
                {
                    // If both legs are too long, we need to find a new knee point that satisfies both constraints.
                    targetKnee = _closestPointOnSphere(targetKnee, targetFoot, lowerLegLength);
                }
                // Wherever targetKnee ends up, targetBack needs to be exactly upperLegLength away.
                targetBack = _closestPointOnSphere(targetBack, targetKnee, upperLegLength);
            }
            // Re-calculate the desired rotations now that the target points have been adjusted.
            targetUpperLegDirRelToHips = Quaternion.Inverse(_seat.SpineRotation) * (targetKnee - targetBack);
            targetLowerLegDirRelToHips = Quaternion.Inverse(_seat.SpineRotation) * (targetFoot - targetKnee);
            desiredLeftUpperLegRot = _alignAroundLocalX(
                BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.rotation,
                leftUpperLegOffset,
                targetUpperLegDirRelToHips
            );
            desiredRightUpperLegRot = _alignAroundLocalX(
                BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.rotation,
                rightUpperLegOffset,
                targetUpperLegDirRelToHips
            );
            desiredLeftLowerLegRot = _alignAroundLocalX(
                BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.rotation,
                leftLowerLegOffset,
                targetLowerLegDirRelToHips
            );
            desiredRightLowerLegRot = _alignAroundLocalX(
                BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.rotation,
                rightLowerLegOffset,
                targetLowerLegDirRelToHips
            );
            // Apply the calculated leg pose.
            _applyLocalLegPose(
                targetBack,
                desiredLeftUpperLegRot,
                desiredRightUpperLegRot,
                desiredLeftLowerLegRot,
                desiredRightLowerLegRot
            );
        }

        private float _getAdjustmentScalar(float angle, float alignedOffset, float perpOffset, float limit)
        {
            if (angle > 90.001f)
            {
                return Mathf.Min(alignedOffset / Mathf.Sin(angle * Mathf.Deg2Rad) - perpOffset / Mathf.Tan(angle * Mathf.Deg2Rad), limit);
            }
            return Mathf.Min(alignedOffset * Mathf.Sin(angle * Mathf.Deg2Rad), limit);
        }

        private void _applyLocalLegPose(
            Vector3 pelvisPos,
            Quaternion leftUpperLegRot,
            Quaternion rightUpperLegRot,
            Quaternion leftLowerLegRot,
            Quaternion rightLowerLegRot
        )
        {
            // Actually set the transforms of the player. These are in global space
            // so we need to apply the seat's global transform to our local data.
            Quaternion seatQuat = _seat.transform.rotation;
            // For the hips, don't actually move the hip bone relative to the player transform,
            // since that would break stuff like the camera. Instead, offset the player transform itself.
            // Note that, for seating purposes, the point we actually want to align is between the legs ("pelvis").
            Vector3 pelvisWorldPos = _seat.transform.TransformPoint(pelvisPos);
            Quaternion hipsWorldRot = seatQuat * _seat.SpineRotation;
            Quaternion playerRot = hipsWorldRot * Quaternion.Inverse(BasisLocalBoneDriver.HipsControl.TposeLocalScaled.rotation);
            Vector3 playerPelvisLocalPos = 0.5f * (BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.position + BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.position);
            Vector3 playerPos = pelvisWorldPos - playerRot * playerPelvisLocalPos;
            LocalPlayer.transform.SetPositionAndRotation(playerPos, playerRot);
            LocalPlayer.AvatarTransform.SetPositionAndRotation(playerPos, playerRot);
            LocalPlayer.LocalAnimatorDriver.HandleTeleport();
            // Despite the above comment, we do ALSO need to set the hips override to prevent it from rotating.
            LocalPlayer.LocalRigDriver.SetOverrideData(HumanBodyBones.Hips, pelvisWorldPos, hipsWorldRot);
            // Set the leg bone transforms.
            LocalPlayer.LocalRigDriver.SetOverrideData(
                HumanBodyBones.LeftUpperLeg,
                BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.position,
                hipsWorldRot * leftUpperLegRot);
            LocalPlayer.LocalRigDriver.SetOverrideData(
                HumanBodyBones.RightUpperLeg,
                BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.position,
                hipsWorldRot * rightUpperLegRot);
            LocalPlayer.LocalRigDriver.SetOverrideData(
                HumanBodyBones.LeftLowerLeg,
                BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.position,
                hipsWorldRot * leftLowerLegRot);
            LocalPlayer.LocalRigDriver.SetOverrideData(
                HumanBodyBones.RightLowerLeg,
                BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.position,
                hipsWorldRot * rightLowerLegRot);
        }

        /// <summary>
        /// Aligns the local align direction (usually +Y) of the provided quaternion to point as closely as possible
        /// to the provided target direction, by rotating around the quaternion's local X axis.
        /// The returned rotation is parent-relative, it should be applied on the left side of the original quaternion.
        /// </summary>
        private Quaternion _alignAroundLocalX(Quaternion quat, Vector3 localAlign, Vector3 targetNormalized)
        {
            Vector3 x = quat * Vector3.right;
            // Project target onto the local YZ plane with the local +X as the normal vector.
            Vector3 targetProj = targetNormalized - Vector3.Dot(targetNormalized, x) * x;
            if (targetProj.sqrMagnitude < 1e-6f)
            {
                BasisDebug.LogWarning("BasisLocalSeatDriver.AlignYAroundLocalX: Failed to align legs to the seat, the local X axis is not sideways enough.");
                return quat;
            }
            targetProj.Normalize();
            // Project dest onto the same plane.
            Vector3 localAlignProj = localAlign - Vector3.Dot(localAlign, x) * x;
            localAlignProj.Normalize();
            // Find signed angle between current Y and targetProj in the local YZ plane around the local X axis.
            float angle = Mathf.Rad2Deg * Mathf.Atan2(
                Vector3.Dot(Vector3.Cross(localAlignProj, targetProj), x),
                Vector3.Dot(localAlignProj, targetProj)
            );
            // Calculate rotation within a sandwich to allow it to be applied in parent space.
            return quat * Quaternion.AngleAxis(angle, Vector3.right) * Quaternion.Inverse(quat);
        }

        private Vector3 _closestPointOnSphere(Vector3 point, Vector3 sphereCenter, float sphereRadius)
        {
            Vector3 dir = point - sphereCenter;
            dir.Normalize();
            return sphereCenter + dir * sphereRadius;
        }

        private void _setAllOverrideUsages(bool enabled)
        {
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.Hips, enabled);
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.LeftUpperLeg, enabled);
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.RightUpperLeg, enabled);
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.LeftLowerLeg, enabled);
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.RightLowerLeg, enabled);
        }
    }
}
