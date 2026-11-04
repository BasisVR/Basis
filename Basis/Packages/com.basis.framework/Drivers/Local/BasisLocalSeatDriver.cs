using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.TransformBinders.BoneControl;
using GatorDragonGames.JigglePhysics;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    [AutoStaticsCleanup]
    [System.Serializable]
    public partial class BasisLocalSeatDriver
    {
        [System.NonSerialized] public BasisLocalPlayer LocalPlayer;

        private BasisSeat _seat;
        public bool IsSeated => _seat != null;
        public bool HipsPinned => _seat != null && hipsLocked;
        private bool hipsLocked = true;
        private bool leftHandTargeted;
        private bool rightHandTargeted;
        private bool leftFootTargeted;
        private bool rightFootTargeted;
        private bool leftHandClaimed;
        private bool rightHandClaimed;
        private bool leftFootClaimed;
        private bool rightFootClaimed;
        private int limbDriveMask;

        /// <summary>The seat the local player is in, or null. A static read so scripts can poll it every frame.</summary>
        public static BasisSeat ActiveSeat { get; private set; }

        public void Initialize(BasisLocalPlayer localPlayer) => LocalPlayer = localPlayer;

        // Player-specific pose values calculated when the player sits in a seat.
        private Vector3 leftLowerLegOffset;
        private Vector3 rightLowerLegOffset;
        private Vector3 leftUpperLegOffset;
        private Vector3 rightUpperLegOffset;

        private BasisSeatFitLegs legs;

        // Per-avatar stable hips basis (from T-pose positions)
        private Quaternion avatarHipsBasisTpose = Quaternion.identity;
        private Quaternion hipsRestFromRoot = Quaternion.identity;
        private Quaternion leftHandRestFromRoot = Quaternion.identity;
        private Quaternion rightHandRestFromRoot = Quaternion.identity;
        private Quaternion leftFootRestFromRoot = Quaternion.identity;
        private Quaternion rightFootRestFromRoot = Quaternion.identity;
        private Vector3 lastSeatPelvisPosition = Vector3.zero;

        public Vector3 HipsOffsetFromPin { get; private set; }
        public Quaternion HipsRotationFromPin { get; private set; } = Quaternion.identity;

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
        private bool hasPlayspaceOffset = false;

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

            var mapping = BasisLocalAvatarDriver.Mapping;
            BasisSeatFitLegs.TryFromMapping(mapping.TposeFromRoot, LocalPlayer.AvatarTransform.localScale, BasisLocalAvatarDriver.TposeSoleHeight, out legs);
            avatarHipsBasisTpose = BasisGenericBoneRotationUtils.GetCharacterBasis(mapping);
            hipsRestFromRoot = mapping.TposeFromRoot.TryGetValue(HumanBodyBones.Hips, out var hipsRest) ? hipsRest.rotation : Quaternion.identity;
            leftHandRestFromRoot = mapping.TposeFromRoot.TryGetValue(HumanBodyBones.LeftHand, out var leftHandRest) ? leftHandRest.rotation : Quaternion.identity;
            rightHandRestFromRoot = mapping.TposeFromRoot.TryGetValue(HumanBodyBones.RightHand, out var rightHandRest) ? rightHandRest.rotation : Quaternion.identity;
            leftFootRestFromRoot = mapping.TposeFromRoot.TryGetValue(HumanBodyBones.LeftFoot, out var leftFootRest) ? leftFootRest.rotation : Quaternion.identity;
            rightFootRestFromRoot = mapping.TposeFromRoot.TryGetValue(HumanBodyBones.RightFoot, out var rightFootRest) ? rightFootRest.rotation : Quaternion.identity;
        }

        private void OnLocalAvatarChanged()
        {
            GrabLatestTposeLocalScaleData(BasisHeightDriver.HeightModeChange.OnTpose);
            ReanchorPlayspaceHeight();
        }

        private void OnPlayersHeightChanged(BasisHeightDriver.HeightModeChange HeightModeChange)
        {
            GrabLatestTposeLocalScaleData(HeightModeChange);
            ReanchorPlayspaceHeight();
        }

        public void Sit(BasisSeat seat)
        {
            if (LocalPlayer == null || seat == null)
                return;

            if (_seat != null)
                Stand();

            _seat = seat;
            ActiveSeat = seat;
            _seat.ResetOccupantYaw();
            _seat.ResetOccupantOffset();
            HipsOffsetFromPin = Vector3.zero;
            HipsRotationFromPin = Quaternion.identity;
            seatedSnapTurnLatched = false;

            BasisLocalPose.GetPose(BasisPoseSlot.PlayerRoot, LocalPlayer.transform, out lastSeatRootPosition, out lastSeatRootRotation);
            hasLastSeatRootPosition = true;

            previousRelativePosition = _seat.transform.ToLocalPoint(BasisLocalPose.GetPosition(BasisPoseSlot.PlayerRoot, LocalPlayer.transform));

            if (BasisDesktopEye.Instance != null)
            {
                previousHeadPitchGlobal = BasisDesktopEye.Instance.rotationPitch;
                previousHeadYawVsSeat = BasisDesktopEye.Instance.rotationYaw - SeatYawDeg();
            }

            CapturePlayspaceOffset();

            LocalPlayer.LocalCharacterDriver.IsEnabled = false;
            LocalPlayer.LocalCharacterDriver.MovementLock.Add(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalCharacterDriver.CrouchingLock.Add(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalAnimatorDriver.StopAllVariables();
            LocalPlayer.LocalAnimatorDriver.PauseAnimator = true;

            LocalPlayer.OnVirtualData += OnSimulate;

            GrabLatestTposeLocalScaleData( BasisHeightDriver.HeightModeChange.OnTpose);

            if (!hasEvent)
            {
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnPlayersHeightChanged;
                BasisLocalPlayer.OnLocalAvatarChanged += OnLocalAvatarChanged;
                hasEvent = true;
            }

            OnSimulate();
        }

        private float SeatYawDeg()
        {
            Quaternion seated = _seat.transform.GetRotation() * _seat.SpineRotation;
            float occupantYaw = _seat.OccupantYawDegrees;
            if (occupantYaw != 0f)
            {
                seated = Quaternion.AngleAxis(occupantYaw, seated * Vector3.up) * seated;
            }
            return Basis.IK.BasisTwistSolveCore.SignedTwistAngleDeg(seated, Vector3.up);
        }

        private void CapturePlayspaceOffset()
        {
            if (!BasisDeviceManagement.Instance.FindDevice(out BasisInput input, TransformBinders.BoneControl.BasisBoneTrackedRole.CenterEye))
            {
                hasPlayspaceOffset = false;
                return;
            }

            BasisSeatFit.ComposePlayspaceOffset(
                input.UnscaledDeviceCoord.position,
                input.UnscaledDeviceCoord.rotation,
                BasisHeightDriver.DeviceScale,
                BasisLocalBoneDriver.EyeControl.TposeLocalScaled.position.y,
                BasisDeviceManagement.IsCurrentModeVR(),
                out Vector3 offsetPosition,
                out Quaternion offsetRotation);

            BasisInput.OffsetCoords = new Common.BasisCalibratedCoords(offsetPosition, offsetRotation);
            hasPlayspaceOffset = true;
        }

        private void ReanchorPlayspaceHeight()
        {
            if (!hasPlayspaceOffset || !BasisDeviceManagement.IsCurrentModeVR())
                return;

            if (!BasisDeviceManagement.Instance.FindDevice(out BasisInput input, TransformBinders.BoneControl.BasisBoneTrackedRole.CenterEye))
                return;

            BasisInput.OffsetCoords.position.y = BasisSeatFit.ComposePlayspaceHeightOffset(
                input.UnscaledDeviceCoord.position,
                BasisInput.OffsetCoords.rotation,
                BasisHeightDriver.DeviceScale,
                BasisLocalBoneDriver.EyeControl.TposeLocalScaled.position.y);
        }

        public void Stand()
        {
            ActiveSeat = null;
            if (hasEvent)
            {
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnPlayersHeightChanged;
                BasisLocalPlayer.OnLocalAvatarChanged -= OnLocalAvatarChanged;
                hasEvent = false;
            }
            hasPlayspaceOffset = false;
            leftHandTargeted = rightHandTargeted = leftFootTargeted = rightFootTargeted = false;

            if (LocalPlayer == null)
                return;

            UpdateLimbDrive();
            LocalPlayer.LocalVirtualSpineDriver.HipsFreezeToTpose = false;
            LocalPlayer.LocalAnimatorDriver.PauseAnimator = false;
            LocalPlayer.OnVirtualData -= OnSimulate;
            hasLastSeatRootPosition = false;
            LocalPlayer.LocalCharacterDriver.MovementLock.Remove(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalCharacterDriver.CrouchingLock.Remove(nameof(BasisLocalSeatDriver));
            LocalPlayer.LocalCharacterDriver.IsEnabled = true;
            BasisInput.OffsetCoords = new Common.BasisCalibratedCoords(Vector3.zero, Quaternion.identity);

            ApplyOverrideUsages(false, false, false);

            var cc = BasisLocalPlayer.Instance.LocalCharacterDriver.characterController;

            if (_seat == null)
                return;

            _seat.OnExitSeat(LocalPlayer);

            if (BasisDesktopEye.Instance != null)
            {
                BasisDesktopEye.Instance.rotationPitch = previousHeadPitchGlobal;
                BasisDesktopEye.Instance.rotationYaw = previousHeadYawVsSeat + SeatYawDeg();
            }

            _seat.ResetOccupantYaw();
            _seat.ResetOccupantOffset();
            seatedSnapTurnLatched = false;

            Vector3 desiredPos = _seat.transform.ToWorldPoint(previousRelativePosition);

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
                LocalPlayer.Teleport(BasisLocalPose.GetPosition(BasisPoseSlot.PlayerRoot, LocalPlayer.transform), Quaternion.identity, true);
            }

            _seat = null;
        }

        private const float SeatedSnapTurnThreshold = 0.8f;
        private bool seatedSnapTurnLatched;

        private void TickOccupantTurn()
        {
            BasisSeatRotationLimits limits = _seat.OccupantRotationLimits;
            if (limits.AllowsRotation == false)
            {
                seatedSnapTurnLatched = false;
                return;
            }

            BasisDesktopEye eye = BasisDesktopEye.Instance;
            if (eye != null && !BasisDeviceManagement.IsCurrentModeVR())
            {
                float look = BasisSeatFit.WrapDegrees((BasisInput.OffsetCoords.rotation * Quaternion.Euler(0f, eye.rotationYaw, 0f)).eulerAngles.y);
                eye.rotationYaw -= _seat.TakeOccupantLookYaw(look);
            }

            float axis = LocalPlayer.LocalCharacterDriver.Rotation.x;
            float seatSnap = _seat.OccupantRotationSnapDegrees;
            bool stepped = seatSnap > 0f
                || (SMModuleControllerSettings.UsingSnapTurnAngle && BasisDeviceManagement.IsCurrentModeVR());

            if (stepped)
            {
                bool held = Mathf.Abs(axis) > SeatedSnapTurnThreshold;
                if (held == seatedSnapTurnLatched)
                {
                    return;
                }
                seatedSnapTurnLatched = held;
                if (held == false)
                {
                    return;
                }
                float step = seatSnap > 0f ? seatSnap : SMModuleControllerSettings.SnapTurnAngle;
                _seat.TurnOccupant(Mathf.Sign(axis) * step);
                return;
            }

            seatedSnapTurnLatched = false;
            if (axis != 0f)
            {
                _seat.TurnOccupant(axis * SMModuleControllerSettings.SmoothTurnSpeed * Time.deltaTime);
            }
        }

        private void OnSimulate()
        {
            if (_seat == null)
                return;

            TickOccupantTurn();

            bool fbt = Basis.BasisUI.BasisSettingsDefaults.EnableFBT.RawValue;
            hipsLocked = BasisSeatFit.IsLocked(_seat.HipsLock, fbt && HasTracker(BasisLocalBoneDriver.HipsControl));
            bool leftLocked = BasisSeatFit.IsLocked(_seat.LeftFootLock, fbt && ((!SeatDrives(BasisLocalBoneDriver.LeftFootControl, leftFootClaimed) && HasTracker(BasisLocalBoneDriver.LeftFootControl)) || HasTracker(BasisLocalBoneDriver.LeftUpperLegControl)));
            bool rightLocked = BasisSeatFit.IsLocked(_seat.RightFootLock, fbt && ((!SeatDrives(BasisLocalBoneDriver.RightFootControl, rightFootClaimed) && HasTracker(BasisLocalBoneDriver.RightFootControl)) || HasTracker(BasisLocalBoneDriver.RightUpperLegControl)));
            leftHandTargeted = BasisSeatFit.IsLocked(_seat.LeftHandLock, !SeatDrives(BasisLocalBoneDriver.LeftHandControl, leftHandClaimed) && HasRigLayer(BasisLocalBoneDriver.LeftHandControl)) && UsableTarget(_seat.LeftHandTarget);
            rightHandTargeted = BasisSeatFit.IsLocked(_seat.RightHandLock, !SeatDrives(BasisLocalBoneDriver.RightHandControl, rightHandClaimed) && HasRigLayer(BasisLocalBoneDriver.RightHandControl)) && UsableTarget(_seat.RightHandTarget);
            leftFootTargeted = fbt && leftLocked && UsableTarget(_seat.LeftFootTarget);
            rightFootTargeted = fbt && rightLocked && UsableTarget(_seat.RightFootTarget);
            bool leftPosed = leftLocked && !leftFootTargeted;
            bool rightPosed = rightLocked && !rightFootTargeted;
            UpdateLimbDrive();
            ApplyOverrideUsages(hipsLocked, leftPosed, rightPosed);
            LocalPlayer.LocalVirtualSpineDriver.HipsFreezeToTpose = hipsLocked;
            CaptureHipsFromPin(hipsLocked);

            BasisSeatFitResult fit = _seat.SolveFit(legs);
            PlaceRoot(fit.Back + _seat.OccupantOffset, out Vector3 pelvisWorldPos, out Quaternion playerRot);

            if (hipsLocked)
            {
                LocalPlayer.LocalRigDriver.SetOverrideData(HumanBodyBones.Hips, pelvisWorldPos, playerRot);
            }
            if (leftPosed || rightPosed)
            {
                ApplyLegPose(fit, playerRot);
            }
        }

        private static bool HasTracker(BasisLocalBoneControl control) => control != null && control.HasTracked == BasisHasTracked.HasTracker;

        private static bool HasRigLayer(BasisLocalBoneControl control) => control != null && control.HasRigLayer == BasisHasRigLayer.HasRigLayer;

        private static bool UsableTarget(Transform target)
        {
            if (target == null)
            {
                return false;
            }
            target.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            return BasisSeatFit.IsFinitePose(position, rotation);
        }

        public static bool SeatDrives(BasisLocalBoneControl control, bool claimed) => claimed && control.DevicesWithRoles.Count == 0;

        public static void ClaimLimb(BasisLocalBoneControl control, bool targeted, ref bool claimed)
        {
            if (control == null)
            {
                return;
            }
            if (targeted && control.DevicesWithRoles.Count == 0 && (claimed || (!HasTracker(control) && !HasRigLayer(control))))
            {
                claimed = true;
                control.HasTracked = BasisHasTracked.HasTracker;
                control.HasRigLayer = BasisHasRigLayer.HasRigLayer;
            }
            else if (claimed)
            {
                claimed = false;
                if (control.DevicesWithRoles.Count == 0)
                {
                    control.HasTracked = BasisHasTracked.HasNoTracker;
                    control.HasRigLayer = BasisHasRigLayer.HasNoRigLayer;
                }
            }
        }

        private void UpdateLimbDrive()
        {
            ClaimLimb(BasisLocalBoneDriver.LeftHandControl, leftHandTargeted, ref leftHandClaimed);
            ClaimLimb(BasisLocalBoneDriver.RightHandControl, rightHandTargeted, ref rightHandClaimed);
            ClaimLimb(BasisLocalBoneDriver.LeftFootControl, leftFootTargeted, ref leftFootClaimed);
            ClaimLimb(BasisLocalBoneDriver.RightFootControl, rightFootTargeted, ref rightFootClaimed);
            int mask = (leftHandTargeted ? 1 : 0) | (rightHandTargeted ? 2 : 0) | (leftFootTargeted ? 4 : 0) | (rightFootTargeted ? 8 : 0);
            if (mask == limbDriveMask)
            {
                return;
            }
            limbDriveMask = mask;
            LocalPlayer.OnLatePollData -= DriveLimbTargets;
            if (mask != 0)
            {
                LocalPlayer.OnLatePollData += DriveLimbTargets;
            }
        }

        private void DriveLimbTargets()
        {
            if (_seat == null)
            {
                return;
            }
            BasisLocalRigDriver rig = LocalPlayer.LocalRigDriver;
            Matrix4x4 playerToWorld = BasisLocalPlayer.localToWorldMatrix;
            if (leftHandTargeted) DriveHand(BasisLocalBoneDriver.LeftHandControl, _seat.LeftHandTarget, leftHandRestFromRoot, rig.LeftHandIKOffset, playerToWorld);
            if (rightHandTargeted) DriveHand(BasisLocalBoneDriver.RightHandControl, _seat.RightHandTarget, rightHandRestFromRoot, rig.RightHandIKOffset, playerToWorld);
            if (leftFootTargeted) DriveFoot(BasisLocalBoneDriver.LeftFootControl, _seat.LeftFootTarget, leftFootRestFromRoot, rig.LeftFootIKOffset, playerToWorld);
            if (rightFootTargeted) DriveFoot(BasisLocalBoneDriver.RightFootControl, _seat.RightFootTarget, rightFootRestFromRoot, rig.RightFootIKOffset, playerToWorld);
        }

        private void DriveHand(BasisLocalBoneControl control, Transform target, Quaternion restFromRoot, Quaternion ikOffset, in Matrix4x4 playerToWorld)
        {
            target.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            DriveControl(control, position, BasisSeatFit.LimbBoneRotation(rotation, avatarHipsBasisTpose, restFromRoot) * Quaternion.Inverse(ikOffset), playerToWorld);
        }

        private void DriveFoot(BasisLocalBoneControl control, Transform target, Quaternion restFromRoot, Quaternion ikOffset, in Matrix4x4 playerToWorld)
        {
            target.GetPositionAndRotation(out Vector3 sole, out Quaternion rotation);
            DriveControl(control, BasisSeatFit.FootTargetPosition(sole, rotation, legs.FootThickness), BasisSeatFit.LimbBoneRotation(rotation, avatarHipsBasisTpose, restFromRoot) * Quaternion.Inverse(ikOffset), playerToWorld);
        }

        private static void DriveControl(BasisLocalBoneControl control, Vector3 worldPosition, Quaternion worldRotation, in Matrix4x4 playerToWorld)
        {
            Common.BasisCalibratedCoords offset = control.UseInverseOffset ? control.InverseOffsetFromBone : Common.BasisCalibratedCoords.Identity;
            BasisSeatFit.ControlIncoming(playerToWorld, worldPosition, worldRotation, offset.position, offset.rotation, out Vector3 position, out Quaternion rotation);
            control.SetIncoming(position, rotation);
        }

        private void CaptureHipsFromPin(bool pinHips)
        {
            if (pinHips || !hasLastSeatRootPosition || !BasisLocalAvatarDriver.Mapping.HasHips)
            {
                HipsOffsetFromPin = Vector3.zero;
                HipsRotationFromPin = Quaternion.identity;
                return;
            }
            BasisLocalAvatarDriver.Mapping.Hips.GetPositionAndRotation(out Vector3 hipsPos, out Quaternion hipsRot);
            Quaternion rootInverse = Quaternion.Inverse(lastSeatRootRotation);
            HipsOffsetFromPin = rootInverse * (hipsPos - lastSeatPelvisPosition);
            HipsRotationFromPin = rootInverse * hipsRot * Quaternion.Inverse(hipsRestFromRoot);
        }

        private void ApplyLegPose(in BasisSeatFitResult fit, Quaternion playerRot)
        {
            Quaternion seatToAvatar = avatarHipsBasisTpose * Quaternion.Inverse(_seat.SpineRotation);
            Vector3 right = avatarHipsBasisTpose * Vector3.right;
            Vector3 forward = avatarHipsBasisTpose * Vector3.forward;
            Vector3 upperDir = fit.Knee - fit.Back;
            Vector3 lowerDir = fit.Foot - fit.Knee;

            BasisSeatLegPose left = BasisSeatFit.SolveLegPose(_seat.Pose, upperDir, lowerDir, _seat.SpineRotation, true);
            BasisSeatLegPose rightLeg = BasisSeatFit.SolveLegPose(_seat.Pose, upperDir, lowerDir, _seat.SpineRotation, false);
            Quaternion leftLower = BasisSeatFit.LegDeltaWithPole(leftLowerLegOffset, forward, seatToAvatar * left.LowerDir, seatToAvatar * left.LowerPole);
            Quaternion rightLower = BasisSeatFit.LegDeltaWithPole(rightLowerLegOffset, forward, seatToAvatar * rightLeg.LowerDir, seatToAvatar * rightLeg.LowerPole);
            Quaternion leftFoot = leftLower * Quaternion.AngleAxis(left.FootPitchDegrees, right);
            Quaternion rightFoot = rightLower * Quaternion.AngleAxis(rightLeg.FootPitchDegrees, right);

            BasisLocalRigDriver rig = LocalPlayer.LocalRigDriver;
            rig.SetOverrideRotation(HumanBodyBones.LeftUpperLeg, playerRot * BasisSeatFit.LegDeltaWithPole(leftUpperLegOffset, forward, seatToAvatar * left.UpperDir, seatToAvatar * left.UpperPole));
            rig.SetOverrideRotation(HumanBodyBones.RightUpperLeg, playerRot * BasisSeatFit.LegDeltaWithPole(rightUpperLegOffset, forward, seatToAvatar * rightLeg.UpperDir, seatToAvatar * rightLeg.UpperPole));
            rig.SetOverrideRotation(HumanBodyBones.LeftLowerLeg, playerRot * leftLower);
            rig.SetOverrideRotation(HumanBodyBones.RightLowerLeg, playerRot * rightLower);
            rig.SetOverrideRotation(HumanBodyBones.LeftFoot, playerRot * leftFoot);
            rig.SetOverrideRotation(HumanBodyBones.RightFoot, playerRot * rightFoot);
            rig.SetOverrideRotation(HumanBodyBones.LeftToes, playerRot * leftFoot);
            rig.SetOverrideRotation(HumanBodyBones.RightToes, playerRot * rightFoot);
        }

        private void PlaceRoot(Vector3 pelvisSeatLocal, out Vector3 pelvisWorldPos, out Quaternion playerRot)
        {
            Transform seatT = _seat.transform;
            Vector3 hipsLocalPos = BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position;

            BasisSeatFit.ComposeHipsWorld(seatT.GetLocalToWorld(), seatT.rotation, _seat.SpineRotation, pelvisSeatLocal,
                _seat.OccupantYawDegrees, out pelvisWorldPos, out Quaternion hipsWorldRot, out _);
            BasisSeatFit.ComposeSeatedRoot(pelvisWorldPos, hipsWorldRot, avatarHipsBasisTpose, hipsLocalPos, out Vector3 playerPos, out playerRot);

            LocalPlayer.transform.SetPose(playerPos, playerRot);
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
            lastSeatPelvisPosition = pelvisWorldPos;
            hasLastSeatRootPosition = true;
        }

        private void ApplyOverrideUsages(bool hips, bool leftLeg, bool rightLeg)
        {
            BasisLocalRigDriver rig = LocalPlayer.LocalRigDriver;
            rig.SetOverrideUsage(HumanBodyBones.Hips, hips);
            rig.SetOverrideUsage(HumanBodyBones.LeftUpperLeg, leftLeg);
            rig.SetOverrideUsage(HumanBodyBones.LeftLowerLeg, leftLeg);
            rig.SetOverrideUsage(HumanBodyBones.LeftFoot, leftLeg);
            rig.SetOverrideUsage(HumanBodyBones.LeftToes, leftLeg);
            rig.SetOverrideUsage(HumanBodyBones.RightUpperLeg, rightLeg);
            rig.SetOverrideUsage(HumanBodyBones.RightLowerLeg, rightLeg);
            rig.SetOverrideUsage(HumanBodyBones.RightFoot, rightLeg);
            rig.SetOverrideUsage(HumanBodyBones.RightToes, rightLeg);
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

            BasisSeatFitResult fit = _seat.SolveFit(legs);

            Transform seatT = _seat.transform;

            Quaternion seatWorldRot = seatT.rotation;

            Vector3 hipsLocalPos = BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position;
            BasisSeatFit.ComposeHipsWorld(seatT.GetLocalToWorld(), seatWorldRot, _seat.SpineRotation, fit.Back + _seat.OccupantOffset, _seat.OccupantYawDegrees, out Vector3 backW, out Quaternion hipsWorldRot, out Quaternion occupantPivot);
            Vector3 kneeW = BasisSeatFit.RotateAboutPivot(seatT.ToWorldPoint(fit.Knee + _seat.OccupantOffset), backW, occupantPivot);
            Vector3 footW = BasisSeatFit.RotateAboutPivot(seatT.ToWorldPoint(fit.Foot + _seat.OccupantOffset), backW, occupantPivot);
            BasisSeatFit.ComposeSeatedRoot(backW, hipsWorldRot, avatarHipsBasisTpose, hipsLocalPos, out Vector3 playerPos, out Quaternion playerRot);

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
        [NoAutoStaticsCleanup] private static readonly int[] _seatAxisIds = NewSeatIds(12);
        [NoAutoStaticsCleanup] private static readonly int[] _seatPointIds = NewSeatIds(3);
        [NoAutoStaticsCleanup] private static readonly int[] _seatSegIds = NewSeatIds(6);
        [NoAutoStaticsCleanup] private static readonly int[] _seatLabelIds = NewSeatIds(5);
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
