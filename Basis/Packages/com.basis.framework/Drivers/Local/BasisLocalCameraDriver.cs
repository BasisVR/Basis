using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.TransformBinders;
using SteamAudio;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Vector3 = UnityEngine.Vector3;
using System;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Local camera driver that exposes static accessors for view vectors and eye positions,
    /// manages render-time head scaling, positions UI relative to the camera,
    /// and wires microphone visual feedback into the camera lifecycle.
    /// </summary>
    public class BasisLocalCameraDriver : MonoBehaviour
    {
        /// <summary>True when an instance is alive and assigned to <see cref="Instance"/>.</summary>
        public static bool HasInstance;

        /// <summary>Singleton instance set in <see cref="OnEnable"/>.</summary>
        public static BasisLocalCameraDriver Instance;
        /// <summary>Main camera used for local rendering.</summary>
        public static Camera CameraInstance;
        /// <summary>Main camera used for local rendering.</summary>
        public Camera Camera;

        /// <summary>Cached instance ID of <see cref="Camera"/> used to gate callbacks.</summary>
        public static int CameraInstanceID;

        /// <summary>AudioListener attached to the local camera (desktop) or XR rig.</summary>
        public AudioListener Listener;

        /// <summary>URP camera data (XR render toggling, etc.).</summary>
        public UniversalAdditionalCameraData CameraData;

        /// <summary>Steam Audio listener reference (optional; guarded by compile symbol).</summary>
        public SteamAudio.SteamAudioListener SteamAudioListener;

        /// <summary>Owning local player reference for scale/height info.</summary>
        public BasisLocalPlayer LocalPlayer;

        /// <summary>Default desktop camera field of view (degrees).</summary>
        public int DefaultCameraFov = 90;

        /// <summary>Raised after the instance is created and <see cref="OnEnable"/> finishes initial wiring.</summary>
        public static event Action InstanceExists;

        /// <summary>Optional input-lock helper for driving camera from input.</summary>
        public BasisLockToInput BasisLockToInput;

        /// <summary>True when event handlers are registered (render pipeline, device mode, mic events).</summary>
        public bool HasEvents = false;

        /// <summary>
        /// Desktop viewport location for the microphone UI icon
        /// (x,y in normalized viewport, z as depth for <see cref="Camera.ViewportToWorldPoint(Vector3)"/>).
        /// </summary>
        public Vector3 DesktopMicrophoneViewportPosition = new(0.2f, 0.15f, 1f);

        public Vector3 MobileMicrophoneViewportPosition = new(0.5f, 0.1f, 1f);

        /// <summary>True when the camera is in Third-Person mode.</summary>
        public bool IsThirdPerson = false;

        /// <summary>Distance behind the avatar in meters (at 1x scale).</summary>
        public float ThirdPersonDistance = 1.0f;

        /// <summary>Screen framing offset: X is horizontal (-1 to 1), Y is vertical (-1 to 1). e.g., X=0.3 puts the player on the left.
        public Vector2 ThirdPersonFraming = new Vector2(-0.25f, 0.1f);

        /// <summary>How fast the camera tracks the player's position
        public float TrackingSmoothness = 30f;

        /// <summary>How fast the camera rotates to match the player's look direction
        public float RotationSmoothness = 24f;

        /// <summary>Radius of the sphere cast used to prevent wall clipping.</summary>
        public float CameraCollisionRadius = 0.15f;

        /// <summary>Mask used for camera collision detection.</summary>
        public LayerMask CameraCollisionMask;

        private struct CameraParams
        {
            public Vector3 trackingPosition;
            public Vector2 framing;
            public float distance;
            public float pitch;
            public float yaw;
        }

        private CameraParams _currentCamParams;
        private bool _wasThirdPerson = false;

        /// <summary>The desired far clipping plane from scene settings before avatar overriding.</summary>
        private float DesiredClipFar = 1000.0f;
        /// <summary>The desired near clipping plane from scene settings before avatar overriding.</summary>
        private float DesiredClipNear = 0.001f;

        /// <summary>World-space position of the left eye (XR). In desktop mode this equals camera position.</summary>
        public static Vector3 LeftEye;

        /// <summary>World-space position of the right eye (XR). In desktop mode this equals camera position.</summary>
        public static Vector3 RightEye;

        /// <summary>Cached camera/world position updated each BeginCameraRendering for the main camera.</summary>
        public static Vector3 Position;

        /// <summary>Cached camera/world rotation updated each BeginCameraRendering for the main camera.</summary>
        public static Quaternion Rotation;

        /// <summary>Parent transform for UI elements anchored to the camera (e.g., mic icon).</summary>
        public Transform ParentOfUI;

#if !BASIS_DISABLE_MICROPHONE
        /// <summary>Driver for microphone icon visuals and layout near the camera.</summary>
        [SerializeField]
        public BasisLocalMicrophoneIconDriver microphoneIconDriver = new BasisLocalMicrophoneIconDriver();
#endif

        /// <summary>Driver for avatar preview camera and HUD display.</summary>
        [SerializeField]
        public BasisLocalAvatarPreviewDriver avatarPreviewDriver = new BasisLocalAvatarPreviewDriver();

        /// <summary>
        /// World forward vector of the active camera instance, or zero if no instance exists.
        /// Derived from the cached <see cref="Rotation"/> to avoid a native transform PInvoke per call.
        /// </summary>
        public static Vector3 Forward()
        {
            if (HasInstance)
            {
                return Rotation * Vector3.forward;
            }
            else
            {
                return Vector3.zero;
            }
        }

        /// <summary>
        /// World up vector of the active camera instance, or zero if no instance exists.
        /// </summary>
        public static Vector3 Up()
        {
            if (HasInstance)
            {
                return Rotation * Vector3.up;
            }
            else
            {
                return Vector3.zero;
            }
        }

        /// <summary>
        /// World right vector of the active camera instance, or zero if no instance exists.
        /// </summary>
        public static Vector3 Right()
        {
            if (HasInstance)
            {
                return Rotation * Vector3.right;
            }
            else
            {
                return Vector3.zero;
            }
        }

        /// <summary>
        /// Returns the left-eye position for XR, or the camera position for desktop mode.
        /// </summary>
        public static Vector3 LeftEyePosition()
        {
            if (BasisDeviceManagement.IsUserInDesktop())
            {
                return Instance.transform.position;
            }
            else
            {
                return LeftEye;
            }
        }

        /// <summary>
        /// Returns the right-eye position for XR, or the camera position for desktop mode.
        /// </summary>
        public static Vector3 RightEyePosition()
        {
            if (BasisDeviceManagement.IsUserInDesktop())
            {
                return Instance.transform.position;
            }
            else
            {
                return RightEye;
            }
        }

        /// <summary>
        /// Unity enable hook: sets singleton, configures camera planes, hooks events, initializes mic icon,
        /// and computes initial UI layout parameters.
        /// </summary>
        public void OnEnable()
        {
            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
                HasInstance = true;
            }
            CameraInstance = Camera;
            CameraInstanceID = Camera.GetEntityId();

            // Set initial scale from player height and set the clip planes.
            UpdateCameraScale();
            SetupCollisionMask();

            if (HasEvents == false)
            {
#if !BASIS_DISABLE_MICROPHONE
                BasisLocalMicrophoneDriver.OnPausedAction += microphoneIconDriver.OnPausedEvent;
                BasisLocalMicrophoneDriver.MainThreadOnHasAudio += microphoneIconDriver.MicrophoneTransmitting;
                BasisLocalMicrophoneDriver.MainThreadOnHasSilence += microphoneIconDriver.MicrophoneNotTransmitting;
                BasisNetworkModeration.OnShoutModeChanged += OnShoutModeChangedForIcon;
#else
                ParentOfUI.gameObject.SetActive(false);
#endif

                RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
                RenderPipelineManager.endCameraRendering += EndCameraRendering;

                BasisDeviceManagement.OnBootModeChanged += OnModeSwitch;
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame += UpdateCameraScale;
                BasisLocalPlayer.OnLocalAvatarChanged += UpdateCameraScale;

                BasisLocalPlayer.AfterSimulateOnLate.AddAction(150, SimulateThirdPerson);

                InstanceExists?.Invoke();
                HasEvents = true;
            }

#if !BASIS_DISABLE_MICROPHONE
            microphoneIconDriver.HardEnableVisuals(false);
            BasisLocalMicrophoneDriver.OnInitializedAction += OnMicrophoneDriverInitialized;
#endif

            avatarPreviewDriver.Initialize(this);

#if STEAMAUDIO_ENABLED
            if (SteamAudioListener != null)
            {
                SteamAudioManager.NotifyAudioListenerChanged();
            }
#endif
        }

        /// <summary>
        /// Unity destroy hook: unregisters pipeline/device/microphone events and clears flags.
        /// </summary>
        public void OnDestroy()
        {
            avatarPreviewDriver.Cleanup();
            CameraInstance = null;
            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= EndCameraRendering;
            BasisDeviceManagement.OnBootModeChanged -= OnModeSwitch;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= UpdateCameraScale;
            BasisLocalPlayer.OnLocalAvatarChanged -= UpdateCameraScale;
            BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(150, SimulateThirdPerson);
#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.OnPausedAction -= microphoneIconDriver.OnPausedEvent;
            BasisNetworkModeration.OnShoutModeChanged -= OnShoutModeChangedForIcon;
#endif
            HasEvents = false;
            HasInstance = false;
        }

        /// <summary>
        /// Unity disable hook: restores head scale, detaches render and mic events, and clears flags.
        /// </summary>
        public void OnDisable()
        {
            if (BasisLocalAvatarDriver.Mapping != null && BasisLocalAvatarDriver.Mapping.head != null)
            {
                BasisLocalAvatarDriver.Mapping.head.localScale = BasisLocalAvatarDriver.HeadScale;
            }
            if (HasEvents)
            {
                RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
                RenderPipelineManager.endCameraRendering -= EndCameraRendering;
                BasisDeviceManagement.OnBootModeChanged -= OnModeSwitch;
                BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(150, SimulateThirdPerson);
#if !BASIS_DISABLE_MICROPHONE
                BasisLocalMicrophoneDriver.MainThreadOnHasAudio -= microphoneIconDriver.MicrophoneTransmitting;
                BasisLocalMicrophoneDriver.MainThreadOnHasSilence -= microphoneIconDriver.MicrophoneNotTransmitting;
                BasisNetworkModeration.OnShoutModeChanged -= OnShoutModeChangedForIcon;
#endif
                HasEvents = false;
            }
            avatarPreviewDriver.Cleanup();
        }

        /// <summary>
        /// Reacts to device mode switches (desktop/XR), adjusting FOV for desktop and rescaling from height.
        /// </summary>
        /// <param name="mode">Device mode string (e.g., <see cref="BasisConstants.Desktop"/>).</param>
        private void OnModeSwitch(string mode)
        {
            if (mode == BasisConstants.Desktop)
            {
                Camera.fieldOfView = DefaultCameraFov;
            }
            UpdateCameraScale(BasisHeightDriver.HeightModeChange.OnTpose);
        }

