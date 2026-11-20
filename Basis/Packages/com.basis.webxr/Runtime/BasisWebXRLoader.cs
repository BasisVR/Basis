using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine.XR;
using UnityEngine.XR.Management;

namespace Basis.Scripts.Device_Management.Devices.WebXR
{
    public class BasisWebXRLoader : XRLoaderHelper
    {
        public const string DisplayId = "Basis WebXR Display", InputId = "Basis WebXR Input";
        private static readonly List<XRDisplaySubsystemDescriptor> displays = new List<XRDisplaySubsystemDescriptor>();
        private static readonly List<XRInputSubsystemDescriptor> inputs = new List<XRInputSubsystemDescriptor>();
        public XRDisplaySubsystem Display => GetLoadedSubsystem<XRDisplaySubsystem>();
        public XRInputSubsystem Input => GetLoadedSubsystem<XRInputSubsystem>();
        public bool SubsystemsRunning { get; private set; }
        public override bool Initialize()
        {
            if (!BasisWebXRNative.IsSupported) return false;
            BasisWebXRNative.Register();
            CreateSubsystem<XRDisplaySubsystemDescriptor, XRDisplaySubsystem>(displays, DisplayId);
            CreateSubsystem<XRInputSubsystemDescriptor, XRInputSubsystem>(inputs, InputId);
            if (Display == null || Input == null)
            {
                BasisDebug.LogError($"WebXR: subsystems missing (display {Display != null}, input {Input != null})", BasisDebug.LogTag.Device);
                Deinitialize();
                return false;
            }
            XRSettings.useOcclusionMesh = false;
            BasisWebXRNative.Request();
            return true;
        }
        public override bool Start()
        {
            return true;
        }
        public void StartSubsystems()
        {
            if (SubsystemsRunning) return;
            StartSubsystem<XRDisplaySubsystem>();
            StartSubsystem<XRInputSubsystem>();
            Input?.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
            XRSettings.gameViewRenderMode = GameViewRenderMode.BothEyes;
            SubsystemsRunning = true;
        }
        public override bool Stop()
        {
            if (SubsystemsRunning)
            {
                StopSubsystem<XRDisplaySubsystem>();
                StopSubsystem<XRInputSubsystem>();
                SubsystemsRunning = false;
            }
            BasisWebXRNative.End();
            return true;
        }
        public override bool Deinitialize()
        {
            Stop();
            DestroySubsystem<XRDisplaySubsystem>();
            DestroySubsystem<XRInputSubsystem>();
            return base.Deinitialize();
        }
        public override List<GraphicsDeviceType> GetSupportedGraphicsDeviceTypes(bool buildingPlayer)
        {
            return new List<GraphicsDeviceType> { GraphicsDeviceType.WebGPU };
        }
    }
}
