using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
namespace Basis.Scripts.UI
{
    public class BasisPointRaycaster : BaseRaycaster
    {
        public float MaxDistance = 30;
        public bool UseWorldPosition = true;
        /// <summary>
        /// Modified externally by Eye Input
        /// </summary>
        public Vector2 ScreenPoint { get; set; }
        public Ray ray { get; private set; }
        public RaycastHit ClosestRayCastHit { get; private set; }
        public RaycastHit[] PhysicHits { get; private set; }
        public int PhysicHitCount { get; private set; }
        public BasisInput BasisInput;
        [Header("Debug")]
        public bool EnableDebug = false;
        public List<GameObject> _DebugHitObjects;
        public override Camera eventCamera => BasisLocalCameraDriver.Instance.Camera;
        public void Initialize(BasisInput basisInput)
        {
            BasisInput = basisInput;
            PhysicHits = new RaycastHit[BasisPlayerInteract.k_MaxPhysicHitCount];

            // Create the ray with the adjusted starting position and direction
            UpdateRay();
        }
        public void UpdateRay()
        {
            this.transform.SetLocalPositionAndRotation(BasisInput.RaycastCoord.position, BasisInput.RaycastCoord.rotation);
            ray = new Ray(this.transform.position,this.transform.forward);
        }
        /// <summary>
        /// <summary>
        /// Run after Input control apply, before `AfterControlApply`
        /// </summary>
        public void UpdateRaycast()
        {
            if (UseWorldPosition)
            {
                UpdateRay();
            }
            else
            {
                // TODO: what? where does this come into play?
                ray = BasisLocalCameraDriver.Instance.Camera.ScreenPointToRay(ScreenPoint, BasisLocalCameraDriver.Instance.Camera.stereoActiveEye);
            }

            PhysicHitCount = Physics.RaycastNonAlloc(ray, PhysicHits, MaxDistance, BasisPlayerInteract.Mask, BasisPlayerInteract.TriggerInteraction);
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

                    for (int Index = 1; Index < PhysicHitCount; Index++)
                    {
                        if (PhysicHits[Index].distance < minDistance)
                        {
                            minDistance = PhysicHits[Index].distance;
                            closestIndex = Index;
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