#if !BASIS_DISABLE_MICROPHONE
        /// <summary>
        /// Initializes microphone icon visibility and layout when the microphone driver signals it is ready.
        /// </summary>
        /// <param name="initialized"></param>
        private void OnMicrophoneDriverInitialized(bool initialized)
        {
            if (initialized)
            {
                microphoneIconDriver.Initalize(this);
            }
            microphoneIconDriver.HardEnableVisuals(initialized);
        }

        private void OnShoutModeChangedForIcon(ushort playerId, bool enabled)
        {
            if (BasisNetworkPlayer.LocalPlayer == null || playerId != BasisNetworkPlayer.LocalPlayer.playerId)
                return;
            microphoneIconDriver.OnShoutModeChanged();
        }
#endif

        /// <summary>
        /// Gets world-space camera transform or returns zero/identity when no instance exists.
        /// </summary>
        /// <param name="Position">Out: world position.</param>
        /// <param name="Rotation">Out: world rotation.</param>
        public static void GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation)
        {
            if (HasInstance)
            {
                Instance.transform.GetPositionAndRotation(out Position, out Rotation);
            }
            else
            {
                Position = Vector3.zero;
                Rotation = Quaternion.identity;
            }
        }

        public void SetDesiredClipPlanes(float clipFar, float clipNear)
        {
            DesiredClipFar = clipFar;
            DesiredClipNear = clipNear;
            UpdateCameraScale(BasisHeightDriver.HeightModeChange.OnTpose);
        }
        private void UpdateCameraScale()
        {
            UpdateCameraScale(BasisHeightDriver.HeightModeChange.OnTpose);
        }
        /// <summary>
        /// Applies scale from the player's height so the camera’s local scale matches avatar scale.
        /// </summary>
        public void UpdateCameraScale(BasisHeightDriver.HeightModeChange HeightModeChange)
        {
            this.transform.localScale = Vector3.one * BasisHeightDriver.DeviceScale;
            // Ensure that the near clip plane is never far enough away that the avatar body clips through it.
            // Critically we need to avoid small player heights causing the UI to become unusable due to clipping.
            // At the same time, we need to pull in the far clip plane on mobile platforms to avoid depth buffer precision issues.
            float eyeHeightMeters = Mathf.Max(BasisHeightDriver.SelectedScaledPlayerHeight, 1e-4f);
            if (BasisDeviceManagement.IsMobileHardware())
            {
                Camera.nearClipPlane = Mathf.Clamp(DesiredClipNear, eyeHeightMeters / 32.0f, eyeHeightMeters / 16.0f);
                Camera.farClipPlane = Mathf.Clamp(DesiredClipFar, eyeHeightMeters * 64.0f, eyeHeightMeters * 512.0f);
            }
            else
            {
                Camera.nearClipPlane = Mathf.Clamp(DesiredClipNear, eyeHeightMeters / 128.0f, eyeHeightMeters / 32.0f);
                Camera.farClipPlane = Mathf.Clamp(DesiredClipFar, eyeHeightMeters * 128.0f, eyeHeightMeters * 8192.0f);
            }
        }
        /// <summary>
        /// Generates the collision mask for the camera in third-person mode.
        /// </summary>
        private void SetupCollisionMask()
        {
            int ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
            int playerLayer = LayerMask.NameToLayer("Player");
            int localPlayerAvatar = LayerMask.NameToLayer("LocalPlayerAvatar");
            int ignoredByInteractable = LayerMask.NameToLayer("IgnoredByInteractable");
            int uiLayer = LayerMask.NameToLayer("UI");
            int overlayUiLayer = LayerMask.NameToLayer("OverlayUI");

            int allLayers = ~0;

            CameraCollisionMask = allLayers &
                ~(1 << ignoreRaycast) &
                ~(1 << playerLayer) &
                ~(1 << localPlayerAvatar) &
                ~(1 << ignoredByInteractable) &
                ~(1 << uiLayer) &
                ~(1 << overlayUiLayer);
        }

        /// <summary>
        /// Frame-rate independent damping (exponential decay).
        /// </summary>
        public static float Damp(float current, float target, float lambda, float dt)
        {
            return Mathf.LerpUnclamped(target, current, Mathf.Exp(-lambda * dt));
        }

        public static Vector2 Damp(Vector2 current, Vector2 target, float lambda, float dt)
        {
            return Vector2.LerpUnclamped(target, current, Mathf.Exp(-lambda * dt));
        }

        public static Vector3 Damp(Vector3 current, Vector3 target, float lambda, float dt)
        {
            return Vector3.LerpUnclamped(target, current, Mathf.Exp(-lambda * dt));
        }

        public static Quaternion Damp(Quaternion current, Quaternion target, float lambda, float dt)
        {
            return Quaternion.SlerpUnclamped(target, current, Mathf.Exp(-lambda * dt));
        }

        /// <summary>
        /// Special version for damping angles (degrees), preventing the 360-degree wrap-around glitch.
        /// </summary>
        public static float DampAngle(float current, float target, float lambda, float dt)
        {
            return Mathf.LerpAngle(current, target, 1.0f - Mathf.Exp(-lambda * dt));
        }


        /// <summary>
        /// Converts a half-life (in seconds) to a lambda for the damping function.
        /// </summary>
        /// <param name="halfLife">The time (in seconds) it takes to reach 50% of the target.</param>
        public static float GetDampRate(float halfLife)
        {
            return 0.69314718f / Mathf.Max(halfLife, 0.00001f);
        }

        /// <summary>
        /// Orchestrates the third-person offset, scaling it by the avatar's height ratio
        /// and performing a SphereCast to prevent the camera from clipping into geometry.
        /// </summary>
        private void SimulateThirdPerson()
        {
            if (!BasisDeviceManagement.IsUserInDesktop() || CameraInstance == null)
            {
                if (_wasThirdPerson)
                {
                    transform.localPosition = Vector3.zero;
                    transform.localRotation = Quaternion.identity;
                    _wasThirdPerson = false;
                }
                return;
            }

            Transform parentTransform = transform.parent; // Typically the Eye/Head bone
            if (parentTransform == null) return;

            float dt = Time.deltaTime;
            float scale = BasisHeightDriver.PlayerToDefaultRatioScaledWithAvatarScale;

            // Target Variables
            Vector3 targetTrackingPos = parentTransform.position;
            float targetPitch = parentTransform.rotation.eulerAngles.x;
            float targetYaw = parentTransform.rotation.eulerAngles.y;
            float targetDistance = IsThirdPerson ? ThirdPersonDistance * scale : 0f;
            Vector2 targetFraming = IsThirdPerson ? ThirdPersonFraming : Vector2.zero;

            // Initialize params if we just switched INTO third person
            if (IsThirdPerson && !_wasThirdPerson)
            {
                _currentCamParams = new CameraParams
                {
                    trackingPosition = targetTrackingPos,
                    framing = targetFraming,
                    distance = targetDistance,
                    pitch = targetPitch,
                    yaw = targetYaw
                };
                _wasThirdPerson = true;
            }

            // If we are fully back in first person, snap to parent and stop updating
            if (!IsThirdPerson && _wasThirdPerson && _currentCamParams.distance < 0.05f)
            {
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
                _wasThirdPerson = false;
                return;
            }

            // We only simulate if we are in 3rd person OR smoothly transitioning back to 1st person
            if (_wasThirdPerson)
            {
                // Smoothly blend the parameter space
                _currentCamParams.trackingPosition = Damp(_currentCamParams.trackingPosition, targetTrackingPos, TrackingSmoothness, dt);
                _currentCamParams.distance = Damp(_currentCamParams.distance, targetDistance, TrackingSmoothness, dt);
                _currentCamParams.framing = Damp(_currentCamParams.framing, targetFraming, TrackingSmoothness, dt);

                _currentCamParams.pitch = DampAngle(_currentCamParams.pitch, targetPitch, RotationSmoothness, dt);
                _currentCamParams.yaw = DampAngle(_currentCamParams.yaw, targetYaw, RotationSmoothness, dt);

                // Project onto picture plane
                float tanFOVY = Mathf.Tan(0.5f * Mathf.Deg2Rad * CameraInstance.fieldOfView);
                float tanFOVX = tanFOVY * CameraInstance.aspect;

                Vector3 localOffset = new Vector3(
                    _currentCamParams.distance * tanFOVX * _currentCamParams.framing.x,
                    _currentCamParams.distance * tanFOVY * _currentCamParams.framing.y,
                    _currentCamParams.distance
                );

                Quaternion desiredRotation = Quaternion.Euler(_currentCamParams.pitch, _currentCamParams.yaw, 0);
                Vector3 desiredWorldPos = _currentCamParams.trackingPosition - (desiredRotation * localOffset);

                // Wall collision detection
                Vector3 direction = desiredWorldPos - _currentCamParams.trackingPosition;
                float maxDistance = direction.magnitude;
                float scaledRadius = CameraCollisionRadius * scale;

                if (Physics.SphereCast(_currentCamParams.trackingPosition, scaledRadius, direction.normalized, out RaycastHit hit, maxDistance, CameraCollisionMask, QueryTriggerInteraction.Ignore))
                {
                    desiredWorldPos = hit.point + (hit.normal * scaledRadius);
                }

                // Apply final world transform (Overrides parent constraint)
                transform.position = desiredWorldPos;
                transform.rotation = desiredRotation;
            }
        }

        /// <summary>
        /// URP callback after camera render: restores head scale to normal for this camera.
        /// </summary>
        private void EndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (BasisLocalAvatarDriver.Mapping.Hashead)
            {
                if (Camera.GetEntityId() == CameraInstanceID)
                {
                    BasisLocalAvatarDriver.ScaleHeadToNormal();
                }
            }
        }
        /// <summary>
        /// URP callback before camera render: caches camera transform, hides head for view,
        /// and positions the microphone UI either in XR or desktop mode.
        /// </summary>
        public void BeginCameraRendering(ScriptableRenderContext context, Camera Camera)
        {
            if (BasisLocalAvatarDriver.Mapping.Hashead)
            {
                if (Camera.GetEntityId() == CameraInstanceID)
                {
                    if (!IsThirdPerson)
                    {
                        BasisLocalAvatarDriver.ScaleheadToZero();
                    }
                }
            }
        }

        public void Simulate()
        {
            if (BasisLocalAvatarDriver.Mapping.Hashead)
            {
                this.transform.GetPositionAndRotation(out Position, out Rotation);
                if (CameraData.allowXRRendering)
                {
#if !BASIS_DISABLE_MICROPHONE
                    ParentOfUI.localPosition = microphoneIconDriver.CalculateClampedLocal(Camera, Position);
#endif
                }
                else
                {
                    if (BasisDeviceManagement.IsMobileHardware())
                    {
                        Vector3 viewportPos = MobileMicrophoneViewportPosition;
                        viewportPos.x += microphoneIconDriver.IconPositionOffset.x;
                        viewportPos.y += microphoneIconDriver.IconPositionOffset.y;
                        Vector3 worldPoint = Camera.ViewportToWorldPoint(viewportPos);
                        // assume this transform is the camera parent
                        Vector3 localPos = this.transform.InverseTransformPoint(worldPoint);
                        ParentOfUI.localPosition = localPos * BasisHeightDriver.PlayerToDefaultRatioScaledWithAvatarScale;
                    }
                    else
                    {
                        Vector3 viewportPos = DesktopMicrophoneViewportPosition;
                        viewportPos.x += microphoneIconDriver.IconPositionOffset.x;
                        viewportPos.y += microphoneIconDriver.IconPositionOffset.y;
                        Vector3 worldPoint = Camera.ViewportToWorldPoint(viewportPos);
                        // assume this transform is the camera parent
                        Vector3 localPos = this.transform.InverseTransformPoint(worldPoint);
                        ParentOfUI.localPosition = localPos * BasisHeightDriver.PlayerToDefaultRatioScaledWithAvatarScale;
                    }
                }
                avatarPreviewDriver.Simulate();
            }
        }

        /// <summary>
        /// Enables/disables XR rendering on the local camera’s URP data.
        /// </summary>
        /// <param name="AllowXRRendering">True to allow XR; false for desktop-only.</param>
        public static void AllowXRRenderering(bool AllowXRRendering)
        {
            if (Instance != null)
            {
                Instance.CameraData.allowXRRendering = AllowXRRendering;
            }
            else
            {
                BasisDebug.LogError("Missing Instance of Local CameraDriver!", BasisDebug.LogTag.Camera);
            }
        }
    }
}
