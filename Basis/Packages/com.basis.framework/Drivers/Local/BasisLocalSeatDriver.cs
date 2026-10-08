using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
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

        public void Initialize(BasisLocalPlayer localPlayer) => LocalPlayer = localPlayer;

        // Player-specific pose values calculated when the player sits in a seat.
        private Vector3 leftLowerLegOffset;
        private Vector3 rightLowerLegOffset;
        private Vector3 leftUpperLegOffset;
        private Vector3 rightUpperLegOffset;

        private BasisSeatFitLegs legs;

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

            legs = BasisSeatFitLegs.FromBones(
                BasisLocalBoneDriver.LeftUpperLegControl.TposeLocalScaled.position,
                BasisLocalBoneDriver.LeftLowerLegControl.TposeLocalScaled.position,
                BasisLocalBoneDriver.LeftFootControl.TposeLocalScaled.position,
                BasisLocalBoneDriver.LeftToeControl.TposeLocalScaled.position);

            var mapping = BasisLocalAvatarDriver.Mapping;
            avatarHipsBasisTpose = BuildAvatarHipsBasisFromTpose(mapping.AvatarForwards, mapping.AvatarUpwards, mapping.AvatarRightwards);
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
            _seat.ResetOccupantYaw();
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
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnPlayersHeightChanged;
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
            if (hasEvent)
            {
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnPlayersHeightChanged;
                hasEvent = false;
            }
            hasPlayspaceOffset = false;

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
                BasisDesktopEye.Instance.rotationYaw = previousHeadYawVsSeat + SeatYawDeg();
            }

            _seat.ResetOccupantYaw();
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

            BasisSeatFitResult fit = BasisSeatFit.Solve(_seat.GetFitFrame(), legs);

            // --- POLE (knee plane) setup ---
            // Define pole in seat hips frame, then map to avatar-local using hips basis.
            Vector3 desiredPoleInSeatHipsFrame = (Vector3.forward + Vector3.up * 0.20f).normalized;
            Vector3 desiredPoleAvatarLocal = (avatarHipsBasisTpose * desiredPoleInSeatHipsFrame).normalized;
            desiredPoleAvatarLocal = EnsureForwardHemisphereInAvatarBasis(desiredPoleAvatarLocal);

            // A stable "knee axis hint" in avatar-local: use hips-basis forward as "knees forward".
            Vector3 poleAxisHintAvatarLocal = (avatarHipsBasisTpose * Vector3.forward).normalized;

            Vector3 upperDirInSeatHipsFrame = Quaternion.Inverse(_seat.SpineRotation) * (fit.Knee - fit.Back);
            Vector3 lowerDirInSeatHipsFrame = Quaternion.Inverse(_seat.SpineRotation) * (fit.Foot - fit.Knee);

            Vector3 targetUpperLegDirRelToHips = EnsureForwardHemisphereInAvatarBasis(avatarHipsBasisTpose * upperDirInSeatHipsFrame);
            Vector3 targetLowerLegDirRelToHips = EnsureForwardHemisphereInAvatarBasis(avatarHipsBasisTpose * lowerDirInSeatHipsFrame);

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

            ApplyLocalLegPose(
                fit.Back,
                fit.Foot,
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

        private Vector3 EnsureForwardHemisphereInAvatarBasis(Vector3 dirAvatarLocal)
        {
            Vector3 dirInBasis = Quaternion.Inverse(avatarHipsBasisTpose) * dirAvatarLocal;
            if (dirInBasis.z < 0f)
                dirInBasis.z = -dirInBasis.z;
            return (avatarHipsBasisTpose * dirInBasis).normalized;
        }

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

            // Avatar T-pose hips pivot in avatar-local
            Vector3 hipsLocalPos = BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position;

            // Stable avatar hips basis
            Quaternion avatarHipsBasis = avatarHipsBasisTpose;

            BasisSeatFit.ComposeHipsWorld(seatT.GetLocalToWorld(), seatT.rotation, _seat.SpineRotation, pelvisSeatLocal,
                _seat.OccupantYawDegrees, out Vector3 pelvisWorldPos, out Quaternion hipsWorldRot, out Quaternion occupantPivot);
            BasisSeatFit.ComposeSeatedRoot(pelvisWorldPos, hipsWorldRot, avatarHipsBasis, hipsLocalPos, out Vector3 playerPos, out Quaternion playerRot);

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

            Vector3 leftFootWorldTarget = BasisSeatFit.RotateAboutPivot(seatT.ToWorldPoint(leftFootSeatLocal), pelvisWorldPos, occupantPivot);
            Vector3 rightFootWorldTarget = BasisSeatFit.RotateAboutPivot(seatT.ToWorldPoint(rightFootSeatLocal), pelvisWorldPos, occupantPivot);

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

            BasisSeatFitResult fit = BasisSeatFit.Solve(_seat.GetFitFrame(), legs);

            Transform seatT = _seat.transform;
            Vector3 kneeW = seatT.ToWorldPoint(fit.Knee);
            Vector3 footW = seatT.ToWorldPoint(fit.Foot);

            Quaternion seatWorldRot = seatT.rotation;

            Vector3 hipsLocalPos = BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position;
            BasisSeatFit.ComposeHipsWorld(seatT.GetLocalToWorld(), seatWorldRot, _seat.SpineRotation, fit.Back, out Vector3 backW, out Quaternion hipsWorldRot);
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
