using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Settings;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Management;

namespace Basis.Scripts.Device_Management.Devices.WebXR
{
    public class BasisWebXRManagement : BasisBaseTypeManagement
    {
        [NonSerialized] private readonly List<BasisInput> controls = new List<BasisInput>();
        [NonSerialized] private bool rendering, leaving;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Application.platform != RuntimePlatform.WebGLPlayer || FindLoader() == null) return;
            if (BasisDeviceManagement.OnInitializationComplete && BasisDeviceManagement.Instance != null) Attach(BasisDeviceManagement.Instance);
            else BasisDeviceManagement.OnInitializationCompleted += AttachWhenReady;
        }
        private static void AttachWhenReady()
        {
            BasisDeviceManagement.OnInitializationCompleted -= AttachWhenReady;
            if (BasisDeviceManagement.Instance != null) Attach(BasisDeviceManagement.Instance);
            else BasisDebug.LogError("WebXR: device management finished without an instance", BasisDebug.LogTag.Device);
        }
        private static void Attach(BasisDeviceManagement management)
        {
            BasisBaseTypeManagement[] types = management.BaseTypes ?? Array.Empty<BasisBaseTypeManagement>();
            for (int index = 0; index < types.Length; index++) if (types[index] is BasisWebXRManagement) return;
            BasisWebXRManagement webXR = management.gameObject.AddComponent<BasisWebXRManagement>();
            Array.Resize(ref types, types.Length + 1);
            types[^1] = webXR;
            management.BaseTypes = types;
        }
        private static BasisWebXRLoader FindLoader()
        {
            IReadOnlyList<XRLoader> loaders = XRGeneralSettings.Instance?.Manager?.activeLoaders;
            if (loaders == null) return null;
            for (int index = 0; index < loaders.Count; index++) if (loaders[index] is BasisWebXRLoader loader) return loader;
            return null;
        }
        public override bool IsDeviceBootable(string BootRequest)
        {
            return BootRequest == BasisConstants.WebXRLoader && BasisWebXRNative.IsSupported;
        }
        public override void StartSDK()
        {
            rendering = false;
            leaving = false;
            CreateHead();
            CreateHand("Left Hand WEBXR", BasisBoneTrackedRole.LeftHand);
            CreateHand("Right Hand WEBXR", BasisBoneTrackedRole.RightHand);
            BasisCursorManagement.UnlockCursorBypassChecks("Forceful Unlock WEBXR");
            BasisDebug.Log("WebXR: SDK started, waiting for the browser session", BasisDebug.LogTag.Device);
        }
        public override void StopSDK()
        {
            for (int index = 0; index < controls.Count; index++)
            {
                if (controls[index] != null) BasisDeviceManagement.Instance.RemoveDevicesFrom(nameof(BasisWebXRManagement), controls[index].UniqueDeviceIdentifier);
            }
            controls.Clear();
            if (rendering) BasisLocalCameraDriver.AllowXRRenderering(false);
            rendering = false;
        }
        public override void Simulate()
        {
            if (!IsDeviceBooted || leaving) return;
            int state = BasisWebXRNative.State;
            if (state == BasisWebXRNative.StateRunning)
            {
                if (rendering) return;
                BasisWebXRLoader loader = XRGeneralSettings.Instance?.Manager?.activeLoader as BasisWebXRLoader;
                if (loader == null) return;
                loader.StartSubsystems();
                BasisLocalCameraDriver.AllowXRRenderering(true);
                rendering = true;
                BasisSettingsSystem.ReapplyAllSettings();
                BasisDebug.Log("WebXR: session running", BasisDebug.LogTag.Device);
                return;
            }
            if (state == BasisWebXRNative.StateFailed)
            {
                Leave(FailureReason(BasisWebXRNative.Error));
            }
            else if (state != BasisWebXRNative.StateRequesting)
            {
                Leave(null);
            }
        }
        private async void Leave(string failure)
        {
            leaving = true;
            BasisDebug.Log(failure == null ? "WebXR: session ended, returning to Desktop" : $"WebXR: session failed ({failure})", BasisDebug.LogTag.Device);
            await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.Desktop);
            if (failure != null) BasisXRRuntimeNotice.ReportFallback(BasisConstants.WebXRLoader, BasisConstants.Desktop, failure);
        }
        private static string FailureReason(int error)
        {
            switch (error)
            {
                case BasisWebXRNative.ErrorDenied: return BasisLocalization.Get("settings.platform.vrFailed.webxrDenied");
                case BasisWebXRNative.ErrorDevice: return BasisLocalization.Get("settings.platform.vrFailed.webxrDevice");
                default: return BasisLocalization.Get("settings.platform.vrFailed.webxrUnsupported");
            }
        }
        private void CreateHead()
        {
            GameObject gameObject = new GameObject("Head WEBXR") { transform = { parent = BasisLocalPlayer.Instance.transform } };
            BasisOpenXRHeadInput input = gameObject.AddComponent<BasisOpenXRHeadInput>();
            input.ClassName = nameof(BasisOpenXRHeadInput);
            input.Initialize("Head WEBXR", "Head WEBXR", nameof(BasisWebXRManagement), true);
            BasisDeviceManagement.Instance.TryAdd(input);
            controls.Add(input);
        }
        private void CreateHand(string id, BasisBoneTrackedRole role)
        {
            GameObject gameObject = new GameObject(id) { transform = { parent = BasisLocalPlayer.Instance.transform } };
            BasisOpenXRHandInput input = gameObject.AddComponent<BasisOpenXRHandInput>();
            input.ClassName = nameof(BasisOpenXRHandInput);
            input.Initialize(id, id, nameof(BasisWebXRManagement), true, role);
            BasisDeviceManagement.Instance.TryAdd(input);
            controls.Add(input);
        }
    }
}
