using Unity.Scripting.LifecycleManagement;
using System.Linq;
using UnityEditor;
using UnityEngine.Rendering;

/// <summary>
/// The graphics APIs the Basis web player ships. Web content has to be compiled for the same list:
/// a material whose shaders only exist for WebGL2 renders magenta in a WebGPU player.
/// </summary>
[NoAutoStaticsCleanup]
public static class BasisWebGraphicsAPIs
{
    private static readonly GraphicsDeviceType[] Shipped = { GraphicsDeviceType.WebGPU };

    public static void Apply()
    {
        GraphicsDeviceType[] current = PlayerSettings.GetGraphicsAPIs(BuildTarget.WebGL);
        if (!PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.WebGL) && current != null && current.SequenceEqual(Shipped)) return;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, Shipped);
        BasisDebug.Log($"Web graphics APIs set to {string.Join(", ", Shipped)} to match the Basis web player.", BasisDebug.LogTag.Editor);
    }
}
