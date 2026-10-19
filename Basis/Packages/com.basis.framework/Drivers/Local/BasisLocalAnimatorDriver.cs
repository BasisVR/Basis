using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using Unity.Mathematics;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.Drivers;
using Basis.Scripts.Avatar;

namespace Basis.Scripts.Animator_Driver
{
    [System.Serializable]
    public class BasisLocalAnimatorDriver
    {
        [System.NonSerialized] public BasisLocalPlayer LocalPlayer;

        [System.NonSerialized] public BasisLocalCharacterDriver LocalCharacterDriver;

        [SerializeField]
        private BasisAnimatorVariableApply basisAnimatorVariableApply = new BasisAnimatorVariableApply();

        [SerializeField]
        private Animator Animator;

        public float StationaryVelocityThreshold = 0.01f;

        public float LargerThenVelocityCheckRotation = 0.03f;

        [Range(0, 1f)] public float CrouchThreshold = 0.35f;

        public float dampeningFactor = 6;

        public float AngularDampingFactor = 30;

        private Vector3 previousRawVelocity = Vector3.zero;

        private Vector3 previousAngularVelocity = Vector3.zero;

        private Quaternion previousHipsRotation;

        public Vector3 currentVelocity;

        public Vector3 dampenedVelocity;

        public Vector3 angularVelocity;

        public Vector3 dampenedAngularVelocity;

        public Quaternion deltaRotation;

        public bool HasEvents = false;

        public BasisInput HipsInput;

        public bool HasHipsInput = false;

        public float dampingRatio = 30;

        public float angularFrequency = 0.4f;

        public float3 hipsDifference;

        public Quaternion hipsDifferenceQ = Quaternion.identity;

        public float smoothFactor = 30f;

        public Quaternion smoothedRotation;

        public bool PauseAnimator;
        public void Initialize(BasisLocalPlayer localPlayer)
        {
            LocalPlayer = localPlayer;
            LocalCharacterDriver = localPlayer.LocalCharacterDriver;
            Animator = localPlayer.BasisAvatar.Animator;
            Animator.logWarnings = false;
            Animator.updateMode = AnimatorUpdateMode.Normal;

            basisAnimatorVariableApply.LoadCachedAnimatorHashes(Animator);
            if (!HasEvents)
            {
                LocalCharacterDriver.JustJumped += JustJumped;
                LocalCharacterDriver.JustLanded += JustLanded;
                BasisDeviceManagement.Instance.AllInputDevices.OnListChanged += AssignHipsFBTracker;
                HasEvents = true;
            }
            AssignHipsFBTracker();
        }
        public void StopAllVariables()
        {
            if (basisAnimatorVariableApply.IsStopped == false)
            {
                basisAnimatorVariableApply.StopAll();
            }
        }

        public BasisLocoParams GetLocoParams()
        {
            var variables = basisAnimatorVariableApply.BasisAnimatorVariables;
            return new BasisLocoParams
            {
                VelocityX = variables.Velocity.x,
                VelocityZ = variables.Velocity.z,
                CurrentSpeed = variables.AnimationsCurrentSpeed,
                CrouchedState = variables.IsCrouching,
                ProneState = variables.IsProne,
                IsFalling = variables.IsFalling,
                IsJumping = variables.IsJumping,
            };
        }
        public void SimulateAnimator(float DeltaTime)
        {
            if (BasisLocalAvatarDriver.CurrentlyTposing || BasisAvatarIKStageCalibration.HasLegFBIKTrackers || PauseAnimator)
            {
                StopAllVariables();
                return;
            }

            Step1SampleVelocity(DeltaTime);
            Step2SampleAngularVelocity(DeltaTime);
            Step3WriteAnimatorVariables();
            Step4PushToAnimator();
            Step5StoreForNextFrame();
        }

        void Step1SampleVelocity(float DeltaTime)
        {
            // Calculate the velocity of the character controller
            var charDriver = LocalPlayer.LocalCharacterDriver;
            Quaternion velocityFrame = charDriver.IsLocomoting ? BasisLocalCharacterDriver.GetMovementFacing() : BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation;
            currentVelocity = Quaternion.Inverse(velocityFrame) *
                              (charDriver.bottomPointLocalSpace - charDriver.LastBottomPoint) / DeltaTime;

            // Sanitize currentVelocity
            currentVelocity = new Vector3(
                float.IsNaN(currentVelocity.x) ? 0f : currentVelocity.x,
                float.IsNaN(currentVelocity.y) ? 0f : currentVelocity.y,
                float.IsNaN(currentVelocity.z) ? 0f : currentVelocity.z
            );

            // Sanitize previousRawVelocity
            previousRawVelocity = new Vector3(
                float.IsNaN(previousRawVelocity.x) ? 0f : previousRawVelocity.x,
                float.IsNaN(previousRawVelocity.y) ? 0f : previousRawVelocity.y,
                float.IsNaN(previousRawVelocity.z) ? 0f : previousRawVelocity.z
            );

            Vector3 velocityDifference = currentVelocity - previousRawVelocity;

            // Calculate damping factor and apply it with additional NaN/Infinity checks
            float dampingFactor = 1f - Mathf.Exp(-dampingRatio * angularFrequency * DeltaTime);
            if (float.IsNaN(dampingFactor) || float.IsInfinity(dampingFactor))
            {
                dampingFactor = 0f; // Safeguard against invalid damping factor
            }

            // Calculate dampened velocity
            dampenedVelocity = previousRawVelocity + dampingFactor * velocityDifference;
        }

