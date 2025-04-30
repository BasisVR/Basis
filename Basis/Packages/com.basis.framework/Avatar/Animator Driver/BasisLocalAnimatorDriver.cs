using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using Unity.Mathematics;
using Basis.Scripts.Avatar;
using Basis.Scripts.Drivers;
namespace Basis.Scripts.Animator_Driver
{
    [System.Serializable]
    public class BasisLocalAnimatorDriver
    {
        [SerializeField]
        private BasisAnimatorVariableApply basisAnimatorVariableApply = new BasisAnimatorVariableApply();
        [SerializeField]
        private Animator Animator;
        public float LargerThenVelocityCheck = 0.01f;
        public float LargerThenVelocityCheckRotation = 0.03f;
        public float ScaleMovementBy = 1;
        public float dampeningFactor = 6; // Adjust this value to control the dampening effect
        public float AngularDampingFactor = 30;
        private Vector3 previousRawVelocity = Vector3.zero;
        private Vector3 previousAngularVelocity = Vector3.zero; // New field for previous angular velocity
        private Quaternion previousHipsRotation;
        public Vector3 currentVelocity;
        public Vector3 dampenedVelocity;
        public Vector3 angularVelocity;
        public Vector3 dampenedAngularVelocity; // New field for dampened angular velocity
        public Quaternion deltaRotation;
        public bool HasEvents = false;
        public BasisInput HipsInput;
        public bool HasHipsInput = false;

