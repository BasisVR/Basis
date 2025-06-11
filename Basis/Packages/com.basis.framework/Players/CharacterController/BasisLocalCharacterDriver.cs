using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Unity.Mathematics;
using UnityEngine;
using static Basis.Scripts.BasisSdk.Players.BasisPlayer;
namespace Basis.Scripts.BasisCharacterController
{
    [System.Serializable]
    public class BasisLocalCharacterDriver
    {
        public CharacterController characterController;
        public Vector3 bottomPointLocalspace;
        public Vector3 LastbottomPoint;
        public bool groundedPlayer;
        [SerializeField] public float FastestRunSpeed = 4;
        [SerializeField] public float SlowestPlayerSpeed = 0.5f;
        [SerializeField] public float gravityValue = -9.81f;
        [SerializeField] public float RaycastDistance = 0.2f;
        [SerializeField] public float MinimumColliderSize = 0.01f;
        [SerializeField] public Vector2 MovementVector;
        private Quaternion currentRotation;
        private float eyeHeight;
        public SimulationHandler JustJumped;
        public SimulationHandler JustLanded;
        public bool LastWasGrounded = true;
        public bool IsFalling;
        public bool HasJumpAction = false;
        public float jumpHeight = 1.0f; // Jump height set to 1 meter
        public float currentVerticalSpeed = 0f; // Vertical speed of the character
        public Vector2 Rotation;
        public float RotationSpeed = 200;
        public bool HasEvents = false;
        public float pushPower = 1f;
        private const float SnapTurnAbsoluteThreshold = 0.8f;
        private bool UseSnapTurn => SMModuleControllerSettings.SnapTurnAngle != -1;
        private float SnapTurnAngle => SMModuleControllerSettings.SnapTurnAngle;
        private bool isSnapTurning;

        public Vector3 CurrentPosition;
        public Quaternion CurrentRotation;
        public CollisionFlags Flags;
        public float SpeedMultiplier = 0.5f;
        public void OnDestroy()
        {
            if (HasEvents)
            {
                HasEvents = false;
            }
        }
        public void Initialize(BasisLocalPlayer LocalPlayer)
        {
            LocalPlayer.LocalCharacterDriver = this;
            characterController.minMoveDistance = 0;
            characterController.skinWidth = 0.01f;
            if (HasEvents == false)
            {
                HasEvents = true;
            }
        }
        public void OnControllerColliderHit(ControllerColliderHit hit)
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
        public void SimulateMovement(float DeltaTime,Transform PlayersTransform)
        {
            if(!IsEnabled)
            {
                return;
            }
            LastbottomPoint = bottomPointLocalspace;
            CalculateCharacterSize();
            HandleMovement(DeltaTime, PlayersTransform);
            GroundCheck();

            // Calculate the rotation amount for this frame
            float rotationAmount;
            if (UseSnapTurn)
            {
                var isAboveThreshold = math.abs(Rotation.x) > SnapTurnAbsoluteThreshold;
                if (isAboveThreshold != isSnapTurning)
                {
                    isSnapTurning = isAboveThreshold;
                    if (isSnapTurning)
                    {
                        rotationAmount = math.sign(Rotation.x) * SnapTurnAngle;
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
            Vector3 pivot = BasisLocalBoneDriver.Eye.OutgoingWorldData.position;
            Vector3 upAxis = Vector3.up;

            // Calculate direction from the pivot to the current position
            Vector3 directionToPivot = CurrentPosition - pivot;

            // Calculate rotation quaternion based on the rotation amount and axis
            Quaternion rotation = Quaternion.AngleAxis(rotationAmount, upAxis);

            // Apply rotation to the direction vector
            Vector3 rotatedDirection = rotation * directionToPivot;

            Vector3 FinalRotation = pivot + rotatedDirection;

            PlayersTransform.SetPositionAndRotation(FinalRotation, rotation * CurrentRotation);

            float HeightOffset = (characterController.height / 2) - characterController.radius;
            bottomPointLocalspace = FinalRotation + (characterController.center - new Vector3(0, HeightOffset, 0));
        }

        public void HandleJump()
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
        public float CurrentSpeed;
        public bool IsEnabled = true;

        public void HandleMovement(float DeltaTime,Transform PlayersTransform)
        {
            bool IsMovementPaused = BasisLocalInputActions.IsMovementPaused();
            if (IsMovementPaused)
            {
                HasJumpAction = false;
            }

            // Cache current rotation and zero out x and z components
            currentRotation = BasisLocalBoneDriver.Head.OutgoingWorldData.rotation;
            Vector3 rotationEulerAngles = currentRotation.eulerAngles;
            rotationEulerAngles.x = 0;
            rotationEulerAngles.z = 0;

            Quaternion flattenedRotation = Quaternion.Euler(rotationEulerAngles);

            // Calculate horizontal movement direction
            Vector3 horizontalMoveDirection = new Vector3(MovementVector.x, 0, MovementVector.y).normalized;

            SpeedMultiplier = math.abs(SpeedMultiplier);
            CurrentSpeed = math.lerp(SlowestPlayerSpeed, FastestRunSpeed, SpeedMultiplier);
            CurrentSpeed = math.clamp(CurrentSpeed, 0, FastestRunSpeed);

            Vector3 totalMoveDirection = flattenedRotation * horizontalMoveDirection * CurrentSpeed * DeltaTime;
            if (IsMovementPaused)
                totalMoveDirection = Vector3.zero;

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
            PlayersTransform.GetPositionAndRotation(out CurrentPosition, out CurrentRotation);
        }
        public void CalculateCharacterSize()
        {
            eyeHeight = BasisLocalBoneDriver.HasEye ? BasisLocalBoneDriver.Eye.OutGoingData.position.y : BasisLocalPlayer.FallbackSize;
            float adjustedHeight = eyeHeight;
            adjustedHeight = Mathf.Max(adjustedHeight, MinimumColliderSize);
            SetCharacterHeight(adjustedHeight);
        }
        public void SetCharacterHeight(float height)
        {
            characterController.height = height;
            float SkinModifiedHeight = height / 2;

            characterController.center = BasisLocalBoneDriver.HasEye ? new Vector3(BasisLocalBoneDriver.Eye.OutGoingData.position.x, SkinModifiedHeight, BasisLocalBoneDriver.Eye.OutGoingData.position.z) : new Vector3(0, SkinModifiedHeight, 0);
        }
    }
}
