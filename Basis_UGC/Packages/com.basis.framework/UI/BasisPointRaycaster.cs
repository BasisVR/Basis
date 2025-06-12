using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Basis.Scripts.UI
{
    public partial class BasisPointRaycaster : BaseRaycaster
    {
        public Vector3 Direction = Vector3.forward;
        public float MaxDistance = 30;
        public LayerMask Mask;
        public QueryTriggerInteraction TriggerInteraction = QueryTriggerInteraction.UseGlobal;
        public bool UseWorldPosition = true;

        /// <summary>
        /// Modified externally by Eye Input
        /// </summary>
        public Vector2 ScreenPoint { get; set; }
        public Ray ray { get; private set; }
        public RaycastHit ClosestRayCastHit { get; private set; }
        public RaycastHit[] PhysicHits { get; private set; }
        public int PhysicHitCount { get; private set; }
        // NOTE: this needs to be >= max number of colliders it can potentiall hit a scene, otherwise it will behave oddly
        public static int k_MaxPhysicHitCount = 128;


        public BasisDeviceMatchSettings BasisDeviceMatchableNames;
        public BasisInput BasisInput;

        [Header("Debug")]
        public bool EnableDebug = false;
        public List<GameObject> _DebugHitObjects;


        public override Camera eventCamera => BasisLocalCameraDriver.Instance.Camera;
        const string k_PlayerLayer = "Player";
        const string k_IgnoreRayCastLayer = "Ignore Raycast";
        const string k_LocalPlayerAvatarLayer = "LocalPlayerAvatar";

        public void Initialize(BasisInput basisInput)
        {
            BasisInput = basisInput;
            BasisDeviceMatchableNames = BasisInput.BasisDeviceMatchSettings;
            PhysicHits = new RaycastHit[k_MaxPhysicHitCount];

            // Get the layer number for "Ignore Raycast" layer
            int ignoreRaycastLayer = LayerMask.NameToLayer(k_IgnoreRayCastLayer);

            // Get the layer number for "Player" layer
            int playerLayer = LayerMask.NameToLayer(k_PlayerLayer);

            int LocalPlayerAvatar = LayerMask.NameToLayer(k_LocalPlayerAvatarLayer);

            // Create a LayerMask that includes all layers
            LayerMask allLayers = ~0;

            // Exclude the "Ignore Raycast" and "Player" layers using bitwise AND and NOT operations
            Mask = allLayers & ~(1 << ignoreRaycastLayer) & ~(1 << playerLayer) & ~(1 << LocalPlayerAvatar);

            // Create the ray with the adjusted starting position and direction
            ray = new Ray(Vector3.zero, Direction);
        }

        private Vector3 LastRotation;
        /// <summary>
        /// Run after Input control apply, before `AfterControlApply`
        /// </summary>
        public void UpdateRaycast()
        {
            if (LastRotation != BasisDeviceMatchableNames.RotationRaycastOffset)
            {
                this.transform.localRotation = Quaternion.Euler(BasisDeviceMatchableNames.RotationRaycastOffset);
                LastRotation = BasisDeviceMatchableNames.RotationRaycastOffset;
            }
            if (UseWorldPosition)
            {
                transform.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                // Create the ray with the adjusted starting position and direction
                ray = new Ray()
                {
                    origin = Position + (Rotation * BasisDeviceMatchableNames.PositionRayCastOffset),
                    direction = transform.forward,
                };
            }
            else
            {
                // TODO: what? where does this come into play?
                ray = BasisLocalCameraDriver.Instance.Camera.ScreenPointToRay(ScreenPoint, Camera.MonoOrStereoscopicEye.Mono);
            }

            PhysicHitCount = Physics.RaycastNonAlloc(ray, PhysicHits, MaxDistance, Mask, TriggerInteraction);
            if (PhysicHitCount == 0)
            {
                ClosestRayCastHit = new RaycastHit();
            }
            else
            {
                if (PhysicHitCount > 1)
                {
                    int closestIndex = 0;
                    float minDistance = PhysicHits[0].distance;

                    for (int i = 1; i < PhysicHitCount; i++)
                    {
                        if (PhysicHits[i].distance < minDistance)
                        {
                            minDistance = PhysicHits[i].distance;
                            closestIndex = i;
                        }
                    }

                    // Swap the closest hit to the first position, if needed
                    if (closestIndex != 0)
                    {
                        (PhysicHits[0], PhysicHits[closestIndex]) = (PhysicHits[closestIndex], PhysicHits[0]);
                    }
                    ClosestRayCastHit = PhysicHits[0];
                }
                else
                {
                    ClosestRayCastHit = PhysicHits[0];
                }
            }
            if (EnableDebug)
            {
                UpdateDebug();
            }
        }

        // get a span of valid hits sorted by distance
        public RaycastHit[] GetHits()
        {
            return PhysicHits[..PhysicHitCount];
        }

        /// <summary>
        /// Gets the closest raycast hit up to maxDistance that is in the layerMask
        /// </summary>
        /// <param name="hitInfo"></param>
        /// <param name="maxDistance"></param>
        /// <param name="layerMask"></param>
        /// <returns>true on valid hit</returns> 
        public bool FirstHitInMask(out RaycastHit hitInfo, float maxDistance = float.PositiveInfinity, int layerMask = Physics.AllLayers)
        {
            hitInfo = default;

            for (int Index = 0; Index < PhysicHitCount; Index++)
            {
                var hit = PhysicHits[Index];
                if (hit.distance > maxDistance)
                    return false;
                if (hit.collider == null)
                    continue;

                if ((hit.collider.gameObject.layer & layerMask) != 0)
                {
                    hitInfo = hit;
                    return true;
                }
            }
            
            return false;
        }

        /// <summary>
        /// Gets the closest raycast hit up to maxDistance
        /// </summary>
        /// <param name="hitInfo"></param>
        /// <param name="maxDistance"></param>
        /// <returns>true on valid hit</returns> 
        public bool FirstHit(out RaycastHit hitInfo, float maxDistance = float.PositiveInfinity)
        {
            hitInfo = default;
            for (int Index = 0; Index < PhysicHitCount; Index++)
            {
                var hit = PhysicHits[Index];
                if (hit.distance > maxDistance)
                    return false;
                if (hit.collider == null)
                    continue;

                hitInfo = hit;
                return true;
            }
            
            return false;
        }
        private void UpdateDebug()
        {
            _DebugHitObjects = PhysicHits[..PhysicHitCount].Select(x => x.collider != null ? x.collider.gameObject : null).ToList();
        }


        public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
        {
        }
        /// <summary>
        /// dont just draw unless selected
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(ray.origin, ray.origin + ray.direction * MaxDistance);
        }
    }
}
