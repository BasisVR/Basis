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
    public class BasisLocalCameraDriver : MonoBehaviour
    {
        public static bool HasInstance;
        public static BasisLocalCameraDriver Instance;
        public Camera Camera;
        public static int CameraInstanceID;
        public AudioListener Listener;
        public UniversalAdditionalCameraData CameraData;
        public SteamAudio.SteamAudioListener SteamAudioListener;
        public BasisLocalPlayer LocalPlayer;
        public int DefaultCameraFov = 90;
        public static event Action InstanceExists;
        public BasisLockToInput BasisLockToInput;
        public bool HasEvents = false;
        public Vector3 DesktopMicrophoneViewportPosition = new(0.2f, 0.15f, 1f); // Adjust as needed for canvas position and depth
        public float NearClip = 0.001f;
        public static Vector3 LeftEye;
        public static Vector3 RightEye;
        public static Vector3 Position;
        public static Quaternion Rotation;
        public Transform ParentOfUI;
        [SerializeField]
        public BasisLocalMicrophoneIconDriver microphoneIconDriver = new BasisLocalMicrophoneIconDriver();
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
            //fire static event that says the instance exists
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
            // 2) Icon half-size in meters, in camera-local axes
            microphoneIconDriver.iconHalfRU = microphoneIconDriver.GetIconHalfSizeRUInCameraSpace(Camera, ParentOfUI);
        }
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
        private void OnModeSwitch(string mode)
        {
            if (mode == BasisConstants.Desktop)
            {
                Camera.fieldOfView = DefaultCameraFov;
            }
            OnHeightChanged();
        }
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
        public void OnHeightChanged()
        {
            //the normal users scale is 1.6m
            //so a avatar the size of 
            this.transform.localScale = Vector3.one * LocalPlayer.CurrentHeight.SelectedAvatarToAvatarDefaultScale;
        }
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
                        Vector3 localPos = this.transform.InverseTransformPoint(worldPoint);//asume this transform is also camera position
                        ParentOfUI.localPosition = localPos;
                    }
                }
            }
        }
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
