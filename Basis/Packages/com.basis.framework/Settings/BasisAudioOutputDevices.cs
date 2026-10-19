using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisAudioOutputDevices
{
    public struct OutputDevice
    {
        public string Id;
        public string Name;
    }

#if UNITY_STANDALONE_WIN
    public static bool IsSupported => Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;

    private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_AudioPolicyConfig_21H2 = new Guid("ab3d4648-e242-459f-b02f-541c70306324");
    private static readonly Guid IID_AudioPolicyConfig_Downlevel = new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");
    private static readonly Guid FmtId_DeviceFriendlyName = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private const uint Pid_DeviceFriendlyName = 14;

    private const string AudioPolicyConfigRuntimeClass = "Windows.Media.Internal.AudioPolicyConfig";
    private const string MmDevApiToken = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterfaceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private const uint CLSCTX_ALL = 23;
    private const uint DEVICE_STATE_ACTIVE = 0x00000001;
    private const uint STGM_READ = 0;
    private const int S_OK = 0;
    private const int RO_E_UNINITIALIZED = unchecked((int)0x8000001B);
    private const int RO_INIT_SINGLETHREADED = 0;
    private const short VT_LPWSTR = 31;

    private const int DataFlowRender = 0;
    private const int RoleConsole = 0;
    private const int RoleMultimedia = 1;

    private const int SlotRelease = 2;
    private const int SlotEnumAudioEndpoints = 3;
    private const int SlotCollectionGetCount = 3;
    private const int SlotCollectionItem = 4;
    private const int SlotDeviceOpenPropertyStore = 4;
    private const int SlotDeviceGetId = 5;
    private const int SlotPropertyStoreGetValue = 5;
    private const int SlotSetPersistedEndpoint = 25;
    private const int SlotGetPersistedEndpoint = 26;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAudioEndpointsFn(IntPtr self, int dataFlow, uint stateMask, out IntPtr collection);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountFn(IntPtr self, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ItemFn(IntPtr self, uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenPropertyStoreFn(IntPtr self, uint stgmAccess, out IntPtr store);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIdFn(IntPtr self, out IntPtr id);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetValueFn(IntPtr self, ref PropertyKey key, IntPtr propVariant);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedEndpointFn(IntPtr self, uint processId, int flow, int role, IntPtr deviceId);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPersistedEndpointFn(IntPtr self, uint processId, int flow, int role, out IntPtr deviceId);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr outer, uint clsContext, ref Guid riid, out IntPtr instance);
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr propVariant);
    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr ptr);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, uint length, out IntPtr hstring);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")]
    private static extern int RoInitialize(int initType);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    private static TDelegate Bind<TDelegate>(IntPtr comObject, int slot) where TDelegate : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(method);
    }

    private static void Release(IntPtr comObject)
    {
        if (comObject == IntPtr.Zero) return;
        try { Bind<ReleaseFn>(comObject, SlotRelease)(comObject); }
        catch { }
    }

    private static IntPtr CreateInstance(Guid clsid, Guid iid)
    {
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IntPtr instance);
        return hr == S_OK ? instance : IntPtr.Zero;
    }

    private static IntPtr CreateHString(string value)
    {
        if (WindowsCreateString(value, (uint)value.Length, out IntPtr hstring) != S_OK) return IntPtr.Zero;
        return hstring;
    }

    private static string ReadHString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return string.Empty;
        IntPtr buffer = WindowsGetStringRawBuffer(hstring, out uint length);
        if (buffer == IntPtr.Zero || length == 0) return string.Empty;
        return Marshal.PtrToStringUni(buffer, (int)length);
    }

    private static string WrapRenderDeviceId(string rawDeviceId) => MmDevApiToken + rawDeviceId + RenderInterfaceSuffix;

    private static string UnwrapDeviceId(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return string.Empty;
        if (deviceId.StartsWith(MmDevApiToken, StringComparison.Ordinal)) deviceId = deviceId.Substring(MmDevApiToken.Length);
        if (deviceId.EndsWith(RenderInterfaceSuffix, StringComparison.Ordinal)) deviceId = deviceId.Substring(0, deviceId.Length - RenderInterfaceSuffix.Length);
        return deviceId;
    }

    private static IntPtr Activate(Guid iid)
    {
        IntPtr classId = CreateHString(AudioPolicyConfigRuntimeClass);
        if (classId == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            int hr = RoGetActivationFactory(classId, ref iid, out IntPtr factory);
            if (hr == RO_E_UNINITIALIZED)
            {
                RoInitialize(RO_INIT_SINGLETHREADED);
                hr = RoGetActivationFactory(classId, ref iid, out factory);
            }
            return hr == S_OK ? factory : IntPtr.Zero;
        }
        finally { WindowsDeleteString(classId); }
    }

    private static IntPtr CreatePolicyConfigFactory()
    {
        IntPtr factory = Activate(IID_AudioPolicyConfig_21H2);
        if (factory == IntPtr.Zero) factory = Activate(IID_AudioPolicyConfig_Downlevel);
        return factory;
    }

    private static string ReadDeviceId(IntPtr device)
    {
        if (Bind<GetIdFn>(device, SlotDeviceGetId)(device, out IntPtr idPtr) != S_OK || idPtr == IntPtr.Zero)
            return null;
        string id = Marshal.PtrToStringUni(idPtr);
        CoTaskMemFree(idPtr);
        return id;
    }

    private static string ReadFriendlyName(IntPtr device)
    {
        if (Bind<OpenPropertyStoreFn>(device, SlotDeviceOpenPropertyStore)(device, STGM_READ, out IntPtr store) != S_OK || store == IntPtr.Zero)
            return null;

        IntPtr propVariant = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt64(propVariant, 0, 0);
            Marshal.WriteInt64(propVariant, 8, 0);
            Marshal.WriteInt64(propVariant, 16, 0);
            Marshal.WriteInt64(propVariant, 24, 0);

            PropertyKey key = new PropertyKey { FormatId = FmtId_DeviceFriendlyName, PropertyId = Pid_DeviceFriendlyName };
            if (Bind<GetValueFn>(store, SlotPropertyStoreGetValue)(store, ref key, propVariant) != S_OK)
                return null;

            string name = null;
            if (Marshal.ReadInt16(propVariant, 0) == VT_LPWSTR)
            {
                IntPtr strPtr = Marshal.ReadIntPtr(propVariant, 8);
                if (strPtr != IntPtr.Zero) name = Marshal.PtrToStringUni(strPtr);
            }
            PropVariantClear(propVariant);
            return name;
        }
        finally
        {
            Marshal.FreeHGlobal(propVariant);
            Release(store);
        }
    }

    public static List<OutputDevice> GetDevices()
    {
        List<OutputDevice> devices = new List<OutputDevice>();
        IntPtr enumerator = CreateInstance(CLSID_MMDeviceEnumerator, IID_IMMDeviceEnumerator);
        if (enumerator == IntPtr.Zero) return devices;
        try
        {
            if (Bind<EnumAudioEndpointsFn>(enumerator, SlotEnumAudioEndpoints)(enumerator, DataFlowRender, DEVICE_STATE_ACTIVE, out IntPtr collection) != S_OK || collection == IntPtr.Zero)
                return devices;
            try
            {
                if (Bind<GetCountFn>(collection, SlotCollectionGetCount)(collection, out uint count) != S_OK)
                    return devices;

                ItemFn item = Bind<ItemFn>(collection, SlotCollectionItem);
                for (uint i = 0; i < count; i++)
                {
                    if (item(collection, i, out IntPtr device) != S_OK || device == IntPtr.Zero) continue;
                    try
                    {
                        string id = ReadDeviceId(device);
                        if (string.IsNullOrEmpty(id)) continue;
                        string name = ReadFriendlyName(device);
                        devices.Add(new OutputDevice { Id = id, Name = string.IsNullOrEmpty(name) ? id : name });
                    }
                    finally { Release(device); }
                }
            }
            finally { Release(collection); }
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to enumerate audio output devices: {e}");
        }
        finally { Release(enumerator); }
        return devices;
    }

    public static string GetRoutedDeviceId()
    {
        IntPtr factory = CreatePolicyConfigFactory();
        if (factory == IntPtr.Zero) return string.Empty;
        try
        {
            uint processId = GetCurrentProcessId();
            if (Bind<GetPersistedEndpointFn>(factory, SlotGetPersistedEndpoint)(factory, processId, DataFlowRender, RoleMultimedia, out IntPtr deviceId) != S_OK)
                return string.Empty;
            try { return UnwrapDeviceId(ReadHString(deviceId)); }
            finally { if (deviceId != IntPtr.Zero) WindowsDeleteString(deviceId); }
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to read routed audio output device: {e}");
            return string.Empty;
        }
        finally { Release(factory); }
    }

    public static bool SetRoutedDevice(string rawDeviceId)
    {
        IntPtr factory = CreatePolicyConfigFactory();
        if (factory == IntPtr.Zero) return false;

        IntPtr deviceId = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrEmpty(rawDeviceId))
                deviceId = CreateHString(WrapRenderDeviceId(rawDeviceId));

            uint processId = GetCurrentProcessId();
            SetPersistedEndpointFn setEndpoint = Bind<SetPersistedEndpointFn>(factory, SlotSetPersistedEndpoint);
            bool ok = setEndpoint(factory, processId, DataFlowRender, RoleMultimedia, deviceId) == S_OK;
            ok &= setEndpoint(factory, processId, DataFlowRender, RoleConsole, deviceId) == S_OK;
            return ok;
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to route audio output device: {e}");
            return false;
        }
        finally
        {
            if (deviceId != IntPtr.Zero) WindowsDeleteString(deviceId);
            Release(factory);
        }
    }
