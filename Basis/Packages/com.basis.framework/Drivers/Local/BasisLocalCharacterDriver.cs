using Basis.Scripts.Animator_Driver;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using System;
using Unity.Mathematics;
using UnityEngine;
using static Basis.Scripts.BasisSdk.Players.BasisPlayer;
namespace Basis.Scripts.BasisCharacterController
{
    [System.Serializable]
    public class BasisLocalCharacterDriver
    {
        public BasisLocalPlayer LocalPlayer;
        [System.NonSerialized] public BasisLocalAnimatorDriver LocalAnimatorDriver;
        public CharacterController characterController;
        public Vector3 bottomPointLocalSpace;
        public Vector3 LastBottomPoint;
        public bool groundedPlayer;
        [SerializeField] public float MaximumMovementSpeed = 4;
        [SerializeField] public float DefaultMovementSpeed = 2.5f;
        [SerializeField] public float MinimumMovementSpeed = 0.5f;
        [SerializeField, Range(0f, 1f)] public float MinimumCrouchPercent = 0.5f;
        [SerializeField] public float gravityValue = -9.81f;
        [SerializeField] public float RaycastDistance = 0.2f;
        [SerializeField] public float MinimumColliderSize = 0.01f;
        private Quaternion currentRotation;
        public SimulationHandler JustJumped;
        public SimulationHandler JustLanded;
        public bool LastWasGrounded = true;
        public bool IsFalling;
        public bool IsJumpHeld = false;
        public bool IsDescendHeld = false;
        public bool HasJumpAction = false;
        public float jumpHeight = 1.0f; // Jump height set to 1 meter
        public float currentVerticalSpeed = 0f; // Vertical speed of the character
        /// <summary>
        /// Temporary hips offset applied on landing to simulate impact absorption.
        /// Eases toward <see cref="landingCrouchTarget"/> then recovers to zero.
        /// </summary>
        [System.NonSerialized] public float landingCrouchEffect;
        [System.NonSerialized] public float landingCrouchTarget;
        [SerializeField] public float landingDescentSpeed = 15f;
        [SerializeField] public float landingRecoverySpeed = 6f;
        [SerializeField] public float landingImpactScale = 0.06f;
        [SerializeField] public float maxLandingCrouchEffect = 0.35f;
        /// <summary>
        /// Duration in seconds after leaving the ground during which the player can still jump.
        /// Helps with unreliable grounded detection on slopes and near ledges.
        /// </summary>
        [SerializeField] public float coyoteTimeDuration = 0.15f;
        [System.NonSerialized] public float coyoteTimeCounter;
        /// <summary>
        /// Whether the player is allowed to jump — true when grounded or within the coyote time window.
        /// </summary>
        public bool CanJump => groundedPlayer || coyoteTimeCounter > 0f;
        /// <summary>
        /// Grace period before the falling state triggers, preventing animation flicker on slopes.
        /// </summary>
        [SerializeField] public float fallingGracePeriod = 0.1f;
        [System.NonSerialized] public float airborneTimer;

        // --- Movement Mode Management ---
        public enum Mode
        {
            Walk,
            Fly,
            NoClip,
        }
        private BasisWalkMovementMode _walkMode = new BasisWalkMovementMode();
        private BasisFlyMovementMode _flyMode = new BasisFlyMovementMode();
        private BasisNoClipMovementMode _noClipMode = new BasisNoClipMovementMode();
        [System.NonSerialized] public IMovementMode CurrentMode;
        [System.NonSerialized] public Mode CurrentModeKind = Mode.Walk;
        public delegate void ModeChangedHandler(Mode newMode);
        public ModeChangedHandler ModeChanged;
        public void SetMode(Mode mode)
        {
            if (CurrentModeKind == mode && CurrentMode != null) return;
            CurrentMode?.Exit(this);
            CurrentModeKind = mode;
            CurrentMode = mode switch
            {
                Mode.Fly => _flyMode,
                Mode.NoClip => _noClipMode,
                _ => _walkMode,
            };
            airborneTimer = 0f;
            coyoteTimeCounter = 0f;
            CurrentMode.Enter(this);
            ModeChanged?.Invoke(mode);
        }

