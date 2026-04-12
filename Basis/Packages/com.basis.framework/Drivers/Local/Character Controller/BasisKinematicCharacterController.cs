using UnityEngine;

namespace Basis.Scripts.BasisCharacterController
{
    /// <summary>
    /// A kinematic character controller that replaces Unity's built-in CharacterController.
    /// Uses a CapsuleCollider + kinematic Rigidbody so that the player can be rotated
    /// freely, enabling custom gravity directions (not Y-axis locked).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(CapsuleCollider))]
    public class BasisKinematicCharacterController : MonoBehaviour
    {
        // Capsule Collider
        public float _height = 2f;
        public float _radius = 0.3f;
        public Vector3 _center = new Vector3(0f, 1f, 0f);

        // CharacterController Parameters
        public float _skinWidth = 0.01f;
        public float _stepOffset = 0.3f;
        public float _minMoveDistance = 0f;
        public float _slopeLimit = 45f;
        public bool _detectCollisions = true;

        // Gravity
        public Vector3 _gravityDirection = Vector3.down;

        // Runtime Info
        public Rigidbody _rigidbody;
        public CapsuleCollider _capsule;
        public bool isGrounded;
        private Vector3 _groundNormal = Vector3.up;
        private CollisionFlags _lastFlags;

        // Collision
        // 16 hits/overlaps covers worst-case scenarios for capsule casts in typical
        // game environments. NonAlloc silently drops excess results but 16 is generous —
        // most casts hit 1-3 colliders. Increase only if levels have extreme collider density.
        private const int MaxHits = 16;
        private const int MaxOverlaps = 16;
        private const int MaxDepenetrationIterations = 4;
        private const int MaxMoveIterations = 4;
        private readonly RaycastHit[] _hitBuffer = new RaycastHit[MaxHits];
        private readonly Collider[] _overlapBuffer = new Collider[MaxOverlaps];
        private const float GroundProbeExtra = 0.04f;
        public delegate void KCCColliderHit(KCCHitInfo hit);
        public KCCColliderHit OnKCCColliderHit;

        // Cached collision mask — rebuilt once per Move() instead of per-cast.
        private int _collisionMask;
        private bool _collisionMaskDirty = true;

        // Cached per-Move() to avoid repeated Transform property access.
        private Quaternion _cachedRotation;

        //Public Get Set

        public float height
        {
            get => _height;
            set
            {
                _height = Mathf.Max(value, 0.001f);
                SyncCapsule();
            }
        }

        public float radius
        {
            get => _radius;
            set
            {
                _radius = Mathf.Max(value, 0.001f);
                SyncCapsule();
            }
        }

        public Vector3 center
        {
            get => _center;
            set
            {
                _center = value;
                SyncCapsule();
            }
        }

        public float skinWidth
        {
            get => _skinWidth;
            set => _skinWidth = Mathf.Max(value, 0.001f);
        }

        public float stepOffset
        {
            get => _stepOffset;
            set => _stepOffset = Mathf.Max(value, 0f);
        }

        public float minMoveDistance
        {
            get => _minMoveDistance;
            set => _minMoveDistance = Mathf.Max(value, 0f);
        }

        public float slopeLimit
        {
            get => _slopeLimit;
            set => _slopeLimit = Mathf.Clamp(value, 0f, 90f);
        }

        public bool detectCollisions
        {
            get => _detectCollisions;
            set
            {
                _detectCollisions = value;
                _capsule.enabled = value;
            }
        }

        /// <summary>
        /// The ground surface normal from the last Move() call. Only valid when isGrounded is true.
        /// </summary>
        public Vector3 groundNormal => _groundNormal;

        /// <summary>
        /// The direction gravity pulls the character. Defaults to Vector3.down.
        /// Setting this allows the character to walk on walls/ceilings.
        /// Must be normalized.
        /// </summary>
        public Vector3 GravityDirection
        {
            get => _gravityDirection;
            set => _gravityDirection = value.normalized;
        }

