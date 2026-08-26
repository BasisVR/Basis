using System;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Networking;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Scripts.Rendering
{
    public static class BasisGraphicsApiSelection
    {
        public const string MarkerArgument = "--graphics-api=";

#if UNITY_STANDALONE_WIN
        // Mirrors m_BuildTargetGraphicsAPIs in ProjectSettings.asset. A player can only start on an
        // API its build compiled shaders for, and nothing reads that list back at runtime.
        private static readonly GraphicsDeviceType[] WindowsApis =
        {
            GraphicsDeviceType.Direct3D11,
            GraphicsDeviceType.Direct3D12
        };
#endif

        private static List<GraphicsDeviceType> _candidates;

        public static GraphicsDeviceType Current => SystemInfo.graphicsDeviceType;

        public static string CurrentId => Current.ToString();

        public static string CurrentDisplayName => DisplayName(Current);

        public static IReadOnlyList<GraphicsDeviceType> Candidates
        {
            get
            {
                if (_candidates != null) return _candidates;

                _candidates = new List<GraphicsDeviceType>();
                GraphicsDeviceType[] shipped = ShippedApis();
                for (int Index = 0; Index < shipped.Length; Index++)
                {
                    if (ForceArgument(shipped[Index]) != null) _candidates.Add(shipped[Index]);
                }

                // The list is only honest while the running API is on it; anything else means the
                // build no longer matches this table, and offering a swap would be a guess.
                if (!_candidates.Contains(Current)) _candidates.Clear();

                return _candidates;
            }
        }

        // Offered wherever the build has more than one API to offer; only switchable in a player,
        // since the editor holds the graphics device it started on for the whole session.
        public static bool IsOffered => Candidates.Count > 1;

        public static bool IsSupported =>
            IsOffered && !Application.isEditor && BasisAppRelaunch.IsSupported;

        public static string SelectedId =>
            TryParse(BasisSettingsDefaults.GraphicsApi.RawValue, out GraphicsDeviceType api) ? api.ToString() : CurrentId;

        public static string SelectedDisplayName =>
            TryParse(BasisSettingsDefaults.GraphicsApi.RawValue, out GraphicsDeviceType api) ? DisplayName(api) : CurrentDisplayName;

        public static bool NeedsRestart =>
            TryParse(BasisSettingsDefaults.GraphicsApi.RawValue, out GraphicsDeviceType api) && api != Current;

        public static void BuildDropdownEntries(List<string> ids, List<string> labels)
        {
            ids.Clear();
            labels.Clear();

            IReadOnlyList<GraphicsDeviceType> candidates = Candidates;
            for (int Index = 0; Index < candidates.Count; Index++)
            {
                GraphicsDeviceType api = candidates[Index];
                ids.Add(api.ToString());
                labels.Add(api == Current
                    ? BasisLocalization.Get("settings.graphics.renderer.api.running", DisplayName(api))
                    : DisplayName(api));
            }
        }

        // Page reset lands on the API this session is actually running, not on the platform
        // default: a reset should stop overriding the renderer, not queue a relaunch into another.
        public static void ResetToRunning()
        {
            if (!IsSupported)
            {
                BasisSettingsDefaults.GraphicsApi.ResetToDefault();
                return;
            }

            BasisSettingsDefaults.GraphicsApi.SetValue(CurrentId);
        }

        // The marker rides along with the force flag so the next boot can tell "the user asked for
        // this" from "the force already ran", which is what stops a failed switch relaunching forever.
        public static bool TryGetRelaunchArguments(out string forceArgument, out string markerArgument)
        {
            forceArgument = null;
            markerArgument = null;

            if (!IsSupported) return false;
            if (!TryParse(BasisSettingsDefaults.GraphicsApi.RawValue, out GraphicsDeviceType api)) return false;

            forceArgument = ForceArgument(api);
            if (forceArgument == null) return false;

            markerArgument = MarkerArgument + api;
            return true;
        }

        // Unity only takes a graphics API from the command line, so a saved choice can only be made
        // real by relaunching into it. Runs straight after BasisSettingsDefaults.LoadAll, before
        // anything has loaded that the relaunch would throw away.
        public static void ApplyStartupSetting()
        {
            if (!IsSupported) return;
            if (!TryParse(BasisSettingsDefaults.GraphicsApi.RawValue, out GraphicsDeviceType api)) return;
            if (api == Current) return;

            if (HasMarkerArgument())
            {
                // The force ran and the player still came up on something else — the API is not
                // available on this machine. Take what booted so the next launch stops trying.
                BasisDebug.LogWarning(
                    $"Asked for {DisplayName(api)} but the player started on {CurrentDisplayName}; staying on {CurrentDisplayName}.",
                    BasisDebug.LogTag.System);
                BasisSettingsDefaults.GraphicsApi.SetValue(CurrentId);
                return;
            }

            // A graphics flag typed on the command line outranks the saved setting, and becomes it:
            // launching with -force-d3d11 is then the way back from an API this machine cannot
            // start on, which would otherwise be a setting only the failing player can change.
            if (HasForcedApiArgument())
            {
                BasisDebug.Log($"Command line asked for {CurrentDisplayName}; keeping it as the saved renderer.");
                BasisSettingsDefaults.GraphicsApi.SetValue(CurrentId);
                return;
            }

            BasisDebug.Log($"Restarting to switch the renderer to {DisplayName(api)}.", BasisDebug.LogTag.System);
            BasisAppRelaunch.RebootAndReconnect();
        }

        public static string DisplayName(GraphicsDeviceType api)
        {
            switch (api)
            {
                case GraphicsDeviceType.Direct3D11: return "DirectX 11";
                case GraphicsDeviceType.Direct3D12: return "DirectX 12";
                case GraphicsDeviceType.Vulkan: return "Vulkan";
                case GraphicsDeviceType.OpenGLCore: return "OpenGL";
                case GraphicsDeviceType.Metal: return "Metal";
                default: return api.ToString();
            }
        }

        public static bool TryParse(string id, out GraphicsDeviceType api)
        {
            api = Current;
            if (string.IsNullOrEmpty(id)) return false;

            IReadOnlyList<GraphicsDeviceType> candidates = Candidates;
            for (int Index = 0; Index < candidates.Count; Index++)
            {
                if (string.Equals(candidates[Index].ToString(), id, StringComparison.OrdinalIgnoreCase))
                {
                    api = candidates[Index];
                    return true;
                }
            }

            return false;
        }

        private static string ForceArgument(GraphicsDeviceType api)
        {
            switch (api)
            {
                case GraphicsDeviceType.Direct3D11: return "-force-d3d11";
                case GraphicsDeviceType.Direct3D12: return "-force-d3d12";
                case GraphicsDeviceType.Vulkan: return "-force-vulkan";
                case GraphicsDeviceType.OpenGLCore: return "-force-glcore";
                default: return null;
            }
        }

        private static GraphicsDeviceType[] ShippedApis()
        {
#if UNITY_STANDALONE_WIN
            return WindowsApis;
#else
            return Array.Empty<GraphicsDeviceType>();
#endif
        }

        private static bool HasMarkerArgument()
        {
            string[] args = CommandLineArgs();
            for (int Index = 0; Index < args.Length; Index++)
            {
                if (args[Index] != null && args[Index].StartsWith(MarkerArgument, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static bool HasForcedApiArgument()
        {
            string[] args = CommandLineArgs();
            for (int Index = 0; Index < args.Length; Index++)
            {
                string arg = args[Index];
                if (string.IsNullOrEmpty(arg)) continue;

                if (arg.StartsWith("-force-d3d", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("-force-vulkan", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("-force-glcore", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("-force-opengl", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("-force-metal", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string[] CommandLineArgs()
        {
            try
            {
                return Environment.GetCommandLineArgs() ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
