using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using GatorDragonGames.JigglePhysics;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Driver class which takes control of the <see cref="BasisLocalPlayer"/>'s
    /// hips and legs in order to fit them onto a <see cref="BasisSeat"/>.
    /// </summary>
    [System.Serializable]
    public class BasisLocalSeatDriver
    {
        [System.NonSerialized] public BasisLocalPlayer LocalPlayer;

        private BasisSeat _seat;
        public bool IsSeated => _seat != null;

        public void Initialize(BasisLocalPlayer localPlayer) => LocalPlayer = localPlayer;

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

        // Per-avatar stable hips basis (from T-pose positions)
        private Quaternion avatarHipsBasisTpose = Quaternion.identity;

        // State during seating
        private Vector3 previousRelativePosition = Vector3.zero;
        private float previousHeadPitchGlobal = 0.0f;
        private float previousHeadYawVsSeat = 0.0f;
        private Vector3 lastSeatRootPosition = Vector3.zero;
        private Quaternion lastSeatRootRotation = Quaternion.identity;
        private bool hasLastSeatRootPosition = false;

        public bool UseDefaultMasking = true;
        public LayerMask GroundMask;
        public LayerMask BlockingMask;
        public float maxDownProbe = 3.0f;
        public float maxUpProbe = 1.0f;

        private bool hasEvent = false;

        private void GrabLatestTposeLocalScaleData(BasisHeightDriver.HeightModeChange HeightModeChange)
        {
            leftLowerLegOffset =
                BasisLocalBoneDriver.LeftFootControl.TposeLocalScaled.position -
                BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.position;

            rightLowerLegOffset =
                BasisLocalBoneDriver.RightFootControl.TposeLocalScaled.position -
                BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.position;

            leftUpperLegOffset =
                BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.position -
                BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.position;

            rightUpperLegOffset =
                BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.position -
                BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.position;

            footThickness = Mathf.Max(
                BasisLocalBoneDriver.LeftFootControl.TposeLocalScaled.position.y,
                BasisLocalBoneDriver.LeftToeControl.TposeLocalScaled.position.y
            );

            upperLegLength = leftUpperLegOffset.magnitude;
            lowerLegLength = leftLowerLegOffset.magnitude;
            totalLegLength = upperLegLength + lowerLegLength;

            spineBackThickness = totalLegLength * 0.14f;
            upperLegBackRadius = totalLegLength * 0.14f;
            upperLegKneeRadius = totalLegLength * 0.08f;
            lowerLegKneeRadius = totalLegLength * 0.10f;
            lowerLegFootRadius = totalLegLength * 0.06f;

            float upperArg = (upperLegBackRadius - upperLegKneeRadius) / Mathf.Max(upperLegLength, 1e-6f);
            float lowerArg = (lowerLegKneeRadius - lowerLegFootRadius) / Mathf.Max(lowerLegLength, 1e-6f);
            upperLegAngleVsSeatRadians = Mathf.Asin(Mathf.Clamp(upperArg, -0.9999f, 0.9999f));
            lowerLegAngleVsSeatRadians = Mathf.Asin(Mathf.Clamp(lowerArg, -0.9999f, 0.9999f));

            var mapping = BasisLocalAvatarDriver.Mapping;
            avatarHipsBasisTpose = BuildAvatarHipsBasisFromTpose(mapping.AvatarForwards, mapping.AvatarUpwards, mapping.AvatarRightwards);
        }

        public void Sit(BasisSeat seat)
        {
            if (LocalPlayer == null || seat == null)
                return;

            if (_seat != null)
                Stand();

            _seat = seat;

            LocalPlayer.transform.GetPositionAndRotation(out lastSeatRootPosition, out lastSeatRootRotation);
            hasLastSeatRootPosition = true;

            previousRelativePosition = _seat.transform.InverseTransformPoint(LocalPlayer.transform.position);

            if (BasisDesktopEye.Instance != null)
            {
                previousHeadPitchGlobal = BasisDesktopEye.Instance.rotationPitch;
                previousHeadYawVsSeat = BasisDesktopEye.Instance.rotationYaw - (_seat.transform.rotation * _seat.SpineRotation).eulerAngles.y;
            }

            if (BasisDeviceManagement.Instance.FindDevice(out BasisInput input, TransformBinders.BoneControl.BasisBoneTrackedRole.CenterEye))
            {
                Quaternion eyeRot = YawOnly(input.UnscaledDeviceCoord.rotation);
                BasisInput.OffsetCoords.rotation = Quaternion.Inverse(eyeRot);

                if (BasisDeviceManagement.IsCurrentModeVR())
                {
                    // Rotate negated device position by the calculated rotation offset to bring it into the correct space
                    BasisInput.OffsetCoords.position = BasisInput.OffsetCoords.rotation * -input.ScaledDeviceCoord.position;

                    // The need for spine height here is confusing
                    // Avatar height changes and playspace movement seem to interact in negative ways that will require further investigation
                    var spineHeight = BasisLocalBoneDriver.EyeControl.TposeLocalScaled.position.y - BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position.y;
                    //    BasisInput.OffsetCoords.position.y = BasisLocalBoneDriver.EyeControl.TposeLocalScaled.position.y - input.UnscaledDeviceCoord.position.y + spineHeight;
                    BasisInput.OffsetCoords.position.y = 0;//revisit later
                }
                else
                {
                    BasisInput.OffsetCoords.position = Vector3.zero;
                }
            }

            LocalPlayer.LocalVirtualSpineDriver.HipsFreezeToTpose = true;
            LocalPlayer.LocalCharacterDriver.IsEnabled = false;
            LocalPlayer.LocalCharacterDriver.MovementLock.Add(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalCharacterDriver.CrouchingLock.Add(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalAnimatorDriver.StopAllVariables();
            LocalPlayer.LocalAnimatorDriver.PauseAnimator = true;

            SetAllOverrideUsages(true);
            LocalPlayer.OnVirtualData += OnSimulate;

            GrabLatestTposeLocalScaleData( BasisHeightDriver.HeightModeChange.OnTpose);

            if (!hasEvent)
            {
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame += GrabLatestTposeLocalScaleData;
                hasEvent = true;
            }

            OnSimulate();
        }

        private static Quaternion YawOnly(Quaternion q)
        {
            var e = q.eulerAngles;
            return Quaternion.Euler(0f, e.y, 0f);
        }

        public void Stand()
        {
            if (hasEvent)
            {
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= GrabLatestTposeLocalScaleData;
                hasEvent = false;
            }

            if (LocalPlayer == null)
                return;

            LocalPlayer.LocalVirtualSpineDriver.HipsFreezeToTpose = false;
            LocalPlayer.LocalAnimatorDriver.PauseAnimator = false;
            LocalPlayer.OnVirtualData -= OnSimulate;
            hasLastSeatRootPosition = false;
            LocalPlayer.LocalCharacterDriver.MovementLock.Remove(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalCharacterDriver.CrouchingLock.Remove(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalCharacterDriver.IsEnabled = true;
            BasisInput.OffsetCoords = new Common.BasisCalibratedCoords(Vector3.zero, Quaternion.identity);

            SetAllOverrideUsages(false);

            var cc = BasisLocalPlayer.Instance.LocalCharacterDriver.characterController;

            if (_seat == null)
                return;

            _seat.OnExitSeat(LocalPlayer);

            if (BasisDesktopEye.Instance != null)
            {
                BasisDesktopEye.Instance.rotationPitch = previousHeadPitchGlobal;
                BasisDesktopEye.Instance.rotationYaw = previousHeadYawVsSeat + (_seat.transform.rotation * _seat.SpineRotation).eulerAngles.y;
            }

            Vector3 desiredPos = _seat.transform.TransformPoint(previousRelativePosition);

            if (BasisSafeTeleportUtil.TryFindSafeStandingPosition(
                    desiredPos, cc.radius, cc.height, cc.skinWidth,
                    GroundMask, BlockingMask,
                    maxDownProbe, maxUpProbe,
                    out Vector3 safePos))
            {
                LocalPlayer.Teleport(safePos, Quaternion.identity, true);
            }
            else
            {
                BasisDebug.LogWarning("No safe exit position found for seat.");
                LocalPlayer.Teleport(LocalPlayer.transform.position, Quaternion.identity, true);
            }

            _seat = null;
        }

        private void OnSimulate()
        {
            if (_seat == null)
                return;

            const float kMinDot = 0.05f;
            const float kMaxBackShift = 0.25f;
            const float kSphereSnapEpsilon = 0.005f;

            // 1) Initial seat-fit target points (seat-local)
            Vector3 targetFoot = _seat.Foot
                                 + (_seat.LowerLegPerp * lowerLegFootRadius)
                                 - (_seat.LowerLegDir * footThickness);

            Vector3 targetKnee = _seat.Knee
                                 + (_seat.UpperLegPerp * upperLegKneeRadius)
                                 + (_seat.UpperLegDir * BasisSeat.GetAdjustmentScalar(
                                     _seat.LegAngleDegrees,
                                     lowerLegKneeRadius,
                                     upperLegKneeRadius,
                                     upperLegLength));

            Vector3 targetBack = _seat.Back
                                 + (_seat.UpperLegPerp * upperLegBackRadius)
                                 + (_seat.UpperLegDir * BasisSeat.GetAdjustmentScalar(
                                     Mathf.Clamp((float)_seat.SpineAngleDegrees, 10f, 170f),
                                     spineBackThickness,
                                     upperLegBackRadius,
                                     upperLegLength));

            Vector3 preferredBack = targetBack;

            // --- POLE (knee plane) setup ---
            // Define pole in seat hips frame, then map to avatar-local using hips basis.
            Vector3 desiredPoleInSeatHipsFrame = (Vector3.forward + Vector3.up * 0.20f).normalized;
            Vector3 desiredPoleAvatarLocal = (avatarHipsBasisTpose * desiredPoleInSeatHipsFrame).normalized;
            desiredPoleAvatarLocal = EnsureForwardHemisphereInAvatarBasis(desiredPoleAvatarLocal);

            // A stable "knee axis hint" in avatar-local: use hips-basis forward as "knees forward".
            Vector3 poleAxisHintAvatarLocal = (avatarHipsBasisTpose * Vector3.forward).normalized;

            // 2) Upper leg desired rotations (analytic)
            float upperLegAngleVsSpineRadians = upperLegAngleVsSeatRadians + Mathf.Deg2Rad * (float)_seat.SpineAngleDegrees;

            Vector3 targetUpperLegDirRelToHips = avatarHipsBasisTpose * new Vector3(
                0.0f,
                -Mathf.Cos(upperLegAngleVsSpineRadians),
                Mathf.Sin(upperLegAngleVsSpineRadians)
            );
            targetUpperLegDirRelToHips = EnsureForwardHemisphereInAvatarBasis(targetUpperLegDirRelToHips);

            Quaternion desiredLeftUpperLegRot = AlignAimWithPole(
                BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.rotation,
                leftUpperLegOffset,
                targetUpperLegDirRelToHips,
                poleAxisHintAvatarLocal,
                desiredPoleAvatarLocal
            );

            Quaternion desiredRightUpperLegRot = AlignAimWithPole(
                BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.rotation,
                rightUpperLegOffset,
                targetUpperLegDirRelToHips,
                poleAxisHintAvatarLocal,
                desiredPoleAvatarLocal
            );

            // 3) Upper leg length mismatch adjustment (clamped)
            float upperLegHorizontalTravelRatio = Vector3.Dot(
                _seat.UpperLegDir,
                _seat.SpineRotation * desiredLeftUpperLegRot * Vector3.down
            );
            upperLegHorizontalTravelRatio = Mathf.Max(kMinDot, Mathf.Abs(upperLegHorizontalTravelRatio));

            float availableUpperLegHorizontalTravel = Vector3.Distance(
                targetKnee - _seat.UpperLegPerp * upperLegKneeRadius,
                targetBack - _seat.UpperLegPerp * upperLegBackRadius
            );

            float characterUpperLegHorizontalTravel = upperLegLength * upperLegHorizontalTravelRatio;

            if (characterUpperLegHorizontalTravel < availableUpperLegHorizontalTravel)
            {
                float delta = (availableUpperLegHorizontalTravel - characterUpperLegHorizontalTravel);
                delta = Mathf.Min(delta, kMaxBackShift);
                targetBack += _seat.UpperLegDir * delta;
            }
            else
            {
                targetKnee += _seat.UpperLegDir * (characterUpperLegHorizontalTravel - availableUpperLegHorizontalTravel);
            }

            targetBack = preferredBack + Vector3.ClampMagnitude(targetBack - preferredBack, kMaxBackShift);

            // 4) Lower leg desired rotations (analytic)
            float lowerLegAngleVsSpineRadians = lowerLegAngleVsSeatRadians
                                                - Mathf.Deg2Rad * ((float)_seat.SpineAngleDegrees + _seat.LegAngleDegrees);

            Vector3 targetLowerLegDirRelToHips = avatarHipsBasisTpose * new Vector3(
                0.0f,
                -Mathf.Cos(lowerLegAngleVsSpineRadians),
                -Mathf.Sin(lowerLegAngleVsSpineRadians)
            );
            targetLowerLegDirRelToHips = EnsureForwardHemisphereInAvatarBasis(targetLowerLegDirRelToHips);

            // Calves: aim + pole too (helps reduce sideways shin twist)
            Quaternion desiredLeftLowerLegRot = AlignAimWithPole(
                BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.rotation,
                leftLowerLegOffset,
                targetLowerLegDirRelToHips,
                poleAxisHintAvatarLocal,
                desiredPoleAvatarLocal
            );

            Quaternion desiredRightLowerLegRot = AlignAimWithPole(
                BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.rotation,
                rightLowerLegOffset,
                targetLowerLegDirRelToHips,
                poleAxisHintAvatarLocal,
                desiredPoleAvatarLocal
            );

            // 5) Lower leg mismatch adjustment
            float lowerLegVerticalTravelRatio = Vector3.Dot(
                _seat.LowerLegDir,
                _seat.SpineRotation * desiredLeftLowerLegRot * Vector3.down
            );
            lowerLegVerticalTravelRatio = Mathf.Max(kMinDot, Mathf.Abs(lowerLegVerticalTravelRatio));

            float availableLowerLegVerticalTravel = Vector3.Distance(
                targetFoot + _seat.LowerLegDir * lowerLegFootRadius,
                targetKnee + _seat.LowerLegDir * lowerLegKneeRadius
            );

            float characterLowerLegVerticalTravel = lowerLegLength * lowerLegVerticalTravelRatio;

            if (characterLowerLegVerticalTravel < availableLowerLegVerticalTravel)
            {
                targetFoot += _seat.LowerLegDir * (characterLowerLegVerticalTravel - availableLowerLegVerticalTravel);
            }
            else
            {
                targetKnee += _seat.LowerLegDir * (availableLowerLegVerticalTravel - characterLowerLegVerticalTravel);

                if (characterUpperLegHorizontalTravel > availableUpperLegHorizontalTravel)
                {
                    float calfErr = Mathf.Abs(Vector3.Distance(targetKnee, targetFoot) - lowerLegLength);
                    if (calfErr > kSphereSnapEpsilon)
                        targetKnee = BasisSeat.ClosestPointOnSphere(targetKnee, targetFoot, lowerLegLength);
                }

                float thighErr = Mathf.Abs(Vector3.Distance(targetBack, targetKnee) - upperLegLength);
                if (thighErr > kSphereSnapEpsilon)
                {
                    Vector3 snappedBack = BasisSeat.ClosestPointOnSphere(targetBack, targetKnee, upperLegLength);
                    targetBack = preferredBack + Vector3.ClampMagnitude(snappedBack - preferredBack, kMaxBackShift);

                    float thighErrAfterClamp = Mathf.Abs(Vector3.Distance(targetBack, targetKnee) - upperLegLength);
                    if (thighErrAfterClamp > (kSphereSnapEpsilon * 4f))
                        targetBack = snappedBack;
                }
            }

            // 6) Recompute target directions and rotations from final targets
            Vector3 upperDirInSeatHipsFrame = Quaternion.Inverse(_seat.SpineRotation) * (targetKnee - targetBack);
            Vector3 lowerDirInSeatHipsFrame = Quaternion.Inverse(_seat.SpineRotation) * (targetFoot - targetKnee);

            targetUpperLegDirRelToHips = avatarHipsBasisTpose * upperDirInSeatHipsFrame;
            targetLowerLegDirRelToHips = avatarHipsBasisTpose * lowerDirInSeatHipsFrame;

            targetUpperLegDirRelToHips = EnsureForwardHemisphereInAvatarBasis(targetUpperLegDirRelToHips);
            targetLowerLegDirRelToHips = EnsureForwardHemisphereInAvatarBasis(targetLowerLegDirRelToHips);

            desiredLeftUpperLegRot = AlignAimWithPole(
                BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.rotation,
                leftUpperLegOffset,
                targetUpperLegDirRelToHips,
                poleAxisHintAvatarLocal,
                desiredPoleAvatarLocal
            );

            desiredRightUpperLegRot = AlignAimWithPole(
                BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.rotation,
                rightUpperLegOffset,
                targetUpperLegDirRelToHips,
                poleAxisHintAvatarLocal,
                desiredPoleAvatarLocal
            );

            desiredLeftLowerLegRot = AlignAimWithPole(
                BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.rotation,
                leftLowerLegOffset,
                targetLowerLegDirRelToHips,
                poleAxisHintAvatarLocal,
                desiredPoleAvatarLocal
            );

            desiredRightLowerLegRot = AlignAimWithPole(
                BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.rotation,
                rightLowerLegOffset,
                targetLowerLegDirRelToHips,
                poleAxisHintAvatarLocal,
                desiredPoleAvatarLocal
            );

            ApplyLocalLegPose(
                targetBack,
                targetFoot,
                desiredLeftUpperLegRot,
                desiredRightUpperLegRot,
                desiredLeftLowerLegRot,
                desiredRightLowerLegRot
            );
        }

        private static Quaternion BuildAvatarHipsBasisFromTpose(Vector3 forwardsLocal, Vector3 upwardsLocal, Vector3 rightsLocal)
        {
            Vector3 hips = BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position;
            Vector3 lHip = BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.position;
            Vector3 rHip = BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.position;

            // UP
            Vector3 up = upwardsLocal;
            if (up.sqrMagnitude < 1e-8f)
            {
                Vector3 upTarget = hips;

                if (BasisLocalBoneDriver.SpineControl != null)
                    upTarget = BasisLocalBoneDriver.SpineControl.TposeLocalScaled.position;
                else if (BasisLocalBoneDriver.ChestControl != null)
                    upTarget = BasisLocalBoneDriver.ChestControl.TposeLocalScaled.position;
                else if (BasisLocalBoneDriver.HeadControl != null)
                    upTarget = BasisLocalBoneDriver.HeadControl.TposeLocalScaled.position;
                else
                    upTarget = hips + Vector3.up;

                up = upTarget - hips;
            }

            if (up.sqrMagnitude < 1e-8f)
                return Quaternion.identity;
            up.Normalize();

            // RIGHT
            Vector3 right = rightsLocal;
            if (right.sqrMagnitude < 1e-8f)
                right = (rHip - lHip);

            right = Vector3.ProjectOnPlane(right, up);
            if (right.sqrMagnitude < 1e-8f)
                return Quaternion.identity;
            right.Normalize();

            // FORWARD
            Vector3 forward = Vector3.Cross(right, up);
            if (forward.sqrMagnitude < 1e-8f)
                return Quaternion.identity;
            forward.Normalize();

            // Hemisphere disambiguation
            Vector3 hintForward = forwardsLocal;
            if (hintForward.sqrMagnitude > 1e-8f)
            {
                hintForward = Vector3.ProjectOnPlane(hintForward, up);
                if (hintForward.sqrMagnitude > 1e-8f)
                {
                    hintForward.Normalize();
                    if (Vector3.Dot(forward, hintForward) < 0f)
                    {
                        forward = -forward;
                        right = -right;
                    }
                }
            }
            else
            {
                if (BasisLocalBoneDriver.LeftToeControl != null && BasisLocalBoneDriver.RightToeControl != null &&
                    BasisLocalBoneDriver.LeftFootControl != null && BasisLocalBoneDriver.RightFootControl != null)
                {
                    Vector3 toesMid =
                        (BasisLocalBoneDriver.LeftToeControl.TposeLocalScaled.position +
                         BasisLocalBoneDriver.RightToeControl.TposeLocalScaled.position) * 0.5f;

                    Vector3 feetMid =
                        (BasisLocalBoneDriver.LeftFootControl.TposeLocalScaled.position +
                         BasisLocalBoneDriver.RightFootControl.TposeLocalScaled.position) * 0.5f;

                    Vector3 toeDir = Vector3.ProjectOnPlane(toesMid - feetMid, up);
                    if (toeDir.sqrMagnitude > 1e-8f)
                    {
                        toeDir.Normalize();
                        if (Vector3.Dot(forward, toeDir) < 0f)
                        {
                            forward = -forward;
                            right = -right;
                        }
                    }
                }
            }

            right = Vector3.Cross(up, forward).normalized;
            forward = Vector3.Cross(right, up).normalized;

            return Quaternion.LookRotation(forward, up);
        }

        /// <summary>
        /// Keeps requested directions in the avatar hips basis "forward hemisphere"
        /// to reduce backwards leg flips.
        /// </summary>
        private Vector3 EnsureForwardHemisphereInAvatarBasis(Vector3 dirAvatarLocal)
        {
            Vector3 dirInBasis = Quaternion.Inverse(avatarHipsBasisTpose) * dirAvatarLocal;
            if (dirInBasis.z < 0f)
                dirInBasis.z = -dirInBasis.z;
            return (avatarHipsBasisTpose * dirInBasis).normalized;
        }

        /// <summary>
        /// Aim + Pole: swing to desired direction, then twist around that direction
        /// so a pole axis aligns with desired pole (prevents sideways knees).
        /// All vectors are in avatar-local space.
        /// </summary>
        private static Quaternion AlignAimWithPole(
            Quaternion tposeLocalRot,
            Vector3 tposeOffsetUpperToLower,
            Vector3 desiredDirAvatarLocal,
            Vector3 poleAxisHintAvatarLocal,
            Vector3 desiredPoleAvatarLocal)
        {
            if (desiredDirAvatarLocal.sqrMagnitude < 1e-8f)
                return tposeLocalRot;

            Vector3 desiredDir = desiredDirAvatarLocal.normalized;

            // --- Swing (aim) ---
            Vector3 offsetDir = (tposeOffsetUpperToLower.sqrMagnitude > 1e-8f)
                ? tposeOffsetUpperToLower.normalized
                : Vector3.down;

            Vector3 aimedNow = tposeLocalRot * offsetDir;
            if (aimedNow.sqrMagnitude < 1e-8f)
                return tposeLocalRot;

            aimedNow.Normalize();

            Quaternion swing = Quaternion.FromToRotation(aimedNow, desiredDir);
            Quaternion rotAfterSwing = swing * tposeLocalRot;

            // --- Twist (pole) ---
            Vector3 currentPole = rotAfterSwing * poleAxisHintAvatarLocal;
            currentPole = Vector3.ProjectOnPlane(currentPole, desiredDir);

            Vector3 desiredPole = Vector3.ProjectOnPlane(desiredPoleAvatarLocal, desiredDir);

            if (currentPole.sqrMagnitude < 1e-8f || desiredPole.sqrMagnitude < 1e-8f)
                return rotAfterSwing;

            currentPole.Normalize();
            desiredPole.Normalize();

            float signedAngle = Vector3.SignedAngle(currentPole, desiredPole, desiredDir);
            Quaternion twist = Quaternion.AngleAxis(signedAngle, desiredDir);

            return twist * rotAfterSwing;
        }

        private void ApplyLocalLegPose(
            Vector3 pelvisSeatLocal,
            Vector3 footSeatLocal,
            Quaternion leftUpperLegRot,
            Quaternion rightUpperLegRot,
            Quaternion leftLowerLegRot,
            Quaternion rightLowerLegRot)
        {
            Transform seatT = _seat.transform;

            // Seat targets in world
            Vector3 pelvisWorldPos = seatT.TransformPoint(pelvisSeatLocal);

            // Seat-authored hips orientation in world
            Quaternion hipsWorldRot = seatT.rotation * _seat.SpineRotation;

            // Avatar T-pose hips pivot in avatar-local
            Vector3 hipsLocalPos = BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position;

            // Stable avatar hips basis
            Quaternion avatarHipsBasis = avatarHipsBasisTpose;

            // Place avatar root
            Quaternion playerRot = hipsWorldRot * Quaternion.Inverse(avatarHipsBasis);
            Vector3 playerPos = pelvisWorldPos - (playerRot * hipsLocalPos);

            LocalPlayer.transform.SetPositionAndRotation(playerPos, playerRot);
            LocalPlayer.LocalAnimatorDriver.HandleTeleport();

            if (hasLastSeatRootPosition)
            {
                Vector3 rootDelta = playerPos - lastSeatRootPosition;
                bool rotated = Mathf.Abs(Quaternion.Dot(playerRot, lastSeatRootRotation)) < 0.9999999f;
                if (rootDelta.sqrMagnitude > 0f || rotated)
                {
                    Quaternion rotationDelta = playerRot * Quaternion.Inverse(lastSeatRootRotation);
                    var jiggleRigs = BasisLocalAvatarDriver.JiggleRigs;
                    for (int Index = 0; Index < jiggleRigs.Length; Index++)
                    {
                        JiggleRig rig = jiggleRigs[Index];
                        if (rig != null)
                        {
                            rig.Teleport(rotationDelta, lastSeatRootPosition, rootDelta);
                        }
                    }
                    LocalPlayer.BasisLocalFootDriver?.Teleport(rootDelta);
                }
            }
            lastSeatRootPosition = playerPos;
            lastSeatRootRotation = playerRot;
            hasLastSeatRootPosition = true;

            // Local->world helper for T-pose points (after root placement)
            Vector3 ToWorld(Vector3 tposeLocalPos) => playerPos + playerRot * tposeLocalPos;

            // --- Compute left/right foot seat-local targets ---
            Vector3 lFootLocal = BasisLocalBoneDriver.LeftFootControl.TposeLocalScaled.position;
            Vector3 rFootLocal = BasisLocalBoneDriver.RightFootControl.TposeLocalScaled.position;

            Vector3 lFootRelHips = lFootLocal - hipsLocalPos;
            Vector3 rFootRelHips = rFootLocal - hipsLocalPos;

            Vector3 lFootRelInBasis = Quaternion.Inverse(avatarHipsBasis) * lFootRelHips;
            Vector3 rFootRelInBasis = Quaternion.Inverse(avatarHipsBasis) * rFootRelHips;

            Vector3 seatRightLocal = _seat.SpineRotation * Vector3.right;

            Vector3 leftFootSeatLocal = footSeatLocal + seatRightLocal * lFootRelInBasis.x;
            Vector3 rightFootSeatLocal = footSeatLocal + seatRightLocal * rFootRelInBasis.x;

            Vector3 leftFootWorldTarget = seatT.TransformPoint(leftFootSeatLocal);
            Vector3 rightFootWorldTarget = seatT.TransformPoint(rightFootSeatLocal);

            // --- World positions for overridden bones ---
            Vector3 hipsW = ToWorld(BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position);
            Vector3 lUpperW = ToWorld(BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.position);
            Vector3 rUpperW = ToWorld(BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.position);
            Vector3 lLowerW = ToWorld(BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.position);
            Vector3 rLowerW = ToWorld(BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.position);

            // --- Apply overrides ---
            LocalPlayer.LocalRigDriver.SetOverrideData(HumanBodyBones.Hips, hipsW, hipsWorldRot);

            LocalPlayer.LocalRigDriver.SetOverrideData(HumanBodyBones.LeftUpperLeg, lUpperW, hipsWorldRot * leftUpperLegRot);
            LocalPlayer.LocalRigDriver.SetOverrideData(HumanBodyBones.RightUpperLeg, rUpperW, hipsWorldRot * rightUpperLegRot);
            LocalPlayer.LocalRigDriver.SetOverrideData(HumanBodyBones.LeftLowerLeg, lLowerW, hipsWorldRot * leftLowerLegRot);
            LocalPlayer.LocalRigDriver.SetOverrideData(HumanBodyBones.RightLowerLeg, rLowerW, hipsWorldRot * rightLowerLegRot);

            // Feet/toes targets
            LocalPlayer.LocalRigDriver.SetOverrideData(HumanBodyBones.LeftFoot, leftFootWorldTarget, hipsWorldRot);
            LocalPlayer.LocalRigDriver.SetOverrideData(HumanBodyBones.RightFoot, rightFootWorldTarget, hipsWorldRot);

            Vector3 lToesW = ToWorld(BasisLocalBoneDriver.LeftToeControl.TposeLocalScaled.position);
            Vector3 rToesW = ToWorld(BasisLocalBoneDriver.RightToeControl.TposeLocalScaled.position);

            LocalPlayer.LocalRigDriver.SetOverrideData(HumanBodyBones.LeftToes, lToesW, hipsWorldRot);
            LocalPlayer.LocalRigDriver.SetOverrideData(HumanBodyBones.RightToes, rToesW, hipsWorldRot);
        }

        private void SetAllOverrideUsages(bool enabled)
        {
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.Hips, enabled);
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.LeftUpperLeg, enabled);
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.RightUpperLeg, enabled);
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.LeftLowerLeg, enabled);
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.RightLowerLeg, enabled);

            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.LeftFoot, enabled);
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.RightFoot, enabled);
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.LeftToes, enabled);
            LocalPlayer.LocalRigDriver.SetOverrideUsage(HumanBodyBones.RightToes, enabled);
        }

        // =============================
        // Debug Gizmos
        // =============================
        [Header("Seat Gizmo Debug")]
        public bool DebugDrawGizmos = true;
        public float DebugPointRadius = 0.03f;
        public float DebugAxisLength = 0.12f;

        public void UpdateSeatGizmos(bool show, bool showLabels, Vector3 cameraPos)
        {
            EnsureSeatGizmoHook();
            if (!show || !DebugDrawGizmos || LocalPlayer == null || _seat == null)
            {
                SetSeatGizmosVisible(false);
                return;
            }

            GrabLatestTposeLocalScaleData( BasisHeightDriver.HeightModeChange.OnTpose);

            const float kMinDot = 0.05f;
            const float kMaxBackShift = 0.25f;
            const float kSphereSnapEpsilon = 0.005f;

            Vector3 targetFoot = _seat.Foot
                                 + (_seat.LowerLegPerp * lowerLegFootRadius)
                                 - (_seat.LowerLegDir * footThickness);

            Vector3 targetKnee = _seat.Knee
                                 + (_seat.UpperLegPerp * upperLegKneeRadius)
                                 + (_seat.UpperLegDir * BasisSeat.GetAdjustmentScalar(
                                     _seat.LegAngleDegrees,
                                     lowerLegKneeRadius,
                                     upperLegKneeRadius,
                                     upperLegLength));

            Vector3 targetBack = _seat.Back
                                 + (_seat.UpperLegPerp * upperLegBackRadius)
                                 + (_seat.UpperLegDir * BasisSeat.GetAdjustmentScalar(
                                     Mathf.Clamp((float)_seat.SpineAngleDegrees, 10f, 170f),
                                     spineBackThickness,
                                     upperLegBackRadius,
                                     upperLegLength));

            Vector3 preferredBack = targetBack;

            Vector3 desiredPoleInSeatHipsFrame = (Vector3.forward + Vector3.up * 0.20f).normalized;
            Vector3 desiredPoleAvatarLocal = (avatarHipsBasisTpose * desiredPoleInSeatHipsFrame).normalized;
            desiredPoleAvatarLocal = EnsureForwardHemisphereInAvatarBasis(desiredPoleAvatarLocal);

            Vector3 poleAxisHintAvatarLocal = (avatarHipsBasisTpose * Vector3.forward).normalized;

            float upperLegAngleVsSpineRadians =
                upperLegAngleVsSeatRadians + Mathf.Deg2Rad * (float)_seat.SpineAngleDegrees;

            Vector3 targetUpperLegDirRelToHips = avatarHipsBasisTpose * new Vector3(
                0.0f,
                -Mathf.Cos(upperLegAngleVsSpineRadians),
                Mathf.Sin(upperLegAngleVsSpineRadians)
            );
            targetUpperLegDirRelToHips = EnsureForwardHemisphereInAvatarBasis(targetUpperLegDirRelToHips);

            Quaternion desiredLeftUpperLegRot = AlignAimWithPole(
                BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.rotation,
                leftUpperLegOffset,
                targetUpperLegDirRelToHips,
                poleAxisHintAvatarLocal,
                desiredPoleAvatarLocal
            );

            float upperLegHorizontalTravelRatio = Vector3.Dot(
                _seat.UpperLegDir,
                _seat.SpineRotation * desiredLeftUpperLegRot * Vector3.down
            );
            upperLegHorizontalTravelRatio = Mathf.Max(kMinDot, Mathf.Abs(upperLegHorizontalTravelRatio));

            float availableUpperLegHorizontalTravel = Vector3.Distance(
                targetKnee - _seat.UpperLegPerp * upperLegKneeRadius,
                targetBack - _seat.UpperLegPerp * upperLegBackRadius
            );

            float characterUpperLegHorizontalTravel = upperLegLength * upperLegHorizontalTravelRatio;

            if (characterUpperLegHorizontalTravel < availableUpperLegHorizontalTravel)
            {
                float delta = (availableUpperLegHorizontalTravel - characterUpperLegHorizontalTravel);
                delta = Mathf.Min(delta, kMaxBackShift);
                targetBack += _seat.UpperLegDir * delta;
            }
            else
            {
                targetKnee += _seat.UpperLegDir * (characterUpperLegHorizontalTravel - availableUpperLegHorizontalTravel);
            }

            targetBack = preferredBack + Vector3.ClampMagnitude(targetBack - preferredBack, kMaxBackShift);

            float lowerLegAngleVsSpineRadians = lowerLegAngleVsSeatRadians
                                                - Mathf.Deg2Rad * ((float)_seat.SpineAngleDegrees + _seat.LegAngleDegrees);

            Vector3 targetLowerLegDirRelToHips = avatarHipsBasisTpose * new Vector3(
                0.0f,
                -Mathf.Cos(lowerLegAngleVsSpineRadians),
                -Mathf.Sin(lowerLegAngleVsSpineRadians)
            );
            targetLowerLegDirRelToHips = EnsureForwardHemisphereInAvatarBasis(targetLowerLegDirRelToHips);

            Quaternion desiredLeftLowerLegRot = AlignAimWithPole(
                BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.rotation,
                leftLowerLegOffset,
                targetLowerLegDirRelToHips,
                poleAxisHintAvatarLocal,
                desiredPoleAvatarLocal
            );

            float lowerLegVerticalTravelRatio = Vector3.Dot(
                _seat.LowerLegDir,
                _seat.SpineRotation * desiredLeftLowerLegRot * Vector3.down
            );
            lowerLegVerticalTravelRatio = Mathf.Max(kMinDot, Mathf.Abs(lowerLegVerticalTravelRatio));

            float availableLowerLegVerticalTravel = Vector3.Distance(
                targetFoot + _seat.LowerLegDir * lowerLegFootRadius,
                targetKnee + _seat.LowerLegDir * lowerLegKneeRadius
            );

            float characterLowerLegVerticalTravel = lowerLegLength * lowerLegVerticalTravelRatio;

            if (characterLowerLegVerticalTravel < availableLowerLegVerticalTravel)
            {
                targetFoot += _seat.LowerLegDir * (characterLowerLegVerticalTravel - availableLowerLegVerticalTravel);
            }
            else
            {
                targetKnee += _seat.LowerLegDir * (availableLowerLegVerticalTravel - characterLowerLegVerticalTravel);

                if (characterUpperLegHorizontalTravel > availableUpperLegHorizontalTravel)
                {
                    float calfErr = Mathf.Abs(Vector3.Distance(targetKnee, targetFoot) - lowerLegLength);
                    if (calfErr > kSphereSnapEpsilon)
                        targetKnee = BasisSeat.ClosestPointOnSphere(targetKnee, targetFoot, lowerLegLength);
                }

                float thighErr = Mathf.Abs(Vector3.Distance(targetBack, targetKnee) - upperLegLength);
                if (thighErr > kSphereSnapEpsilon)
                {
                    Vector3 snappedBack = BasisSeat.ClosestPointOnSphere(targetBack, targetKnee, upperLegLength);
                    targetBack = preferredBack + Vector3.ClampMagnitude(snappedBack - preferredBack, kMaxBackShift);

                    float thighErrAfterClamp = Mathf.Abs(Vector3.Distance(targetBack, targetKnee) - upperLegLength);
                    if (thighErrAfterClamp > (kSphereSnapEpsilon * 4f))
                        targetBack = snappedBack;
                }
            }

            Transform seatT = _seat.transform;
            Vector3 backW = seatT.TransformPoint(targetBack);
            Vector3 kneeW = seatT.TransformPoint(targetKnee);
            Vector3 footW = seatT.TransformPoint(targetFoot);

            Quaternion seatWorldRot = seatT.rotation;
            Quaternion hipsWorldRot = seatT.rotation * _seat.SpineRotation;

            Vector3 hipsLocalPos = BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position;
            Quaternion playerRot = hipsWorldRot * Quaternion.Inverse(avatarHipsBasisTpose);
            Vector3 playerPos = backW - (playerRot * hipsLocalPos);

            Vector3 ToWorld(Vector3 tposeLocalPos) => playerPos + playerRot * tposeLocalPos;

            Vector3 hipsW = ToWorld(BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position);
            Vector3 lUpperW = ToWorld(BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.position);
            Vector3 rUpperW = ToWorld(BasisLocalBoneDriver.RightUpperLegControl.TposeLocalScaled.position);
            Vector3 lLowerW = ToWorld(BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.position);
            Vector3 rLowerW = ToWorld(BasisLocalBoneDriver.RightLowerLegControl.TposeLocalScaled.position);
            Vector3 lFootW = ToWorld(BasisLocalBoneDriver.LeftFootControl.TposeLocalScaled.position);
            Vector3 rFootW = ToWorld(BasisLocalBoneDriver.RightFootControl.TposeLocalScaled.position);

            EnsureSeatGizmosCreated();

            UpdateSeatAxes(0, seatT.position, seatWorldRot, DebugAxisLength * 0.75f);
            UpdateSeatAxes(3, seatT.position, hipsWorldRot, DebugAxisLength * 0.95f);

            UpdateSeatPoint(0, backW, DebugPointRadius * 1.2f * 2f);
            UpdateSeatPoint(1, kneeW, DebugPointRadius * 2f);
            UpdateSeatPoint(2, footW, DebugPointRadius * 2f);

            UpdateSeatSegment(0, backW, kneeW);
            UpdateSeatSegment(1, kneeW, footW);

            UpdateSeatAxes(6, backW, hipsWorldRot, DebugAxisLength);
            UpdateSeatAxes(9, playerPos, playerRot, DebugAxisLength * 0.75f);

            UpdateSeatSegment(2, lUpperW, lLowerW);
            UpdateSeatSegment(3, rUpperW, rLowerW);
            UpdateSeatSegment(4, lLowerW, lFootW);
            UpdateSeatSegment(5, rLowerW, rFootW);

            UpdateSeatLabel(0, backW, "Seat Back (pelvis target)", showLabels, cameraPos);
            UpdateSeatLabel(1, kneeW, "Seat Knee target", showLabels, cameraPos);
            UpdateSeatLabel(2, footW, "Seat Foot target", showLabels, cameraPos);
            UpdateSeatLabel(3, playerPos, "Avatar Root (placed)", showLabels, cameraPos);
            UpdateSeatLabel(4, hipsW, "Hips (override)", showLabels, cameraPos);

            _seatGizmosVisible = true;
        }

        private const float SeatGizmoLineWidth = 0.004f;
        private static readonly int[] _seatAxisIds = NewSeatIds(12);
        private static readonly int[] _seatPointIds = NewSeatIds(3);
        private static readonly int[] _seatSegIds = NewSeatIds(6);
        private static readonly int[] _seatLabelIds = NewSeatIds(5);
        private static bool _seatGizmosCreated;
        private static bool _seatGizmosVisible;
        private static bool _seatGizmoHooked;

        private static readonly Color SeatAxisXColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        private static readonly Color SeatAxisYColor = new Color(0.2f, 1f, 0.2f, 0.9f);
        private static readonly Color SeatAxisZColor = new Color(0.2f, 0.4f, 1f, 0.9f);
        private static readonly Color SeatPointColor = new Color(1f, 0.9f, 0.2f, 0.9f);
        private static readonly Color SeatSegColor = new Color(0.9f, 0.9f, 0.9f, 0.9f);

        private static void EnsureSeatGizmosCreated()
        {
            if (_seatGizmosCreated)
            {
                return;
            }
            for (int i = 0; i < 12; i++)
            {
                Color c = (i % 3) == 0 ? SeatAxisXColor : (i % 3) == 1 ? SeatAxisYColor : SeatAxisZColor;
                BasisGizmoManager.CreateLineGizmo($"SeatAxis_{i}", out _seatAxisIds[i], Vector3.zero, Vector3.zero, SeatGizmoLineWidth, c);
                BasisGizmoManager.SetGizmoActive(_seatAxisIds[i], false);
            }
            for (int i = 0; i < 3; i++)
            {
                BasisGizmoManager.CreateSphereGizmo($"SeatPoint_{i}", out _seatPointIds[i], Vector3.zero, 0.06f, SeatPointColor);
                BasisGizmoManager.SetGizmoActive(_seatPointIds[i], false);
            }
            for (int i = 0; i < 6; i++)
            {
                BasisGizmoManager.CreateLineGizmo($"SeatSeg_{i}", out _seatSegIds[i], Vector3.zero, Vector3.zero, SeatGizmoLineWidth, SeatSegColor);
                BasisGizmoManager.SetGizmoActive(_seatSegIds[i], false);
            }
            _seatGizmosCreated = true;
            _seatGizmosVisible = true;
        }

        private static void UpdateSeatAxes(int baseIdx, Vector3 pos, Quaternion rot, float len)
        {
            BasisGizmoManager.UpdateLineGizmo(_seatAxisIds[baseIdx + 0], pos, pos + rot * Vector3.right * len);
            BasisGizmoManager.UpdateLineGizmo(_seatAxisIds[baseIdx + 1], pos, pos + rot * Vector3.up * len);
            BasisGizmoManager.UpdateLineGizmo(_seatAxisIds[baseIdx + 2], pos, pos + rot * Vector3.forward * len);
            BasisGizmoManager.SetGizmoActive(_seatAxisIds[baseIdx + 0], true);
            BasisGizmoManager.SetGizmoActive(_seatAxisIds[baseIdx + 1], true);
            BasisGizmoManager.SetGizmoActive(_seatAxisIds[baseIdx + 2], true);
        }

        private static void UpdateSeatPoint(int idx, Vector3 pos, float diameter)
        {
            BasisGizmoManager.UpdateSphereGizmo(_seatPointIds[idx], pos, Vector3.one * diameter);
            BasisGizmoManager.SetGizmoActive(_seatPointIds[idx], true);
        }

        private static void UpdateSeatSegment(int idx, Vector3 a, Vector3 b)
        {
            BasisGizmoManager.UpdateLineGizmo(_seatSegIds[idx], a, b);
            BasisGizmoManager.SetGizmoActive(_seatSegIds[idx], true);
        }

        private static void UpdateSeatLabel(int idx, Vector3 pos, string text, bool showLabels, Vector3 cameraPos)
        {
            if (showLabels)
            {
                if (_seatLabelIds[idx] <= 0)
                {
                    BasisGizmoManager.CreateTextGizmo($"SeatLabel_{idx}", out _seatLabelIds[idx], pos, text, Color.white);
                }
                Quaternion rot = BasisGizmoManager.BillboardRotation(pos, cameraPos);
                BasisGizmoManager.UpdateTextGizmo(_seatLabelIds[idx], pos, rot, 0.02f * Mathf.Max(0.01f, BasisHeightDriver.ScaledToMatchValue), text, Color.white);
                BasisGizmoManager.SetGizmoActive(_seatLabelIds[idx], true);
            }
            else if (_seatLabelIds[idx] > 0)
            {
                BasisGizmoManager.DestroyGizmo(_seatLabelIds[idx]);
                _seatLabelIds[idx] = -1;
            }
        }

        private static void SetSeatGizmosVisible(bool visible)
        {
            if (!_seatGizmosCreated || _seatGizmosVisible == visible)
            {
                return;
            }
            for (int i = 0; i < _seatAxisIds.Length; i++)
            {
                BasisGizmoManager.SetGizmoActive(_seatAxisIds[i], visible);
            }
            for (int i = 0; i < _seatPointIds.Length; i++)
            {
                BasisGizmoManager.SetGizmoActive(_seatPointIds[i], visible);
            }
            for (int i = 0; i < _seatSegIds.Length; i++)
            {
                BasisGizmoManager.SetGizmoActive(_seatSegIds[i], visible);
            }
            for (int i = 0; i < _seatLabelIds.Length; i++)
            {
                if (_seatLabelIds[i] > 0)
                {
                    BasisGizmoManager.SetGizmoActive(_seatLabelIds[i], visible);
                }
            }
            _seatGizmosVisible = visible;
        }

        private static void EnsureSeatGizmoHook()
        {
            if (_seatGizmoHooked)
            {
                return;
            }
            BasisGizmoManager.OnUseGizmosChanged += OnSeatGizmoMasterToggleChanged;
            _seatGizmoHooked = true;
        }

        private static void OnSeatGizmoMasterToggleChanged(bool state)
        {
            if (!state)
            {
                ResetSeatGizmoState();
            }
        }

        private static void ResetSeatGizmoState()
        {
            for (int i = 0; i < _seatAxisIds.Length; i++) _seatAxisIds[i] = -1;
            for (int i = 0; i < _seatPointIds.Length; i++) _seatPointIds[i] = -1;
            for (int i = 0; i < _seatSegIds.Length; i++) _seatSegIds[i] = -1;
            for (int i = 0; i < _seatLabelIds.Length; i++) _seatLabelIds[i] = -1;
            _seatGizmosCreated = false;
            _seatGizmosVisible = false;
        }

        private static int[] NewSeatIds(int n)
        {
            int[] ids = new int[n];
            for (int i = 0; i < n; i++) ids[i] = -1;
            return ids;
        }
    }
}
