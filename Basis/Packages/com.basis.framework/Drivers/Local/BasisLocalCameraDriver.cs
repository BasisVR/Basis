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

        /// <summary>Near clip plane override.</summary>
        public float NearClip = 0.001f;

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
            Camera.nearClipPlane = NearClip;
            Camera.farClipPlane = 1500;
            CameraInstanceID = Camera.GetInstanceID();

            // Set initial scale from player height
            OnHeightChanged();

            if (HasEvents == false)
            {
                BasisLocalMicrophoneDriver.OnPausedAction += microphoneIconDriver.OnPausedEvent;
                BasisLocalMicrophoneDriver.MainThreadOnHasAudio += microphoneIconDriver.MicrophoneTransmitting;
                BasisLocalMicrophoneDriver.MainThreadOnHasSilence += microphoneIconDriver.MicrophoneNotTransmitting;

                RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
                RenderPipelineManager.endCameraRendering += EndCameraRendering;

                BasisDeviceManagement.OnBootModeChanged += OnModeSwitch;
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnHeightChanged;

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
            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= EndCameraRendering;
            BasisDeviceManagement.OnBootModeChanged -= OnModeSwitch;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnHeightChanged;
            BasisLocalMicrophoneDriver.OnPausedAction -= microphoneIconDriver.OnPausedEvent;
            HasEvents = false;
            HasInstance = false;
        }

        /// <summary>
        /// Unity disable hook: restores head scale, detaches render and mic events, and clears flags.
        /// </summary>
        public void OnDisable()
        {
            if (BasisLocalAvatarDriver.References != null && BasisLocalAvatarDriver.References.head != null)
            {
                BasisLocalAvatarDriver.References.head.localScale = BasisLocalAvatarDriver.HeadScale;
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
            OnHeightChanged();
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

        /// <summary>
        /// Applies scale from the player's height so the camera’s local scale matches avatar scale.
        /// </summary>
        public void OnHeightChanged()
        {
            // the normal users scale is 1.6m; scale camera with selected avatar scale
            this.transform.localScale = Vector3.one * LocalPlayer.CurrentHeight.SelectedAvatarToAvatarDefaultScale;
        }

        /// <summary>
        /// URP callback after camera render: restores head scale to normal for this camera.
        /// </summary>
        private void EndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (BasisLocalAvatarDriver.References.Hashead)
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
            if (BasisLocalAvatarDriver.References.Hashead)
            {
                if (Camera.GetInstanceID() == CameraInstanceID)
                {
                    this.transform.GetPositionAndRotation(out Position, out Rotation);
                    BasisLocalAvatarDriver.ScaleheadToZero();

                    if (CameraData.allowXRRendering)
                    {
                        ParentOfUI.localPosition = microphoneIconDriver.CalculateClampedLocal(Camera, Position);
                    }
                    else
                    {
                        Vector3 worldPoint = Camera.ViewportToWorldPoint(DesktopMicrophoneViewportPosition);
                        // assume this transform is the camera parent
                        Vector3 localPos = this.transform.InverseTransformPoint(worldPoint);
                        ParentOfUI.localPosition = localPos;
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