        /// <summary>
        /// The "up" direction for this character, opposite of gravity.
        /// Guaranteed to be normalized; falls back to Vector3.up if gravity direction is zero.
        /// </summary>
        public Vector3 UpDirection
        {
            get
            {
                Vector3 up = -_gravityDirection;
                if (up.sqrMagnitude < 0.0001f) up = Vector3.up;
                return up.normalized;
            }
        }

        #region UNITY LIFECYCLE

        public void PlayerInitialize()
        {
            // Ensure gravity direction is valid (may be zero if deserialized from a fresh component)
            if (_gravityDirection.sqrMagnitude < 0.0001f)
                _gravityDirection = Vector3.down;
            SyncCapsule();
        }

        private void SyncCapsule()
        {
            _capsule.direction = 1; // Y-axis
            _capsule.center = _center;
            _capsule.radius = _radius;
            _capsule.height = _height;
            _capsule.isTrigger = false;
        }

        // ── Core Move method ────────────────────────────────────────────

        /// <summary>
        /// Moves the character by <paramref name="motion"/> with full collision
        /// resolution. Returns CollisionFlags indicating which sides were hit.
        /// Gravity/jump velocity should already be included in motion.
        /// </summary>
        public CollisionFlags Move(Vector3 motion)
        {
            _lastFlags = CollisionFlags.None;
            _collisionMaskDirty = true; // Refresh mask once per Move()

            if (!enabled || !_detectCollisions)
            {
                transform.position += motion;
                isGrounded = false;
                return _lastFlags;
            }

            // Cache rotation once per Move() — used by GetCapsuleEnds, GroundProbe, Depenetrate.
            transform.GetPositionAndRotation(out Vector3 pos, out _cachedRotation);

            if (motion.sqrMagnitude < _minMoveDistance * _minMoveDistance)
            {
                GroundProbe(pos);
                return _lastFlags;
            }

            Vector3 up = UpDirection;
            float cosSlope = Mathf.Cos(_slopeLimit * Mathf.Deg2Rad);
            float verticalComponent = Vector3.Dot(motion, up);
            Vector3 verticalMotion = up * verticalComponent;
            Vector3 horizontalMotion = motion - verticalMotion;
            bool movingDown = verticalComponent < 0f;

            // ── Grounded behaviour: slope projection + ground snap ──────
            if (isGrounded && movingDown)
            {
                float groundDot = Vector3.Dot(_groundNormal, up);
                bool walkableSlope = groundDot >= cosSlope;

                if (walkableSlope)
                {
                    if (horizontalMotion.sqrMagnitude > 0.00001f)
                    {
                        horizontalMotion = Vector3.ProjectOnPlane(horizontalMotion, _groundNormal);
                        float origLen = (motion - verticalMotion).magnitude;
                        float projLen = horizontalMotion.magnitude;
                        if (projLen > 0.00001f)
                            horizontalMotion = horizontalMotion * (origLen / projLen);
                    }
                    verticalMotion = Vector3.zero;
                }
            }

            // ── Horizontal movement with step-up fallback ───────────────
            if (horizontalMotion.sqrMagnitude > 0.00001f)
            {
                Vector3 beforeSlide = pos;
                pos = SimpleMove(pos, horizontalMotion, ref _lastFlags, up, cosSlope, isHorizontal: true);

                // If grounded and slide made little horizontal progress, try stepping up
                if (isGrounded && _stepOffset > 0f && movingDown)
                {
                    Vector3 traveled = pos - beforeSlide;
                    Vector3 horizontalTraveled = traveled - up * Vector3.Dot(traveled, up);
                    Vector3 horizontalWanted = horizontalMotion - up * Vector3.Dot(horizontalMotion, up);
                    float wantedLen = horizontalWanted.magnitude;

                    if (wantedLen > 0.001f && horizontalTraveled.magnitude < wantedLen * 0.5f)
                    {
                        // Blocked — try step-up from the pre-slide position
                        Vector3 stepPos = beforeSlide;
                        if (TryStepUp(ref stepPos, horizontalMotion, up, cosSlope))
                        {
                            pos = stepPos;
                        }
                    }
                }
            }

            // vertical movement
            if (verticalMotion.sqrMagnitude > 0.00001f)
            {
                pos = SimpleMove(pos, verticalMotion, ref _lastFlags, up, cosSlope, isHorizontal: false);
            }

            // snap to ground
            if (isGrounded && verticalComponent <= 0f)
            {
                pos = GroundSnap(pos, up, cosSlope);
            }

            // Depenetration if clipping with a collider
            pos = Depenetrate(pos);

            // Single transform write per Move() call
            transform.position = pos;

            // Ground probe
            GroundProbe(pos);

            return _lastFlags;
        }

