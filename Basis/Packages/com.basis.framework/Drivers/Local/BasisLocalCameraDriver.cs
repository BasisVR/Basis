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

        /// <summary>Driver for microphone icon visuals and layout near the camera.</summary>
        [SerializeField]
        public BasisLocalMicrophoneIconDriver microphoneIconDriver = new BasisLocalMicrophoneIconDriver();

        /// <summary>
        /// World forward vector of the active camera instance, or zero if no instance exists.
        /// </summary>
        public static Vector3 Forward()
        {
            if (HasInstance)
            {
                return Instance.transform.forward;
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
                return Instance.transform.up;
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
                return Instance.transform.right;
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
            CameraInstanceID = Camera.GetInstanceID();

            // Set initial scale from player height and set the clip planes.
            UpdateCameraScale();

            if (HasEvents == false)
            {
                BasisLocalMicrophoneDriver.OnPausedAction += microphoneIconDriver.OnPausedEvent;
                BasisLocalMicrophoneDriver.MainThreadOnHasAudio += microphoneIconDriver.MicrophoneTransmitting;
                BasisLocalMicrophoneDriver.MainThreadOnHasSilence += microphoneIconDriver.MicrophoneNotTransmitting;

                RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
                RenderPipelineManager.endCameraRendering += EndCameraRendering;

                BasisDeviceManagement.OnBootModeChanged += OnModeSwitch;
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame += UpdateCameraScale;
                BasisLocalPlayer.OnLocalAvatarChanged += UpdateCameraScale;

                InstanceExists?.Invoke();
                HasEvents = true;
            }

            microphoneIconDriver.Initalize(this);
            microphoneIconDriver.UpdateMicrophoneVisuals(BasisLocalMicrophoneDriver.isPaused, false);

#if STEAMAUDIO_ENABLED
            if (SteamAudioListener != null)
            {
                SteamAudioManager.NotifyAudioListenerChanged();
            }
#endif
            microphoneIconDriver.SpriteRendererIcon.gameObject.SetActive(true);

            // Cache icon half-size in camera-local RU for layout
            microphoneIconDriver.iconHalfRU = microphoneIconDriver.GetIconHalfSizeRUInCameraSpace(Camera, ParentOfUI);
        }

        /// <summary>
        /// Unity destroy hook: unregisters pipeline/device/microphone events and clears flags.
        /// </summary>
        public void OnDestroy()
        {
            CameraInstance = null;
            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= EndCameraRendering;
            BasisDeviceManagement.OnBootModeChanged -= OnModeSwitch;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= UpdateCameraScale;
            BasisLocalPlayer.OnLocalAvatarChanged -= UpdateCameraScale;
            BasisLocalMicrophoneDriver.OnPausedAction -= microphoneIconDriver.OnPausedEvent;
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
                BasisLocalMicrophoneDriver.MainThreadOnHasAudio -= microphoneIconDriver.MicrophoneTransmitting;
                BasisLocalMicrophoneDriver.MainThreadOnHasSilence -= microphoneIconDriver.MicrophoneNotTransmitting;
                HasEvents = false;
            }
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
        /// URP callback after camera render: restores head scale to normal for this camera.
        /// </summary>
        private void EndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (BasisLocalAvatarDriver.Mapping.Hashead)
            {
                if (Camera.GetInstanceID() == CameraInstanceID)
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
                if (Camera.GetInstanceID() == CameraInstanceID)
                {
                    BasisLocalAvatarDriver.ScaleheadToZero();
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
                    ParentOfUI.localPosition = microphoneIconDriver.CalculateClampedLocal(Camera, Position);
                }
                else
                {
                    if (BasisDeviceManagement.IsMobileHardware())
                    {
                        Vector3 worldPoint = Camera.ViewportToWorldPoint(MobileMicrophoneViewportPosition);
                        // assume this transform is the camera parent
                        Vector3 localPos = this.transform.InverseTransformPoint(worldPoint);
                        ParentOfUI.localPosition = localPos * BasisHeightDriver.PlayerToDefaultRatioScaledWithAvatarScale;
                    }
                    else
                    {
                        Vector3 worldPoint = Camera.ViewportToWorldPoint(DesktopMicrophoneViewportPosition);
                        // assume this transform is the camera parent
                        Vector3 localPos = this.transform.InverseTransformPoint(worldPoint);
                        ParentOfUI.localPosition = localPos * BasisHeightDriver.PlayerToDefaultRatioScaledWithAvatarScale;
                    }
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