        public Vector2 Rotation;
        public bool HasEvents = false;
        public float pushPower = 1f;
        private const float CrouchDeltaCoefficient = 0.01f;
        private const float SnapTurnAbsoluteThreshold = 0.8f;
        private bool isSnapTurning;
        public Vector3 CurrentPosition;
        public Quaternion CurrentRotation;
        public CollisionFlags Flags;
        public float radius;

        // Inputs of the last CalculateCharacterSize() call. CharacterController.height
        // and .center are skipped when none of these have changed (bit-exact compare —
        // not Vector3 ==, which uses an epsilon and would let sub-epsilon drift slip
        // through and pop the collider once the drift accumulated past threshold).
        private Vector3 _sizeCache_EyePos;
        private bool _sizeCache_HasEye;
        private float _sizeCache_Radius;
        private bool _sizeCache_Valid;
        public Vector2 MovementVector { get; private set; }
        /// <summary>
        /// A value between 0 and 1 representing the relative speed of player movement.
        /// </summary>
        [field: SerializeField] public float MovementSpeedScale { get; private set; }
        [field: SerializeField] public float MovementSpeedBoost { get; private set; }
        private float DefaultMovementSpeedMultiplier = 0.625f;
        private float MaximumMovementSpeedBoost = 1.6f;
        /// <summary>
        /// A value between 0 and 1 representing the character's crouch state, where 0 is fully crouched and 1 is fully standing.
        /// </summary>
        public float CrouchBlend = 1f;
        /// <summary>
        /// Value updated by <see cref="SetCrouchBlendDelta"/> which triggers <see cref="UpdateCrouchBlend"/> implicitly each simulation frame.
        /// This is generally used by event based input systems where a start and stop event are called, but per-frame updates are not.
        /// </summary>
        public float CrouchBlendDelta = 0f;
        /// <summary>
        /// Indicates whether the character is considered crouching based on the CrouchBlend value being less than the defined threshold.
        /// </summary>
        public bool IsCrouching => CrouchBlend <= LocalAnimatorDriver.CrouchThreshold;
        public bool IsRunning => CurrentSpeed > DefaultMovementSpeed;
        public bool UseMaxSpeed => BasisLocalInputActions.Instance.IsRunHeld;
        public bool CanPushRigidbodys = false;
        public bool IsEnabled
        {
            get
            {
                return isEnabled;
            }

            set
            {
                isEnabled = value;
                Validate();
                CalculateCharacterSize();
                characterController.enabled = value;
            }
        }

        public BasisLocks.LockContext MovementLock = BasisLocks.GetContext(BasisLocks.Movement);
        public BasisLocks.LockContext CrouchingLock = BasisLocks.GetContext(BasisLocks.Crouching);
        public Transform BasisLocalPlayerTransform;
        private bool isEnabled = true;
        public float CurrentSpeed;
        public void DeInitalize()
        {
            CurrentMode?.Exit(this);
            CurrentMode = null;
            if (HasEvents)
            {
                HasEvents = false;
            }
        }
        public void Initialize(BasisLocalPlayer localPlayer)
        {
            LocalPlayer = localPlayer;
            BasisLocalPlayerTransform = localPlayer.transform;
            LocalAnimatorDriver = localPlayer.LocalAnimatorDriver;
            characterController.minMoveDistance = 0;
            characterController.skinWidth = 0.01f;
            if (!HasEvents)
            {
                HasEvents = true;
            }
            MaximumMovementSpeedBoost = MaximumMovementSpeed / DefaultMovementSpeed;
            SetMovementSpeedMultiplier(GetMultiplierForMovementSpeed(DefaultMovementSpeed));
            Validate();
            CalculateCharacterSize();
            SetMode(Mode.Walk);
        }

