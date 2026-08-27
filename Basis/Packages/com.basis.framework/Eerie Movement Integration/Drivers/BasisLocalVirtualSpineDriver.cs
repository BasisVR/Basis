using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.IK
{
    [System.Serializable]
    public class BasisLocalVirtualSpineDriver
    {
        private bool initialized;
        private float lenNeckToChest, lenChestToSpine, lenSpineToHips, lenSpineTotal, chestTransform, spineTransform, restHipsLocalY;
        private float restHeadLocalY, hipsRestDropY;
        private float3 hipsFromEyeTposeXZ, headFromEyeTposeXZ, yawPivotFromEyeTposeXZ, eyeFromHeadTpose;
        private float tposeNeckMinusEyeY;
        private readonly BasisNodPivotSampler nodPivotSampler = new BasisNodPivotSampler(30);
        private float3 gazeSwingLever;
        private bool lengthsDirty = true;
        private NativeArray<BasisVirtualSpineCore.SpineSolveState> solveState;
        public bool HipsFreezeToTpose = false;
        public void Initialize()
        {
            if (initialized) return;

            BasisLocalBoneDriver.HeadControl.HasVirtualOverride = true;
            BasisLocalBoneDriver.NeckControl.HasVirtualOverride = true;
            BasisLocalBoneDriver.ChestControl.HasVirtualOverride = true;
            BasisLocalBoneDriver.SpineControl.HasVirtualOverride = true;
            BasisLocalBoneDriver.HipsControl.HasVirtualOverride = true;

            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnHeightChanged;

            solveState = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.Persistent);
            solveState[0] = default;

            lengthsDirty = true;
            initialized = true;
        }
        public void DeInitialize()
        {
            if (!initialized) return;

            BasisLocalBoneDriver.HeadControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.NeckControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.ChestControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.SpineControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.HipsControl.HasVirtualOverride = false;

            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnHeightChanged;

            if (solveState.IsCreated) solveState.Dispose();

            initialized = false;
        }
        private void OnHeightChanged(BasisHeightDriver.HeightModeChange _)
        {
            lengthsDirty = true;

            nodPivotSampler.Reset();
            gazeSwingLever = float3.zero;

            if (solveState.IsCreated)
            {
                BasisVirtualSpineCore.SpineSolveState s = solveState[0];
                s.HeadBaselineInitialized = 0;
                solveState[0] = s;
            }
        }
        public void Simulate()
        {
            if (!initialized) return;

            var eye = BasisLocalBoneDriver.EyeControl;
            var head = BasisLocalBoneDriver.HeadControl;
            var neck = BasisLocalBoneDriver.NeckControl;
            var chest = BasisLocalBoneDriver.ChestControl;
            var spine = BasisLocalBoneDriver.SpineControl;
            var hips = BasisLocalBoneDriver.HipsControl;

            BasisCalibratedCoords freshEye;
            if (eye.HasTracked == BasisHasTracked.HasTracker)
            {
                freshEye = eye.IncomingData;
                if (eye.UseInverseOffset)
                {
                    var off = eye.InverseOffsetFromBone;
                    freshEye.position += freshEye.rotation * off.position;
                    freshEye.rotation *= off.rotation;
                }
            }
            else
            {
                freshEye = eye.OutGoingData;
            }

            if (lengthsDirty)
            {
                RecomputeSegmentLengths(eye, head, neck, chest, spine, hips);
                gazeSwingLever = eyeFromHeadTpose;
                lengthsDirty = false;
            }

            if (BasisDeviceManagement.IsCurrentModeVR() && Basis.BasisUI.BasisSettingsDefaults.VSpineNodPivotEstimate.RawValue)
            {
                BasisNodPivotSettings pivotSettings = BasisNodPivotEstimatorCore.Defaults();
                pivotSettings.Scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
                gazeSwingLever = nodPivotSampler.Update(freshEye.position, freshEye.rotation, Time.deltaTime, eyeFromHeadTpose, in pivotSettings);
            }
            else
            {
                gazeSwingLever = eyeFromHeadTpose;
            }

            if (!BasisLocalPlayer.Instance.LocalBoneDriver.TryGetSimStates(out NativeArray<BasisBoneSimState> simStates))
            {
                return;
            }

            Matrix4x4 parentMatrix = BasisLocalPlayer.localToWorldMatrix;

            bool isVR = BasisDeviceManagement.IsCurrentModeVR();

            float torsoYawDeadzoneDeg = isVR ? (Basis.BasisUI.BasisSettingsDefaults.VSpineTorsoYawPlayInVR.RawValue ? Basis.BasisUI.BasisSettingsDefaults.VSpineTorsoYawDeadzoneVRDeg.RawValue : 0f) : Basis.BasisUI.BasisSettingsDefaults.VSpineTorsoYawDeadzoneDeg.RawValue;

            if (BasisLocalPlayer.Instance.LocalCharacterDriver.IsProne)
            {
                torsoYawDeadzoneDeg = 0f;
            }

            var leftFoot = BasisLocalBoneDriver.LeftFootControl;
            var rightFoot = BasisLocalBoneDriver.RightFootControl;
            bool leftFootTracked = leftFoot != null && leftFoot.HasTracked == BasisHasTracked.HasTracker;
            bool rightFootTracked = rightFoot != null && rightFoot.HasTracked == BasisHasTracked.HasTracker;

            BasisVirtualSpineCore.SpineSolveParams p = new BasisVirtualSpineCore.SpineSolveParams
            {
                Dt = Time.deltaTime,
                Scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale,
                TrackingLiftY = BasisLocalPlayspaceMover.VerticalOffset * BasisHeightDriver.DeviceScale,
                ParentMatrix = parentMatrix,
                ParentRotation = parentMatrix.rotation,
                EyeRot = freshEye.rotation,

                HeadTargetPos = ResolveTargetPos(head, eye, in freshEye),
                HeadTargetRot = ResolveTargetRot(head, eye, in freshEye),
                NeckTargetPos = ResolveTargetPos(neck, eye, in freshEye),
                NeckTargetRot = ResolveTargetRot(neck, eye, in freshEye),
                ChestTargetPos = ResolveTargetPos(chest, eye, in freshEye),
                ChestTargetRot = ResolveTargetRot(chest, eye, in freshEye),
                SpineTargetPos = ResolveTargetPos(spine, eye, in freshEye),
                SpineTargetRot = ResolveTargetRot(spine, eye, in freshEye),

                HeadScaledOffset = head.ScaledOffset,
                NeckScaledOffset = neck.ScaledOffset,
                ChestScaledOffset = chest.ScaledOffset,
                SpineScaledOffset = spine.ScaledOffset,

                ChestTposeY = chest.TposeLocalScaled.position.y,
                SpineTposeY = spine.TposeLocalScaled.position.y,
                TposeHips = hips.TposeLocalScaled.position,

                LeftFootPos = leftFootTracked ? (float3)leftFoot.OutGoingData.position : float3.zero,
                RightFootPos = rightFootTracked ? (float3)rightFoot.OutGoingData.position : float3.zero,
                LeftFootTracked = (byte)(leftFootTracked ? 1 : 0),
                RightFootTracked = (byte)(rightFootTracked ? 1 : 0),

                ChestPitchFrac = Basis.BasisUI.BasisSettingsDefaults.VSpineChestPitchFrac.RawValue,
                ChestRollFrac = Basis.BasisUI.BasisSettingsDefaults.VSpineChestRollFrac.RawValue,
                SpinePitchFrac = Basis.BasisUI.BasisSettingsDefaults.VSpineSpinePitchFrac.RawValue,
                SpineRollFrac = Basis.BasisUI.BasisSettingsDefaults.VSpineSpineRollFrac.RawValue,
                NeckRotationSpeed = Basis.BasisUI.BasisSettingsDefaults.VSpineNeckRotationSpeed.RawValue,
                ChestRotationSpeed = Basis.BasisUI.BasisSettingsDefaults.VSpineChestRotationSpeed.RawValue,
                SpineRotationSpeed = Basis.BasisUI.BasisSettingsDefaults.VSpineSpineRotationSpeed.RawValue,
                HipsRotationSpeed = Basis.BasisUI.BasisSettingsDefaults.VSpineHipsRotationSpeed.RawValue,
                HipsForwardBias = Basis.BasisUI.BasisSettingsDefaults.VSpineHipsForwardBias.RawValue,

                NeckExtensionDamp = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckExtensionDamp.RawValue,
                NeckFlexionDamp = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckFlexionDamp.RawValue,
                TorsoYawDeadzoneDeg = torsoYawDeadzoneDeg,
                TorsoYawBlendSpeed = Basis.BasisUI.BasisSettingsDefaults.VSpineTorsoYawBlendSpeed.RawValue,

                HipsFreeze = (byte)(HipsFreezeToTpose ? 1 : 0),
                IsLocomoting = (byte)(BasisLocalPlayer.Instance.LocalCharacterDriver.IsLocomoting ? 1 : 0),

                LenTotal = lenSpineTotal,
                TChest = chestTransform,
                TSpine = spineTransform,

                StandingHipsLocalY = restHipsLocalY,
                StandingHeadLocalY = restHeadLocalY,
                EyePos = freshEye.position,
                GazeSwingLever = gazeSwingLever,
                TposeNeckMinusEyeY = tposeNeckMinusEyeY,
                GazeSwingRemoval = Basis.BasisUI.BasisSettingsDefaults.VSpineGazeSwingRemoval.RawValue,
                HipsAnchorOffsetLocal = hipsFromEyeTposeXZ,
                HeadRestFromEyeLocal = headFromEyeTposeXZ,

                YawPivotFromEyeLocal = isVR ? yawPivotFromEyeTposeXZ : float3.zero,
                PostureModel = (byte)(Basis.BasisUI.BasisSettingsDefaults.VSpinePostureModel.RawValue ? 1 : 0),
                HipsCompressionStrength = Basis.BasisUI.BasisSettingsDefaults.VSpineHipsCompressionStrength.RawValue,
                HipsMaxDropMeters = Basis.BasisUI.BasisSettingsDefaults.VSpineHipsMaxDropMeters.RawValue * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale,
                HipsRestDropY = hipsRestDropY,
            };

            new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
            {
                States = simStates,
                State = solveState,
                P = p,
                IdxHead = head.Index,
                IdxNeck = neck.Index,
                IdxChest = chest.Index,
                IdxSpine = spine.Index,
                IdxHips = hips.Index,
                SkipHead = TrackerOwned(head),
                SkipNeck = TrackerOwned(neck),
                SkipChest = TrackerOwned(chest),
                SkipSpine = TrackerOwned(spine),
                SkipHips = TrackerOwned(hips),
            }.Run();
        }
        private static byte TrackerOwned(BasisLocalBoneControl c)
        {
            return (byte)(c.HasTracked == BasisHasTracked.HasTracker ? 1 : 0);
        }
        private static float3 ResolveTargetPos(BasisLocalBoneControl c, BasisLocalBoneControl eye, in BasisCalibratedCoords freshEye)
        {
            BasisLocalBoneControl target = ResolveTarget(c);
            return ReferenceEquals(target, eye) ? (float3)freshEye.position : (float3)target.OutGoingData.position;
        }
        private static quaternion ResolveTargetRot(BasisLocalBoneControl c, BasisLocalBoneControl eye, in BasisCalibratedCoords freshEye)
        {
            BasisLocalBoneControl target = ResolveTarget(c);
            return ReferenceEquals(target, eye) ? (quaternion)freshEye.rotation : (quaternion)target.OutGoingData.rotation;
        }
        private static BasisLocalBoneControl ResolveTarget(BasisLocalBoneControl c)
        {
            return c.TargetIndex >= 0 ? c.Owner.Controls[c.TargetIndex] : c;
        }
        private void RecomputeSegmentLengths(BasisLocalBoneControl eye, BasisLocalBoneControl head, BasisLocalBoneControl neck, BasisLocalBoneControl chest, BasisLocalBoneControl spine, BasisLocalBoneControl hips)
        {
            float3 pHead = head.TposeLocalScaled.position;
            float3 pNeck = neck.TposeLocalScaled.position;
            float3 pChest = chest.TposeLocalScaled.position;
            float3 pSpine = spine.TposeLocalScaled.position;
            float3 pHips = hips.TposeLocalScaled.position;

            lenNeckToChest = math.distance(pNeck, pChest);
            lenChestToSpine = math.distance(pChest, pSpine);
            lenSpineToHips = math.distance(pSpine, pHips);
            lenSpineTotal = math.max(1e-4f, lenNeckToChest + lenChestToSpine + lenSpineToHips);
            chestTransform = math.saturate(lenNeckToChest / lenSpineTotal);
            spineTransform = math.saturate((lenNeckToChest + lenChestToSpine) / lenSpineTotal);

            restHipsLocalY = pNeck.y - lenSpineTotal;

            restHeadLocalY = math.max(pHead.y, 1e-3f);

            float3 pEye = eye.TposeLocalScaled.position;
            eyeFromHeadTpose = pEye - pHead;
            hipsFromEyeTposeXZ = new float3(pHips.x - pEye.x, 0f, pHips.z - pEye.z);
            headFromEyeTposeXZ = new float3(pHead.x - pEye.x, 0f, pHead.z - pEye.z);
            yawPivotFromEyeTposeXZ = new float3(pNeck.x - pEye.x, 0f, pNeck.z - pEye.z);
            tposeNeckMinusEyeY = pNeck.y - pEye.y;
        }
    }
}