        private Vector3 SimpleMove(Vector3 position, Vector3 motion, ref CollisionFlags flags, Vector3 up, float cosSlope, bool isHorizontal)
        {
            if (motion.sqrMagnitude < 0.00001f) return position;

            Vector3 remaining = motion;
            for (int i = 0; i < MaxMoveIterations && remaining.sqrMagnitude > 0.00001f; i++)
            {
                float dist = remaining.magnitude;
                Vector3 dir = remaining / dist;

                GetCapsuleEnds(position, out Vector3 p1, out Vector3 p2);
                float castRadius = _radius - _skinWidth;
                if (castRadius < 0.001f) castRadius = 0.001f;

                int hitCount = Physics.CapsuleCastNonAlloc(
                    p1, p2, castRadius,
                    dir, _hitBuffer,
                    dist + _skinWidth,
                    GetCollisionMask(),
                    QueryTriggerInteraction.Ignore
                );

                if (!FindClosestHit(hitCount, out RaycastHit closestHit))
                {
                    position += remaining;
                    break;
                }

                // Move up to the hit point (minus skin)
                float safeDistance = Mathf.Max(closestHit.distance - _skinWidth, 0f);
                position += dir * safeDistance;

                // Classify collision
                Vector3 hitNormal = closestHit.normal;
                float dotUp = Vector3.Dot(hitNormal, up);

                if (dotUp > 0.7f)
                    flags |= CollisionFlags.Below;
                else if (dotUp < -0.7f)
                    flags |= CollisionFlags.Above;
                else
                    flags |= CollisionFlags.Sides;

                FireHitCallback(closestHit, dir, dist);

                // move along the surface
                remaining -= dir * safeDistance;
                remaining = Vector3.ProjectOnPlane(remaining, hitNormal);

                // For horizontal movement on steep slopes, prevent climbing
                if (isHorizontal && dotUp > 0f && dotUp < cosSlope)
                {
                    float upComponent = Vector3.Dot(remaining, up);
                    if (upComponent > 0f)
                        remaining -= up * upComponent;
                }
            }

            return position;
        }