        public void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (CanPushRigidbodys)
            {
                // Check if the hit object has a Rigidbody and if it is not kinematic
                Rigidbody body = hit.collider.attachedRigidbody;

                if (body == null || body.isKinematic)
                {
                    return;
                }

                // Ensure we're only pushing objects in the horizontal plane
                Vector3 pushDir = new Vector3(hit.moveDirection.x, 0, hit.moveDirection.z);

                // Apply the force to the object
                body.AddForce(pushDir * pushPower, ForceMode.Impulse);
            }
        }
        public void SimulateMovement(float DeltaTime)
        {
            if (!IsEnabled)
            {

                // If you want basis localToWorld using the *new* pose:
                BasisLocalPlayerTransform.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                BasisLocalPlayer.localToWorldMatrix = Matrix4x4.TRS(Position, Rotation, BasisLocalPlayerTransform.lossyScale);
                return;
            }
            LastBottomPoint = bottomPointLocalSpace;
            CalculateCharacterSize();
            // Two-phase landing impact: ease into dip, then ease back up
            if (landingCrouchTarget > 0f)
            {
                // Phase 1: descend toward peak impact
                landingCrouchEffect = Mathf.Lerp(landingCrouchEffect, landingCrouchTarget, landingDescentSpeed * DeltaTime);
                if (landingCrouchTarget - landingCrouchEffect < 0.01f)
                {
                    landingCrouchTarget = 0f;
                }
            }
            else if (landingCrouchEffect > 0f)
            {
                // Phase 2: recover back to standing
                landingCrouchEffect = Mathf.Lerp(landingCrouchEffect, 0f, landingRecoverySpeed * DeltaTime);
                if (landingCrouchEffect < 0.001f) landingCrouchEffect = 0f;
            }
            // Delegate movement, gravity, and ground checking to the active mode.
            if (CurrentMode != null)
            {
                CurrentMode.Tick(this, DeltaTime);
            }
            else
            {
                HandleMovement(DeltaTime);
                GroundCheck(DeltaTime);
            }

            // Calculate the rotation amount for this frame
            float rotationAmount;
            if (SMModuleControllerSettings.UsingSnapTurnAngle && BasisDeviceManagement.IsCurrentModeVR())
            {
                var isAboveThreshold = math.abs(Rotation.x) > SnapTurnAbsoluteThreshold;
                if (isAboveThreshold != isSnapTurning)
                {
                    isSnapTurning = isAboveThreshold;
                    if (isSnapTurning)
                    {
                        rotationAmount = math.sign(Rotation.x) * SMModuleControllerSettings.SnapTurnAngle;
                    }
                    else
                    {
                        rotationAmount = 0f;
                    }
                }
                else
                {
                    rotationAmount = 0f;
                }
            }
            else
            {
                rotationAmount = Rotation.x * SMModuleControllerSettings.SmoothTurnSpeed * DeltaTime;
            }


            // Get the current rotation and position of the player
            Vector3 pivot = BasisLocalBoneDriver.EyeControl.OutgoingWorldData.position;
            Vector3 upAxis = Vector3.up;

            // Calculate direction from the pivot to the current position
            Vector3 directionToPivot = CurrentPosition - pivot;

            // Calculate rotation quaternion based on the rotation amount and axis
            Quaternion rotation = Quaternion.AngleAxis(rotationAmount, upAxis);

            // Apply rotation to the direction vector
            Vector3 rotatedDirection = rotation * directionToPivot;

            Vector3 FinalRotation = pivot + rotatedDirection;

            BasisLocalPlayerTransform.SetPositionAndRotation(FinalRotation, rotation * CurrentRotation);

            float HeightOffset = (characterController.height / 2) - characterController.radius;
            bottomPointLocalSpace = FinalRotation + (characterController.center - new Vector3(0, HeightOffset, 0));

            Quaternion newRot = rotation * CurrentRotation;
            Vector3 newPos = FinalRotation;

            // If you want basis localToWorld using the *new* pose:
            BasisLocalPlayer.localToWorldMatrix = Matrix4x4.TRS(newPos, newRot, BasisLocalPlayerTransform.lossyScale);
        }

        public float GetVerticalMovement()
        {
            float moveLocal = BasisLocalInputActions.Instance.MoveLocalUpDown.action.ReadValue<float>();
            float ascend = IsJumpHeld ? 1.0f : 0.0f;
            float descend = (IsDescendHeld || BasisLocalInputActions.Instance.IsCrouchHeld) ? -1.0f : 0.0f;
            return Mathf.Clamp(moveLocal + ascend + descend, -1.0f, 1.0f);
        }

