using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Eye_Follow
{
    [DefaultExecutionOrder(15002)]
    [System.Serializable]
    public class BasisLocalEyeDriver
    {
        public quaternion leftEyeInitialRotation;
        public quaternion rightEyeInitialRotation;
        public bool HasEvents = false;
        public static bool Override = false;
        public float lookSpeed; // Speed of looking
                                // Adjustable parameters
        public float MinLookAroundInterval = 1; // Interval between each look around in seconds
        public float MaxLookAroundInterval = 6;
        public float MaximumLookDistance = 0.25f; // Maximum offset from the target position
        public float minLookSpeed = 0.03f; // Minimum speed of looking
        public float maxLookSpeed = 0.1f; // Maximum speed of looking
        public Transform leftEyeTransform;
        public Transform rightEyeTransform;
        public Transform HeadTransform;
        public BasisCalibratedCoords LeftEyeInitallocalSpace;
        public BasisCalibratedCoords RightEyeInitallocalSpace;
        public Vector3 RandomizedPosition; // Target position to look at

        public bool HasLeftEye = false;
        public bool HasRightEye = false;
        public static bool HasHead = false;
        public bool LeftEyeHasGizmo;
        public bool RightEyeHasGizmo;
        public int LeftEyeGizmoIndex;
        public int RightEyeGizmoIndex;
        public Vector3 LeftEyeTargetWorld;
        public Vector3 RightEyeTargetWorld;
        public Vector3 CenterTargetWorld;
        public Vector3 AppliedOffset;
        public Vector3 EyeForwards = new float3(0, 0, 1);

        public float CurrentLookAroundInterval;
        public float timer; // Timer to track look-around interval
        public float DistanceBeforeTeleport = 30;
        public static BasisLocalEyeDriver Instance;
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
            //its regenerated this script will be nuked and rebuilt BasisLocalPlayer.OnLocalAvatarChanged -= AfterTeleport;
        }
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
            rightEyeTransform = BasisLocalPlayer.Instance.LocalAvatarDriver.References.RightEye;
            leftEyeTransform = BasisLocalPlayer.Instance.LocalAvatarDriver.References.LeftEye;
            HeadTransform = BasisLocalPlayer.Instance.LocalAvatarDriver.References.head;

            HasLeftEye = BasisLocalPlayer.Instance.LocalAvatarDriver.References.HasLeftEye;
            HasRightEye = BasisLocalPlayer.Instance.LocalAvatarDriver.References.HasRightEye;
            HasHead = BasisLocalPlayer.Instance.LocalAvatarDriver.References.Hashead;
            Vector3 HeadPosition = BasisLocalPlayer.Instance.LocalAvatarDriver.References.head.position;
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
        private void UpdateFaceVisibility(bool State)
        {
            IsEnabled = State;
        }
        public void UpdateGizmoUsage(bool State)
        {
            BasisDebug.Log("Running Bone EyeFollow Gizmos");
            if (State)
            {
                if (LeftEyeHasGizmo == false)
                {
                    BasisGizmoManager.CreateSphereGizmo(out LeftEyeGizmoIndex, LeftEyeTargetWorld, 0.1f, Color.cyan);
                    LeftEyeHasGizmo = true;
                }
                if (RightEyeHasGizmo == false)
                {
                    BasisGizmoManager.CreateSphereGizmo(out RightEyeGizmoIndex, RightEyeTargetWorld, 0.1f, Color.magenta);
                    RightEyeHasGizmo = true;
                }
            }
            else
            {
                LeftEyeHasGizmo = false;
                RightEyeHasGizmo = false;
            }
        }
        public void AfterTeleport()
        {
            if (RequiresUpdate())
            {
                Simulate();
            }
            CenterTargetWorld = RandomizedPosition;//will be caught up

        }
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
        public bool wasDisabled = false;
        public bool IsEnabled;

        public static bool RequiresUpdate()
        {
            return Override == false && HasHead;
        }
        public void Simulate()
        {
            if (IsEnabled)
            {
                // Update timer using DeltaTime
                timer += Time.deltaTime;

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
                Vector3 float3headPosition = headPosition;
                Quaternion QHeadRotation = headRotation;
                Quaternion InversedHeadRotation = math.inverse(headRotation);
                // Calculate the randomized target position using float3 for optimized math operations
                Vector3 targetPosition = float3headPosition + (QHeadRotation * EyeForwards) + AppliedOffset;

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
                // Set eye rotations using optimized float3 and quaternion operations
                if (HasLeftEye)
                {
                    LeftEyeTargetWorld = CenterTargetWorld + LeftEyeInitallocalSpace.position;
                    leftEyeTransform.rotation = LookAtTarget(leftEyeTransform.position, LeftEyeTargetWorld, math.mul(LeftEyeInitallocalSpace.rotation, InversedHeadRotation), HeadTransform.up);
                }
                if (HasRightEye)
                {
                    RightEyeTargetWorld = CenterTargetWorld + RightEyeInitallocalSpace.position;
                    rightEyeTransform.rotation = LookAtTarget(rightEyeTransform.position, RightEyeTargetWorld, math.mul(RightEyeInitallocalSpace.rotation, InversedHeadRotation), HeadTransform.up);
                }
                if (BasisGizmoManager.UseGizmos)
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
