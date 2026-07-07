using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Basis.Setup.Modules
{
    public sealed class BasisLinkXmlSetup : BasisSetupModuleBase
    {
        private const string TargetPath = "Assets/Basis/link.xml";

        public override string Key => "linker.linkxml";
        public override string DisplayName => "IL2CPP link.xml";
        public override string Category => "Linker";
        public override int Version => 1;

        private static readonly string[] Preserve =
        {
            "AudioLink", "Base128", "Basis Framework", "Basis.Addon.SnapControls",
            "Basis.Developer.Exceptions", "Basis.Developer.Recorder", "Basis.ImagePickup",
            "Basis.Integration.AudioLink", "Basis.OpenLipSync.Runtime", "Basis.Shims.Samples",
            "BasisBundleManagement", "BasisCommon", "BasisConsoleDisplay", "BasisDebug",
            "BasisEventDriver", "BasisExamples", "BasisGizmos", "BasisMediaPipe",
            "BasisMediaPipe.Homuler", "BasisMediaPlayer", "BasisNetworkClient", "BasisNetworkCore",
            "BasisNetworkServer", "BasisOpenVR", "BasisOpenXR", "BasisPoolTable", "BasisProfiler",
            "BasisSDK", "BasisSDKEditor", "BasisSeatExamples", "BasisSerializer.OdinSerializer",
            "BasisServersProvider", "BasisSettings", "BasisShims", "BasisVehicles",
            "BasisVehiclesNetwork", "BouncyCastle.Cryptography", "Cilbox", "Crypto", "Did",
            "GPUUtilities", "Generator.Equals", "Generator.Equals.Runtime", "Google.Protobuf",
            "HVR.Basis.Comms", "HVR.Basis.Comms.HVRUtility", "HVR.LicenseReview",
            "K4os.Compression.LZ4", "LiteNetLib", "MeaMod.DNS", "Mediapipe.Runtime",
            "Microsoft.ML.OnnxRuntime", "Newtonsoft.Json", "OpusSharp", "RNNoiseIntergration",
            "RenderAs2DModule", "SimpleBase", "SteamAudioUnity", "SteamVR", "SteamVR_Actions",
            "System.Buffers", "System.Memory", "System.Runtime.CompilerServices.Unsafe",
            "TrueAudioNext", "UdonSharp.Lib", "UdonSharp.Runtime", "Unity.Addressables",
            "Unity.Animation.Rigging", "Unity.Animation.Rigging.DocCodeExamples", "Unity.Burst",
            "Unity.Burst.Cecil", "Unity.Collections", "Unity.Collections.LowLevel.ILSupport",
            "Unity.InputSystem", "Unity.InputSystem.ForUI", "Unity.InternalAPIEngineBridge.004",
            "Unity.InternalAPIEngineBridge.RenderPipelines.Core.Runtime.Shared", "Unity.Mathematics",
            "Unity.MemoryProfiler", "Unity.PathTracing.Runtime", "Unity.Profiling.Core",
            "Unity.RenderPipeline.Universal.ShaderLibrary", "Unity.RenderPipelines.Core.Runtime",
            "Unity.RenderPipelines.Core.Runtime.Shared", "Unity.RenderPipelines.Core.ShaderLibrary",
            "Unity.RenderPipelines.GPUDriven.Runtime",
            "Unity.RenderPipelines.ShaderGraph.ShaderGraphLibrary",
            "Unity.RenderPipelines.Universal.2D.Runtime",
            "Unity.RenderPipelines.Universal.Config.Runtime",
            "Unity.RenderPipelines.Universal.Runtime", "Unity.RenderPipelines.Universal.Shaders",
            "Unity.ResourceManager", "Unity.ScriptableBuildPipeline", "Unity.Services.Analytics",
            "Unity.Services.CloudDiagnostics", "Unity.Services.Core", "Unity.Services.Core.Analytics",
            "Unity.Services.Core.Components", "Unity.Services.Core.Configuration",
            "Unity.Services.Core.Device", "Unity.Services.Core.Environments",
            "Unity.Services.Core.Environments.Internal", "Unity.Services.Core.Internal",
            "Unity.Services.Core.Networking", "Unity.Services.Core.Registration",
            "Unity.Services.Core.Scheduler", "Unity.Services.Core.Telemetry",
            "Unity.Services.Core.Threading", "Unity.SurfaceCache.Runtime", "Unity.TextMeshPro",
            "Unity.Timeline", "Unity.UnifiedRayTracing.Runtime", "Unity.VisualEffectGraph.Runtime",
            "Unity.XR.CoreUtils", "Unity.XR.Hands", "Unity.XR.Management", "Unity.XR.OpenVR",
            "Unity.XR.OpenXR", "Unity.XR.OpenXR.Features.ConformanceAutomation",
            "Unity.XR.OpenXR.Features.MetaQuestSupport", "Unity.XR.OpenXR.Features.MockRuntime",
            "Unity.XR.OpenXR.Features.OculusQuestSupport", "Unity.XR.OpenXR.Features.RuntimeDebugger",
            "Unity.XR.OpenXR.TestTooling", "UnityEngine.CoreModule", "UnityEngine.SpatialTracking",
            "UnityEngine.UI", "UnityEngine.XR.LegacyInputHelpers", "VRC.Udon",
            "Valve.Newtonsoft.Json", "Valve.OpenXR.Utils", "XRSDKOpenVR", "YtDlp.Runtime",
            "audioplugin_phonon", "basis_media_native", "com.cqf.urpvolumetricfog.runtime",
            "com.gator-dragon-games.jigglephysics", "openvr_api", "opus", "phonon", "phonon_fmod",
            "ucrtbased"
        };

        protected override bool Exists()
        {
            return File.Exists(BasisSetupIO.ToFullPath(TargetPath));
        }

        protected override bool Build(BasisSetupMode mode)
        {
            var assemblies = new SortedSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Preserve.Length; i++)
            {
                assemblies.Add(Preserve[i]);
            }

            string full = BasisSetupIO.ToFullPath(TargetPath);
            if (File.Exists(full))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(full), "fullname=\"([^\"]+)\""))
                {
                    assemblies.Add(match.Groups[1].Value);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("<linker>");
            sb.AppendLine();
            foreach (string assembly in assemblies)
            {
                sb.AppendLine($"    <assembly fullname=\"{assembly}\" preserve=\"all\" />");
            }

            sb.AppendLine();
            sb.AppendLine("</linker>");

            return BasisSetupIO.WriteTextAsset(TargetPath, sb.ToString(), mode);
        }
    }
}