        // Critically damped spring smoothing
        public float dampingRatio = 30; // Adjust for desired dampening effect
        public float angularFrequency = 0.4f; // Adjust for the speed of dampening
        public float3 hipsDifference;
        public Quaternion hipsDifferenceQ = Quaternion.identity;
        public float smoothFactor = 30f;
        public Quaternion smoothedRotation;
        public void SimulateAnimator(float DeltaTime)
        {
            if (BasisLocalPlayer.Instance.LocalAvatarDriver.CurrentlyTposing || BasisAvatarIKStageCalibration.HasFBIKTrackers)
            {
                if (basisAnimatorVariableApply.IsStopped == false)
                {
                    basisAnimatorVariableApply.StopAll();
                }
                return;
            }
            // Calculate the velocity of the character controller
            currentVelocity = Quaternion.Inverse(BasisLocalBoneDriver.Hips.OutgoingWorldData.rotation)
                              * (BasisLocalPlayer.Instance.LocalCharacterDriver.bottomPointLocalspace - BasisLocalPlayer.Instance.LocalCharacterDriver.LastbottomPoint) / DeltaTime;

            // Check if currentVelocity or previousRawVelocity contain NaN values
            if (float.IsNaN(currentVelocity.x) || float.IsNaN(currentVelocity.y) || float.IsNaN(currentVelocity.z) ||
                float.IsNaN(previousRawVelocity.x) || float.IsNaN(previousRawVelocity.y) || float.IsNaN(previousRawVelocity.z))
            {
                previousRawVelocity = Vector3.zero;  // Reset to a safe default
                return;
            }

            Vector3 velocityDifference = currentVelocity - previousRawVelocity;

            // Calculate damping factor and apply it with additional NaN/Infinity checks
            float dampingFactor = 1f - Mathf.Exp(-dampingRatio * angularFrequency * DeltaTime);
            if (float.IsNaN(dampingFactor) || float.IsInfinity(dampingFactor))
            {
                dampingFactor = 0f; // Safeguard against invalid damping factor
            }

            // Calculate dampened velocity
            dampenedVelocity = previousRawVelocity + dampingFactor * velocityDifference;

            // Update previous velocity for the next frame
            previousRawVelocity = dampenedVelocity;

            basisAnimatorVariableApply.BasisAnimatorVariables.Velocity = dampenedVelocity;
            basisAnimatorVariableApply.BasisAnimatorVariables.isMoving = basisAnimatorVariableApply.BasisAnimatorVariables.Velocity.sqrMagnitude > LargerThenVelocityCheck;
            basisAnimatorVariableApply.BasisAnimatorVariables.AnimationsCurrentSpeed = 1;

            if (HasHipsInput && basisAnimatorVariableApply.BasisAnimatorVariables.isMoving == false)
            {
                if (HipsInput.TryGetRole(out BasisBoneTrackedRole role))
                {
                    if (role == BasisBoneTrackedRole.Hips)
                    {
                        basisAnimatorVariableApply.BasisAnimatorVariables.AnimationsCurrentSpeed = 0;
                    }
                }
            }

            basisAnimatorVariableApply.BasisAnimatorVariables.IsFalling = BasisLocalPlayer.Instance.LocalCharacterDriver.IsFalling;
            basisAnimatorVariableApply.BasisAnimatorVariables.IsCrouching = BasisLocalInputActions.Crouching;

            // Calculate the angular velocity of the hips
            deltaRotation = BasisLocalBoneDriver.Hips.OutgoingWorldData.rotation * Quaternion.Inverse(previousHipsRotation);
            deltaRotation.ToAngleAxis(out float angle, out Vector3 axis);

            angularVelocity = axis * angle / DeltaTime;

            // Apply dampening to the angular velocity
            dampenedAngularVelocity = Vector3.Lerp(previousAngularVelocity, angularVelocity, AngularDampingFactor);

            basisAnimatorVariableApply.BasisAnimatorVariables.AngularVelocity = dampenedAngularVelocity;
            /*
            if (basisAnimatorVariableApply.BasisAnimatorVariables.isMoving == false)
            {
                basisAnimatorVariableApply.BasisAnimatorVariables.isMoving = angularVelocity.sqrMagnitude > LargerThenVelocityCheckRotation;
                basisAnimatorVariableApply.BasisAnimatorVariables.Velocity = dampenedAngularVelocity; // Update to use dampened angular velocity
            }
            */

            basisAnimatorVariableApply.UpdateAnimator(ScaleMovementBy);

            if (basisAnimatorVariableApply.BasisAnimatorVariables.IsFalling)
            {
                basisAnimatorVariableApply.BasisAnimatorVariables.IsJumping = false;
            }
            // Update the previous velocities and rotations for the next frame
            previousRawVelocity = dampenedVelocity;
            previousAngularVelocity = dampenedAngularVelocity;
            previousHipsRotation = BasisLocalBoneDriver.Hips.OutgoingWorldData.rotation;
        }
        private void JustJumped()
        {
            basisAnimatorVariableApply.BasisAnimatorVariables.IsJumping = true;
            basisAnimatorVariableApply.UpdateJumpState();
        }

        private void JustLanded()
        {
            basisAnimatorVariableApply.UpdateIsLandingState();
        }

        public void Initialize(Animator animator)
        {
            this.Animator = animator;
            Animator.logWarnings = false;
            Animator.updateMode = AnimatorUpdateMode.Normal;
            Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            basisAnimatorVariableApply.LoadCachedAnimatorHashes(Animator);
            if (HasEvents == false)
            {
                BasisLocalPlayer.Instance.LocalCharacterDriver.JustJumped += JustJumped;
                BasisLocalPlayer.Instance.LocalCharacterDriver.JustLanded += JustLanded;
                BasisDeviceManagement.Instance.AllInputDevices.OnListChanged += AssignHipsFBTracker;
                HasEvents = true;
            }
            AssignHipsFBTracker();
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

        public void OnDestroy(BasisLocalPlayer localPlayer)
        {
            if (HasEvents)
            {
                localPlayer.LocalCharacterDriver.JustJumped -= JustJumped;
                localPlayer.LocalCharacterDriver.JustLanded -= JustLanded;
                BasisDeviceManagement.Instance.AllInputDevices.OnListChanged -= AssignHipsFBTracker;
            }
        }
    }
}
