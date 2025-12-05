using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
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

        // Player-specific pose values calculated when the player sits in a seat.
        private Vector3 leftLowerLegOffset;
        private Vector3 rightLowerLegOffset;
        private Vector3 leftUpperLegOffset;
        private Vector3 rightUpperLegOffset;
        private float footThickness;
        private float upperLegLength;
        private float lowerLegLength;
        private float totalLegLength;
        private float spineBackThickness;
        private float upperLegBackRadius;
        private float upperLegKneeRadius;
        private float lowerLegKneeRadius;
        private float lowerLegFootRadius;
        private float upperLegAngleVsSeatRadians;
        private float lowerLegAngleVsSeatRadians;

        // Player-specific non-pose values to keep track of state during seating.
        private Vector3 previousRelativePosition = Vector3.zero;
        private float previousHeadPitchGlobal = 0.0f;
        private float previousHeadYawVsSeat = 0.0f;
        private bool hasEvent = false;

        public bool DoesSeatingBlockLocalDesktopEyePitch()
        {
            if (_seat == null)
            {
                return false; // Pitch is allowed if not seated.
            }
            return false;
        }

        public bool DoesSeatingBlockLocalDesktopEyeYaw()
        {
            if (_seat == null)
            {
                return true; // Yaw is not allowed if not seated (handled by player rotation instead).
            }
            return false;
        }

        private void GrabLatestTposeLocalScaleData()
        {
            leftLowerLegOffset = BasisLocalBoneDriver.LeftFootControl.TposeLocalScaled.position - BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.position;
            rightLowerLegOffset = BasisLocalBoneDriver.RightFootControl.TposeLocalScaled.position - BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.position;
            leftUpperLegOffset = BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.position - BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.position;
            rightUpperLegOffset = BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.position - BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.position;
            footThickness = Mathf.Max(BasisLocalBoneDriver.LeftFootControl.TposeLocalScaled.position.y, BasisLocalBoneDriver.LeftToeControl.TposeLocalScaled.position.y);
            // Note: This algorithm assumes that the left and right legs are symmetrical.
            // This should be the case on 99.999% of avatars, and solving otherwise is too complex.
            upperLegLength = leftUpperLegOffset.magnitude;
            lowerLegLength = leftLowerLegOffset.magnitude;
            // TODO: These could be supplied by avatars, or calculated from them, in the future.
            // For example, a character with a big butt should have upperLegBackThickness increased.
            // For now, just estimate these values based on the total leg length.
            totalLegLength = upperLegLength + lowerLegLength;
            spineBackThickness = totalLegLength * 0.14f;
            upperLegBackRadius = totalLegLength * 0.14f;
            upperLegKneeRadius = totalLegLength * 0.08f;
            lowerLegKneeRadius = totalLegLength * 0.10f;
            lowerLegFootRadius = totalLegLength * 0.06f;
            // Calculate the desired upper leg rotations based on the thickness of the legs.
            upperLegAngleVsSeatRadians = Mathf.Asin((upperLegBackRadius - upperLegKneeRadius) / upperLegLength);
            // Calculate the desired lower leg rotations based on the thickness of the legs.
            lowerLegAngleVsSeatRadians = Mathf.Asin((lowerLegKneeRadius - lowerLegFootRadius) / lowerLegLength);
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
            // Offset the player's position/rotation to match the seat and keep track of the
            // old position/rotation so that it can be restored later when exiting the seat.
            previousRelativePosition = _seat.transform.InverseTransformPoint(LocalPlayer.transform.position);
            if (BasisDesktopEye.Instance != null)
            {
                previousHeadPitchGlobal = BasisDesktopEye.Instance.rotationPitch;
                previousHeadYawVsSeat = BasisDesktopEye.Instance.rotationYaw - (_seat.transform.rotation * _seat.SpineRotation).eulerAngles.y;
            }
            if (BasisDeviceManagement.Instance.FindDevice(out BasisInput Input, TransformBinders.BoneControl.BasisBoneTrackedRole.CenterEye))
            {
                Vector3 offset = -Input.ScaledDeviceCoord.position;
                offset.y = 0.0f;
                Quaternion rot = Input.ScaledDeviceCoord.rotation;
                rot.x = 0.0f;
                rot.z = 0.0f;
                rot.Normalize();
                BasisInput.OffsetCoords = new Common.BasisCalibratedCoords(offset, Quaternion.Inverse(rot));
            }
            // Disable character movement and add a movement lock so other systems respect being seated.
            BasisLocalVirtualSpineDriver.HipsFreezeToTpose = true;
            LocalPlayer.LocalCharacterDriver.IsEnabled = false;
            LocalPlayer.LocalCharacterDriver.MovementLock.Add(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalCharacterDriver.CrouchingLock.Add(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalAnimatorDriver.StopAllVariables();
            LocalPlayer.LocalAnimatorDriver.PauseAnimator = true;
            if (BasisDesktopEye.Instance != null)
            {
                // Set the player's relative yaw to zero to face forward on the seat.
                BasisDesktopEye.Instance.rotationYaw = 0.0f;
                // Only do the same for pitch if the seat needs to block or consume pitch.
                if (DoesSeatingBlockLocalDesktopEyePitch())
                {
                    BasisDesktopEye.Instance.rotationPitch = 0.0f;
                }
            }
            _setAllOverrideUsages(true);
            LocalPlayer.OnPreSimulateBones += OnSimulate;
            GrabLatestTposeLocalScaleData();
            if (hasEvent == false)
            {
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame += GrabLatestTposeLocalScaleData;
                hasEvent = true;
            }
            OnSimulate();
        }

        /// <summary>
        /// Releases the player from the seat, re-enabling movement and disabling leg overrides.
        /// </summary>
        public void Stand()
        {
            if (LocalPlayer == null || _seat == null)
            {
                return;
            }

            LocalPlayer.LocalAnimatorDriver.PauseAnimator = false;
            _seat.OnExitSeat();
            BasisLocalVirtualSpineDriver.HipsFreezeToTpose = false;
            LocalPlayer.OnPreSimulateBones -= OnSimulate;
            LocalPlayer.LocalCharacterDriver.MovementLock.Remove(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalCharacterDriver.CrouchingLock.Remove(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalCharacterDriver.IsEnabled = true;
            BasisInput.OffsetCoords = new Common.BasisCalibratedCoords(Vector3.zero, Quaternion.identity);
            _setAllOverrideUsages(false);
            if (BasisDesktopEye.Instance != null)
            {
                BasisDesktopEye.Instance.rotationPitch = previousHeadPitchGlobal;
                BasisDesktopEye.Instance.rotationYaw = previousHeadYawVsSeat + (_seat.transform.rotation * _seat.SpineRotation).eulerAngles.y;
            }
            LocalPlayer.transform.SetPositionAndRotation(_seat.transform.TransformPoint(previousRelativePosition), Quaternion.identity);
            LocalPlayer.AvatarTransform.rotation = Quaternion.identity;
            LocalPlayer.LocalAnimatorDriver.HandleTeleport();
            GrabLatestTposeLocalScaleData();
            if (hasEvent)
            {
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= GrabLatestTposeLocalScaleData;
                hasEvent = false;
            }
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


            // Calculate targeting information for the seat in the object's local space.
            Vector3 targetFoot = _seat.Foot + (_seat.LowerLegPerp * lowerLegFootRadius) - (_seat.LowerLegDir * footThickness);
            // The IK position for the knees is more complicated. It needs to be adjusted by both the upper leg and
            // lower leg offsets. For perpendicular upper and lower legs, this would be trivial, you just add the
            // vectors together. Otherwise some trigonometry is required. The knee offset can be found by adding the
            // upper leg offset with the upper leg direction multiplied by an adjustment scalar, sliding it forward.
            Vector3 targetKnee = _seat.Knee + (_seat.UpperLegPerp * upperLegKneeRadius) + (_seat.UpperLegDir * BasisSeat.GetAdjustmentScalar(_seat.LegAngleDegrees, lowerLegKneeRadius, upperLegKneeRadius, upperLegLength));
            // targetBack needs to be similarly adjusted, but with the spine, and fitting within instead of around the seat.
            Vector3 targetBack = _seat.Back + (_seat.UpperLegPerp * upperLegBackRadius) + (_seat.UpperLegDir * BasisSeat.GetAdjustmentScalar(180.0f - (float)_seat.SpineAngleDegrees, spineBackThickness, upperLegBackRadius, upperLegLength));
            // Positive numbers here mean the back of the leg near the hips is thicker than near the knee.
            float upperLegAngleVsSpineRadians = upperLegAngleVsSeatRadians + Mathf.Deg2Rad * (float)_seat.SpineAngleDegrees;
            // This code assumes that the hips have a rest T-pose with the local +Y axis pointing up.
            Vector3 targetUpperLegDirRelToHips = new Vector3(0.0f, Mathf.Cos(upperLegAngleVsSpineRadians), Mathf.Sin(upperLegAngleVsSpineRadians));
            Quaternion desiredLeftUpperLegRot = BasisSeat.AlignAroundLocalX(
                BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.rotation,
                leftUpperLegOffset,
                targetUpperLegDirRelToHips
            );
            Quaternion desiredRightUpperLegRot = BasisSeat.AlignAroundLocalX(
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
            float lowerLegAngleVsSpineRadians = lowerLegAngleVsSeatRadians - Mathf.Deg2Rad * ((float)_seat.SpineAngleDegrees + _seat.LegAngleDegrees);
            Vector3 targetLowerLegDirRelToHips = new Vector3(0.0f, Mathf.Cos(lowerLegAngleVsSpineRadians), -Mathf.Sin(lowerLegAngleVsSpineRadians));
            Quaternion desiredLeftLowerLegRot = BasisSeat.AlignAroundLocalX(
                BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.rotation,
                leftLowerLegOffset,
                targetLowerLegDirRelToHips
            );
            Quaternion desiredRightLowerLegRot = BasisSeat.AlignAroundLocalX(
                BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.rotation,
                rightLowerLegOffset,
                targetLowerLegDirRelToHips
            );
            // Adjust the lower leg target points to account for the character's legs being shorter or longer than the seat's ideal leg lengths.
            float lowerLegVerticalTravelRatio = Vector3.Dot(_seat.LowerLegDir,
                    _seat.SpineRotation * desiredLeftLowerLegRot * Vector3.down);
            float availableLowerLegVerticalTravel = Vector3.Distance(targetFoot + _seat.LowerLegDir * lowerLegFootRadius, targetKnee + _seat.LowerLegDir * lowerLegKneeRadius);
            float characterLowerLegVerticalTravel = lowerLegLength * lowerLegVerticalTravelRatio;

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
                    targetKnee = BasisSeat.ClosestPointOnSphere(targetKnee, targetFoot, lowerLegLength);
                }
                // Wherever targetKnee ends up, targetBack needs to be exactly upperLegLength away.
                targetBack = BasisSeat.ClosestPointOnSphere(targetBack, targetKnee, upperLegLength);
            }
            // Re-calculate the desired rotations now that the target points have been adjusted.
            targetUpperLegDirRelToHips = Quaternion.Inverse(_seat.SpineRotation) * (targetKnee - targetBack);
            targetLowerLegDirRelToHips = Quaternion.Inverse(_seat.SpineRotation) * (targetFoot - targetKnee);
            desiredLeftUpperLegRot = BasisSeat.AlignAroundLocalX(
                BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.rotation,
                leftUpperLegOffset,
                targetUpperLegDirRelToHips
            );
            desiredRightUpperLegRot = BasisSeat.AlignAroundLocalX(
                BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.rotation,
                rightUpperLegOffset,
                targetUpperLegDirRelToHips
            );
            desiredLeftLowerLegRot = BasisSeat.AlignAroundLocalX(
                BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.rotation,
                leftLowerLegOffset,
                targetLowerLegDirRelToHips
            );
            desiredRightLowerLegRot = BasisSeat.AlignAroundLocalX(
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
           //dont need todo this LocalPlayer.AvatarTransform.SetPositionAndRotation(playerPos, playerRot);
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
