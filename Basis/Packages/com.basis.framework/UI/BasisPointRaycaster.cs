using Basis.Scripts.BasisSdk.Interactions;
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
        public float MaxDistance = 120;
        public bool UseWorldPosition = true;

        /// <summary>
        /// Modified externally by Eye Input
        /// </summary>
        public Vector2 ScreenPoint { get; set; }

        public Ray ray { get; private set; }
        public RaycastHit ClosestRayCastHit { get; private set; }
        public RaycastHit[] PhysicHits { get; private set; }
        private RaycastHit[] PhysicBackcastHits;
        public int PhysicHitCount { get; private set; }
        public BasisInput BasisInput;

        [Header("Debug")]
        public bool EnableDebug = false;
        public List<GameObject> _DebugHitObjects;

        // Layer index, not a LayerMask
        private static int OverlayUILayer;

        public override Camera eventCamera => BasisLocalCameraDriver.Instance.Camera;

        public void Initialize(BasisInput basisInput)
        {
            OverlayUILayer = LayerMask.NameToLayer("OverlayUI");
            BasisInput = basisInput;
            PhysicHits = new RaycastHit[BasisPlayerInteract.k_MaxPhysicHitCount];
            PhysicBackcastHits = new RaycastHit[4]; // We don't need as many backcast hits.

            // Create the ray with the adjusted starting position and direction
            UpdateRay();
        }
        public void UpdateRay()
        {
            ray = new Ray(BasisInput.RaycastCoord.position, BasisInput.RaycastCoord.rotation * Vector3.forward);
        }
        /// <summary>
        /// Run after Input control apply, before `AfterControlApply` alloc free,
        /// uses camera raycasting when required of it.
        /// </summary>
        public void UpdateRaycast()
        {
            if (UseWorldPosition)
            {
                UpdateRay();
            }
            else
            {
                ray = BasisLocalCameraDriver.Instance.Camera.ScreenPointToRay(ScreenPoint, BasisLocalCameraDriver.Instance.Camera.stereoActiveEye);
            }

            PhysicHitCount = Physics.RaycastNonAlloc(ray, PhysicHits, MaxDistance, BasisPlayerInteract.Mask, BasisPlayerInteract.TriggerInteraction);
            if (PhysicHitCount == 0)
            {
                ClosestRayCastHit = new RaycastHit();
            }
            else
            {
                // Select best hit:
                // 1. Prefer OverlayUI layer (closest among those)
                // 2. If none on OverlayUI, choose closest by distance
                int bestIndex = -1;
                bool foundOverlay = false;
                float bestDistance = float.PositiveInfinity;

                for (int i = 0; i < PhysicHitCount; i++)
                {
                    var hit = PhysicHits[i];
                    if (hit.collider == null)
                    {
                        continue;
                    }

                    int hitLayer = hit.collider.gameObject.layer;
                    bool isOverlay = hitLayer == OverlayUILayer;

                    if (isOverlay)
                    {
                        if (!foundOverlay || hit.distance < bestDistance)
                        {
                            foundOverlay = true;
                            bestIndex = i;
                            bestDistance = hit.distance;
                        }
                    }
                    else if (!foundOverlay)
                    {
                        // Only consider non-overlay hits if we haven't found any overlay yet
                        if (hit.distance < bestDistance)
                        {
                            bestIndex = i;
                            bestDistance = hit.distance;
                        }
                    }
                }

                if (bestIndex >= 0)
                {
                    ClosestRayCastHit = PhysicHits[bestIndex];

                    // Keep "primary" hit at index 0 for any existing assumptions
                    if (bestIndex != 0)
                    {
                        (PhysicHits[0], PhysicHits[bestIndex]) = (PhysicHits[bestIndex], PhysicHits[0]);
                    }
                }
                else
                {
                    // No valid collider hits found
                    ClosestRayCastHit = new RaycastHit();
                }
            }
            // One last thing: Cast backwards just in case the origin of the ray was inside a collider.
            {
                float backcastDistance = ClosestRayCastHit.distance > 0 ? ClosestRayCastHit.distance : MaxDistance;
                Ray backcastRay = new Ray(ray.origin + ray.direction * backcastDistance, -ray.direction);
                int backcastHitCount = Physics.RaycastNonAlloc(backcastRay, PhysicBackcastHits, backcastDistance, BasisPlayerInteract.Mask, BasisPlayerInteract.TriggerInteraction);
                // Search for the farthest distance here (closest to the original ray origin)
                float bestBackcastDistance = 0.0f;
                for (int i = 0; i < backcastHitCount; i++)
                {
                    RaycastHit hit = PhysicBackcastHits[i];
                    if (hit.distance > bestBackcastDistance)
                    {
                        bestBackcastDistance = hit.distance;
                        ClosestRayCastHit = hit;
                    }
                }
            }

            if (EnableDebug)
            {
                UpdateDebug();
            }
        }

        // Get a span of valid hits (still sorted by original Physics order,
        // but index 0 is now the "best" hit according to our rule).
        public RaycastHit[] GetHits()
        {
            return PhysicHits[..PhysicHitCount];
        }

        /// <summary>
        /// Gets the closest raycast hit up to maxDistance,
        /// with OverlayUI layer overriding if present.
        /// </summary>
        /// <param name="hitInfo"></param>
        /// <param name="maxDistance"></param>
        /// <returns>true on valid hit</returns>
        public bool FirstHit(out RaycastHit hitInfo, float maxDistance = float.PositiveInfinity)
        {
            hitInfo = default;

            if (ClosestRayCastHit.collider == null)
                return false;

            if (ClosestRayCastHit.distance > maxDistance)
                return false;

            hitInfo = ClosestRayCastHit;
            return true;
        }

        private void UpdateDebug()
        {
            _DebugHitObjects = PhysicHits[..PhysicHitCount]
                .Select(x => x.collider != null ? x.collider.gameObject : null)
                .ToList();
        }

        public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
        {
        }

        /// <summary>
        /// Don't just draw unless selected
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (SMModuleDebugOptions.UseGizmos)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(ray.origin, ray.origin + ray.direction * MaxDistance);
            }
        }
    }
}
