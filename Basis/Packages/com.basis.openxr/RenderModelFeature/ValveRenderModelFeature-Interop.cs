using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.NativeTypes;

public partial class ValveRenderModelFeature : OpenXRFeature
{
    private delegate XrResult GetInstanceProcAddrDelegate(ulong instance, string name, ref IntPtr procAddr);
    private GetInstanceProcAddrDelegate _getInstanceProcAddr;
    
    private T GetOpenXrInstanceProc<T>(string procName)
    {
        if (_getInstanceProcAddr == null)
        {
            if (xrGetInstanceProcAddr == IntPtr.Zero)
            {
                Debug.LogWarning($"Unity's OpenXR GetInstance function accessor is invalid!");
                return default;
            }

            _getInstanceProcAddr = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrDelegate>(xrGetInstanceProcAddr);
        }

        IntPtr resultProcAddr = IntPtr.Zero;
            
        if (_getInstanceProcAddr != null)
        {
            XrResult result = _getInstanceProcAddr(_xrInstance, procName, ref resultProcAddr);

            if (result < 0 || resultProcAddr == IntPtr.Zero)
            {
                Debug.LogWarning($"Failed to find OpenXR instance function '{procName} (result: {result}, instance: {_xrInstance}).'");
                return default;
            }
        }
        
        return Marshal.GetDelegateForFunctionPointer<T>(resultProcAddr);
    }

    private bool XrSucceeded(int xrResult)
    {
        return xrResult == 0;
    }

    private static long ToXrTime(double seconds)
    {
        return (long)(seconds * 1_000_000_000.0);
    }
    
    #region OpenXR Structures
    
