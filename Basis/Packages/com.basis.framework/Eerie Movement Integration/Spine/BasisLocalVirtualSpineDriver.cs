using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.IK
{
    /// <summary>
    /// Virtual spine solver for local avatars. It blends tracker-driven cues (head/neck)
    /// with preserved TPose segment lengths to synthesize chest, spine, and hips motion,
    /// keeping yaw coherent down the chain and offering XZ follow for hips.
    /// </summary>
    [System.Serializable]
    public class BasisLocalVirtualSpineDriver
    {
        /// <summary>Initialization guard.</summary>
        private bool _initialized;

        // Cached T-pose segment lengths (local). Recomputed when the height/scale system fires
        // OnPlayersHeightChangedNextFrame, not every simulate tick.
        /// <summary>Length from neck→chest captured from scaled TPose.</summary>
        private float _lenNeckToChest;
        /// <summary>Length from chest→spine captured from scaled TPose.</summary>
        private float _lenChestToSpine;
        /// <summary>Length from spine→hips captured from scaled TPose.</summary>
        private float _lenSpineToHips;
        /// <summary>Total neck→hips length captured from scaled TPose.</summary>
        private float _lenTotal;
        /// <summary>tChest = lenNeckToChest / lenTotal, cached alongside lengths.</summary>
        private float _tChest;
        /// <summary>tSpine = (lenNeckToChest + lenChestToSpine) / lenTotal, cached alongside lengths.</summary>
        private float _tSpine;
        /// <summary>Standing hips local Y = rest neck Y − total spine length: the rigid model's hips height
        /// when the head is at rest. Spine compression measures the head drop relative to this.</summary>
        private float _standingHipsLocalY;
        private float _standingHeadLocalY;
        /// <summary>The avatar's authored eye→hips horizontal arm at T-pose (its standing spine curve),
        /// re-applied over the leashed eye baseline so the standing pelvis matches the avatar's own
        /// skeleton at every facing (the eye is the only point that does not orbit under view yaw).</summary>
        private float3 _hipsFromEyeTposeXZ;
        /// <summary>The avatar's authored eye→head horizontal arm at T-pose, for the posture model's
        /// head-rest reference over the support base.</summary>
        private float3 _headFromEyeTposeXZ;

        /// <summary>Set whenever cached lengths need to be recomputed (scale or TPose changed).</summary>
        private bool _lengthsDirty = true;

        private NativeArray<BasisVirtualSpineCore.SpineSolveState> _solveState;

        /// <summary>
        /// If true, the hips avatar-local transform will be set to the T-pose, overriding the computed hips position.
        /// The actual hips world position is therefore fixed in place relative to the avatar's transform.
        /// </summary>
        public bool HipsFreezeToTpose = false;

        /// <summary>
        /// Enables the virtual overrides on all torso controls and hooks simulation callback.
        /// Safe to call multiple times.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;

            BasisLocalBoneDriver.HeadControl.HasVirtualOverride = true;
            BasisLocalBoneDriver.NeckControl.HasVirtualOverride = true;
            BasisLocalBoneDriver.ChestControl.HasVirtualOverride = true;
            BasisLocalBoneDriver.SpineControl.HasVirtualOverride = true;
            BasisLocalBoneDriver.HipsControl.HasVirtualOverride = true;

            BasisLocalPlayer.Instance.OnVirtualData += OnSimulate;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnHeightChanged;

            _solveState = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.Persistent);
            _solveState[0] = default;

            _lengthsDirty = true;
            _initialized = true;
        }

        /// <summary>
        /// Disables virtual overrides and unhooks the simulation callback.
        /// </summary>
        public void DeInitialize()
        {
            if (!_initialized) return;

            BasisLocalBoneDriver.HeadControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.NeckControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.ChestControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.SpineControl.HasVirtualOverride = false;
            BasisLocalBoneDriver.HipsControl.HasVirtualOverride = false;

            if (BasisLocalPlayer.Instance != null)
            {
                BasisLocalPlayer.Instance.OnVirtualData -= OnSimulate;
            }
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnHeightChanged;

            if (_solveState.IsCreated) _solveState.Dispose();

            _initialized = false;
        }

        private void OnHeightChanged(BasisHeightDriver.HeightModeChange _)
        {
            _lengthsDirty = true;
            // Drop the head-XZ baseline so the new avatar scale starts fresh — reusing the prior
            // baseline can read as a phantom lean for a second while the low-pass catches up.
            if (_solveState.IsCreated)
            {
                BasisVirtualSpineCore.SpineSolveState s = _solveState[0];
                s.HeadBaselineInitialized = 0;
                _solveState[0] = s;
            }
        }

        /// <summary>
        /// Main simulation pass executed before bone application. Gathers the head/neck cues and the
        /// current torso pose, runs the Burst spine solve, then writes the synthesized chest/spine/hips
        /// (and head/neck position) back onto the managed controls. The heavy math lives in
        /// <see cref="BasisVirtualSpineCore.BasisVirtualSpineSolveJob"/>; this method is the managed
        /// gather/scatter shell.
        /// </summary>
        public void OnSimulate()
        {
            var eye = BasisLocalBoneDriver.EyeControl;
            var head = BasisLocalBoneDriver.HeadControl;
            var neck = BasisLocalBoneDriver.NeckControl;
            var chest = BasisLocalBoneDriver.ChestControl;
            var spine = BasisLocalBoneDriver.SpineControl;
            var hips = BasisLocalBoneDriver.HipsControl;

            if (_lengthsDirty)
            {
                RecomputeSegmentLengths(eye, head, neck, chest, spine, hips);
                _lengthsDirty = false;
            }

            if (!BasisLocalPlayer.Instance.LocalBoneDriver.TryGetSimStates(out NativeArray<BasisBoneSimState> simStates))
            {
                return;
            }

            Matrix4x4 parentMatrix = BasisLocalPlayer.localToWorldMatrix;

            float torsoYawDeadzoneDeg = Basis.BasisUI.BasisSettingsDefaults.VSpineTorsoYawDeadzoneDeg.RawValue;
            if (BasisDeviceManagement.IsCurrentModeVR() && !Basis.BasisUI.BasisSettingsDefaults.VSpineTorsoYawPlayInVR.RawValue)
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
                EyeRot = eye.OutGoingData.rotation,

                HeadTargetPos = ResolveTargetPos(head),
                HeadTargetRot = ResolveTargetRot(head),
                NeckTargetPos = ResolveTargetPos(neck),
                NeckTargetRot = ResolveTargetRot(neck),
                ChestTargetPos = ResolveTargetPos(chest),
                ChestTargetRot = ResolveTargetRot(chest),
                SpineTargetPos = ResolveTargetPos(spine),
                SpineTargetRot = ResolveTargetRot(spine),

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
                TorsoYawDeadzoneDeg = torsoYawDeadzoneDeg,
                TorsoYawBlendSpeed = Basis.BasisUI.BasisSettingsDefaults.VSpineTorsoYawBlendSpeed.RawValue,

                HipsFreeze = (byte)(HipsFreezeToTpose ? 1 : 0),
                IsLocomoting = (byte)(BasisLocalPlayer.Instance.LocalCharacterDriver.MovementVector.sqrMagnitude > 0.001f ? 1 : 0),

                LenTotal = _lenTotal,
                TChest = _tChest,
                TSpine = _tSpine,

                StandingHipsLocalY = _standingHipsLocalY,
                StandingHeadLocalY = _standingHeadLocalY,
                EyePos = eye.OutGoingData.position,
                HipsAnchorOffsetLocal = _hipsFromEyeTposeXZ,
                HeadRestFromEyeLocal = _headFromEyeTposeXZ,
                PostureModel = (byte)(Basis.BasisUI.BasisSettingsDefaults.VSpinePostureModel.RawValue ? 1 : 0),
                HipsCompressionStrength = Basis.BasisUI.BasisSettingsDefaults.VSpineHipsCompressionStrength.RawValue,
                HipsMaxDropMeters = Basis.BasisUI.BasisSettingsDefaults.VSpineHipsMaxDropMeters.RawValue * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale,
            };

            new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
            {
                States = simStates,
                State = _solveState,
                P = p,
                IdxHead = head.Index,
                IdxNeck = neck.Index,
                IdxChest = chest.Index,
                IdxSpine = spine.Index,
                IdxHips = hips.Index,
            }.Run();
        }

        private static float3 ResolveTargetPos(BasisLocalBoneControl c)
        {
            return ResolveTarget(c).OutGoingData.position;
        }

        private static quaternion ResolveTargetRot(BasisLocalBoneControl c)
        {
            return ResolveTarget(c).OutGoingData.rotation;
        }

        // Resolves the target bone by index through the owner's Controls array (no recursive ref);
        // falls back to the bone itself when it has no target.
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

            _lenNeckToChest = math.distance(pNeck, pChest);
            _lenChestToSpine = math.distance(pChest, pSpine);
            _lenSpineToHips = math.distance(pSpine, pHips);
            _lenTotal = math.max(1e-4f, _lenNeckToChest + _lenChestToSpine + _lenSpineToHips);
            _tChest = math.saturate(_lenNeckToChest / _lenTotal);
            _tSpine = math.saturate((_lenNeckToChest + _lenChestToSpine) / _lenTotal);
            // Rigid-model hips Y at rest (neck at rest height): drop below this drives spine compression.
            _standingHipsLocalY = pNeck.y - _lenTotal;
            // The posture model normalises by the user's own standing HEAD height, which is what makes it
            // scale-free. Guarded: a rig that puts the head at the origin would otherwise divide by zero.
            _standingHeadLocalY = math.max(pHead.y, 1e-3f);
            // The avatar's own authored horizontal arms from the EYE (its standing spine curve, measured
            // from the one point that cannot orbit under view yaw). Reproduced over the leashed eye
            // baseline, the standing pelvis lands exactly where THIS avatar's skeleton stands. The T-pose
            // capture zeroes x for spine bones, so these are sagittal scalars in practice.
            float3 pEye = eye.TposeLocalScaled.position;
            _hipsFromEyeTposeXZ = new float3(pHips.x - pEye.x, 0f, pHips.z - pEye.z);
            _headFromEyeTposeXZ = new float3(pHead.x - pEye.x, 0f, pHead.z - pEye.z);
        }
    }
}
