using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.OpenXR
{
    /// <summary>
    /// Bridges the <see cref="BasisSettingsDefaults.EnablePassthrough"/> setting to
    /// <see cref="BasisPassthroughFeature"/> and the local camera. While active it clears the main
    /// camera to transparent and removes the skybox (both the camera component and RenderSettings)
    /// so the real world shows through; it saves and restores the world's values on toggle and
    /// re-captures them on each scene load. Only takes effect on standalone VR hardware whose
    /// runtime supports passthrough.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisPassthroughController
    {
        const string Tag = "[BasisPassthrough]";

        static bool s_Initialized;
        static bool s_DesiredActive;

        static bool s_Saved;
        static CameraClearFlags s_SavedFlags;
        static Color s_SavedBackground;
        static Material s_SavedCameraSky;
        static Material s_SavedRenderSky;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Initialize()
        {
            if (s_Initialized)
            {
                return;
            }
            s_Initialized = true;
            BasisSettingsDefaults.EnablePassthrough.OnChanged += OnSettingChanged;
            BasisLocalCameraDriver.RenderSettingsApplied += OnRenderSettingsApplied;
            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
            BasisDeviceManagement.OnDeviceManagementLoop += Tick;
            Apply();
        }

        static void Tick()
        {
            BasisPassthroughFeature.MainThreadTick();
        }

        static void OnSettingChanged(bool value)
        {
            BasisDebug.Log($"{Tag} Setting changed -> {value}", BasisDebug.LogTag.Device);
            Apply();
        }

        static void OnBootModeChanged(string mode)
        {
            BasisDebug.Log($"{Tag} Boot mode changed -> {mode}", BasisDebug.LogTag.Device);
            Apply();
        }

        static void OnRenderSettingsApplied()
        {
            if (s_DesiredActive)
            {
                BasisDebug.Log($"{Tag} Scene loaded while active — recapturing world visuals and re-applying passthrough clear.", BasisDebug.LogTag.Device);
                s_Saved = false;
                ApplyVisuals(true);
            }
        }

        public static void NotifyRuntimeReady()
        {
            BasisDebug.Log($"{Tag} Runtime ready (IsSupported={BasisPassthroughFeature.IsSupported}).", BasisDebug.LogTag.Device);
            Apply();
        }

        public static void NotifyRuntimeLost()
        {
            BasisDebug.Log($"{Tag} Runtime lost — restoring world visuals.", BasisDebug.LogTag.Device);
            s_DesiredActive = false;
            ApplyVisuals(false);
        }

        static void Apply()
        {
            bool setting = BasisSettingsDefaults.EnablePassthrough.RawValue;
            bool mobile = BasisDeviceManagement.IsMobileHardware();
            bool vr = BasisDeviceManagement.IsCurrentModeVR();
            bool supported = BasisPassthroughFeature.IsSupported;
            bool want = setting && mobile && vr && supported;

            BasisDebug.Log($"{Tag} Apply: setting={setting} mobile={mobile} vr={vr} supported={supported} -> want={want}", BasisDebug.LogTag.Device);

            s_DesiredActive = want;
            BasisPassthroughFeature.SetActive(want);
            ApplyVisuals(want);
        }

        static void ApplyVisuals(bool on)
        {
            if (!BasisLocalCameraDriver.HasInstance)
            {
                BasisDebug.Log($"{Tag} ApplyVisuals({on}) skipped — camera not ready yet (will re-apply on scene load).", BasisDebug.LogTag.Device);
                return;
            }
            Camera cam = BasisLocalCameraDriver.Instance.Camera;
            if (cam == null)
            {
                return;
            }
            cam.TryGetComponent(out Skybox camSky);

            if (on)
            {
                if (!s_Saved)
                {
                    s_SavedFlags = cam.clearFlags;
                    s_SavedBackground = cam.backgroundColor;
                    s_SavedCameraSky = camSky != null ? camSky.material : null;
                    s_SavedRenderSky = RenderSettings.skybox;
                    s_Saved = true;
                }
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                if (camSky != null)
                {
                    camSky.material = null;
                }
                RenderSettings.skybox = null;
                BasisDebug.Log($"{Tag} Visuals ON — clear=SolidColor(a=0), skybox removed.", BasisDebug.LogTag.Device);
            }
            else if (s_Saved)
            {
                cam.clearFlags = s_SavedFlags;
                cam.backgroundColor = s_SavedBackground;
                if (camSky != null)
                {
                    camSky.material = s_SavedCameraSky;
                }
                RenderSettings.skybox = s_SavedRenderSky;
                s_Saved = false;
                BasisDebug.Log($"{Tag} Visuals OFF — restored clear/skybox.", BasisDebug.LogTag.Device);
            }
        }
    }
}