        public void HandleJumpRequest()
        {
            if (CanJump && !HasJumpAction)
            {
                HasJumpAction = true;
            }
        }
        public void GroundCheck(float deltaTime)
        {
            groundedPlayer = characterController.isGrounded;

            if (groundedPlayer)
            {
                airborneTimer = 0f;
                IsFalling = false;

                if (!LastWasGrounded)
                {
                    float fallSpeed = Mathf.Abs(currentVerticalSpeed);
                    // Suppress hip dip in FBT to avoid fighting real hip tracker data on landing.
                    if (!(BasisAvatarIKStageCalibration.HasFBIKTrackers && Basis.BasisUI.BasisSettingsDefaults.DisableAnimationsInFBT.RawValue))
                    {
                        landingCrouchTarget = Mathf.Clamp(fallSpeed * landingImpactScale, 0f, maxLandingCrouchEffect);
                    }
                    JustLanded?.Invoke();
                    currentVerticalSpeed = 0f;
                }
            }
            else
            {
                // Only trigger the falling state after a grace period to prevent
                // animation flickering on slopes and during ground-type transitions.
                airborneTimer += deltaTime;
                IsFalling = airborneTimer > fallingGracePeriod;

                // Grant coyote time on the frame we leave the ground,
                // but only when walking off (not after an active jump).
                if (LastWasGrounded && currentVerticalSpeed <= 0f)
                {
                    coyoteTimeCounter = coyoteTimeDuration;
                    currentVerticalSpeed = -2f; // Smooth ledge transition without terminal velocity
                }
                else if (coyoteTimeCounter > 0f)
                {
                    coyoteTimeCounter -= deltaTime;
                }
            }

            LastWasGrounded = groundedPlayer;
        }

        public void CrouchToggle()
        {
            // check what the animator driver considers to be crouching, and standup if crouch threshold is matched, otherwise, full crouch
            CrouchBlend = CrouchingLock || CrouchBlend <= LocalAnimatorDriver.CrouchThreshold ? 1f : 0f;
            UpdateMovementSpeed(UseMaxSpeed);
        }

        public void SetCrouchBlendDelta(float delta)
        {
            CrouchBlendDelta = delta;
        }

        public void UpdateCrouchBlend(float delta)
        {
            if (CrouchingLock) return;
            CrouchBlend = math.clamp(CrouchBlend + delta * CrouchDeltaCoefficient, 0, 1);
            UpdateMovementSpeed(UseMaxSpeed);
        }

        public void UpdateMovementSpeed(bool maxSpeed)
        {
            var topSpeed = maxSpeed ? 1f : DefaultMovementSpeedMultiplier;
            var boostSpeed = maxSpeed ? MaximumMovementSpeedBoost : 1f;
            // inverse of crouch blend so standing is the least value, multiply by the boost that running gives
            MovementSpeedBoost = (1 - CrouchBlend) * boostSpeed;
            SetMovementSpeedMultiplier(topSpeed * CrouchBlend * MovementVector.magnitude);
        }

        public float GetMultiplierForMovementSpeed(float speed)
        {
            return math.unlerp(MinimumMovementSpeed, MaximumMovementSpeed, speed);
        }
        public void SetMovementSpeedMultiplier(float multiplier, bool constrain = true)
        {
            MovementSpeedScale = multiplier;
            if (constrain) MovementSpeedScale = math.clamp(MovementSpeedScale, 0, 1);
        }

