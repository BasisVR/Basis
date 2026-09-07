using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
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

        public Transform ListenerTransform;
        public AudioListener Listener;
#if STEAMAUDIO_ENABLED
        public SteamAudioListener SteamListener;
#endif

        /// <summary>URP camera data (XR render toggling, etc.).</summary>
        public UniversalAdditionalCameraData CameraData;

        /// <summary>Owning local player reference for scale/height info.</summary>
        public BasisLocalPlayer LocalPlayer;

        /// <summary>Default desktop camera field of view (degrees).</summary>
        public int DefaultCameraFov = 90;

        /// <summary>Raised after the instance is created and <see cref="OnEnable"/> finishes initial wiring.</summary>
        public static event Action InstanceExists;

        /// <summary>Raised after main camera render settings (clearFlags, backgroundColor, skybox, clip planes) are applied from a scene.</summary>
        public static event Action RenderSettingsApplied;

        public static void RaiseRenderSettingsApplied()
        {
            RenderSettingsApplied?.Invoke();
        }

        /// <summary>
        /// Raised once per rendered frame from <c>BasisEventDriver</c>'s before-render pass, after the
        /// local player's render-time simulation has settled the camera and head. The late-latch slot:
        /// the camera pose statics (<see cref="Position"/>, <see cref="HeadPosition"/>, the eye
        /// vectors) are final here, and nothing has been drawn yet.
        ///
        /// Anything writing a transform that must not trail the head by a frame belongs here rather
        /// than in <c>LateUpdate</c>. It runs with the whole render waiting on it, so keep handlers
        /// short. Does not fire on a headless client, which never renders.
        /// </summary>
        public static event Action BeforeRender;

        public static void RaiseBeforeRender()
        {
            // Subscribers are independent, and one of them is the sandbox bridge — a throwing
            // handler must not take the rest of the frame's late-latch work down with it.
            Delegate[] handlers = BeforeRender?.GetInvocationList();
            if (handlers == null)
            {
                return;
            }
            for (int Index = 0; Index < handlers.Length; Index++)
            {
                try
                {
                    ((Action)handlers[Index])();
                }
                catch (Exception ex)
                {
                    BasisDebug.LogErrorOnce($"BasisLocalCameraDriver.BeforeRender handler failed: {ex}", BasisDebug.LogTag.Event);
                }
            }
        }

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

        public Vector3 VRMicrophoneLocalOffset = new(-0.175f, -0.17f, 0.5f);

        public float MicrophoneAnchorDistance = 0.5f;

        /// <summary>True when the camera is in Third-Person mode.</summary>
        public bool IsThirdPerson = false;

        /// <summary>
        /// Admin-controlled hard lockout. While true, third-person is rejected at
        /// <see cref="IsThirdPersonAllowed"/> and any active third-person camera is snapped
        /// back to first-person. Toggle through <see cref="SetAdminThirdPersonLocked"/> so
        /// the lockout actually takes hold mid-session.
        /// </summary>
        public static bool AdminThirdPersonLocked;

        /// <summary>
        /// Sets the admin third-person lockout and immediately yanks the active camera back
        /// to first-person so an admin clicking the toggle sees the change without waiting
        /// for the next ToggleThirdPerson press.
        /// </summary>
        public static void SetAdminThirdPersonLocked(bool locked)
        {
            AdminThirdPersonLocked = locked;
            if (locked && HasInstance)
            {
                Instance.IsThirdPerson = false;
            }
        }

        /// <summary>Screen framing offset: X is horizontal (-1 to 1), Y is vertical (-1 to 1). e.g., X=0.3 puts the player on the right.
        public Vector2 ThirdPersonFraming = new Vector2(-0.25f, 0.1f);

        /// <summary>How fast the camera tracks the player's position
        public float TrackingSmoothness = 30f;

        /// <summary>How fast the camera rotates to match the player's look direction
        public float RotationSmoothness = 24f;

        /// <summary>Radius of the sphere cast used to prevent wall clipping.</summary>
        public float CameraCollisionRadius = 0.15f;

        /// <summary>Mask used for camera collision detection.</summary>
        public LayerMask CameraCollisionMask;

        public float ThirdPersonMinZoomDist = 0.5f;
        public float ThirdPersonMaxZoomDist = 3.0f;
        public float ThirdPersonZoomSensitivity = 0.5f;

        public float ThirdPersonMinZoomDistFoV = 75f;
        public float ThirdPersonMaxZoomDistFoV = 50f;

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
        private float _currentThirdPersonDistance = 1.0f;

        // True while the audio listener has been pushed off its camera-local-zero anchor to
        // stay on the head (third-person follow-head). Used to snap it back exactly once.
        private bool _listenerDetachedFromCamera;

        /// <summary>
        /// Optional external world pose for the audio listener. When set and it returns a
        /// value, the listener is driven there instead of tracking the player's head — a
        /// handheld camera uses it to make the world be heard from the camera's viewpoint.
        /// Returning null defers to the normal head/camera logic.
        /// <para>
        /// Only one AudioListener exists per scene, so this moves the real listener rather
        /// than adding another; the last provider to set it wins, and it must be cleared when
        /// the source goes away or the listener would stay stuck at a dead pose.
        /// </para>
        /// </summary>
        public static Func<(Vector3 position, Quaternion rotation)?> AudioListenerPoseOverride;

        // Cached desktop mic-icon layout. The viewport->camera-local position cancels out the
        // camera's world pose, so it is recomputed only when one of these inputs actually changes.
        private bool _micLayoutValid;
        private float _micLayoutFov, _micLayoutAspect, _micLayoutDistance;
        private Vector2 _micLayoutOffset;
        private bool _micLayoutMobile;

        /// <summary>The desired far clipping plane from scene settings before avatar overriding.</summary>
        private float DesiredClipFar = 1000.0f;
        /// <summary>The desired near clipping plane from scene settings before avatar overriding.</summary>
        private float DesiredClipNear = 0.001f;

        /// <summary>Raw left-eye position from the active XR backend: tracking space on OpenXR, head-relative on OpenVR. Not world space; not written in desktop mode.</summary>
        public static Vector3 LeftEye;

        /// <summary>Raw right-eye position from the active XR backend: tracking space on OpenXR, head-relative on OpenVR. Not world space; not written in desktop mode.</summary>
        public static Vector3 RightEye;

        /// <summary>
        /// World-space origin of the OpenXR eye-gaze ray. Updated by the active eye-tracking
        /// device when EyeGazeInteraction reports a tracked pose. Stale (last-known) when
        /// <see cref="HasEyeGaze"/> is false.
        /// </summary>
        public static Vector3 GazeOrigin;

        /// <summary>
        /// World-space forward direction of the OpenXR eye-gaze ray. See <see cref="GazeOrigin"/>.
        /// </summary>
        public static Vector3 GazeDirection;

        /// <summary>
        /// True while the active eye-tracking device is reporting a tracked gaze pose this frame.
        /// Consumers should fall back to <see cref="Forward"/> / <see cref="HeadForward"/> when false.
        /// </summary>
        public static bool HasEyeGaze;

        /// <summary>Cached camera/world position updated each BeginCameraRendering for the main camera.</summary>
        public static Vector3 Position;

        /// <summary>Cached camera/world rotation updated each BeginCameraRendering for the main camera.</summary>
        public static Quaternion Rotation;

        /// <summary>
        /// World-space head bone position. Equal to <see cref="Position"/> in first-person and XR;
        /// in third-person it stays anchored to the player's head while <see cref="Position"/> moves
        /// to the orbital camera. Use this for any system that conceptually wants "where is the
        /// player's eye/ear" — gaze targeting, audio listener distance, social-triangle math.
        /// </summary>
        public static Vector3 HeadPosition;

        /// <summary>World-space head bone rotation. See <see cref="HeadPosition"/>.</summary>
        public static Quaternion HeadRotation;

        /// <summary>World forward vector of the head, or zero if no instance exists.</summary>
        public static Vector3 HeadForward()
        {
            if (HasInstance)
            {
                return HeadRotation * Vector3.forward;
            }
            return Vector3.zero;
        }


        /// <summary>Parent transform for UI elements anchored to the camera (e.g., mic icon).</summary>
        public Transform ParentOfUI;

        /// <summary>
        /// Parent Transform Should be the Input 
        /// </summary>
        public Transform ParentTransform;

        public static Transform SelfTransform;

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
        /// Raw backend eye position: <see cref="LeftEye"/> in XR (tracking space on OpenXR, head-relative
        /// on OpenVR — not world space), or the world-space camera position in desktop mode.
        /// Use <see cref="LeftEyeWorldPosition"/> for the world-space render-camera eye.
        /// </summary>
        public static Vector3 LeftEyeRawPosition()
        {
            if (BasisDeviceManagement.IsUserInDesktop())
            {
                return SelfTransform.position;
            }
            else
            {
                return LeftEye;
            }
        }

        /// <summary>
        /// Raw backend eye position: <see cref="RightEye"/> in XR (tracking space on OpenXR, head-relative
        /// on OpenVR — not world space), or the world-space camera position in desktop mode.
        /// Use <see cref="RightEyeWorldPosition"/> for the world-space render-camera eye.
        /// </summary>
        public static Vector3 RightEyeRawPosition()
        {
            if (BasisDeviceManagement.IsUserInDesktop())
            {
                return SelfTransform.position;
            }
            else
            {
                return RightEye;
            }
        }

        /// <summary>
        /// World-space position of the stereo render camera's left eye, recovered from the camera's
        /// stereo view matrix. Falls back to the camera position when stereo rendering is inactive
        /// (desktop mode), or zero when no camera instance exists.
        /// </summary>
        public static Vector3 LeftEyeWorldPosition()
        {
            return EyeWorldPosition(Camera.StereoscopicEye.Left);
        }

        /// <summary>
        /// World-space position of the stereo render camera's right eye, recovered from the camera's
        /// stereo view matrix. Falls back to the camera position when stereo rendering is inactive
        /// (desktop mode), or zero when no camera instance exists.
        /// </summary>
        public static Vector3 RightEyeWorldPosition()
        {
            return EyeWorldPosition(Camera.StereoscopicEye.Right);
        }

        private static Vector3 EyeWorldPosition(Camera.StereoscopicEye eye)
        {
            if (!HasInstance || CameraInstance == null)
            {
                return Vector3.zero;
            }
            if (CameraInstance.stereoEnabled)
            {
                return CameraInstance.GetStereoViewMatrix(eye).inverse.MultiplyPoint(Vector3.zero);
            }
            return SelfTransform.position;
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
            SelfTransform = Instance.transform;
            // Set initial scale from player height and set the clip planes.
            UpdateCameraScale();
            SetupCollisionMask();

            if (HasEvents == false)
            {
#if !BASIS_DISABLE_MICROPHONE
                BasisLocalMicrophoneDriver.OnPausedAction += microphoneIconDriver.OnPausedEvent;
                BasisLocalMicrophoneDriver.MainThreadOnHasAudio += microphoneIconDriver.MicrophoneTransmitting;
                BasisLocalMicrophoneDriver.MainThreadOnHasSilence += microphoneIconDriver.MicrophoneNotTransmitting;
                BasisNetworkModeration.OnAnnounceModeChanged += OnAnnounceModeChangedForIcon;
                Basis.Scripts.Networking.BasisTalkModeManager.OnLocalTalkModeChanged += microphoneIconDriver.OnTalkModeChanged;
                BasisLocalMicrophoneDriver.OnInitializedAction += OnMicrophoneDriverInitialized;
#endif

                RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
                RenderPipelineManager.endCameraRendering += EndCameraRendering;

                BasisDeviceManagement.OnBootModeChanged += OnModeSwitch;
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame += UpdateCameraScale;
                BasisLocalPlayer.OnLocalAvatarChanged += UpdateCameraScale;

                BasisSettingsDefaults.UseCameraClipOverride.OnChanged += OnClipOverrideToggleChanged;
                BasisSettingsDefaults.CameraClipNear.OnChanged += OnClipBindingChangedFloat;
                BasisSettingsDefaults.CameraClipFar.OnChanged += OnClipBindingChangedFloat;
                BasisSettingsDefaults.EnableThirdPersonCamera.OnChanged += OnThirdPersonEnabledChanged;
                BasisNetworkModeration.OnGlobalThirdPersonDisabledChanged += OnGlobalThirdPersonDisabledChanged;
                // Latch any state already received before this driver came up (reconnect, scene reload).
                if (BasisNetworkModeration.GlobalThirdPersonDisabled)
                {
                    SetAdminThirdPersonLocked(true);
                }

                InstanceExists?.Invoke();
                HasEvents = true;
            }

#if !BASIS_DISABLE_MICROPHONE
            microphoneIconDriver.HardEnableVisuals(false);
#endif

            avatarPreviewDriver.Initialize(this);

#if STEAMAUDIO_ENABLED
            if (SteamListener != null)
            {
                SteamAudioManager.NotifyAudioListenerChanged();
            }
#endif
            ParentTransform = SelfTransform.parent;
        }
        /// <summary>
        /// Unity destroy hook: unregisters pipeline/device/microphone events and clears flags.
        /// </summary>
        public void OnDestroy()
        {
            avatarPreviewDriver.Cleanup();
            CameraInstance = null;

            ListenerTransform = null;
            Listener = null;
#if STEAMAUDIO_ENABLED
            SteamListener = null;
#endif

            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= EndCameraRendering;
            BasisDeviceManagement.OnBootModeChanged -= OnModeSwitch;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= UpdateCameraScale;
            BasisLocalPlayer.OnLocalAvatarChanged -= UpdateCameraScale;
            BasisSettingsDefaults.UseCameraClipOverride.OnChanged -= OnClipOverrideToggleChanged;
            BasisSettingsDefaults.CameraClipNear.OnChanged -= OnClipBindingChangedFloat;
            BasisSettingsDefaults.CameraClipFar.OnChanged -= OnClipBindingChangedFloat;
            BasisSettingsDefaults.EnableThirdPersonCamera.OnChanged -= OnThirdPersonEnabledChanged;
            BasisNetworkModeration.OnGlobalThirdPersonDisabledChanged -= OnGlobalThirdPersonDisabledChanged;
#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.OnPausedAction -= microphoneIconDriver.OnPausedEvent;
            BasisLocalMicrophoneDriver.OnInitializedAction -= OnMicrophoneDriverInitialized;
            BasisNetworkModeration.OnAnnounceModeChanged -= OnAnnounceModeChangedForIcon;
            Basis.Scripts.Networking.BasisTalkModeManager.OnLocalTalkModeChanged -= microphoneIconDriver.OnTalkModeChanged;
            microphoneIconDriver.Dispose();
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
                BasisLocalAvatarDriver.Mapping.head.SetLocalScale(BasisLocalAvatarDriver.HeadScale);
            }
            if (HasEvents)
            {
                RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
                RenderPipelineManager.endCameraRendering -= EndCameraRendering;
                BasisDeviceManagement.OnBootModeChanged -= OnModeSwitch;
#if !BASIS_DISABLE_MICROPHONE
                BasisLocalMicrophoneDriver.MainThreadOnHasAudio -= microphoneIconDriver.MicrophoneTransmitting;
                BasisLocalMicrophoneDriver.MainThreadOnHasSilence -= microphoneIconDriver.MicrophoneNotTransmitting;
                BasisNetworkModeration.OnAnnounceModeChanged -= OnAnnounceModeChangedForIcon;
                Basis.Scripts.Networking.BasisTalkModeManager.OnLocalTalkModeChanged -= microphoneIconDriver.OnTalkModeChanged;
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
            else
            {
                // Leaving desktop (VR/XR) hard-disables third-person; the next Simulate
                // tick snaps the camera back to first-person to restore the camera transform.
                IsThirdPerson = false;
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
                microphoneIconDriver.Initialize(this);
            }
            microphoneIconDriver.HardEnableVisuals(initialized);
        }

        private void OnAnnounceModeChangedForIcon(ushort playerId, bool enabled)
        {
            // The icon color is derived purely from local state (BasisAudioTransmission.IsInAnnounceMode,
            // which is only ever set for the local player, plus the local talk mode), so any announce-mode
            // signal just re-derives the local color. No need to look up the network player to filter.
            microphoneIconDriver.OnAnnounceModeChanged();
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
                SelfTransform.GetPose(out Position, out Rotation);
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
            SelfTransform.SetLocalScale(Vector3.one * BasisHeightDriver.DeviceScale);
            if (BasisSettingsDefaults.UseCameraClipOverride.RawValue)
            {
                // User has explicitly opted into raw clip values; bypass the eye-height clamp.
                float overrideNear = Mathf.Max(BasisSettingsDefaults.CameraClipNear.RawValue, 1e-4f);
                float overrideFar = Mathf.Max(BasisSettingsDefaults.CameraClipFar.RawValue, overrideNear + 1e-3f);
                Camera.nearClipPlane = overrideNear;
                Camera.farClipPlane = overrideFar;
                return;
            }
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
                ~(1 << overlayUiLayer) &
                ~BasisLayerMapper.HandHeldCameraUIMask;
        }

        public void ToggleThirdPerson()
        {
            if (IsThirdPersonAllowed())
            {
                IsThirdPerson = !IsThirdPerson;
            }
            else
            {
                IsThirdPerson = false;
            }
        }

        public void ExitThirdPerson()
        {
            if (IsThirdPerson)
            {
                IsThirdPerson = false;
            }
        }

        /// <summary>
        /// True when the third-person camera is permitted: requires the admin lockout to be off,
        /// the General-tab toggle to be enabled, AND the user to be in desktop mode (VR/XR
        /// are hard-disabled).
        /// </summary>
        private static bool IsThirdPersonAllowed()
        {
            return !AdminThirdPersonLocked && BasisDeviceManagement.IsUserInDesktop() && BasisSettingsDefaults.EnableThirdPersonCamera.RawValue;
        }

        /// <summary>
        /// </summary>
        public void ApplyZoom(float zoomDelta)
        {
            if (IsThirdPersonAllowed())
            {
                // If scrolling out while in 1st person
                if (!IsThirdPerson && zoomDelta < 0)
                {
                    IsThirdPerson = true;
                    _currentThirdPersonDistance = ThirdPersonMinZoomDist + 0.1f;
                }
                else if (IsThirdPerson)
                {
                    _currentThirdPersonDistance -= zoomDelta * ThirdPersonZoomSensitivity;

                    // If zoomed all the way in, return to first person
                    if (_currentThirdPersonDistance < ThirdPersonMinZoomDist)
                    {
                        IsThirdPerson = false;
                        _currentThirdPersonDistance = ThirdPersonMinZoomDist;
                    }
                    else if (_currentThirdPersonDistance > ThirdPersonMaxZoomDist)
                    {
                        _currentThirdPersonDistance = ThirdPersonMaxZoomDist;
                    }
                }
            }
        }

        /// <summary>
        /// Orchestrates the third-person offset, scaling it by the avatar's height ratio
        /// and performing a SphereCast to prevent the camera from clipping into geometry.
        /// Called from <see cref="Basis.EventDriver.BasisEventDriver.LateUpdate"/> after all
        /// jobified transform access has completed for the frame, so this can safely write
        /// to the camera transform without racing with in-flight transform jobs.
        /// </summary>
        public void Simulate(float DeltaTime)
        {
            BasisAutoScaleEstimator.Tick(DeltaTime);
            if (BasisLocalAvatarDriver.Mapping.Hashead)
            {
                SelfTransform.GetPose(out Position, out Rotation);

                if (IsThirdPerson)
                {
                    var headWorld = BasisLocalBoneDriver.HeadControl.OutgoingWorldData;
                    HeadPosition = headWorld.position;
                    HeadRotation = headWorld.rotation;
                }
                else
                {
                    HeadPosition = Position;
                    HeadRotation = Rotation;
                }

                // ListenerTransform is a child of the camera at local zero, so it tracks the camera
                // for free in first-person and orbit-follow third-person. It only needs an explicit
                // world pose to stay on the head while the camera orbits away (follow-head option),
                // or when something claims the listener outright (handheld camera audio).
                (Vector3 position, Quaternion rotation)? listenerOverride = AudioListenerPoseOverride?.Invoke();
                if (listenerOverride.HasValue)
                {
                    ListenerTransform.SetPose(listenerOverride.Value.position, listenerOverride.Value.rotation);
                    _listenerDetachedFromCamera = true;
                }
                else if (IsThirdPerson && BasisSettingsDefaults.AudioListenerFollowsHead.RawValue)
                {
                    ListenerTransform.SetPose(HeadPosition, HeadRotation);
                    _listenerDetachedFromCamera = true;
                }
                else if (_listenerDetachedFromCamera)
                {
                    ListenerTransform.SetLocalPose(Vector3.zero, Quaternion.identity);
                    _listenerDetachedFromCamera = false;
                }
                if (CameraData.allowXRRendering)
                {
                    Vector3 offset = VRMicrophoneLocalOffset;
                    offset.x += microphoneIconDriver.IconPositionOffset.x;
                    offset.y += microphoneIconDriver.IconPositionOffset.y;
                    ParentOfUI.SetLocalPosition(offset);
                    _micLayoutValid = false;
                }
                else
                {
                    // The viewport->camera-local mic direction is independent of the camera's world
                    // pose and scale, so the anchor only depends on FOV, aspect, the icon offset,
                    // mobile mode and the anchor distance. Recompute (and re-set the transform) only
                    // on a change instead of running ViewportToWorldPoint + InverseTransformPoint every frame.
                    bool isMobile = BasisDeviceManagement.IsMobileHardware();
                    float fov = CameraInstance.fieldOfView;
                    float aspect = CameraInstance.aspect;
                    Vector2 offset = microphoneIconDriver.IconPositionOffset;

                    if (!_micLayoutValid || fov != _micLayoutFov || aspect != _micLayoutAspect
                        || offset != _micLayoutOffset || isMobile != _micLayoutMobile
                        || MicrophoneAnchorDistance != _micLayoutDistance)
                    {
                        Vector3 viewportPos = isMobile ? MobileMicrophoneViewportPosition : DesktopMicrophoneViewportPosition;
                        viewportPos.x += offset.x;
                        viewportPos.y += offset.y;
                        Vector3 worldPoint = Camera.ViewportToWorldPoint(viewportPos);
                        // assume this transform is the camera parent
                        Vector3 localPos = SelfTransform.ToLocalPoint(worldPoint);
                        ParentOfUI.SetLocalPosition(localPos.normalized * MicrophoneAnchorDistance);

                        _micLayoutValid = true;
                        _micLayoutFov = fov;
                        _micLayoutAspect = aspect;
                        _micLayoutOffset = offset;
                        _micLayoutMobile = isMobile;
                        _micLayoutDistance = MicrophoneAnchorDistance;
                    }
                }
                avatarPreviewDriver.Simulate();
            }

            if (!IsThirdPerson || !BasisDeviceManagement.IsUserInDesktop())
            {
                if (_wasThirdPerson)
                {
                    SelfTransform.SetLocalPose(Vector3.zero, Quaternion.identity);
                    CameraInstance.fieldOfView = DefaultCameraFov;
                    _wasThirdPerson = false;
                }
                return;
            }

            // BasisLockToInput re-parents this camera at runtime (player root <-> head input),
            // so the orbit center must be read fresh. A value cached in OnEnable goes stale and
            // leaves third-person orbiting the player root, ~eye-height too low (Y only).
            Transform parentTransform = SelfTransform.parent;

            float scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            parentTransform.GetPose(out Vector3 targetTrackingPos, out Quaternion targetTrackingRot);

            Vector3 euler = targetTrackingRot.eulerAngles;
            float targetPitch = euler.x;
            float targetYaw = euler.y;
            float targetDistance = _currentThirdPersonDistance * scale;

            if (!_wasThirdPerson)
            {
                // Instant snap
                _currentCamParams = new CameraParams
                {
                    trackingPosition = targetTrackingPos,
                    framing = ThirdPersonFraming,
                    distance = targetDistance,
                    pitch = targetPitch,
                    yaw = targetYaw
                };
                _wasThirdPerson = true;
            }
            else
            {
                // Smoothly damp parameters toward targets
                float positionDecay = Mathf.Exp(-TrackingSmoothness * DeltaTime);
                _currentCamParams.trackingPosition = Vector3.LerpUnclamped(targetTrackingPos, _currentCamParams.trackingPosition, positionDecay);
                _currentCamParams.distance = Mathf.LerpUnclamped(targetDistance, _currentCamParams.distance, positionDecay);
                _currentCamParams.framing = Vector2.LerpUnclamped(ThirdPersonFraming, _currentCamParams.framing, positionDecay);

                float rotationDecay = Mathf.Exp(-RotationSmoothness * DeltaTime);
                _currentCamParams.pitch = Mathf.LerpAngle(_currentCamParams.pitch, targetPitch, 1.0f - rotationDecay);
                _currentCamParams.yaw = Mathf.LerpAngle(_currentCamParams.yaw, targetYaw, 1.0f - rotationDecay);
            }

            Quaternion desiredRotation = Quaternion.Euler(_currentCamParams.pitch, _currentCamParams.yaw, 0);

            float tanFOVY = Mathf.Tan(0.5f * Mathf.Deg2Rad * CameraInstance.fieldOfView);
            float tanFOVX = tanFOVY * CameraInstance.aspect;

            Vector3 localOffset = new Vector3(
                _currentCamParams.distance * tanFOVX * _currentCamParams.framing.x,
                _currentCamParams.distance * tanFOVY * _currentCamParams.framing.y,
                _currentCamParams.distance
            );

            Vector3 desiredWorldPos = _currentCamParams.trackingPosition - (desiredRotation * localOffset);
            Vector3 direction = desiredWorldPos - _currentCamParams.trackingPosition;
            float maxDistance = direction.magnitude;
            float scaledRadius = CameraCollisionRadius * scale;

            if (maxDistance > 0.001f && Physics.SphereCast(_currentCamParams.trackingPosition, scaledRadius, direction.normalized, out RaycastHit hit, maxDistance, CameraCollisionMask, QueryTriggerInteraction.Ignore))
            {
                desiredWorldPos = hit.point + (hit.normal * scaledRadius);
            }

            SelfTransform.SetPose(desiredWorldPos, desiredRotation);
        }
        private void OnClipOverrideToggleChanged(bool _) => UpdateCameraScale();
        private void OnClipBindingChangedFloat(float _) => UpdateCameraScale();

        private void OnThirdPersonEnabledChanged(bool enabled)
        {
            if (!enabled)
            {
                IsThirdPerson = false;
            }
        }

        private void OnGlobalThirdPersonDisabledChanged(bool disabled)
        {
            // Mirror the server-pushed flag onto the local lockout. SetAdminThirdPersonLocked
            // also yanks any active third-person camera back to first-person on the spot.
            SetAdminThirdPersonLocked(disabled);
        }

        /// <summary>
        /// URP callback after camera render: restores head scale to normal for this camera.
        /// </summary>
        private void EndCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
        {
            if (ReferenceEquals(renderingCamera, CameraInstance) && BasisLocalAvatarDriver.Mapping.Hashead)
            {
                BasisLocalAvatarDriver.ScaleHeadToNormal();
            }
        }
        /// <summary>
        /// URP callback before camera render: sets the local head's visibility for the main
        /// camera — hidden in first-person, shown in third-person.
        /// </summary>
        public void BeginCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
        {
            if (ReferenceEquals(renderingCamera, CameraInstance) && BasisLocalAvatarDriver.Mapping.Hashead)
            {
                // Be authoritative for the main camera's own head state. A mirror/handheld camera
                // renders in onBeforeRender and leaves the head zeroed (it assumes first-person),
                // so third-person must restore it here or the main view shows a headless avatar.
                if (IsThirdPerson)
                {
                    BasisLocalAvatarDriver.ScaleHeadToNormal();
                }
                else
                {
                    BasisLocalAvatarDriver.ScaleHeadToZero();
                }
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