    public enum XrStructureType : uint
    {
        XR_TYPE_RENDER_MODEL_ASSET_CREATE_INFO_EXT = 1000300006,
        XR_TYPE_RENDER_MODEL_ASSET_DATA_GET_INFO_EXT = 1000300007,
        XR_TYPE_RENDER_MODEL_ASSET_DATA_EXT = 1000300008,
        XR_TYPE_RENDER_MODEL_CREATE_INFO_EXT = 1000300000,
        XR_TYPE_RENDER_MODEL_PROPERTIES_GET_INFO_EXT = 1000300001,        
        XR_TYPE_RENDER_MODEL_PROPERTIES_EXT = 1000300002,        
        XR_TYPE_RENDER_MODEL_STATE_EXT = 1000300005,
        XR_TYPE_RENDER_MODEL_ASSET_PROPERTIES_GET_INFO_EXT = 1000300009,
        XR_TYPE_RENDER_MODEL_ASSET_PROPERTIES_EXT = 1000300010,
        XR_TYPE_RENDER_MODEL_STATE_GET_INFO_EXT = 1000300004,
        XR_TYPE_INTERACTION_RENDER_MODEL_IDS_ENUMERATE_INFO_EXT = 1000301000,
        XR_TYPE_INTERACTION_RENDER_MODEL_SUBACTION_PATH_INFO_EXT = 1000301001,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XrPath
    {
        public ulong Value;

        public static implicit operator ulong(XrPath p) => p.Value;
        public static implicit operator XrPath(ulong v) => new XrPath { Value = v };
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct XrInteractionRenderModelIdsEnumerateInfoEXT
    {
        public XrStructureType type;
        public IntPtr next;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XrRenderModelPropertiesGetInfoEXT
    {
        public XrStructureType type;
        public IntPtr next;
    }

    // 16-byte UUID
    [StructLayout(LayoutKind.Sequential)]
    public struct XrUuidEXT
    {
        private const int XR_UUID_SIZE = 16;
        
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = XR_UUID_SIZE)]
        public byte[] bytes;

        public bool IsValid => bytes != null && bytes.Length == XR_UUID_SIZE;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct XrRenderModelPropertiesEXT
    {
        public XrStructureType type;
        public IntPtr next;
        public XrUuidEXT assetId;
        public uint animatableNodeCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct XrRenderModelAssetNodePropertiesEXT
    {
        private const int XR_MAX_RENDER_MODEL_ASSET_NODE_NAME_SIZE_EXT = 64;

        // Placeholder for inline char[64]
        private byte _dummy;
        
        public static int stride => XR_MAX_RENDER_MODEL_ASSET_NODE_NAME_SIZE_EXT;

        public static IntPtr Allocate(int nodeCount)
        {
            int size = nodeCount * XR_MAX_RENDER_MODEL_ASSET_NODE_NAME_SIZE_EXT;
            IntPtr ptr = Marshal.AllocHGlobal(size);

            // Zero memory
            Span<byte> zero = stackalloc byte[XR_MAX_RENDER_MODEL_ASSET_NODE_NAME_SIZE_EXT];
            for (int i = 0; i < nodeCount; i++)
                Marshal.Copy(zero.ToArray(), 0, ptr + i * zero.Length, zero.Length);

            return ptr;
        }        
        
        public static void Free(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }        
        
        public static string HandleToUtf8String(IntPtr structPtr, int maxLen = XR_MAX_RENDER_MODEL_ASSET_NODE_NAME_SIZE_EXT)
        {
            byte[] buffer = new byte[maxLen];
            Marshal.Copy(structPtr, buffer, 0, maxLen);

            int len = Array.IndexOf(buffer, (byte)0);
            if (len < 0) len = maxLen;

            return Encoding.UTF8.GetString(buffer, 0, len);
        }        
    }    
    
    [StructLayout(LayoutKind.Sequential)]
    public struct XrRenderModelAssetPropertiesGetInfoEXT
    {
        public XrStructureType type;
        public IntPtr next;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct XrRenderModelAssetPropertiesEXT
    {
        public XrStructureType type;
        public IntPtr next;
        public uint nodePropertyCount;
        public IntPtr nodeProperties; // XrRenderModelAssetNodePropertiesEXT*
    }    
    
    [StructLayout(LayoutKind.Sequential)]
    public struct XrRenderModelNodeStateEXT
    {
        public XrPosef nodePose;
        public uint isVisible;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XrRenderModelStateEXT
    {
        public XrStructureType type;
        public IntPtr next;
        public uint nodeStateCount;
        public IntPtr nodeStates; // XrRenderModelNodeStateEXT*
    }    
    
    [StructLayout(LayoutKind.Sequential)]
    private struct XrRenderModelAssetCreateInfoEXT
    {
        public XrStructureType type;
        public IntPtr next;
        public XrUuidEXT cacheId; // uuid to request asset
    }    
    
    [StructLayout(LayoutKind.Sequential)]
    public struct XrRenderModelCreateInfoEXT
    {
        public XrStructureType type;
        public IntPtr next;
        public XrRenderModelIdEXT renderModelId;
        public uint gltfExtensionCount;
        public IntPtr gltfExtensions; // const char* const*
    }    

    [StructLayout(LayoutKind.Sequential)]
    private struct XrRenderModelAssetDataGetInfoEXT
    {
        public XrStructureType type;
        public IntPtr next;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrInteractionRenderModelSubactionPathInfoEXT
    {
        public XrStructureType type;
        public IntPtr next;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct XrRenderModelStateGetInfoEXT
    {
        public XrStructureType type;
        public IntPtr next;
        public long displayTime;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct XrRenderModelAssetDataEXT
    {
        public XrStructureType type;
        public IntPtr next;
        public uint bufferCapacityInput;
        public uint bufferCountOutput;
        public IntPtr buffer; // void* to GLB data
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct XrRenderModelIdEXT : IEquatable<XrRenderModelIdEXT>
    {
        private readonly ulong _value;
        public ulong Value => _value;
        public XrRenderModelIdEXT(ulong value) { _value = value; }
        public static implicit operator ulong(XrRenderModelIdEXT id) { return id._value; }
        public static implicit operator XrRenderModelIdEXT(ulong value) { return new XrRenderModelIdEXT(value); }
        public override bool Equals(object obj) { if (obj is XrRenderModelIdEXT other) { return Equals(other); } return false; }
        public bool Equals(XrRenderModelIdEXT other) { return _value == other._value; }
        public override int GetHashCode() { return _value.GetHashCode(); }
        public static bool operator ==(XrRenderModelIdEXT left, XrRenderModelIdEXT right) { return left.Equals(right); }
        public static bool operator !=(XrRenderModelIdEXT left, XrRenderModelIdEXT right) { return !(left == right); }
    }

    #endregion

    #region OpenXR Functions
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_xrCreateRenderModelEXT(ulong session, ref XrRenderModelCreateInfoEXT createInfo, out ulong renderModel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_xrGetRenderModelPropertiesEXT(ulong renderModel, ref XrRenderModelPropertiesGetInfoEXT info, ref XrRenderModelPropertiesEXT properties);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_xrCreateRenderModelAssetEXT(ulong session, ref XrRenderModelAssetCreateInfoEXT createInfo, out ulong asset /* XrRenderModelAssetEXT* */);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_xrGetRenderModelAssetDataEXT(ulong asset, ref XrRenderModelAssetDataGetInfoEXT getInfo, out XrRenderModelAssetDataEXT buffer);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_xrGetRenderModelAssetPropertiesEXT(ulong asset, ref XrRenderModelAssetPropertiesGetInfoEXT getInfo, ref XrRenderModelAssetPropertiesEXT properties);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_xrDestroyRenderModelAssetEXT(ulong asset);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_xrDestroyRenderModelEXT(ulong renderModel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_xrGetRenderModelStateEXT(ulong renderModel, ref XrRenderModelStateGetInfoEXT getInfo, ref XrRenderModelStateEXT state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int PFN_xrGetPredictedDisplayTime(ulong session, int viewConfigurationType, out long predictedDisplayTime);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_xrEnumerateInteractionRenderModelIdsEXT (ulong session, ref XrInteractionRenderModelIdsEnumerateInfoEXT enumerateInfo, uint renderModelIdCapacityInput, out uint renderModelIdCountOutput, [Out] XrRenderModelIdEXT[] renderModelIds);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_xrEnumerateRenderModelSubactionPathsEXT(
        ulong renderModel,
        ref XrInteractionRenderModelSubactionPathInfoEXT info,
        uint pathCapacityInput,
        out uint pathCountOutput,
        IntPtr paths // pointer to pre-allocated unmanaged array of XrPath
    );
    
    #endregion
}