        public void SetMovementVector(Vector2 movement)
        {
            MovementVector = movement;
        }
        public void HandleMovement(float DeltaTime)
        {
            // Cache current rotation and zero out x and z components
            currentRotation = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation;
            Vector3 rotationEulerAngles = currentRotation.eulerAngles;
            rotationEulerAngles.x = 0;
            rotationEulerAngles.z = 0;

            Quaternion flattenedRotation = Quaternion.Euler(rotationEulerAngles);

            if (CrouchBlendDelta != 0) UpdateCrouchBlend(CrouchBlendDelta);
            // Calculate horizontal movement direction
            Vector3 horizontalMoveDirection = new Vector3(MovementVector.x, 0, MovementVector.y).normalized;

            CurrentSpeed = math.lerp(MinimumMovementSpeed, MaximumMovementSpeed, MovementSpeedScale) + MinimumMovementSpeed * MovementSpeedBoost;

            Vector3 totalMoveDirection = flattenedRotation * horizontalMoveDirection * CurrentSpeed * DeltaTime;
            if (MovementLock)
            {
                HasJumpAction = false;
                totalMoveDirection = Vector3.zero;
            }


            // Handle jumping and falling
            if (CanJump && HasJumpAction)
            {
                currentVerticalSpeed = Mathf.Sqrt(jumpHeight * -2f * gravityValue);
                coyoteTimeCounter = 0f; // Consume coyote time to prevent double jumps
                JustJumped?.Invoke();
            }
            else
            {
                currentVerticalSpeed += gravityValue * DeltaTime;
            }

            // Ensure we don't exceed maximum gravity value speed
            currentVerticalSpeed = Mathf.Max(currentVerticalSpeed, -Mathf.Abs(gravityValue));


            HasJumpAction = false;
            totalMoveDirection.y = currentVerticalSpeed * DeltaTime;

            // Move character
            Flags = characterController.Move(totalMoveDirection);
            BasisLocalPlayerTransform.GetPositionAndRotation(out CurrentPosition, out CurrentRotation);
        }
        public void Validate()
        {
            radius = characterController.radius;
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f)
            {
                radius = 0.1f;
            }

            characterController.radius = radius;
        }
        public void CalculateCharacterSize()
        {
            bool hasEye = BasisLocalBoneDriver.HasEye;
            Vector3 eyePos = hasEye
                ? BasisLocalBoneDriver.EyeControl.OutGoingData.position
                : default;

            // Bit-exact change check — Vector3 == uses an epsilon (~9.99e-11 squared)
            // which would silently swallow sub-epsilon eye drift; the height stays
            // stale until the drift clears the threshold and then snaps, which reads
            // as jitter. Component-wise float compares catch every bit change so the
            // collider tracks the eye smoothly.
            if (_sizeCache_Valid
                && hasEye == _sizeCache_HasEye
                && radius == _sizeCache_Radius
                && eyePos.x == _sizeCache_EyePos.x
                && eyePos.y == _sizeCache_EyePos.y
                && eyePos.z == _sizeCache_EyePos.z)
            {
                return;
            }

            float rawEyeHeight = hasEye ? eyePos.y : BasisHeightDriver.FallbackHeightInMeters;

            // Validate tracking data
            if (float.IsNaN(rawEyeHeight) || float.IsInfinity(rawEyeHeight) || rawEyeHeight <= 0f)
            {
                rawEyeHeight = BasisHeightDriver.FallbackHeightInMeters;
            }

            // Enforce minimum collider size
            if (rawEyeHeight < MinimumColliderSize)
            {
                rawEyeHeight = MinimumColliderSize;
            }

            // Ensure height is valid relative to radius
            float minHeight = 2f * radius + 0.001f;
            float finalHeight = Mathf.Max(rawEyeHeight, minHeight);

            characterController.height = finalHeight;

            float halfHeight = finalHeight * 0.5f;

            // Offset the capsule down by skinWidth so the collider bottom
            // (including its skin shell) sits flush with the floor instead
            // of hovering skinWidth above it.
            float skinCompensation = characterController.skinWidth;

            if (hasEye)
            {
                characterController.center = new Vector3(eyePos.x, halfHeight - skinCompensation, eyePos.z);
            }
            else
            {
                characterController.center = new Vector3(0f, halfHeight - skinCompensation, 0f);
            }

            // Clamp stepOffset to something sane relative to height
            float maxStep = (finalHeight + 2f * characterController.radius) - 0.001f;
            maxStep = Mathf.Max(0f, maxStep);
            maxStep = Mathf.Min(maxStep, finalHeight * 0.25f);

            characterController.stepOffset = Mathf.Min(characterController.stepOffset, maxStep);

            _sizeCache_HasEye = hasEye;
            _sizeCache_EyePos = eyePos;
            _sizeCache_Radius = radius;
            _sizeCache_Valid = true;
        }
    }
}
