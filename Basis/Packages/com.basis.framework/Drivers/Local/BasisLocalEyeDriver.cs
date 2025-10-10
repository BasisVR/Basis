using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Eye_Follow
{
    /// <summary>
    /// Drives local eye “look around” behavior for the local avatar by periodically
    /// picking randomized gaze offsets relative to the head and smoothly rotating
    /// eye bones toward those targets. Integrates with gizmos and face visibility.
    /// </summary>
    /// <remarks>
    /// This component:
    /// <list type="bullet">
    /// <item>
    /// Subscribes to local player spawn and renderer visibility to enable/disable updates.
    /// </item>
    /// <item>
    /// Randomizes gaze offsets and speeds within configured ranges.
    /// </item>
    /// <item>
    /// Optionally visualizes per–eye targets via <see cref="BasisGizmoManager"/>.
    /// </item>
    /// </list>
    /// Updates are skipped when <see cref="Override"/> is <c>true</c> or when no head is present.
    /// </remarks>
    [DefaultExecutionOrder(15002)]
    [System.Serializable]
    public class BasisLocalEyeDriver
    {
        /// <summary>Initial local rotation of the left eye (captured on initialize).</summary>
        public quaternion leftEyeInitialRotation;

        /// <summary>Initial local rotation of the right eye (captured on initialize).</summary>
        public quaternion rightEyeInitialRotation;

        /// <summary>Tracks whether runtime events have been subscribed.</summary>
        public bool HasEvents = false;

        /// <summary>
        /// Global switch to bypass updates for all instances. When <c>true</c>,
        /// <see cref="RequiresUpdate"/> returns <c>false</c>.
        /// </summary>
        public static bool Override = false;

        /// <summary>Current look interpolation speed used by <see cref="Simulate(float)"/>.</summary>
        public float lookSpeed;

        /// <summary>Minimum seconds between randomized look-around target changes.</summary>
        public float MinLookAroundInterval = 1;

        /// <summary>Maximum seconds between randomized look-around target changes.</summary>
        public float MaxLookAroundInterval = 6;

        /// <summary>Maximum randomized offset distance from the forward gaze target (in meters).</summary>
        public float MaximumLookDistance = 0.25f;

        /// <summary>Lower bound for randomized look speed per interval.</summary>
        public float minLookSpeed = 0.03f;

        /// <summary>Upper bound for randomized look speed per interval.</summary>
        public float maxLookSpeed = 0.1f;

        /// <summary>World-space transform of the left eye bone.</summary>
        public Transform leftEyeTransform;

        /// <summary>World-space transform of the right eye bone.</summary>
        public Transform rightEyeTransform;

        /// <summary>World-space transform of the head.</summary>
        public Transform HeadTransform;

        /// <summary>
        /// Captured left-eye reference pose relative to the head at initialization
        /// (position is offset from head, rotation is world rotation at capture).
        /// </summary>
        public BasisCalibratedCoords LeftEyeInitallocalSpace;

        /// <summary>
        /// Captured right-eye reference pose relative to the head at initialization
        /// (position is offset from head, rotation is world rotation at capture).
        /// </summary>
        public BasisCalibratedCoords RightEyeInitallocalSpace;

        /// <summary>The current randomized gaze center in world space.</summary>
        public Vector3 RandomizedPosition;

        /// <summary>True if a left eye bone is present in the current avatar.</summary>
        public bool HasLeftEye = false;

        /// <summary>True if a right eye bone is present in the current avatar.</summary>
        public bool HasRightEye = false;

        /// <summary>True if a head transform is present in the current avatar.</summary>
        public static bool HasHead = false;

        /// <summary>Whether a gizmo sphere has been created for the left eye target.</summary>
        public bool LeftEyeHasGizmo;

        /// <summary>Whether a gizmo sphere has been created for the right eye target.</summary>
        public bool RightEyeHasGizmo;

        /// <summary>Gizmo index for the left eye target sphere.</summary>
        public int LeftEyeGizmoIndex;

        /// <summary>Gizmo index for the right eye target sphere.</summary>
        public int RightEyeGizmoIndex;

        /// <summary>Computed left-eye target position in world space.</summary>
        public Vector3 LeftEyeTargetWorld;

        /// <summary>Computed right-eye target position in world space.</summary>
        public Vector3 RightEyeTargetWorld;

        /// <summary>Current center gaze target position in world space.</summary>
        public Vector3 CenterTargetWorld;

        /// <summary>The current randomized offset applied to the forward gaze.</summary>
        public Vector3 AppliedOffset;

        /// <summary>Local forward direction from the head used as the base gaze vector.</summary>
        public Vector3 EyeForwards = new float3(0, 0, 1);

        /// <summary>The active interval length (seconds) until the next randomization.</summary>
        public float CurrentLookAroundInterval;

        /// <summary>Elapsed time accumulator for interval timing (seconds).</summary>
        public float timer;

        /// <summary>
        /// Distance threshold at which the target teleports instead of smoothing
        /// (useful after disabling, teleport, or large head movement).
        /// </summary>
        public float DistanceBeforeTeleport = 30;

        /// <summary>Singleton-like reference to the most recently initialized instance.</summary>
        public static BasisLocalEyeDriver Instance;

        /// <summary>Tracks if the driver was recently disabled to force a teleport catch-up.</summary>
        public bool wasDisabled = false;

        /// <summary>Whether the driver is currently enabled (e.g., face visible).</summary>
        public bool IsEnabled;

        /// <summary>
        /// Cleans up subscriptions and static flags on destroy.
        /// </summary>
        /// <param name="Player">The local player instance associated with this driver.</param>
        public void OnDestroy(BasisLocalPlayer Player)
        {
            HasHead = false;
            Instance = null;
            if (HasEvents)
            {
                if (Player.IsLocal)
                {
                    BasisLocalPlayer.OnSpawnedEvent -= AfterTeleport;
                }
                HasEvents = false;
            }
            BasisGizmoManager.OnUseGizmosChanged -= UpdateGizmoUsage;
            if (Player.FaceRenderer != null)
            {
                Player.FaceRenderer.Check -= UpdateFaceVisibility;
            }
            // its regenerated this script will be nuked and rebuilt BasisLocalPlayer.OnLocalAvatarChanged -= AfterTeleport;
        }

        /// <summary>
        /// Initializes references, baseline eye/head poses, gizmos, and visibility hooks.
        /// Also randomizes the initial look speed within configured bounds.
        /// </summary>
        /// <param name="CAD">Avatar driver providing calibration context.</param>
        /// <param name="Player">Player owning the avatar; used to gate local-only events.</param>
        public void Initalize(BasisAvatarDriver CAD, BasisPlayer Player)
        {
            // Initialize look speed
            lookSpeed = UnityEngine.Random.Range(minLookSpeed, maxLookSpeed);
            if (HasEvents == false)
            {
                if (Player.IsLocal)
                {
                    BasisLocalPlayer.OnSpawnedEvent += AfterTeleport;
                }
                HasEvents = true;
            }

            var References = BasisLocalAvatarDriver.References;
            rightEyeTransform = References.RightEye;
            leftEyeTransform = References.LeftEye;
            HeadTransform = References.head;

            HasLeftEye = References.HasLeftEye;
            HasRightEye = References.HasRightEye;
            HasHead = References.Hashead;
            Vector3 HeadPosition = References.head.position;

            if (HasLeftEye)
            {
                LeftEyeInitallocalSpace.rotation = leftEyeTransform.rotation;
                LeftEyeInitallocalSpace.position = leftEyeTransform.position - HeadPosition;

                leftEyeInitialRotation = leftEyeTransform.localRotation;
            }

            if (HasRightEye)
            {
                RightEyeInitallocalSpace.rotation = rightEyeTransform.rotation;
                RightEyeInitallocalSpace.position = rightEyeTransform.position - HeadPosition;

                rightEyeInitialRotation = rightEyeTransform.localRotation;
            }

            BasisGizmoManager.OnUseGizmosChanged += UpdateGizmoUsage;

            if (BasisLocalPlayer.Instance != null && BasisLocalPlayer.Instance.FaceRenderer != null)
            {
                BasisDebug.Log("Wired up Renderer Check For Blinking");
                BasisLocalPlayer.Instance.FaceRenderer.Check += UpdateFaceVisibility;
                UpdateFaceVisibility(BasisLocalPlayer.Instance.FaceIsVisible);
            }
            else
            {
                BasisDebug.LogError("Missing Render Checks");
            }

            Instance = this;
            IsEnabled = true;
        }

        /// <summary>
        /// Renderer visibility callback. Enables/disables simulation based on face visibility.
        /// </summary>
        /// <param name="State">True when the face is visible and eye updates should run.</param>
        private void UpdateFaceVisibility(bool State)
        {
            IsEnabled = State;
        }

        /// <summary>
        /// Creates or removes gizmo spheres for eye targets based on <paramref name="State"/>.
        /// </summary>
        /// <param name="State">Whether gizmos should be active.</param>
        public void UpdateGizmoUsage(bool State)
        {
            BasisDebug.Log("Running Bone EyeFollow Gizmos");
            if (State)
            {
                if (LeftEyeHasGizmo == false)
                {
                    BasisGizmoManager.CreateSphereGizmo("LeftEye Target", out LeftEyeGizmoIndex, LeftEyeTargetWorld, 0.1f, Color.cyan);
                    LeftEyeHasGizmo = true;
                }
                if (RightEyeHasGizmo == false)
                {
                    BasisGizmoManager.CreateSphereGizmo("RightEye Target", out RightEyeGizmoIndex, RightEyeTargetWorld, 0.1f, Color.magenta);
                    RightEyeHasGizmo = true;
                }
            }
            else
            {
                LeftEyeHasGizmo = false;
                RightEyeHasGizmo = false;
            }
        }

        /// <summary>
        /// Post-teleport handler that forces a catch-up simulation step and re-seeds the center target.
        /// </summary>
        public void AfterTeleport()
        {
            if (RequiresUpdate())
            {
                Simulate(Time.deltaTime);
            }
            CenterTargetWorld = RandomizedPosition; // will be caught up
        }

        /// <summary>
        /// Restores initial eye rotations and disables head availability when the driver is disabled.
        /// </summary>
        public void OnDisable()
        {
            if (HasLeftEye && leftEyeTransform != null)
            {
                leftEyeTransform.rotation = LeftEyeInitallocalSpace.rotation;
            }

            if (HasRightEye && rightEyeTransform != null)
            {
                rightEyeTransform.rotation = RightEyeInitallocalSpace.rotation;
            }
            CenterTargetWorld = RandomizedPosition;
            wasDisabled = true;
            HasHead = false;
        }

        /// <summary>
        /// Returns whether the driver should run simulation this frame.
        /// </summary>
        /// <returns>
        /// <c>true</c> when <see cref="Override"/> is <c>false</c> and a head is present; otherwise <c>false</c>.
        /// </returns>
        public static bool RequiresUpdate()
        {
            return Override == false && HasHead;
        }

        /// <summary>
        /// Advances the eye-look simulation by <paramref name="DeltaTime"/> seconds:
        /// randomizes intervals and speeds, computes a center gaze target from head pose,
        /// teleports for large changes or after disable, and rotates each eye toward its target.
        /// </summary>
        /// <param name="DeltaTime">Time step in seconds (typically <see cref="Time.deltaTime"/>).</param>
        public void Simulate(float DeltaTime)
        {
            if (IsEnabled)
            {
                // Update timer using DeltaTime
                timer += DeltaTime;

                // Check if it's time to look around
                if (timer > CurrentLookAroundInterval)
                {
                    CurrentLookAroundInterval = UnityEngine.Random.Range(MinLookAroundInterval, MaxLookAroundInterval);
                    AppliedOffset = UnityEngine.Random.insideUnitSphere * MaximumLookDistance;

                    // Reset timer and randomize look speed
                    timer = 0f;
                    lookSpeed = UnityEngine.Random.Range(minLookSpeed, maxLookSpeed);
                }

                HeadTransform.GetPositionAndRotation(out Vector3 headPosition, out Quaternion headRotation);

                // Calculate the randomized target position using float3 for optimized math operations
                Vector3 targetPosition = headPosition + (headRotation * EyeForwards) + AppliedOffset;

                // Check distance for teleporting, otherwise smooth move
                if (math.distance(targetPosition, CenterTargetWorld) > DistanceBeforeTeleport || wasDisabled)
                {
                    CenterTargetWorld = targetPosition;
                    wasDisabled = false;
                }
                else
                {
                    CenterTargetWorld = Vector3.MoveTowards(CenterTargetWorld, targetPosition, lookSpeed);
                }

                Quaternion InversedHeadRotation = math.inverse(headRotation);
                Vector3 Up = HeadTransform.up;

                // Set eye rotations using optimized float3 and quaternion operations
                if (HasLeftEye)
                {
                    LeftEyeTargetWorld = CenterTargetWorld + LeftEyeInitallocalSpace.position;
                    leftEyeTransform.rotation = LookAtTarget(leftEyeTransform.position, LeftEyeTargetWorld, math.mul(LeftEyeInitallocalSpace.rotation, InversedHeadRotation), Up);
                }
                if (HasRightEye)
                {
                    RightEyeTargetWorld = CenterTargetWorld + RightEyeInitallocalSpace.position;
                    rightEyeTransform.rotation = LookAtTarget(rightEyeTransform.position, RightEyeTargetWorld, math.mul(RightEyeInitallocalSpace.rotation, InversedHeadRotation), Up);
                }

                if (SMModuleDebugOptions.UseGizmos)
                {
                    if (RightEyeHasGizmo)
                    {
                        if (BasisGizmoManager.UpdateSphereGizmo(RightEyeGizmoIndex, RightEyeTargetWorld) == false)
                        {
                            RightEyeHasGizmo = false;
                        }
                    }
                    if (LeftEyeHasGizmo)
                    {
                        if (BasisGizmoManager.UpdateSphereGizmo(LeftEyeGizmoIndex, LeftEyeTargetWorld) == false)
                        {
                            LeftEyeHasGizmo = false;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Computes a world rotation for an eye so it looks from <paramref name="observerPosition"/>
        /// toward <paramref name="targetPosition"/>, using <paramref name="UP"/> as the up vector
        /// and blending with the provided <paramref name="initialRotation"/>.
        /// </summary>
        /// <param name="observerPosition">World position of the eye (observer).</param>
        /// <param name="targetPosition">World position the eye should look at.</param>
        /// <param name="initialRotation">Reference rotation captured at initialization.</param>
        /// <param name="UP">Up vector used to resolve roll.</param>
        /// <returns>Quaternion rotation that points the eye toward the target.</returns>
        private quaternion LookAtTarget(Vector3 observerPosition, Vector3 targetPosition, Quaternion initialRotation, Vector3 UP)
        {
            // Calculate direction to target
            float3 direction = (targetPosition - observerPosition).normalized;

            // Calculate look rotation
            quaternion lookRotation = Quaternion.LookRotation(direction, UP);

            // Combine with initial rotation for maintained orientation
            return initialRotation * math.inverse(initialRotation) * lookRotation;
        }
    }
}