        void Step2SampleAngularVelocity(float DeltaTime)
        {
            // Calculate the angular velocity of the hips
            deltaRotation = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation * Quaternion.Inverse(previousHipsRotation);
            deltaRotation.ToAngleAxis(out float angle, out Vector3 axis);

            angularVelocity = axis * angle / DeltaTime;

            // Apply dampening to the angular velocity
            dampenedAngularVelocity = Vector3.Lerp(previousAngularVelocity, angularVelocity, AngularDampingFactor);
        }

        void Step3WriteAnimatorVariables()
        {
            basisAnimatorVariableApply.BasisAnimatorVariables.Velocity = dampenedVelocity;
            bool isMoving = dampenedVelocity.sqrMagnitude > StationaryVelocityThreshold;
            basisAnimatorVariableApply.BasisAnimatorVariables.isMoving = isMoving;
            basisAnimatorVariableApply.BasisAnimatorVariables.AnimationsCurrentSpeed = 1;

            if (HasHipsInput && isMoving == false)
            {
                if (HipsInput.TryGetRole(out BasisBoneTrackedRole role))
                {
                    if (role == BasisBoneTrackedRole.Hips)
                    {
                        basisAnimatorVariableApply.BasisAnimatorVariables.AnimationsCurrentSpeed = 0;
                    }
                }
            }

            basisAnimatorVariableApply.BasisAnimatorVariables.IsFalling = LocalCharacterDriver.IsFalling;
            basisAnimatorVariableApply.BasisAnimatorVariables.CrouchBlend = LocalCharacterDriver.CrouchBlend;
            basisAnimatorVariableApply.BasisAnimatorVariables.IsCrouching = LocalCharacterDriver.CrouchBlend < CrouchThreshold;
            basisAnimatorVariableApply.BasisAnimatorVariables.IsProne = LocalCharacterDriver.IsProne;
            basisAnimatorVariableApply.BasisAnimatorVariables.AngularVelocity = dampenedAngularVelocity;
        }

        void Step4PushToAnimator()
        {
            basisAnimatorVariableApply.UpdateAnimator();

            if (basisAnimatorVariableApply.BasisAnimatorVariables.IsFalling)
            {
                basisAnimatorVariableApply.BasisAnimatorVariables.IsJumping = false;
            }
        }

        void Step5StoreForNextFrame()
        {
            // Update the previous velocities and rotations for the next frame
            previousRawVelocity = dampenedVelocity;
            previousAngularVelocity = dampenedAngularVelocity;
            previousHipsRotation = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation;
        }

        private void JustJumped()
        {
            // LEG trackers, not ANY tracker: the jump animation poses the LEGS, so only a leg tracker has standing
            // to veto it. A chest/shoulder/elbow tracker (MediaPipe spawns all three) would otherwise silence the
            // jump animation while leaving nothing to drive the legs.
            if (BasisAvatarIKStageCalibration.HasLegFBIKTrackers && Basis.BasisUI.BasisSettingsDefaults.DisableAnimationsInFBT.RawValue)
            {
                return;
            }
            basisAnimatorVariableApply.BasisAnimatorVariables.IsJumping = true;
            //basisAnimatorVariableApply.UpdateJumpState();
        }

        private void JustLanded()
        {
            // As JustJumped: the landing animation poses the LEGS, so only a leg tracker gets to veto it.
            if (BasisAvatarIKStageCalibration.HasLegFBIKTrackers && Basis.BasisUI.BasisSettingsDefaults.DisableAnimationsInFBT.RawValue)
            {
                return;
            }
            basisAnimatorVariableApply.UpdateIsLandingState();
            LocalPlayer.LocalRigDriver.LocomotionPose.NotifyLanding();
        }

        public void AssignHipsFBTracker()
        {
            basisAnimatorVariableApply.StopAll();
            HasHipsInput = BasisDeviceManagement.Instance.FindDevice(out HipsInput, BasisBoneTrackedRole.Hips);
        }

        public void HandleTeleport()
        {
            currentVelocity = Vector3.zero;
            dampenedVelocity = Vector3.zero;
            previousAngularVelocity = Vector3.zero; // Reset angular velocity dampening on teleport
        }

        public void OnDestroy()
        {
            if (HasEvents)
            {
                LocalCharacterDriver.JustJumped -= JustJumped;
                LocalCharacterDriver.JustLanded -= JustLanded;
                BasisDeviceManagement.Instance.AllInputDevices.OnListChanged -= AssignHipsFBTracker;
            }
        }
    }
}
