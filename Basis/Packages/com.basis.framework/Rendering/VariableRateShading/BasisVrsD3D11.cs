using System;
using System.Runtime.InteropServices;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Scripts.Rendering
{
    /// <summary>
    /// Direct3D11 variable rate shading through the BasisVrsD3D11 native plugin (NVAPI, NVIDIA
    /// Turing and newer). D3D11 has no shading rate image API in Unity, so the plugin wraps the
    /// same rate image the build compute writes and switches it on around each foveated pass.
    /// </summary>
    [NoAutoStaticsCleanup]
    public static class BasisVrsD3D11
    {
        public enum PluginStatus { NotLoaded = 0, Supported = 1, NotD3D11 = 2, NoNvapi = 3, NvapiInitFailed = 4, NoVrs = 5, NotNvidia = 6, MissingPlugin = -1 }

        public const int TileSize = 16;
        public const int EventRelease = 3;

        private const string Plugin = "BasisVrsD3D11";
        [DllImport(Plugin)] private static extern int BasisVrsD3D11_Status();
        [DllImport(Plugin)] private static extern int BasisVrsD3D11_LastError();
        [DllImport(Plugin)] private static extern int BasisVrsD3D11_EnableCount();
        [DllImport(Plugin)] private static extern int BasisVrsD3D11_UncoveredCount();
        [DllImport(Plugin)] private static extern IntPtr BasisVrsD3D11_GetRenderEventFunc();

        private static bool sQueried;
        private static PluginStatus sStatus;
        private static IntPtr sRenderEvent;

        public static PluginStatus Status { get { Query(); return sStatus; } }
        public static bool Supported => Status == PluginStatus.Supported;
        public static IntPtr RenderEvent { get { Query(); return sRenderEvent; } }
        public static int LastError { get { try { return BasisVrsD3D11_LastError(); } catch (Exception) { return 0; } } }
        public static int EnableCount { get { try { return BasisVrsD3D11_EnableCount(); } catch (Exception) { return 0; } } }
        // Passes where the plugin left VRS off because the shading rate image did not cover the bound target.
        public static int UncoveredCount { get { try { return BasisVrsD3D11_UncoveredCount(); } catch (Exception) { return 0; } } }

        private static void Query()
        {
            if (sQueried && sStatus != PluginStatus.NotLoaded) return;
            sQueried = true;
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 || Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor)
            {
                sStatus = PluginStatus.NotD3D11;
                return;
            }
            try
            {
                sStatus = (PluginStatus)BasisVrsD3D11_Status();
                if (sStatus == PluginStatus.Supported) sRenderEvent = BasisVrsD3D11_GetRenderEventFunc();
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                sStatus = PluginStatus.MissingPlugin;
            }
            if (sStatus != PluginStatus.Supported) sRenderEvent = IntPtr.Zero;
        }
    }
}
