using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct BasisExternalRayView
{
    public Matrix4x4 view, projection, inverseViewProjection, previousViewProjection;
    public Vector4 position;
}

[StructLayout(LayoutKind.Sequential)]
public struct BasisExternalRayFrame
{
    public ulong frame;
    public uint instanceCount, materialCount, lightCount, commandCount, uploadTexels, flags, width, height, viewCount, samples, bounces;
    public float maxDistance, aoRadius, emissionScale, skyIntensity, time, fireflyClamp, temporalResponse, lightIntensity, normalBias;
    public uint emitterCount;
    public float aoIntensity;
    public uint context, parity;
    public float cacheCellSize;
    public uint dirtyCount;
    public Vector4 ambient0, ambient1, ambient2, ambient3, ambient4, ambient5, ambient6;
    public BasisExternalRayView view0, view1;
}

[StructLayout(LayoutKind.Sequential)]
public struct BasisExternalRayInstance
{
    public Vector4 row0, row1, row2;
    public uint mesh, materialBase, mask, flags;
    public Vector4 lightmapST;
    public int lightmap;
    public uint previous, pad0, pad1;
}

[StructLayout(LayoutKind.Sequential)]
public struct BasisExternalRayMaterial
{
    public Vector4 baseColor, emission, baseST, emissionST;
    public int baseTexture, emissionTexture;
    public uint alphaMode;
    public float cutoff, metallic;
    public uint emission2;
    public uint flags;
    public float emissionBase;
}

[StructLayout(LayoutKind.Sequential)]
public struct BasisExternalRayLight
{
    public Vector4 position, direction, color, spot;
}

[StructLayout(LayoutKind.Sequential)]
public struct BasisExternalRayCommand
{
    public uint op, id, a, b, c, d, e, f;
}

[StructLayout(LayoutKind.Sequential)]
public struct BasisExternalRayEmitter
{
    public uint instance, geometry, triangleCount;
    public float cdf;
}

[NoAutoStaticsCleanup]
public static unsafe partial class BasisExternalRayTracer
{
    private const string Dll = "BasisRayTracingBridge2";
    public const int ProtocolVersion = 3;
    public const int MaxContexts = 6;
    public const int MaxInstances = 16384, MaxMaterials = 8192, MaxLights = 64, MaxCommands = 8192, MaxEmitters = 2048, MaxMeshes = 16384, MaxTextures = 4096, MaxDirty = 512;
    public const int UploadWidth = 2048, UploadHeight = 512, UploadTexels = UploadWidth * UploadHeight, Ring = 8;
    public const uint OpMeshDefine = 1, OpMeshSubmesh = 2, OpMeshVertices = 3, OpMeshIndices = 4, OpMeshCommit = 5, OpMeshRefit = 6, OpMeshRelease = 7;
    public const uint OpTextureDefine = 8, OpTexturePixels = 9, OpTextureCommit = 10, OpTextureRelease = 11, OpMeshPositions = 12;
    public const uint SubmeshOpaque = 1, SubmeshAlphaTest = 2, SubmeshBlend = 4, MeshDynamic = 1, TextureHdr = 1;
    public const uint AlphaOpaque = 0, AlphaCutout = 1, AlphaBlend = 2;
    public const uint MaterialDoubleSided = 1, MaterialUnlit = 2, MaterialAnimated = 4, MaterialEmitter = 8;
    public const uint InstanceAvatar = 1, InstanceNoShadow = 2, InstanceDynamic = 8, MaskDynamic = 2;
    public const uint FlagResetHistory = 1, FlagScreenReuse = 2, FlagShadows = 4, FlagDenoise = 8, FlagSameFrame = 16, FlagCacheReset = 64, FlagProbes = 128, FlagBakedHybrid = 256;
    public const int StatusStarting = 0, StatusReady = 1, StatusFailed = 2, StatusExited = 3;
    public const uint CapSer = 1, CapSerReorders = 2, CapOmm = 4;
    public const int ApiDirectX = 1, ApiVulkan = 2;

