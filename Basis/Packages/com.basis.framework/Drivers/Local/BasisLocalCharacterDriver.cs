using Basis.Scripts.Animator_Driver;
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
        public bool HasJumpAction = false;
        public float jumpHeight = 1.0f; // Jump height set to 1 meter
        public float currentVerticalSpeed = 0f; // Vertical speed of the character
        public Vector2 Rotation;
        public float RotationSpeed = 200;
        public bool HasEvents = false;
        public float pushPower = 1f;
        private const float CrouchDeltaCoefficient = 0.01f;
        private const float SnapTurnAbsoluteThreshold = 0.8f;
        private bool isSnapTurning;
        public Vector3 CurrentPosition;
        public Quaternion CurrentRotation;
        public CollisionFlags Flags;
        public float radius;
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
            HandleMovement(DeltaTime);
            GroundCheck();

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
                rotationAmount = Rotation.x * RotationSpeed * DeltaTime;
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
            float ascend = IsJumpHeld ? 1.0f : 0.0f; // This works for both VR and desktop.
            float descend = BasisLocalInputActions.Instance.IsCrouchHeld ? -1.0f : 0.0f; // No crouch button in VR.
            return Mathf.Clamp(moveLocal + ascend + descend, -1.0f, 1.0f);
        }

        public void HandleJumpRequest()
        {
            if (groundedPlayer && !HasJumpAction)
            {
                HasJumpAction = true;
            }
        }
        public void GroundCheck()
        {
            groundedPlayer = characterController.isGrounded;
            IsFalling = !groundedPlayer;

            if (groundedPlayer && !LastWasGrounded)
            {
                JustLanded?.Invoke();
                currentVerticalSpeed = 0f; // Reset vertical speed on landing
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
            CrouchBlend = CrouchingLock ? 1f : math.clamp(CrouchBlend + delta * CrouchDeltaCoefficient, 0, 1);
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
            if (groundedPlayer && HasJumpAction)
            {
                currentVerticalSpeed = Mathf.Sqrt(jumpHeight * -2f * gravityValue);
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
            float rawEyeHeight = BasisLocalBoneDriver.HasEye ? BasisLocalBoneDriver.EyeControl.OutGoingData.position.y : BasisHeightDriver.FallbackHeightInMeters;

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

            // Keep capsule bottom aligned with floor
            if (BasisLocalBoneDriver.HasEye)
            {
                var outgoing = BasisLocalBoneDriver.EyeControl.OutGoingData.position;
                characterController.center = new Vector3(outgoing.x, halfHeight, outgoing.z);
            }
            else
            {
                characterController.center = new Vector3(0f, halfHeight, 0f);
            }

            // Clamp stepOffset to something sane relative to height
            float maxStep = (finalHeight + 2f * characterController.radius) - 0.001f;
            maxStep = Mathf.Max(0f, maxStep);
            maxStep = Mathf.Min(maxStep, finalHeight * 0.25f);

            characterController.stepOffset = Mathf.Min(characterController.stepOffset, maxStep);
        }
    }
}
