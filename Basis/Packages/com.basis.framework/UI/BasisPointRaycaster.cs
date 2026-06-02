using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
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
        private const float MaxDistanceScaleThresholdMeters = 100f;
        public float MaxDistance = 120;
        public float EffectiveMaxDistance = 120;
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
        public const string OverlayUI = "OverlayUI";
        public override Camera eventCamera => BasisLocalCameraDriver.Instance.Camera;

        // ---------------------------
        // Mode / Placement additions
        // ---------------------------

        public enum ControlMode
        {
            Normal,     // existing behavior (prefers OverlayUI)
            Placement   // placement-focused behavior (typically ignores OverlayUI and blocks other consumers)
        }

        [Header("Control Mode")]
        [SerializeField]
        private ControlMode _mode = ControlMode.Normal;
        public ControlMode Mode => _mode;

        /// <summary>
        /// When true, other interaction systems should not consume this raycaster this frame.
        /// (e.g. do not "click" or "grab" while placing)
        /// </summary>
        public bool BlocksOtherControl => _mode == ControlMode.Placement;

        /// <summary>
        /// Placement OBB result (center/rotation/halfExtents) derived from the best placement hit.
        /// Consumers should read this instead of raw hits when in Placement mode.
        /// </summary>
        public struct PlacementResult
        {
            public bool HasHit;
            public Ray Ray;
            public RaycastHit Hit;

            // Oriented box for placement preview/validation
            public Vector3 Center;
            public Quaternion Rotation;

            // Half size of the placement box in *local* space where local Y is "up"
            public Vector3 Extents;
            public Vector3 LocalBoundsCenter; // add this

            public float Distance => HasHit ? Hit.distance : float.PositiveInfinity;
        }
        [SerializeField]
        public PlacementResult CurrentPlacement;

        /// <summary>
        /// Enter placement mode and define the placement bounds half extents.
        /// Example half extents: (0.5, 0.1, 0.5) for a 1m x 0.2m x 1m footprint.
        /// </summary>
        public void EnterPlacementMode(Vector3 halfExtents, Vector3 localBoundsCenter)
        {
            _mode = ControlMode.Placement;
            CurrentPlacement = new PlacementResult
            {
                HasHit = false,
                Ray = ray,
                Extents = halfExtents,
                LocalBoundsCenter = localBoundsCenter
            };
        }
        public void ExitPlacementMode()
        {
            _mode = ControlMode.Normal;
            CurrentPlacement = default;
        }

        /// <summary>
        /// Optional: direct setter if you prefer driving mode externally.
        /// </summary>
        public void SetMode(ControlMode mode)
        {
            if (_mode == mode)
            {
                return;
            }

            _mode = mode;

            if (_mode != ControlMode.Placement)
            {
                CurrentPlacement = default;
            }
        }
        public void Initialize(BasisInput basisInput)
        {
            OverlayUILayer = LayerMask.NameToLayer(OverlayUI);
            BasisInput = basisInput;
            PhysicHits = new RaycastHit[BasisPlayerInteract.k_MaxPhysicHitCount];
            PhysicBackcastHits = new RaycastHit[4]; // We don't need as many backcast hits.
            RefreshEffectiveMaxDistance();
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnPlayersHeightChangedNextFrame;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnPlayersHeightChangedNextFrame;

            // Create the ray with the adjusted starting position and direction
            UpdateRay();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnPlayersHeightChangedNextFrame;
        }

        private void OnPlayersHeightChangedNextFrame(BasisHeightDriver.HeightModeChange _)
        {
            RefreshEffectiveMaxDistance();
        }

        private void RefreshEffectiveMaxDistance()
        {
            EffectiveMaxDistance = MaxDistance + Mathf.Max(0f, BasisHeightDriver.SelectedScaledAvatarHeight - MaxDistanceScaleThresholdMeters);
        }

        public void UpdateRay()
        {
            ray = GetUpdatedRay();
        }

        public Ray GetUpdatedRay()
        {
            if (UseWorldPosition)
            {
                return new Ray(BasisInput.RaycastCoord.position, BasisInput.RaycastCoord.rotation * Vector3.forward);
            }
            var Camera = BasisLocalCameraDriver.Instance.Camera;
            return Camera.ScreenPointToRay(ScreenPoint, Camera.stereoActiveEye);
        }

        /// <summary>
        /// Run after Input control apply, before `AfterControlApply` alloc free,
        /// uses camera raycasting when required of it.
        /// </summary>
        public void UpdateRaycast()
        {
            UpdateRay();

            PhysicHitCount = Physics.RaycastNonAlloc(
                ray,
                PhysicHits,
                EffectiveMaxDistance,
                BasisPlayerInteract.Mask,
                BasisPlayerInteract.TriggerInteraction);

            // Branch per mode
            if (_mode == ControlMode.Placement)
            {
                UpdatePlacementFromHits();
                if (EnableDebug)
                {
                    UpdateDebug();
                }

                return;
            }

            // Normal mode: keep your existing OverlayUI-preferred best hit
            UpdateClosestHitPreferOverlayUI();

            // One last thing: Cast backwards just in case the origin of the ray was inside a collider.
            DoBackcastFixup();

            if (EnableDebug)
            {
                UpdateDebug();
            }
        }
        private void UpdateClosestHitPreferOverlayUI()
        {
            if (PhysicHitCount == 0)
            {
                ClosestRayCastHit = new RaycastHit();
                return;
            }

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
                    continue;

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

        private void DoBackcastFixup()
        {
            float backcastDistance = ClosestRayCastHit.distance > 0 ? ClosestRayCastHit.distance : EffectiveMaxDistance;
            Ray backcastRay = new Ray(ray.origin + ray.direction * backcastDistance, -ray.direction);

            int backcastHitCount = Physics.RaycastNonAlloc(
                backcastRay,
                PhysicBackcastHits,
                backcastDistance,
                BasisPlayerInteract.Mask,
                BasisPlayerInteract.TriggerInteraction);

            // Only colliders the forward raycast missed (origin-inside-collider case)
            // should override the choice already made by UpdateClosestHitPreferOverlayUI.
            // Skipping forward-known colliders preserves the OverlayUI preference when
            // a non-overlay collider (e.g. a pickup) sits between the origin and the menu.
            float bestBackcastDistance = 0.0f;
            for (int i = 0; i < backcastHitCount; i++)
            {
                RaycastHit hit = PhysicBackcastHits[i];
                if (hit.collider == null)
                    continue;
                if (ColliderInForwardHits(hit.collider))
                    continue;
                if (hit.distance > bestBackcastDistance)
                {
                    bestBackcastDistance = hit.distance;
                    ClosestRayCastHit = hit;
                }
            }
        }

        private bool ColliderInForwardHits(Collider collider)
        {
            for (int i = 0; i < PhysicHitCount; i++)
            {
                if (PhysicHits[i].collider == collider)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Placement-focused: pick best placement hit (typically ignoring OverlayUI),
        /// then compute an oriented bounds (OBB) that sits on the surface.
        /// </summary>
        private void UpdatePlacementFromHits()
        {
            // Choose best placement hit: closest NON-OverlayUI
            RaycastHit best = default;
            bool has = false;
            float bestDist = float.PositiveInfinity;

            for (int i = 0; i < PhysicHitCount; i++)
            {
                var hit = PhysicHits[i];
                if (hit.collider == null)
                    continue;

                // Placement should usually ignore UI. If you want UI placement, remove this.
                if (hit.collider.gameObject.layer == OverlayUILayer)
                    continue;

                if (hit.distance < bestDist)
                {
                    bestDist = hit.distance;
                    best = hit;
                    has = true;
                }
            }

            if (!has)
            {
                CurrentPlacement = new PlacementResult
                {
                    HasHit = false,
                    Ray = ray,
                    Extents = CurrentPlacement.Extents // preserve configured extents
                };
                return;
            }

            // Compute placement OBB pose (center + rotation)
            ComputePlacementOBB(best, ray, CurrentPlacement.Extents, CurrentPlacement.LocalBoundsCenter, out var center, out var rot);//ComputePlacementOBB(best, ray, CurrentPlacement.Extents, out var center, out var rot);

            CurrentPlacement = new PlacementResult
            {
                HasHit = true,
                Ray = ray,
                Hit = best,
                Center = center,
                Rotation = rot,
                Extents = CurrentPlacement.Extents
            };
        }

        /// <summary>
        /// Build a rotation from hit.normal as up, and ray direction projected onto surface as forward.
        /// Place the OBB so its "bottom" touches the hit point (local Y assumed up).
        /// </summary>
        // private static void ComputePlacementOBB(
        //     RaycastHit hit,
        //     Ray ray,
        //     Vector3 halfExtentsLocal,
        //     out Vector3 center,
        //     out Quaternion rotation)
        // {
        //     Vector3 up = hit.normal.normalized;

        //     Vector3 forward = Vector3.ProjectOnPlane(ray.direction, up);
        //     if (forward.sqrMagnitude < 1e-6f)
        //     {
        //         forward = Vector3.Cross(up, Vector3.right);
        //         if (forward.sqrMagnitude < 1e-6f)
        //             forward = Vector3.Cross(up, Vector3.forward);
        //     }
        //     forward.Normalize();

        //     rotation = Quaternion.LookRotation(forward, up);

        //     // local Y is up => halfExtentsLocal.y is "half height"
        //     center = hit.point + up * halfExtentsLocal.y;
        // }

        private static void ComputePlacementOBB(
            RaycastHit hit,
            Ray ray,
            Vector3 halfExtentsLocal,
            Vector3 localBoundsCenter,
            out Vector3 center,
            out Quaternion rotation)
        {
            Vector3 up = hit.normal.normalized;

            Vector3 forward = Vector3.ProjectOnPlane(ray.direction, up);
            if (forward.sqrMagnitude < 1e-6f)
            {
                forward = Vector3.Cross(up, Vector3.right);
                if (forward.sqrMagnitude < 1e-6f)
                    forward = Vector3.Cross(up, Vector3.forward);
            }
            forward.Normalize();

            rotation = Quaternion.LookRotation(forward, up);

            // Distance from pivot to the very bottom of the bounds in local space
            float pivotToBottom = localBoundsCenter.y - halfExtentsLocal.y;

            // Lift pivot up by the negated offset so the mesh bottom sits on the surface
            center = hit.point + up * (-pivotToBottom);
        }

        // Get a span of valid hits (still sorted by original Physics order,
        // but index 0 is now the "best" hit according to normal mode rule).
        public RaycastHit[] GetHits()
        {
            return PhysicHits[..PhysicHitCount];
        }

        /// <summary>
        /// Gets the closest raycast hit up to maxDistance,
        /// with OverlayUI layer overriding if present.
        /// In Placement mode this returns false to "block other data".
        /// Use TryGetPlacement instead.
        /// </summary>
        public bool FirstHit(out RaycastHit hitInfo, float maxDistance = float.PositiveInfinity)
        {
            hitInfo = default;

            // Hard block interaction consumers during placement
            if (_mode == ControlMode.Placement)
                return false;

            if (ClosestRayCastHit.collider == null)
                return false;

            if (ClosestRayCastHit.distance > maxDistance)
                return false;

            hitInfo = ClosestRayCastHit;
            return true;
        }

        /// <summary>
        /// Access placement data when in Placement mode.
        /// </summary>
        public bool TryGetPlacement(out PlacementResult placement, float maxDistance = float.PositiveInfinity)
        {
            placement = CurrentPlacement;

            if (_mode != ControlMode.Placement)
            {
                return false;
            }

            if (!placement.HasHit || placement.Hit.collider == null)
            {
                return false;
            }

            if (placement.Hit.distance > maxDistance)
            {
                return false;
            }

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
            if (!SMModuleDebugOptions.UseGizmos)
                return;

            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(ray.origin, ray.origin + ray.direction * EffectiveMaxDistance);

            if (_mode == ControlMode.Placement && CurrentPlacement.HasHit)
            {
                Gizmos.color = Color.yellow;

                Matrix4x4 old = Gizmos.matrix;
                Gizmos.matrix = Matrix4x4.TRS(CurrentPlacement.Center, CurrentPlacement.Rotation, Vector3.one);

                // DrawWireCube expects FULL size; CurrentPlacement.Extents is HALF size
                Gizmos.DrawWireCube(Vector3.zero, CurrentPlacement.Extents * 2f);

                Gizmos.matrix = old;
            }
        }
    }
}