        private bool TryStepUp(ref Vector3 pos, Vector3 horizontalMotion, Vector3 up, float cosSlope)
        {
            float castRadius = _radius - _skinWidth;
            if (castRadius < 0.001f) castRadius = 0.001f;

            Vector3 hDir = horizontalMotion.normalized;
            // Use a generous forward distance so we clear the step edge.
            // At minimum cast the frame motion, but ensure we cast at least
            // radius + skin so the capsule actually clears the step lip.
            float hDist = Mathf.Max(horizontalMotion.magnitude, _radius + _skinWidth);

            // Phase 1: Cast UP to find ceiling clearance
            float maxUpDist = _stepOffset;
            GetCapsuleEnds(pos, out Vector3 up1, out Vector3 up2);
            int hitCount = Physics.CapsuleCastNonAlloc(
                up1, up2, castRadius,
                up, _hitBuffer,
                maxUpDist + _skinWidth,
                GetCollisionMask(),
                QueryTriggerInteraction.Ignore
            );
            if (FindClosestHit(hitCount, out RaycastHit ceilingHit))
            {
                maxUpDist = Mathf.Max(ceilingHit.distance - _skinWidth, 0f);
            }
            if (maxUpDist < 0.01f)
                return false; // No room to step up

            Vector3 elevated = pos + up * maxUpDist;

            // Phase 2: Cast FORWARD at elevated height
            GetCapsuleEnds(elevated, out Vector3 ep1, out Vector3 ep2);
            hitCount = Physics.CapsuleCastNonAlloc(
                ep1, ep2, castRadius,
                hDir, _hitBuffer,
                hDist + _skinWidth,
                GetCollisionMask(),
                QueryTriggerInteraction.Ignore
            );

            float forwardDist = hDist;
            if (FindClosestHit(hitCount, out RaycastHit forwardHit))
            {
                forwardDist = Mathf.Max(forwardHit.distance - _skinWidth, 0f);
            }
            // Need at least a tiny forward movement to land on top of the step
            if (forwardDist < _skinWidth)
                return false;

            Vector3 forwarded = elevated + hDir * Mathf.Min(forwardDist, hDist);

            // Phase 3: Cast DOWN to find the step surface
            GetCapsuleEnds(forwarded, out Vector3 dp1, out Vector3 dp2);
            float downDist = maxUpDist + GroundProbeExtra;
            hitCount = Physics.CapsuleCastNonAlloc(
                dp1, dp2, castRadius,
                -up, _hitBuffer,
                downDist,
                GetCollisionMask(),
                QueryTriggerInteraction.Ignore
            );

            if (!FindClosestHit(hitCount, out RaycastHit stepHit))
                return false; // No ground found after stepping

            // Verify the surface is walkable
            float dotUp = Vector3.Dot(stepHit.normal, up);
            if (dotUp < cosSlope)
                return false;

            // Snap down to the step surface
            float snapDown = Mathf.Max(stepHit.distance - _skinWidth, 0f);
            Vector3 finalPos = forwarded - up * snapDown;

            // Must have actually gained height
            float heightGain = Vector3.Dot(finalPos - pos, up);
            if (heightGain < 0.001f)
                return false;

            pos = finalPos;
            _lastFlags |= CollisionFlags.Below;
            return true;
        }

        /// <summary>
        /// When grounded and not jumping, cast downward to anchor the character
        /// to the ground surface. Prevents floating over small bumps and slopes.
        /// </summary>
        private Vector3 GroundSnap(Vector3 position, Vector3 up, float cosSlope)
        {
            GetCapsuleEnds(position, out Vector3 p1, out Vector3 p2);
            float castRadius = _radius - _skinWidth;
            if (castRadius < 0.001f) castRadius = 0.001f;

            // Snap distance: enough to cover step offset + skin + small gap
            float snapDist = _stepOffset + _skinWidth + GroundProbeExtra;
            int hitCount = Physics.CapsuleCastNonAlloc(
                p1, p2, castRadius,
                -up, _hitBuffer,
                snapDist,
                GetCollisionMask(),
                QueryTriggerInteraction.Ignore
            );

            if (FindClosestHit(hitCount, out RaycastHit snapHit))
            {
                float dotUp = Vector3.Dot(snapHit.normal, up);
                if (dotUp >= cosSlope)
                {
                    float drop = Mathf.Max(snapHit.distance - _skinWidth, 0f);
                    if (drop > 0.0001f)
                    {
                        position -= up * drop;
                        _lastFlags |= CollisionFlags.Below;
                    }
                }
            }

            return position;
        }

        private Vector3 Depenetrate(Vector3 position)
        {
            for (int iter = 0; iter < MaxDepenetrationIterations; iter++)
            {
                GetCapsuleEnds(position, out Vector3 p1, out Vector3 p2);

                int overlapCount = Physics.OverlapCapsuleNonAlloc(
                    p1, p2, _radius,
                    _overlapBuffer,
                    GetCollisionMask(),
                    QueryTriggerInteraction.Ignore
                );

                bool resolved = true;
                for (int i = 0; i < overlapCount; i++)
                {
                    Collider other = _overlapBuffer[i];
                    if (other == _capsule) continue;

                    if (Physics.ComputePenetration(
                            _capsule, position, _cachedRotation,
                            other, other.transform.position, other.transform.rotation,
                            out Vector3 dir, out float dist))
                    {
                        position += dir * (dist + 0.001f);
                        resolved = false;
                    }
                }

                if (resolved) break;
            }

            return position;
        }