    [DllImport(Dll)] private static extern int BasisRT_StartApi([MarshalAs(UnmanagedType.LPWStr)] string exe, int api);
    [DllImport(Dll)] private static extern int BasisRT_StartMode([MarshalAs(UnmanagedType.LPWStr)] string exe, int api, int embedded);
    [DllImport(Dll)] private static extern int BasisRT_Embedded();
    [DllImport(Dll)] private static extern int BasisRT_DirectAvailable();
    [DllImport(Dll)] private static extern ulong BasisRT_Completed();
    [DllImport(Dll)] private static extern void BasisRT_SetContextGBuffer1(int context, IntPtr gbuffer1);
    [DllImport(Dll)] private static extern void BasisRT_ArmTexture(uint width, uint height);
    [DllImport(Dll)] private static extern void BasisRT_DisarmTargets();
    [DllImport(Dll)] private static extern int BasisRT_SetContextTargets(int context, IntPtr gbuffer0, IntPtr gbuffer1, IntPtr result0, IntPtr result1, uint width, uint height, uint views);
    [DllImport(Dll)] private static extern void BasisRT_Stop();
    [DllImport(Dll)] private static extern IntPtr BasisRT_Shared();
    [DllImport(Dll)] private static extern int BasisRT_Status();
    [DllImport(Dll)] private static extern int BasisRT_Ready(int context);
    [DllImport(Dll)] private static extern uint BasisRT_Caps();
    [DllImport(Dll)] private static extern uint BasisRT_Stat(int index);
    [DllImport(Dll)] private static extern long BasisRT_Layout(int index);
    [DllImport(Dll)] private static extern void BasisRT_SetSize(int context, uint width, uint height, uint views);
    [DllImport(Dll)] private static extern void BasisRT_Request(ulong frame);
    [DllImport(Dll)] private static extern ulong BasisRT_Submitted();
    [DllImport(Dll)] private static extern ulong BasisRT_Consumed();
    [DllImport(Dll)] private static extern void BasisRT_SetUpload(IntPtr upload);
    [DllImport(Dll)] private static extern void BasisRT_SetContextTextures(int context, IntPtr gbuffer, IntPtr result);
    [DllImport(Dll)] private static extern void BasisRT_Configure(int sameFrame, int budgetMicroseconds);
    [DllImport(Dll)] private static extern IntPtr BasisRT_RenderEventFunc();
    [DllImport(Dll)] private static extern int BasisRT_EventBase();
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern int BasisRT_Error(char[] buffer, int length);

    public static bool Started { get; private set; }
    public static bool Failed { get; private set; }
    public static string Failure { get; private set; } = string.Empty;
    public static ulong Frame { get; private set; }
    public static uint Caps => Started ? BasisRT_Caps() : 0u;
    public sealed class Context
    {
        public int Id;
        public Camera Camera;
        public RenderTexture GBuffer, Result, GBuffer1, Result1;
        public bool Direct;
        public uint PackedParity, ResultParity;
        public RenderTexture GBufferAt(uint parity) => (parity & 1) == 1 && GBuffer1 != null ? GBuffer1 : GBuffer;
        public readonly Matrix4x4[] FrameViewProjection = new Matrix4x4[4];
        public readonly Vector4[] FramePosition = new Vector4[4];
        public readonly int[] FrameUnity = new int[2];
        public float SubmitTime;
        public RenderTexture ResultAt(uint parity) => Direct && (parity & 1) == 1 ? Result1 : Result;
        public int Width, Height, Views, LastUsed;
        public uint Parity, LastParity;
        public ulong LastFrame;
        public bool ResultValid, Reset = true;
        public readonly Matrix4x4[] Previous = new Matrix4x4[2];
        public readonly Vector4[] Position = new Vector4[2];
    }

    public static RenderTexture Upload { get; private set; }
    public static IntPtr EventFunc { get; private set; }
    public static int EventBase { get; private set; }
    [NoAutoStaticsCleanup] private static readonly Context[] contexts = new Context[MaxContexts];
    private static int filledUnityFrame = -1;
    private static ulong filledFrame;
    public static BasisExternalRayScene Scene { get; private set; }

