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
    /// <summary>
    /// Drives a local player's <see cref="Animator"/> parameters from character and tracker data.
    /// </summary>
    /// <remarks>
    /// This driver samples positional and angular motion from the character and hips control, applies smoothing,
    /// and writes results into a cached set of animator variables via <see cref="BasisAnimatorVariableApply"/>.
    /// It also reacts to character events (jump/land) and (re)binds a hips tracker when device lists change.
    /// </remarks>
    /// <seealso cref="BasisLocalPlayer"/>
    /// <seealso cref="BasisLocalCharacterDriver"/>
    /// <seealso cref="BasisAnimatorVariableApply"/>
    [System.Serializable]
    public class BasisLocalAnimatorDriver
    {
        /// <summary>
        /// Owning local player instance assigned at <see cref="Initialize(BasisLocalPlayer)"/>.
        /// </summary>
        [System.NonSerialized] public BasisLocalPlayer LocalPlayer;

        /// <summary>
        /// Cached reference to the local character driver for movement/jump/land state.
        /// </summary>
        [System.NonSerialized] public BasisLocalCharacterDriver LocalCharacterDriver;

        /// <summary>
        /// Helper that caches animator hashes and exposes strongly-typed variables.
        /// </summary>
        [SerializeField]
        private BasisAnimatorVariableApply basisAnimatorVariableApply = new BasisAnimatorVariableApply();

        /// <summary>
        /// Unity <see cref="Animator"/> used to play character animations.
        /// </summary>
        [SerializeField]
        private Animator Animator;

        /// <summary>
        /// Squared-magnitude threshold below which the character is considered stationary.
        /// </summary>
        /// <value>Default: <c>0.01</c>.</value>
        public float StationaryVelocityThreshold = 0.01f;

        /// <summary>
        /// Minimum velocity magnitude that triggers rotation checks for animation blending.
        /// </summary>
        /// <value>Default: <c>0.03</c>.</value>
        public float LargerThenVelocityCheckRotation = 0.03f;

        /// <summary>
        /// Percentage of crouch blend below which the avatar is considered crouching.
        /// </summary>
        /// <value>Default: <c>0.35</c>.</value>
        [Range(0, 1f)] public float CrouchThreshold = 0.35f;

        /// <summary>
        /// Damping factor controlling linear velocity smoothing intensity.
        /// </summary>
        /// <value>Default: <c>6</c>.</value>
        public float dampeningFactor = 6;

        /// <summary>
        /// Damping factor used when smoothing angular velocity.
        /// </summary>
        /// <value>Default: <c>30</c>.</value>
        public float AngularDampingFactor = 30;

        /// <summary>
        /// Last raw (pre-damped) velocity sample used for smoothing.
        /// </summary>
        private Vector3 previousRawVelocity = Vector3.zero;

        /// <summary>
        /// Last smoothed angular velocity sample used for interpolation.
        /// </summary>
        private Vector3 previousAngularVelocity = Vector3.zero;

        /// <summary>
        /// Previous hips rotation sample used to compute angular velocity.
        /// </summary>
        private Quaternion previousHipsRotation;

        /// <summary>
        /// Current raw local-space velocity computed this frame.
        /// </summary>
        public Vector3 currentVelocity;

        /// <summary>
        /// Smoothed local-space velocity after damping.
        /// </summary>
        public Vector3 dampenedVelocity;

        /// <summary>
        /// Current raw angular velocity of the hips in radians per second (approx).
        /// </summary>
        public Vector3 angularVelocity;

        /// <summary>
        /// Smoothed angular velocity after damping.
        /// </summary>
        public Vector3 dampenedAngularVelocity;

        /// <summary>
        /// Frame-to-frame delta rotation of the hips used to derive angular velocity.
        /// </summary>
        public Quaternion deltaRotation;

        /// <summary>
        /// Indicates whether event subscriptions have been established.
        /// </summary>
        public bool HasEvents = false;

        /// <summary>
        /// Input device bound to the hips (if found).
        /// </summary>
        public BasisInput HipsInput;

        /// <summary>
        /// Indicates whether a hips input device is currently assigned.
        /// </summary>
        public bool HasHipsInput = false;

        /// <summary>
        /// Damping ratio for critically damped spring smoothing of velocity.
        /// </summary>
        /// <value>Default: <c>30</c>.</value>
        public float dampingRatio = 30;

        /// <summary>
        /// Angular frequency for the spring smoothing of velocity.
        /// </summary>
        /// <value>Default: <c>0.4</c>.</value>
        public float angularFrequency = 0.4f;

        /// <summary>
        /// Difference vector between hips targets (debug/telemetry).
        /// </summary>
        public float3 hipsDifference;

        /// <summary>
        /// Quaternion representation of hips difference (debug/telemetry).
        /// </summary>
        public Quaternion hipsDifferenceQ = Quaternion.identity;

        /// <summary>
        /// Smoothing factor for auxiliary rotation smoothing utilities.
        /// </summary>
        /// <value>Default: <c>30</c>.</value>
        public float smoothFactor = 30f;

        /// <summary>
        /// Smoothed rotation result (debug/telemetry).
        /// </summary>
        public Quaternion smoothedRotation;

        public bool PauseAnimator;
        /// <summary>
        /// Initializes the driver with a <see cref="BasisLocalPlayer"/>, configures the <see cref="Animator"/>,
        /// preloads animator hashes, subscribes to character and device events, and attempts to bind a hips tracker.
        /// </summary>
        /// <param name="localPlayer">The local player whose animator will be driven.</param>
        public void Initialize(BasisLocalPlayer localPlayer)
        {
            LocalPlayer = localPlayer;
            LocalCharacterDriver = localPlayer.LocalCharacterDriver;
            Animator = localPlayer.BasisAvatar.Animator;
            Animator.logWarnings = false;
            Animator.updateMode = AnimatorUpdateMode.Normal;
            Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

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
        /// <summary>
        /// Samples motion and state, applies smoothing, updates animator variables, and pushes values into the animator.
        /// </summary>
        /// <param name="DeltaTime">Delta time in seconds since the previous simulation step.</param>
        /// <remarks>
        /// This method returns early and halts variable application when T-posing or when full-body IK trackers are present.
        /// Velocity is computed in hips-local space, sanitized for NaN/Inf, and smoothed using an exponential spring-like filter.
        /// Angular velocity is derived from hips delta rotation and interpolated to reduce jitter.
        /// </remarks>
        public void SimulateAnimator(float DeltaTime)
        {
            if (BasisLocalAvatarDriver.CurrentlyTposing || BasisAvatarIKStageCalibration.HasFBIKTrackers || PauseAnimator)
            {
                StopAllVariables();
                return;
            }

            // Calculate the velocity of the character controller
            var charDriver = LocalPlayer.LocalCharacterDriver;
            currentVelocity = Quaternion.Inverse(BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation) *
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

            // Update previous velocity for the next frame
            previousRawVelocity = dampenedVelocity;

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

            // Calculate the angular velocity of the hips
            deltaRotation = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation * Quaternion.Inverse(previousHipsRotation);
            deltaRotation.ToAngleAxis(out float angle, out Vector3 axis);

            angularVelocity = axis * angle / DeltaTime;

            // Apply dampening to the angular velocity
            dampenedAngularVelocity = Vector3.Lerp(previousAngularVelocity, angularVelocity, AngularDampingFactor);

            basisAnimatorVariableApply.BasisAnimatorVariables.AngularVelocity = dampenedAngularVelocity;

            basisAnimatorVariableApply.UpdateAnimator();

            if (basisAnimatorVariableApply.BasisAnimatorVariables.IsFalling)
            {
                basisAnimatorVariableApply.BasisAnimatorVariables.IsJumping = false;
            }

            // Update the previous velocities and rotations for the next frame
            previousRawVelocity = dampenedVelocity;
            previousAngularVelocity = dampenedAngularVelocity;
            previousHipsRotation = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation;
        }

        /// <summary>
        /// Event handler that sets the animator's jump state immediately after a jump begins.
        /// </summary>
        private void JustJumped()
        {
            basisAnimatorVariableApply.BasisAnimatorVariables.IsJumping = true;
            //basisAnimatorVariableApply.UpdateJumpState();
        }

        /// <summary>
        /// Event handler that updates the animator's landing state when the character lands.
        /// </summary>
        private void JustLanded()
        {
            basisAnimatorVariableApply.UpdateIsLandingState();
        }

        /// <summary>
        /// Attempts to (re)assign a full-body hips tracker input device and stops animator variable application
        /// momentarily to avoid stale values during rebinding.
        /// </summary>
        /// <remarks>
        /// This is invoked on initialization and whenever the device list changes.
        /// </remarks>
        public void AssignHipsFBTracker()
        {
            basisAnimatorVariableApply.StopAll();
            HasHipsInput = BasisDeviceManagement.Instance.FindDevice(out HipsInput, BasisBoneTrackedRole.Hips);
        }

        /// <summary>
        /// Resets transient motion state after a teleport to prevent post-teleport animation spikes.
        /// </summary>
        public void HandleTeleport()
        {
            currentVelocity = Vector3.zero;
            dampenedVelocity = Vector3.zero;
            previousAngularVelocity = Vector3.zero; // Reset angular velocity dampening on teleport
        }

        /// <summary>
        /// Unsubscribes from character and device events to prevent leaks and stray callbacks.
        /// </summary>
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
