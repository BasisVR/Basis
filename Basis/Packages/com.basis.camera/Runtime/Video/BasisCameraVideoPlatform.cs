#if BASIS_HAS_SPOUT && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR))
#define BASIS_VIDEO_OUTPUT_SPOUT
#elif BASIS_HAS_SYPHON && (UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR))
#define BASIS_VIDEO_OUTPUT_SYPHON
#elif UNITY_EDITOR_LINUX || (UNITY_STANDALONE_LINUX && !UNITY_EDITOR)
#define BASIS_VIDEO_OUTPUT_V4L2
#endif
using Basis;
using System.Collections.Generic;
using UnityEngine;
public static class BasisCameraVideoPlatform
{
    private static readonly HashSet<string> ClaimedSenderNames = new HashSet<string>();
    private static readonly HashSet<int> ClaimedWebPorts = new HashSet<int>();
#if BASIS_VIDEO_OUTPUT_SPOUT
    public const string BackendName = "Spout";
    public const string Requirement = "Publishes as \"Basis Camera\". Needs the Spout2 plugin installed in OBS — stock OBS has no Spout source.";
    public static bool Supported => true;
    public static bool FlipsRows => false;
    public static RenderTextureFormat FrameFormat => RenderTextureFormat.ARGB32;
    public static IBasisVideoOutputSink CreateSink() => new BasisSpoutVideoOutputSink();
#elif BASIS_VIDEO_OUTPUT_SYPHON
    public const string BackendName = "Syphon";
    public const string Requirement = "Publishes as \"Basis Camera\". Needs a Syphon-capable receiver, such as OBS with the Syphon plugin.";
    public static bool Supported => true;
    public static bool FlipsRows => false;
    public static RenderTextureFormat FrameFormat => RenderTextureFormat.ARGB32;
    public static IBasisVideoOutputSink CreateSink() => new BasisSyphonVideoOutputSink();
#elif BASIS_VIDEO_OUTPUT_V4L2
    public const string BackendName = "Virtual Camera";
    public const string Requirement = "Appears as a webcam. Needs the loopback module loaded: sudo modprobe v4l2loopback exclusive_caps=1";
    public static bool Supported => true;
    public static bool FlipsRows => true;
    public static RenderTextureFormat FrameFormat => SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.BGRA32) ? RenderTextureFormat.BGRA32 : RenderTextureFormat.ARGB32;
    public static IBasisVideoOutputSink CreateSink() => new BasisV4L2VideoOutputSink();
#else
    public const string BackendName = "Video", Requirement = "Not supported on this platform.";
    public static bool Supported => false;
    public static bool FlipsRows => false;
    public static RenderTextureFormat FrameFormat => RenderTextureFormat.ARGB32;
    public static IBasisVideoOutputSink CreateSink() => null;
#endif
    public static List<BasisVideoTransport> Transports()
    {
        List<BasisVideoTransport> transports = new List<BasisVideoTransport>();
        if (Supported) transports.Add(BasisVideoTransport.Platform);
        transports.Add(BasisVideoTransport.Web);
        return transports;
    }
    public static string TransportName(BasisVideoTransport transport) => transport == BasisVideoTransport.Web ? "Web Stream (MJPEG)" : BackendName;
    public static string TransportRequirement(BasisVideoTransport transport) => transport == BasisVideoTransport.Web ? "Needs nothing installed — add the address to OBS as a Browser source, or open it in a browser." : Requirement;
    public static bool IsAvailable(BasisVideoTransport transport) => transport == BasisVideoTransport.Web || (transport == BasisVideoTransport.Platform && Supported);
    public static string ClaimSenderName(string requested)
    {
        string baseName = string.IsNullOrEmpty(requested) ? "Basis Camera" : requested, name = baseName;
        for (int Suffix = 2; ClaimedSenderNames.Contains(name); Suffix++) name = $"{baseName} {Suffix}";
        ClaimedSenderNames.Add(name);
        return name;
    }
    public static void ReleaseSenderName(string name)
    {
        if (!string.IsNullOrEmpty(name)) ClaimedSenderNames.Remove(name);
    }
    public static int FirstUnclaimedPort(int port)
    {
        while (ClaimedWebPorts.Contains(port) && port < 65500) port++;
        return port;
    }
    public static void ClaimPort(int port) => ClaimedWebPorts.Add(port);
    public static void ReleasePort(int port) => ClaimedWebPorts.Remove(port);
}