    private static byte* shared;
    private static long headerBytes, slotBytes, instancesOffset, materialsOffset, lightsOffset, commandsOffset, emittersOffset, dirtyOffset;
    private static float retryAt;
    private static int retries;
    private static bool loggedFailure;
    private static bool startedInProcess, inProcessFailed;
    // In process the bridge has Unity's GPU wait on the tracer's fence whenever a frame is late (no budget,
    // unlike the separate process), which stutters. The separate process skips a late result.
    public static bool InProcess = true;

    public static void SetInProcess(bool inProcess)
    {
        if (InProcess == inProcess) { return; }
        InProcess = inProcess;
        if (inProcess) { inProcessFailed = false; }
        if (Started || Failed)
        {
            Retry();
            retries = 0;
        }
    }
    public static bool DirectTargets = false;
    private static bool pairedGBuffers = true;
    public static bool RunningInProcess => Started && startedInProcess;

    public static bool IsWindows => Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;
    public static bool IsLinux => Application.platform == RuntimePlatform.LinuxPlayer || Application.platform == RuntimePlatform.LinuxEditor;

    public static bool Supported
    {
        get
        {
            GraphicsDeviceType api = SystemInfo.graphicsDeviceType;
            if (IsWindows) { return api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Direct3D12 || api == GraphicsDeviceType.Vulkan; }
            if (IsLinux) { return api == GraphicsDeviceType.Vulkan; }
            return false;
        }
    }

    public static bool BackendSelectable => IsWindows;
    public static int RayCount = 2;
    public static int AvatarRayCount = 2;
    public static bool PreferVulkan { get; private set; }
    public static int RequestedApi => IsWindows && !PreferVulkan ? ApiDirectX : ApiVulkan;
    public static int RunningApi => Started ? (int)BasisRT_Stat(7) : 0;

    public static void SetBackend(bool vulkan)
    {
        if (PreferVulkan == vulkan) { return; }
        PreferVulkan = vulkan;
        if (Started || Failed)
        {
            Retry();
            retries = 0;
        }
    }

    public const string AdapterDefault = "Same As Game";
    public static string PreferredAdapter { get; private set; } = AdapterDefault;
    [NoAutoStaticsCleanup] private static readonly List<string> adapterNames = new List<string>();
    [NoAutoStaticsCleanup] private static readonly List<string> adapterLuids = new List<string>();
    private static System.Threading.Tasks.Task adapterQuery;

    public static void QueryAdapters()
    {
        if (adapterQuery != null || !IsWindows) { return; }
        string exe = ExecutablePath;
        string output = Path.Combine(Application.temporaryCachePath, "BasisRayTracerAdapters.txt");
        adapterQuery = System.Threading.Tasks.Task.Run(() => ReadAdapters(exe, output));
    }