#elif UNITY_STANDALONE_LINUX
    private static bool? _pactlAvailable;

    public static bool IsSupported
    {
        get
        {
            if (Application.platform != RuntimePlatform.LinuxPlayer && Application.platform != RuntimePlatform.LinuxEditor) return false;
            if (_pactlAvailable == null) _pactlAvailable = TryRunPactl("info", out _);
            return _pactlAvailable.Value;
        }
    }

    private struct OwnSinkInput
    {
        public int InputIndex;
        public int SinkIndex;
    }

    private static int GetPid() => System.Diagnostics.Process.GetCurrentProcess().Id;

    private static bool TryRunPactl(string arguments, out string stdout)
    {
        stdout = string.Empty;
        try
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pactl",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(psi))
            {
                if (process == null) return false;
                stdout = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(); } catch { }
                    return false;
                }
                return process.ExitCode == 0;
            }
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning($"pactl '{arguments}' failed: {e.Message}");
            return false;
        }
    }

    private static int ParseIndexAfter(string line, string prefix)
    {
        string rest = line.Substring(prefix.Length).Trim();
        int count = 0;
        while (count < rest.Length && char.IsDigit(rest[count])) count++;
        return count == 0 ? -1 : int.Parse(rest.Substring(0, count));
    }

    private static List<OwnSinkInput> FindOwnSinkInputs(string sinkInputsOutput, int pid)
    {
        List<OwnSinkInput> result = new List<OwnSinkInput>();
        string pidNeedle = "application.process.id = \"" + pid + "\"";
        int inputIndex = -1;
        int sinkIndex = -1;
        bool isOurs = false;

        foreach (string rawLine in sinkInputsOutput.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("Sink Input #"))
            {
                if (inputIndex >= 0 && isOurs) result.Add(new OwnSinkInput { InputIndex = inputIndex, SinkIndex = sinkIndex });
                inputIndex = ParseIndexAfter(line, "Sink Input #");
                sinkIndex = -1;
                isOurs = false;
            }
            else if (line.StartsWith("Sink:"))
            {
                int.TryParse(line.Substring("Sink:".Length).Trim(), out sinkIndex);
            }
            else if (line.Contains(pidNeedle))
            {
                isOurs = true;
            }
        }
        if (inputIndex >= 0 && isOurs) result.Add(new OwnSinkInput { InputIndex = inputIndex, SinkIndex = sinkIndex });
        return result;
    }

    private static string SinkIndexToName(string shortSinksOutput, int sinkIndex)
    {
        string key = sinkIndex.ToString();
        foreach (string rawLine in shortSinksOutput.Split('\n'))
        {
            string[] parts = rawLine.Split('\t');
            if (parts.Length >= 2 && parts[0].Trim() == key) return parts[1].Trim();
        }
        return string.Empty;
    }

    public static List<OutputDevice> GetDevices()
    {
        List<OutputDevice> devices = new List<OutputDevice>();
        if (!TryRunPactl("list sinks", out string output)) return devices;

        string name = null;
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("Name:"))
            {
                name = line.Substring("Name:".Length).Trim();
            }
            else if (line.StartsWith("Description:") && name != null)
            {
                string description = line.Substring("Description:".Length).Trim();
                devices.Add(new OutputDevice { Id = name, Name = string.IsNullOrEmpty(description) ? name : description });
                name = null;
            }
        }
        return devices;
    }

    public static string GetRoutedDeviceId()
    {
        if (!TryRunPactl("list sink-inputs", out string sinkInputs)) return string.Empty;
        List<OwnSinkInput> inputs = FindOwnSinkInputs(sinkInputs, GetPid());
        if (inputs.Count == 0 || inputs[0].SinkIndex < 0) return string.Empty;
        if (!TryRunPactl("list short sinks", out string shortSinks)) return string.Empty;
        return SinkIndexToName(shortSinks, inputs[0].SinkIndex);
    }

    public static bool SetRoutedDevice(string sinkName)
    {
        if (!TryRunPactl("list sink-inputs", out string sinkInputs)) return false;
        List<OwnSinkInput> inputs = FindOwnSinkInputs(sinkInputs, GetPid());
        if (inputs.Count == 0) return false;

        string target = string.IsNullOrEmpty(sinkName) ? "@DEFAULT_SINK@" : sinkName;
        bool ok = true;
        foreach (OwnSinkInput input in inputs)
            ok &= TryRunPactl("move-sink-input " + input.InputIndex + " " + target, out _);
        return ok;
    }
#else
    public static bool IsSupported => false;
    public static List<OutputDevice> GetDevices() => new List<OutputDevice>();
    public static string GetRoutedDeviceId() => string.Empty;
    public static bool SetRoutedDevice(string rawDeviceId) => false;
#endif
}