        private void GroundProbe(Vector3 pos)
        {
            if (!_detectCollisions || !enabled)
            {
                isGrounded = false;
                _groundNormal = UpDirection;
                return;
            }

            Vector3 up = UpDirection;

            // Cast a small sphere downward from the bottom of the capsule
            Vector3 worldCenter = pos + _cachedRotation * _center;
            float halfHeight = (_height * 0.5f) - _radius;
            Vector3 bottom = worldCenter - up * halfHeight;

            float castRadius = _radius - _skinWidth;
            if (castRadius < 0.001f) castRadius = 0.001f;

            float probeOffset = _skinWidth + 0.01f;
            int hitCount = Physics.SphereCastNonAlloc(
                bottom + up * probeOffset,
                castRadius,
                -up,
                _hitBuffer,
                probeOffset + GroundProbeExtra,
                GetCollisionMask(),
                QueryTriggerInteraction.Ignore
            );

            isGrounded = false;
            _groundNormal = up;
            float cosSlope = Mathf.Cos(_slopeLimit * Mathf.Deg2Rad);
            float closestDist = float.MaxValue;

            for (int i = 0; i < hitCount; i++)
            {
                if (_hitBuffer[i].collider == _capsule) continue;
                float dotUp = Vector3.Dot(_hitBuffer[i].normal, up);
                if (dotUp >= cosSlope && _hitBuffer[i].distance < closestDist)
                {
                    closestDist = _hitBuffer[i].distance;
                    isGrounded = true;
                    _groundNormal = _hitBuffer[i].normal;
                }
            }
        }
        #endregion

        #region HELPERS

        private void GetCapsuleEnds(Vector3 position, out Vector3 point1, out Vector3 point2)
        {
            Vector3 worldCenter = position + _cachedRotation * _center;
            Vector3 capsuleUp = _cachedRotation * Vector3.up;
            float halfHeight = (_height * 0.5f) - _radius;
            if (halfHeight < 0f) halfHeight = 0f;
            point1 = worldCenter + capsuleUp * halfHeight;
            point2 = worldCenter - capsuleUp * halfHeight;
        }

        private int GetCollisionMask()
        {
            if (_collisionMaskDirty)
            {
                int layer = gameObject.layer;
                int mask = 0;
                for (int i = 0; i < 32; i++)
                {
                    if (!Physics.GetIgnoreLayerCollision(layer, i))
                        mask |= (1 << i);
                }
                _collisionMask = mask;
                _collisionMaskDirty = false;
            }
            return _collisionMask;
        }

        /// <summary>
        /// Call if the object's layer or physics layer collision matrix changes at runtime.
        /// The mask is automatically refreshed at the start of each Move() call.
        /// </summary>
        public void InvalidateCollisionMask()
        {
            _collisionMaskDirty = true;
        }

        private bool FindClosestHit(int hitCount, out RaycastHit closest)
        {
            float closestDist = float.MaxValue;
            closest = default;
            bool found = false;
            for (int i = 0; i < hitCount; i++)
            {
                if (_hitBuffer[i].collider == _capsule) continue;
                if (_hitBuffer[i].distance < closestDist)
                {
                    closestDist = _hitBuffer[i].distance;
                    closest = _hitBuffer[i];
                    found = true;
                }
            }
            return found;
        }

        private bool HasValidHit(int hitCount)
        {
            for (int i = 0; i < hitCount; i++)
            {
                if (_hitBuffer[i].collider != _capsule) return true;
            }
            return false;
        }

        private void FireHitCallback(RaycastHit hit, Vector3 moveDir, float moveDist)
        {
            if (OnKCCColliderHit == null) return;
            KCCHitInfo info;
            info.collider = hit.collider;
            info.point = hit.point;
            info.normal = hit.normal;
            info.moveDirection = moveDir;
            info.moveLength = moveDist;
            OnKCCColliderHit(info);
        }

        #endregion
    }

    /// <summary>
    /// Hit information passed to the KCC collision callback, mirroring ControllerColliderHit.
    /// </summary>
    public struct KCCHitInfo
    {
        public Collider collider;
        public Vector3 point;
        public Vector3 normal;
        public Vector3 moveDirection;
        public float moveLength;
    }
}