    private static void ReadAdapters(string exe, string output)
    {
        try
        {
            if (!File.Exists(exe)) { return; }
            File.Delete(output);
            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--list-adapters \"{output}\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) }))
            {
                if (process == null || !process.WaitForExit(10000) || !File.Exists(output)) { return; }
            }
            foreach (string line in File.ReadAllLines(output))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 4 || parts[1] != "1") { continue; }
                string name = parts[3].Trim();
                string label = name;
                for (int copy = 2; adapterNames.Contains(label); copy++) { label = name + " #" + copy; }
                adapterNames.Add(label);
                adapterLuids.Add(parts[0]);
            }
        }
        catch (Exception e) { Debug.LogWarning($"[BasisGI] Could not list the GPUs for the path tracer: {e.Message}"); }
    }

    public static IReadOnlyList<string> Adapters
    {
        get
        {
            QueryAdapters();
            adapterQuery?.Wait(10000);
            return adapterQuery != null && adapterQuery.IsCompleted ? adapterNames : Array.Empty<string>();
        }
    }

    public static bool AdapterSelectable => IsWindows && Adapters.Count > 1;

    public static void SetAdapter(string name)
    {
        if (string.IsNullOrEmpty(name)) { name = AdapterDefault; }
        if (PreferredAdapter == name) { return; }
        PreferredAdapter = name;
        if (Started || Failed)
        {
            Retry();
            retries = 0;
        }
    }

    private static string RequestedAdapterLuid()
    {
        if (PreferredAdapter == AdapterDefault || RequestedApi != ApiDirectX) { return null; }
        IReadOnlyList<string> names = Adapters;
        for (int i = 0; i < names.Count; i++) { if (names[i] == PreferredAdapter) { return adapterLuids[i]; } }
        return null;
    }

    public static string ExecutablePath
    {
        get
        {
#if UNITY_EDITOR
            return Path.GetFullPath("Packages/com.basis.globalillumination/Native~/Tracer/" + ExecutableName);
#else
            return Path.Combine(Application.dataPath, "Plugins", "x86_64", "BasisRayTracer", ExecutableName);
#endif
        }
    }

    public static string ExecutableName => IsWindows ? "BasisRayTracer.exe" : "BasisRayTracer";

    public static string ReadError()
    {
        char[] buffer = new char[512];
        int n = BasisRT_Error(buffer, buffer.Length);
        return n > 0 ? new string(buffer, 0, n) : string.Empty;
    }

    private static void Fail(string reason)
    {
        Failed = true;
        retryAt = Time.realtimeSinceStartup + 3f;
        Failure = reason;
        if (startedInProcess && !inProcessFailed)
        {
            inProcessFailed = true;
            startedInProcess = false;
            retryAt = Time.realtimeSinceStartup;
            Debug.LogWarning($"[BasisGI] The in-process path tracer failed, switching to the separate tracer process: {reason}");
            Stop();
            return;
        }
        if (!loggedFailure)
        {
            loggedFailure = true;
            Debug.LogWarning($"[BasisGI] The external path tracer is unavailable, falling back to radiance cascades: {reason}");
        }
        Stop();
    }

    public static bool EnsureStarted()
    {
        if (Started) { return true; }
        if (Failed)
        {
            if (retries >= 20 || (Time.realtimeSinceStartup < retryAt && retryAt - Time.realtimeSinceStartup <= 3f) || !Supported) { return false; }
            retries++;
            Failed = false;
            loggedFailure = false;
        }
        if (!Supported) { Fail(IsLinux ? "it needs Unity to render with Vulkan on Linux" : "it needs Windows with Direct3D 11, Direct3D 12 or Vulkan, or Linux with Vulkan"); return false; }
        string exe = ExecutablePath;
        if (!File.Exists(exe)) { Fail($"the tracer executable is missing at {exe}"); return false; }
        try
        {
            if (BasisRT_Layout(8) != ProtocolVersion) { Fail("the bridge plugin does not match this protocol version"); return false; }
            Environment.SetEnvironmentVariable("BASIS_RT_ADAPTER", RequestedAdapterLuid());
            bool inProcess = InProcess && !inProcessFailed && RequestedApi == ApiDirectX;
            int started;
            try { started = BasisRT_StartMode(exe, RequestedApi, inProcess ? 1 : 0); }
            catch (EntryPointNotFoundException) { inProcess = false; started = BasisRT_StartApi(exe, RequestedApi); }
            startedInProcess = inProcess && started != 0 && BasisRT_Embedded() != 0;
            if (started == 0) { Fail(ReadError()); return false; }
        }
        catch (DllNotFoundException) { Fail(IsLinux ? "the bridge plugin libBasisRayTracingBridge2.so is missing" : "the bridge plugin BasisRayTracingBridge2.dll is missing"); return false; }
        catch (EntryPointNotFoundException) { Fail("the bridge plugin is out of date"); return false; }
        headerBytes = BasisRT_Layout(0);
        slotBytes = BasisRT_Layout(1);
        instancesOffset = BasisRT_Layout(2);
        materialsOffset = BasisRT_Layout(3);
        lightsOffset = BasisRT_Layout(4);
        commandsOffset = BasisRT_Layout(5);
        emittersOffset = BasisRT_Layout(6);
        dirtyOffset = BasisRT_Layout(10);
        if (BasisRT_Layout(7) != sizeof(BasisExternalRayFrame)) { Fail("frame layout mismatch between C# and the bridge"); return false; }
        shared = (byte*)BasisRT_Shared();
        EventFunc = BasisRT_RenderEventFunc();
        EventBase = BasisRT_EventBase();
        Started = true;
        Frame = 0;
        filledUnityFrame = -1;
        Scene = new BasisExternalRayScene();
        Application.quitting -= Stop;
        Application.quitting += Stop;
        return true;
    }

    public static void Stop()
    {
        if (Started)
        {
            Scene?.Dispose();
            Scene = null;
            BasisRT_Stop();
        }
        Started = false;
        shared = null;
        ReleaseTextures();
    }

    public static void Retry()
    {
        Stop();
        Failed = false;
        loggedFailure = false;
        Failure = string.Empty;
    }

    private static void Destroy(RenderTexture rt)
    {
        if (rt == null) { return; }
        rt.Release();
        UnityEngine.Object.Destroy(rt);
    }

    private static void ReleaseContext(int index)
    {
        Context c = contexts[index];
        if (c == null) { return; }
        if (Started) { if (c.Direct) { BasisRT_SetContextTargets(index, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 0, 0); } BasisRT_SetContextTextures(index, IntPtr.Zero, IntPtr.Zero); BasisRT_SetSize(index, 0, 0, 0); }
        Destroy(c.GBuffer);
        Destroy(c.Result);
        Destroy(c.GBuffer1);
        Destroy(c.Result1);
        contexts[index] = null;
    }

    private static void ReleaseTextures()
    {
        for (int i = 0; i < MaxContexts; i++) { ReleaseContext(i); }
        Destroy(Upload);
        Upload = null;
    }

    private static RenderTexture Create(string name, int width, int height, GraphicsFormat format)
    {
        RenderTexture rt = new RenderTexture(new RenderTextureDescriptor(width, height, format, GraphicsFormat.None) { enableRandomWrite = true, msaaSamples = 1, useMipMap = false }) { name = name, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
        rt.Create();
        return rt;
    }

    private static bool DirectAvailable()
    {
        if (!DirectTargets) { return false; }
        try { return BasisRT_DirectAvailable() != 0; }
        catch (EntryPointNotFoundException) { DirectTargets = false; return false; }
    }

    private static RenderTexture CreateTarget(string name, int width, int height, GraphicsFormat format, bool shared, out IntPtr native)
    {
        if (shared) { BasisRT_ArmTexture((uint)width, (uint)height); }
        RenderTexture rt = Create(name, width, height, format);
        native = rt.GetNativeTexturePtr();
        if (shared) { BasisRT_DisarmTargets(); }
        return rt;
    }

    private static Context ContextFor(Camera camera, int width, int height, int views)
    {
        int frame = Time.frameCount;
        int free = -1, oldest = -1;
        for (int i = 0; i < MaxContexts; i++)
        {
            Context c = contexts[i];
            if (c != null && c.Camera == null) { ReleaseContext(i); c = null; }
            if (c != null && frame - c.LastUsed > 300) { ReleaseContext(i); c = null; }
            if (c == null) { if (free < 0) { free = i; } continue; }
            if (ReferenceEquals(c.Camera, camera)) { free = -2; oldest = i; break; }
            if (frame - c.LastUsed > 2 && (oldest < 0 || c.LastUsed < contexts[oldest].LastUsed)) { oldest = i; }
        }
        int index = free == -2 ? oldest : free >= 0 ? free : oldest;
        if (index < 0) { return null; }
        Context context = contexts[index];
        if (context != null && !ReferenceEquals(context.Camera, camera)) { ReleaseContext(index); context = null; }
        if (context == null) { context = contexts[index] = new Context { Id = index, Camera = camera }; }
        context.LastUsed = frame;
        bool lost = (context.Direct && (context.GBuffer1 == null || context.Result1 == null || !context.Result1.IsCreated())) || (context.GBuffer1 != null && !context.GBuffer1.IsCreated());
        if (context.GBuffer == null || context.Width != width || context.Height != height || context.Views != views || !context.GBuffer.IsCreated() || !context.Result.IsCreated() || lost)
        {
            if (context.Direct) { BasisRT_SetContextTargets(index, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 0, 0); }
            Destroy(context.GBuffer);
            Destroy(context.Result);
            Destroy(context.GBuffer1);
            Destroy(context.Result1);
            context.GBuffer1 = context.Result1 = null;
            context.Direct = false;
            context.Width = width;
            context.Height = height;
            context.Views = views;
            bool direct = DirectAvailable();
            context.GBuffer = CreateTarget("BasisRT GBuffer " + index, width * views * 2, height * 2, GraphicsFormat.R16G16B16A16_UInt, direct, out IntPtr gbuffer);
            context.Result = CreateTarget("BasisRT Result " + index, width * views, height, GraphicsFormat.R16G16B16A16_SFloat, direct, out IntPtr result);
            if (direct)
            {
                context.GBuffer1 = CreateTarget("BasisRT GBuffer B " + index, width * views * 2, height * 2, GraphicsFormat.R16G16B16A16_UInt, true, out IntPtr gbuffer1);
                context.Result1 = CreateTarget("BasisRT Result B " + index, width * views, height, GraphicsFormat.R16G16B16A16_SFloat, true, out IntPtr result1);
                context.Direct = BasisRT_SetContextTargets(index, gbuffer, gbuffer1, result, result1, (uint)width, (uint)height, (uint)views) != 0;
                if (!context.Direct) { Destroy(context.Result1); context.Result1 = null; }
            }
            context.PackedParity = context.ResultParity = 0;
            if (!context.Direct)
            {
                BasisRT_SetContextTextures(index, gbuffer, result);
                if (pairedGBuffers)
                {
                    if (context.GBuffer1 == null) { context.GBuffer1 = Create("BasisRT GBuffer B " + index, width * views * 2, height * 2, GraphicsFormat.R16G16B16A16_UInt); }
                    try { BasisRT_SetContextGBuffer1(index, context.GBuffer1.GetNativeTexturePtr()); }
                    catch (EntryPointNotFoundException) { pairedGBuffers = false; Destroy(context.GBuffer1); context.GBuffer1 = null; }
                }
                BasisRT_SetSize(index, (uint)width, (uint)height, (uint)views);
            }
            context.ResultValid = false;
            context.Reset = true;
            context.LastFrame = 0;
        }
        return context;
    }

    public static bool Poll()
    {
        if (!Started) { return false; }
        int status = BasisRT_Status();
        if (status == StatusFailed || status == StatusExited || status < 0)
        {
            string error = ReadError();
            Fail(string.IsNullOrEmpty(error) ? "the tracer process exited" : error);
            return false;
        }
        return status == StatusReady;
    }

    public static BasisExternalRayFrame* Params(ulong frame) => (BasisExternalRayFrame*)(shared + headerBytes + slotBytes * (long)(frame % Ring));
    public static BasisExternalRayInstance* Instances(ulong frame) => (BasisExternalRayInstance*)((byte*)Params(frame) + instancesOffset);
    public static BasisExternalRayMaterial* Materials(ulong frame) => (BasisExternalRayMaterial*)((byte*)Params(frame) + materialsOffset);
    public static BasisExternalRayLight* Lights(ulong frame) => (BasisExternalRayLight*)((byte*)Params(frame) + lightsOffset);
    public static BasisExternalRayCommand* Commands(ulong frame) => (BasisExternalRayCommand*)((byte*)Params(frame) + commandsOffset);
    public static BasisExternalRayEmitter* Emitters(ulong frame) => (BasisExternalRayEmitter*)((byte*)Params(frame) + emittersOffset);
    public static Vector4* Dirty(ulong frame) => (Vector4*)((byte*)Params(frame) + dirtyOffset);

    public static uint Stat(int index) => Started ? BasisRT_Stat(index) : 0u;
    public static int DropSubmits;
    private static uint uploadLostSeen;

    public static bool UploadLost()
    {
        uint lost = Stat(18);
        if (lost == uploadLostSeen) { return false; }
        uploadLostSeen = lost;
        return true;
    }
    public static uint GpuMicroseconds(int pass) => Stat(10 + pass);

    public static Context Prepare(Camera camera, int width, int height, int views, bool sameFrame)
    {
        if (!EnsureStarted()) { return null; }
        if (Upload == null || !Upload.IsCreated())
        {
            Destroy(Upload);
            Upload = Create("BasisRT Upload", UploadWidth, UploadHeight * 2, GraphicsFormat.R16G16B16A16_UInt);
            BasisRT_SetUpload(Upload.GetNativeTexturePtr());
        }
        if (!Poll()) { return null; }
        Context context = ContextFor(camera, width, height, views);
        if (context == null) { return null; }
        BasisRT_Configure(sameFrame ? 1 : 0, 50000);
        return BasisRT_Ready(context.Id) != 0 ? context : null;
    }

    public static bool CanSubmit(out ulong frame)
    {
        frame = Frame + 1;
        ulong consumed = BasisRT_Consumed();
        if (consumed + Ring - 1 < frame) { return false; }
        return Scene == null || !Scene.UploadDeferred || consumed + 2 >= frame;
    }

    public static bool UploadFree(ulong frame) => BasisRT_Consumed() + 2 >= frame;

    public static int LateFrames;
    private static bool completionKnown = true;

    public static bool Finished(ulong frame)
    {
        if (frame == 0 || !completionKnown) { return true; }
        ulong completed;
        try { completed = BasisRT_Completed(); }
        catch (EntryPointNotFoundException) { completionKnown = false; return true; }
        if (completed >= frame) { return true; }
        if (BasisRT_Consumed() >= frame && BasisRT_Submitted() < frame) { return true; }
        LateFrames++;
        return false;
    }

    public static bool SceneFilledThisFrame(out ulong source)
    {
        source = filledFrame;
        return filledUnityFrame == Time.frameCount && filledFrame != 0;
    }

    public static void MarkSceneFilled(ulong frame)
    {
        filledUnityFrame = Time.frameCount;
        filledFrame = frame;
    }

    public static void CopyScene(ulong source, ulong target, ref BasisExternalRayFrame parameters)
    {
        BasisExternalRayFrame* from = Params(source);
        parameters.instanceCount = from->instanceCount;
        parameters.materialCount = from->materialCount;
        parameters.lightCount = from->lightCount;
        parameters.emitterCount = from->emitterCount;
        parameters.commandCount = 0;
        parameters.uploadTexels = 0;
        parameters.skyIntensity = from->skyIntensity;
        parameters.ambient0 = from->ambient0; parameters.ambient1 = from->ambient1; parameters.ambient2 = from->ambient2;
        parameters.ambient3 = from->ambient3; parameters.ambient4 = from->ambient4; parameters.ambient5 = from->ambient5; parameters.ambient6 = from->ambient6;
        Buffer.MemoryCopy(Instances(source), Instances(target), sizeof(BasisExternalRayInstance) * MaxInstances, sizeof(BasisExternalRayInstance) * (long)from->instanceCount);
        Buffer.MemoryCopy(Materials(source), Materials(target), sizeof(BasisExternalRayMaterial) * MaxMaterials, sizeof(BasisExternalRayMaterial) * (long)from->materialCount);
        Buffer.MemoryCopy(Lights(source), Lights(target), sizeof(BasisExternalRayLight) * MaxLights, sizeof(BasisExternalRayLight) * (long)from->lightCount);
        Buffer.MemoryCopy(Emitters(source), Emitters(target), sizeof(BasisExternalRayEmitter) * MaxEmitters, sizeof(BasisExternalRayEmitter) * (long)from->emitterCount);
    }

    public static void Commit(Context context, ulong frame)
    {
        Frame = frame;
        BasisRT_Request(frame);
        context.ResultValid = true;
        context.Parity ^= 1u;
    }

    public static uint ConsumeResetFlag(Context context)
    {
        bool reset = context.Reset;
        context.Reset = false;
        return reset ? FlagResetHistory : 0u;
    }

    public static Matrix4x4 PreviousViewProjection(Context context, int view, Matrix4x4 current)
    {
        Matrix4x4 previous = context.Previous[view];
        context.Previous[view] = current;
        return previous == Matrix4x4.zero ? current : previous;
    }

    public static IntPtr EventData(ulong frame, int context, uint parity, uint rows) => (IntPtr)(long)((frame & 0xffffffffffUL) | ((ulong)(context & 0xf) << 40) | ((ulong)(parity & 1) << 44) | ((ulong)rows << 48));
}
