using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Headless
{
    /// <summary>
    /// Headless input driver that simulates a CenterEye device for server/headless builds.
    /// Generates gentle movement/look input with mode-based behavior and periodic respawns.
    /// Includes controls to stop and resume movement at runtime.
    /// </summary>
    public class BasisHeadlessInput : BasisInput
    {
        public Camera Camera;
        public BasisLocalAvatarDriver AvatarDriver;
        public static BasisHeadlessInput Instance;
        public BasisLocalVirtualSpineDriver BasisVirtualSpine = new BasisLocalVirtualSpineDriver();
        public bool HasEyeEvents = false;

        // --- Tunables (exposed) ------------------------------------------------

        [Header("Lerps & Smoothing")]
        [Tooltip("How quickly rotation eases toward targets when not in rotate-only modes.")]
        public float rotationLerpSpeed = 1.1f;
        [Tooltip("How quickly axes/movement ease toward targets.")]
        public float inputLerpSpeed = 2.0f;

        [Header("Obstacle Avoidance")]
        [Tooltip("How far ahead to check for obstacles (meters).")]
        public float obstacleCheckDistance = 2.0f;
        [Tooltip("Radius for the forward check (SphereCast).")]
        public float obstacleCheckRadius = 0.2f;
        [Tooltip("Layers considered obstacles.")]
        public LayerMask obstacleLayers = ~0; // everything by default
        [Tooltip("Max randomized turns we'll try before flipping around.")]
        public int maxAvoidanceTries = 6;
        [Tooltip("Range of yaw (degrees) to try when avoiding.")]
        public Vector2 avoidTurnAngleRange = new Vector2(25f, 65f);

        [Header("Behavior Mode Weights (sum doesn't need to be 1)")]
        public float weightIdle = 0.2f;
        public float weightRotateOnly = 0.1f; // split left/right evenly
        public float weightMove = 0.4f;
        public float weightMoveAndRotate = 0.8f;

        [Header("Behavior Durations (seconds)")]
        public Vector2 idleDurationRange = new Vector2(3f, 8f);
        public Vector2 rotateDurationRange = new Vector2(2.5f, 6f);
        public Vector2 moveDurationRange = new Vector2(4f, 10f);
        public Vector2 moveAndRotateDurationRange = new Vector2(4f, 10f);

        [Header("Movement Magnitudes")]
        [Tooltip("Magnitude (0..1) for move vector when moving.")]
        public Vector2 moveMagnitudeRange = new Vector2(0.15f, 0.45f);
        [Tooltip("Chance to insert micro-pauses during move modes.")]
        public float microPauseChance = 0.25f;
        [Tooltip("Duration range for micro-pauses inside move modes.")]
        public Vector2 microPauseDurationRange = new Vector2(0.5f, 1.5f);

        [Header("Rotation")]
        [Tooltip("Yaw rate (deg/sec) used in rotate-only and move+rotate modes.")]
        public Vector2 yawRateDegPerSecRange = new Vector2(10f, 40f);
        [Tooltip("Pitch range for target look (degrees).")]
        public Vector2 pitchRange = new Vector2(-15f, 15f);
        [Tooltip("Roll range for target look (degrees).")]
        public Vector2 rollRange = new Vector2(-6f, 6f);

        [Header("Jump")]
        [Tooltip("Seconds between jumps; next jump is sampled in this range.")]
        public Vector2 jumpCooldownRange = new Vector2(20f, 60f);

        [Header("Respawn")]
        public bool ShouldTelepoprtRespawn = false; // keeping original name to avoid breaking external refs
        public float respawnInterval = 300f;

        // --- Internal state ----------------------------------------------------

        private enum BehaviorMode { Idle, RotateLeft, RotateRight, Move, MoveAndRotate }

        private BehaviorMode currentMode = BehaviorMode.Idle;
        private float modeRemaining = 0f;

        private bool inMicroPause = false;
        private float microPauseRemaining = 0f;

        private float nextJumpTime = Mathf.Infinity; // absolute unscaled time

        private Vector2 currentMoveVector = Vector2.zero;
        private Vector2 targetMoveVector = Vector2.zero;

        private Vector2 currentPrimary2DAxis = Vector2.zero;
        private Vector2 targetPrimary2DAxis = Vector2.zero;

        private Vector2 currentSecondary2DAxis = Vector2.zero;
        private Vector2 targetSecondary2DAxis = Vector2.zero;

        private Quaternion currentRotation = Quaternion.identity;
        private Quaternion targetRotation = Quaternion.identity;

        private float currentYawRate = 0f; // deg/sec, sign encodes left/right

        // --- Movement control (new) -------------------------------------------
        private bool movementLocked = false;
        private Coroutine stopForSecondsCo;

        /// <summary>Immediately stops headless movement and prevents new movement until released.</summary>
        public void StopMovement()
        {
            movementLocked = true;
            CancelStopForSecondsCo();

            // zero all motion targets and current values
            targetMoveVector = Vector2.zero;
            targetPrimary2DAxis = Vector2.zero;
            targetSecondary2DAxis = Vector2.zero;

            currentMoveVector = Vector2.zero;
            currentPrimary2DAxis = Vector2.zero;
            currentSecondary2DAxis = Vector2.zero;

            // freeze yaw spinning
            currentYawRate = 0f;
        }

        /// <summary>Allows movement again and resumes normal behavior.</summary>
        public void ResumeMovement()
        {
            movementLocked = false;
            CancelStopForSecondsCo();
            // Optionally re-seed mode so it doesn't immediately pick up old transient targets.
            PickNextMode();
        }

        /// <summary>Toggles movement lock/unlock.</summary>
        public void ToggleMovement()
        {
            if (movementLocked) ResumeMovement();
            else StopMovement();
        }

        /// <summary>Stops movement for a number of seconds, then resumes automatically.</summary>
        public void StopForSeconds(float seconds)
        {
            if (seconds <= 0f) { StopMovement(); return; }
            StopMovement();
            CancelStopForSecondsCo();
            stopForSecondsCo = StartCoroutine(StopForSecondsRoutine(seconds));
        }

        private IEnumerator StopForSecondsRoutine(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            ResumeMovement();
        }

        private void CancelStopForSecondsCo()
        {
            if (stopForSecondsCo != null)
            {
                StopCoroutine(stopForSecondsCo);
                stopForSecondsCo = null;
            }
        }

        // ----------------------------------------------------------------------

        public void Initialize(string ID = "Desktop Eye", string subSystems = "BasisDesktopManagement")
        {
            BasisDebug.Log("Initializing Avatar Eye", BasisDebug.LogTag.Input);

            float height = BasisHeightDriver.SelectedScaledPlayerHeight;

            ScaledDeviceCoord.position = new Vector3(0, height, 0);
            ScaledDeviceCoord.rotation = Quaternion.identity;

            InitalizeTracking(ID, ID, subSystems, true, BasisBoneTrackedRole.CenterEye);

            if (BasisHelpers.CheckInstance(Instance))
                Instance = this;

            PlayerInitialized();

            if (!HasEyeEvents)
            {
                BasisLocalPlayer.OnLocalAvatarChanged += PlayerInitialized;
                BasisPointRaycaster.UseWorldPosition = false;

                BasisVirtualSpine.Initialize();
                HasEyeEvents = true;

                // seed behavior & jump timers
                PickNextMode();
                ScheduleNextJump();

                if (ShouldTelepoprtRespawn)
                    StartCoroutine(RespawnRoutine());
            }
        }

        public new void OnDestroy()
        {
            if (HasEyeEvents)
            {
                BasisLocalPlayer.OnLocalAvatarChanged -= PlayerInitialized;
                HasEyeEvents = false;
                BasisVirtualSpine.DeInitialize();
            }
            base.OnDestroy();
        }

        public void PlayerInitialized()
        {
            AvatarDriver = BasisLocalPlayer.Instance.LocalAvatarDriver;
            Camera = BasisLocalCameraDriver.Instance.Camera;

            foreach (var input in BasisDeviceManagement.Instance.BasisLockToInputs)
                input.FindRole();
        }

        public void OnDisable()
        {
            BasisLocalPlayer.OnLocalAvatarChanged -= PlayerInitialized;
        }
        public bool ForceJump = false;
        public override void LateDoPollData()
        {
            if (!hasRoleAssigned) return;

            float dt = Time.unscaledDeltaTime;
            if(ForceJump)
            {
                BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJumpRequest();
            }
            // If movement is locked, override locomotion and inputs but keep pose updates
            if (movementLocked)
            {
                var charDriverLocked = BasisLocalPlayer.Instance.LocalCharacterDriver;

                // hold everything at zero movement
                currentMoveVector = Vector2.zero;
                targetMoveVector = Vector2.zero;
                currentPrimary2DAxis = Vector2.zero;
                targetPrimary2DAxis = Vector2.zero;
                currentSecondary2DAxis = Vector2.zero;
                targetSecondary2DAxis = Vector2.zero;
                currentYawRate = 0f;

                // push to character/input
                charDriverLocked.SetMovementVector(Vector2.zero);
                charDriverLocked.UpdateMovementSpeed(false);

                CurrentInputState.Trigger = 0f;
                CurrentInputState.SecondaryTrigger = 0f;
                CurrentInputState.Primary2DAxisRaw = Vector2.zero;
                CurrentInputState.Secondary2DAxisRaw = Vector2.zero;

                // maintain current head rotation (no extra spin)
                UnscaledDeviceCoord.rotation = currentRotation;

                // maintain height with crouch compensation
                float baseHeightLocked = BasisHeightDriver.SelectedScaledPlayerHeight;
                Vector3 posLocked = new Vector3(0, baseHeightLocked, 0);

                if (!BasisLocks.GetContext(BasisLocks.Crouching))
                {
                    float crouchMin = charDriverLocked.MinimumCrouchPercent;
                    float crouchBlend = charDriverLocked.CrouchBlend;
                    float heightAdjust = (1f - crouchMin) * crouchBlend + crouchMin;
                    posLocked.y -= Control.TposeLocalScaled.position.y * (1f - heightAdjust);
                }

                UnscaledDeviceCoord.position = posLocked;
                ScaledDeviceCoord.position = posLocked;
                ScaledDeviceCoord.rotation = currentRotation;

                ControlOnlyAsDevice();
                ComputeRaycastDirection(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation, Quaternion.identity);
                UpdateInputEvents();
                return; // skip normal AI behavior while locked
            }

            // --- Mode timing / transitions ---
            modeRemaining -= dt;
            if (modeRemaining <= 0f)
            {
                PickNextMode();
            }

            // micro-pauses inside move modes
            if (currentMode == BehaviorMode.Move || currentMode == BehaviorMode.MoveAndRotate)
            {
                if (inMicroPause)
                {
                    microPauseRemaining -= dt;
                    if (microPauseRemaining <= 0f) inMicroPause = false;
                }
                else if (Random.value < microPauseChance * dt) // low probability per second
                {
                    inMicroPause = true;
                    microPauseRemaining = Random.Range(microPauseDurationRange.x, microPauseDurationRange.y);
                }
            }
            else
            {
                inMicroPause = false;
                microPauseRemaining = 0f;
            }

            // --- Jump cooldown ---
            if (Time.unscaledTime >= nextJumpTime)
            {
                BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJumpRequest();
                ScheduleNextJump();
            }

            // --- Update targets based on current mode ---
            UpdateTargetsForMode(dt);

            // Obstacle avoidance when moving
            if ((currentMode == BehaviorMode.Move || currentMode == BehaviorMode.MoveAndRotate) && !inMicroPause)
            {
                TryAvoidObstacle();
            }

            // --- Smoothly approach targets (for non-rotate-only yaw) ---
            currentMoveVector = Vector2.Lerp(currentMoveVector, targetMoveVector, dt * inputLerpSpeed);
            currentPrimary2DAxis = Vector2.Lerp(currentPrimary2DAxis, targetPrimary2DAxis, dt * inputLerpSpeed);
            currentSecondary2DAxis = Vector2.Lerp(currentSecondary2DAxis, targetSecondary2DAxis, dt * inputLerpSpeed);

            // If we're in rotate-only or move+rotate, yaw integrates continuously.
            if (currentMode == BehaviorMode.RotateLeft || currentMode == BehaviorMode.RotateRight || currentMode == BehaviorMode.MoveAndRotate)
            {
                float yawStep = currentYawRate * dt;
                currentRotation = Quaternion.Euler(0f, yawStep, 0f) * currentRotation;

                // keep pitch/roll gently drifting toward a soft target to avoid rigid head
                Quaternion smoothPR =
                    Quaternion.Slerp(
                        Quaternion.Euler(0f, 0f, 0f) * currentRotation,
                        Quaternion.Euler(GetClampedPitch(), 0f, GetClampedRoll()) * currentRotation,
                        dt * 0.25f);
                currentRotation = Quaternion.Slerp(currentRotation, smoothPR, dt * 0.5f);
            }
            else
            {
                currentRotation = Quaternion.Slerp(currentRotation, targetRotation, dt * rotationLerpSpeed);
            }

            // --- Write movement into character driver (walk, never sprint) ---
            var charDriver = BasisLocalPlayer.Instance.LocalCharacterDriver;
            charDriver.SetMovementVector(currentMoveVector);
            charDriver.UpdateMovementSpeed(false);

            // --- Input state: subtle/noisy but calmer ---
            CurrentInputState.Trigger = 0f;          // disable spammy triggers in headless
            CurrentInputState.SecondaryTrigger = 0f; // keep quiet unless you need them
            CurrentInputState.Primary2DAxisRaw = currentPrimary2DAxis;
            CurrentInputState.Secondary2DAxisRaw = currentSecondary2DAxis;

            // --- Head pose at eye height with crouch compensation ---
            UnscaledDeviceCoord.rotation = currentRotation;
            float baseHeight = BasisHeightDriver.SelectedScaledPlayerHeight;
            Vector3 pos = new Vector3(0, baseHeight, 0);

            if (!BasisLocks.GetContext(BasisLocks.Crouching))
            {
                float crouchMin = charDriver.MinimumCrouchPercent;
                float crouchBlend = charDriver.CrouchBlend;
                float heightAdjust = (1f - crouchMin) * crouchBlend + crouchMin;
                pos.y -= Control.TposeLocalScaled.position.y * (1f - heightAdjust);
            }

            UnscaledDeviceCoord.position = pos;
            ScaledDeviceCoord.position = pos;
            ScaledDeviceCoord.rotation = currentRotation;

            // Drive our CenterEye bone
            ControlOnlyAsDevice();
            ComputeRaycastDirection(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation, Quaternion.identity);
            UpdateInputEvents();
        }

        // --- Target/update helpers --------------------------------------------

        private void PickNextMode()
        {
            // Weighted choice: Idle, RotateOnly (split L/R), Move, MoveAndRotate
            float total = weightIdle + weightRotateOnly + weightMove + weightMoveAndRotate;
            float r = Random.value * Mathf.Max(total, 0.0001f);

            if (r < weightIdle)
            {
                currentMode = BehaviorMode.Idle;
                modeRemaining = Random.Range(idleDurationRange.x, idleDurationRange.y);
                SetTargetsForIdle();
            }
            else if ((r -= weightIdle) < weightRotateOnly)
            {
                currentMode = (Random.value < 0.5f) ? BehaviorMode.RotateLeft : BehaviorMode.RotateRight;
                modeRemaining = Random.Range(rotateDurationRange.x, rotateDurationRange.y);
                SetTargetsForRotateOnly(currentMode == BehaviorMode.RotateLeft ? -1f : 1f);
            }
            else if ((r -= weightRotateOnly) < weightMove)
            {
                currentMode = BehaviorMode.Move;
                modeRemaining = Random.Range(moveDurationRange.x, moveDurationRange.y);
                SetTargetsForMove(alsoRotate: false);
            }
            else
            {
                currentMode = BehaviorMode.MoveAndRotate;
                modeRemaining = Random.Range(moveAndRotateDurationRange.x, moveAndRotateDurationRange.y);
                SetTargetsForMove(alsoRotate: true);
            }
        }

        private void SetTargetsForIdle()
        {
            targetMoveVector = Vector2.zero;
            targetPrimary2DAxis = Vector2.zero;
            targetSecondary2DAxis = Vector2.zero;

            // hold yaw mostly steady; pick soft pitch/roll
            targetRotation = Quaternion.Euler(GetClampedPitch(), 0f, GetClampedRoll()) * Quaternion.Euler(0f, GetCurrentYaw(), 0f);
            currentYawRate = 0f;
        }

        private void SetTargetsForRotateOnly(float directionSign)
        {
            targetMoveVector = Vector2.zero;
            targetPrimary2DAxis = Vector2.zero;
            targetSecondary2DAxis = Vector2.zero;

            // steady yaw rate, gentle pitch/roll
            currentYawRate = directionSign * Random.Range(yawRateDegPerSecRange.x, yawRateDegPerSecRange.y);

            // set an initial orientation target with small PR so Slerp has something to do
            targetRotation = Quaternion.Euler(GetClampedPitch(), GetCurrentYaw(), GetClampedRoll());
        }

        private void SetTargetsForMove(bool alsoRotate)
        {
            float mag = Random.Range(moveMagnitudeRange.x, moveMagnitudeRange.y);

            // bias a bit towards forward movement
            Vector2 rand = Random.insideUnitCircle.normalized;
            rand.y = Mathf.Lerp(rand.y, 1f, 0.35f);
            targetMoveVector = (rand.normalized * mag);

            // axes: tame noise
            targetPrimary2DAxis = targetMoveVector * 0.5f;
            targetSecondary2DAxis = Vector2.zero;

            // ALWAYS look where we move
            UpdateFacingToMove();

            // Optional small spin if allowed
            if (alsoRotate)
            {
                float dir = (Random.value < 0.5f) ? -1f : 1f;
                currentYawRate = dir * Random.Range(yawRateDegPerSecRange.x, yawRateDegPerSecRange.y) * 0.15f; // 15% of normal
            }
            else
            {
                currentYawRate = 0f;
            }
        }

        private void UpdateTargetsForMode(float dt)
        {
            switch (currentMode)
            {
                case BehaviorMode.Idle:
                    // very slight breathing-like drift on axes
                    targetPrimary2DAxis = Vector2.Lerp(targetPrimary2DAxis, Vector2.zero, dt * 1.5f);
                    targetSecondary2DAxis = Vector2.Lerp(targetSecondary2DAxis, Vector2.zero, dt * 1.5f);
                    break;

                case BehaviorMode.Move:
                case BehaviorMode.MoveAndRotate:
                    if (!inMicroPause && Random.value < 0.2f * dt)
                    {
                        var jitter = Random.insideUnitCircle * 0.05f;
                        targetMoveVector = Vector2.ClampMagnitude(targetMoveVector + jitter, moveMagnitudeRange.y);
                        targetPrimary2DAxis = targetMoveVector * 0.5f;
                        UpdateFacingToMove(); // keep head matched to new move dir
                    }

                    if (inMicroPause)
                    {
                        targetMoveVector = Vector2.zero;
                        targetPrimary2DAxis = Vector2.zero;
                        // when paused, keep current facing; no need to update here
                    }
                    break;

                case BehaviorMode.RotateLeft:
                case BehaviorMode.RotateRight:
                    // nudge pitch/roll now and then
                    if (Random.value < 0.15f * dt)
                    {
                        targetRotation = Quaternion.Euler(GetClampedPitch(), GetCurrentYaw(), GetClampedRoll());
                    }
                    break;
            }
        }

        private float GetCurrentYaw()
        {
            Vector3 fwd = currentRotation * Vector3.forward;
            float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            return yaw;
        }

        private float GetClampedPitch() => Random.Range(pitchRange.x, pitchRange.y);
        private float GetClampedRoll() => Random.Range(rollRange.x, rollRange.y);

        private void ScheduleNextJump()
        {
            float delay = Random.Range(jumpCooldownRange.x, jumpCooldownRange.y);
            nextJumpTime = Time.unscaledTime + Mathf.Max(2f, delay); // at least 2s after init
        }

        // Face the current targetMoveVector (yaw), with gentle pitch/roll.
        private void UpdateFacingToMove()
        {
            float yawTarget = GetCurrentYaw();
            if (targetMoveVector.sqrMagnitude > 1e-4f)
                yawTarget = Mathf.Atan2(targetMoveVector.x, targetMoveVector.y) * Mathf.Rad2Deg;

            targetRotation = Quaternion.Euler(GetClampedPitch(), yawTarget, GetClampedRoll());

            // We want to look where we go, so don't free-spin yaw here.
            currentYawRate = 0f;
        }

        private bool IsObstacleAhead(out RaycastHit hit)
        {
            Vector3 origin = (Camera != null ? Camera.transform.position : ScaledDeviceCoord.position);
            Vector3 dir = (Camera != null ? Camera.transform.forward : (currentRotation * Vector3.forward)).normalized;

            return Physics.SphereCast(origin, obstacleCheckRadius, dir, out hit, obstacleCheckDistance, obstacleLayers, QueryTriggerInteraction.Ignore);
        }

        // Try several alternative headings; if all blocked, flip around.
        private void TryAvoidObstacle()
        {
            if (Camera == null) return;

            if (!IsObstacleAhead(out _)) return; // nothing to do

            Vector3 currXZ = new Vector3(targetMoveVector.x, 0f, targetMoveVector.y);
            if (currXZ.sqrMagnitude < 1e-6f)
                currXZ = Vector3.forward * Random.Range(moveMagnitudeRange.x, moveMagnitudeRange.y);

            for (int i = 0; i < maxAvoidanceTries; i++)
            {
                float sign = (Random.value < 0.5f) ? -1f : 1f;
                float angle = Random.Range(avoidTurnAngleRange.x, avoidTurnAngleRange.y) * sign;
                Vector3 cand = Quaternion.Euler(0f, angle, 0f) * currXZ;

                // Probe in that candidate facing
                Vector3 origin = Camera.transform.position;
                Vector3 dir = cand.normalized; // use candidate as "forward"
                bool blocked = Physics.SphereCast(origin, obstacleCheckRadius, dir, out _, obstacleCheckDistance, obstacleLayers, QueryTriggerInteraction.Ignore);

                if (!blocked)
                {
                    targetMoveVector = new Vector2(cand.x, cand.z);
                    targetPrimary2DAxis = targetMoveVector * 0.5f;
                    UpdateFacingToMove();
                    return;
                }
            }

            // Last resort: go backwards
            Vector3 flipped = -currXZ.normalized * currXZ.magnitude;
            targetMoveVector = new Vector2(flipped.x, flipped.z);
            targetPrimary2DAxis = targetMoveVector * 0.5f;
            UpdateFacingToMove();
        }

        // --- Visuals / sound / respawn unchanged ------------------------------

        public override void ShowTrackedVisual()
        {
            if (BasisVisualTracker != null) return;

            DeviceSupportInformation match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
            if (match.CanDisplayPhysicalTracker)
            {
                LoadModelWithKey(match.DeviceID);
            }
            else if (UseFallbackModel())
            {
                LoadModelWithKey(FallbackDeviceID);
            }
        }

        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F) { }

        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }

        private IEnumerator RespawnRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(respawnInterval);
                BasisSceneFactory.SpawnPlayer(BasisLocalPlayer.Instance);
            }
        }
    }
}
