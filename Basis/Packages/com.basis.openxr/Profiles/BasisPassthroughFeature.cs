using Unity.Scripting.LifecycleManagement;
using System;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using Basis.Scripts.Device_Management;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace Basis.OpenXR
{
    /// <summary>
    /// Video passthrough via <c>XR_FB_passthrough</c>. Creates a reconstruction passthrough layer
    /// and, while active, submits it as an underlay in <c>xrEndFrame</c> so the projection layer's
    /// transparent pixels reveal the headset's real-world camera feed. Standalone VR (Quest) only.
    /// </summary>
#if UNITY_EDITOR
    [AutoStaticsCleanup]
    [OpenXRFeature(UiName = "Basis Passthrough",
        BuildTargetGroups = new[] { BuildTargetGroup.Android },
        Company = "Basis",
        Desc = "FB passthrough underlay for standalone mixed reality.",
        OpenxrExtensionStrings = ExtensionString,
        Version = "1.0.0",
        FeatureId = FeatureIdString)]
#endif
    public partial class BasisPassthroughFeature : OpenXRFeature
    {
        public const string FeatureIdString = "com.basis.openxr.feature.passthrough";
        public const string ExtensionString = "XR_FB_passthrough";
        const string Tag = "[BasisPassthrough]";

        /// <summary>True once the runtime reported the passthrough extension enabled this session.</summary>
        public static bool IsSupported { get; private set; }

        const uint XR_TYPE_PASSTHROUGH_CREATE_INFO_FB = 1000118001;
        const uint XR_TYPE_PASSTHROUGH_LAYER_CREATE_INFO_FB = 1000118002;
        const uint XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_FB = 1000118003;
        const uint XR_TYPE_COMPOSITION_LAYER_PROJECTION = 35;
        const uint XR_PASSTHROUGH_LAYER_PURPOSE_RECONSTRUCTION_FB = 0;
        const long XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT = 0x00000002;
        const long XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT = 0x00000004;
        const long PROJECTION_ALPHA_FLAGS = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT | XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT;
        const int LAYER_FLAGS_OFFSET = 16;
        const int XR_SESSION_STATE_FOCUSED = 5;
        const int XR_SESSION_STATE_STOPPING = 6;
        const uint XR_TYPE_EVENT_DATA_PASSTHROUGH_STATE_CHANGED_FB = 1000118020;
        const long XR_PASSTHROUGH_STATE_CHANGED_REINIT_REQUIRED_BIT_FB = 0x00000001;
        const long XR_PASSTHROUGH_STATE_CHANGED_NON_RECOVERABLE_ERROR_BIT_FB = 0x00000004;
        const long XR_PASSTHROUGH_STATE_CHANGED_RESTORED_ERROR_BIT_FB = 0x00000008;
        const int EVENT_FLAGS_OFFSET = 16;
        const int RETIRE_TICKS = 3;
        const int XR_ERROR_RUNTIME_FAILURE = -2;

        static ulong s_Session;
        static ulong s_Passthrough;
        static ulong s_Layer;
        static bool s_LayerCreated;
        static bool s_Inject;
        static int s_FrameLog;
        static bool s_LoggedFirstFrame;
        static bool s_SessionStopped;
        static bool s_ReinitRequested;
        static bool s_RestoredNotify;
        static bool s_RetryArmed = true;
        static ulong s_RetiredPassthrough;
        static ulong s_RetiredLayer;
        static int s_RetireTicks;

        [NoAutoStaticsCleanup] static IntPtr s_UnderlayPtr;
        [NoAutoStaticsCleanup] static IntPtr s_FrameEndInfoPtr;
        [NoAutoStaticsCleanup] static IntPtr s_LayersPtr;
        static int s_LayersCapacity;

        [StructLayout(LayoutKind.Sequential)]
        struct XrPassthroughCreateInfoFB
        {
            public uint type;
            public IntPtr next;
            public ulong flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XrPassthroughLayerCreateInfoFB
        {
            public uint type;
            public IntPtr next;
            public ulong passthrough;
            public ulong flags;
            public uint purpose;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XrCompositionLayerPassthroughFB
        {
            public uint type;
            public IntPtr next;
            public ulong flags;
            public ulong space;
            public ulong layerHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XrFrameEndInfo
        {
            public uint type;
            public IntPtr next;
            public long displayTime;
            public uint environmentBlendMode;
            public uint layerCount;
            public IntPtr layers;
        }

        delegate int Type_xrGetInstanceProcAddr(ulong instance, string name, out IntPtr function);
        delegate int Type_xrCreatePassthroughFB(ulong session, ref XrPassthroughCreateInfoFB createInfo, out ulong passthrough);
        delegate int Type_xrDestroyPassthroughFB(ulong passthrough);
        delegate int Type_xrPassthroughStartFB(ulong passthrough);
        delegate int Type_xrPassthroughPauseFB(ulong passthrough);
        delegate int Type_xrCreatePassthroughLayerFB(ulong session, ref XrPassthroughLayerCreateInfoFB createInfo, out ulong layer);
        delegate int Type_xrDestroyPassthroughLayerFB(ulong layer);
        delegate int Type_xrPassthroughLayerResumeFB(ulong layer);
        delegate int Type_xrPassthroughLayerPauseFB(ulong layer);
        delegate int Type_xrEndFrame(ulong session, IntPtr frameEndInfo);
        delegate int Type_xrPollEvent(ulong instance, IntPtr eventData);

        static Type_xrGetInstanceProcAddr d_getProc;
        [NoAutoStaticsCleanup] static Type_xrGetInstanceProcAddr d_originalGetProc;
        static Type_xrCreatePassthroughFB d_createPassthrough;
        static Type_xrDestroyPassthroughFB d_destroyPassthrough;
        static Type_xrPassthroughStartFB d_startPassthrough;
        static Type_xrPassthroughPauseFB d_pausePassthrough;
        static Type_xrCreatePassthroughLayerFB d_createLayer;
        static Type_xrDestroyPassthroughLayerFB d_destroyLayer;
        static Type_xrPassthroughLayerResumeFB d_resumeLayer;
        static Type_xrPassthroughLayerPauseFB d_pauseLayer;
        [NoAutoStaticsCleanup] static Type_xrEndFrame d_originalEndFrame;
        [NoAutoStaticsCleanup] static Type_xrPollEvent d_originalPollEvent;

        [NoAutoStaticsCleanup] static readonly Type_xrGetInstanceProcAddr s_getProcHook = HookGetProc;
        [NoAutoStaticsCleanup] static readonly Type_xrEndFrame s_endFrameHook = HookEndFrame;
        [NoAutoStaticsCleanup] static readonly Type_xrPollEvent s_pollEventHook = HookPollEvent;
        static readonly int UNDERLAY_LAYER_HANDLE_OFFSET = (int)Marshal.OffsetOf<XrCompositionLayerPassthroughFB>(nameof(XrCompositionLayerPassthroughFB.layerHandle));

        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            d_originalGetProc = Marshal.GetDelegateForFunctionPointer<Type_xrGetInstanceProcAddr>(func);
            return Marshal.GetFunctionPointerForDelegate(s_getProcHook);
        }

        [MonoPInvokeCallback(typeof(Type_xrGetInstanceProcAddr))]
        static int HookGetProc(ulong instance, string name, out IntPtr function)
        {
            function = IntPtr.Zero;
            int r = XR_ERROR_RUNTIME_FAILURE;
            try
            {
                r = d_originalGetProc.Invoke(instance, name, out function);
                bool endFrame = name == "xrEndFrame";
                if (!endFrame && name != "xrPollEvent")
                {
                    return r;
                }
                if (r != 0 || function == IntPtr.Zero)
                {
                    function = IntPtr.Zero;
                    return r;
                }
                if (endFrame)
                {
                    d_originalEndFrame = Marshal.GetDelegateForFunctionPointer<Type_xrEndFrame>(function);
                    function = Marshal.GetFunctionPointerForDelegate(s_endFrameHook);
                    BasisDebug.Log($"{Tag} Installed xrEndFrame interception hook.", BasisDebug.LogTag.Device);
                }
                else
                {
                    d_originalPollEvent = Marshal.GetDelegateForFunctionPointer<Type_xrPollEvent>(function);
                    function = Marshal.GetFunctionPointerForDelegate(s_pollEventHook);
                    BasisDebug.Log($"{Tag} Installed xrPollEvent interception hook.", BasisDebug.LogTag.Device);
                }
                return 0;
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"{Tag} xrGetInstanceProcAddr hook failed for {name}: {e}", BasisDebug.LogTag.Device);
                return r;
            }
        }

        [MonoPInvokeCallback(typeof(Type_xrPollEvent))]
        static int HookPollEvent(ulong instance, IntPtr eventData)
        {
            int r = XR_ERROR_RUNTIME_FAILURE;
            try
            {
                r = d_originalPollEvent.Invoke(instance, eventData);
                if (r != 0 || eventData == IntPtr.Zero || !IsSupported)
                {
                    return r;
                }
                if ((uint)Marshal.ReadInt32(eventData, 0) != XR_TYPE_EVENT_DATA_PASSTHROUGH_STATE_CHANGED_FB)
                {
                    return r;
                }
                long flags = Marshal.ReadInt64(eventData, EVENT_FLAGS_OFFSET);
                BasisDebug.Log($"{Tag} Passthrough state changed, flags=0x{flags:X}.", BasisDebug.LogTag.Device);
                if ((flags & XR_PASSTHROUGH_STATE_CHANGED_REINIT_REQUIRED_BIT_FB) != 0)
                {
                    s_ReinitRequested = true;
                }
                else if ((flags & XR_PASSTHROUGH_STATE_CHANGED_RESTORED_ERROR_BIT_FB) != 0)
                {
                    s_RestoredNotify = true;
                }
                if ((flags & XR_PASSTHROUGH_STATE_CHANGED_NON_RECOVERABLE_ERROR_BIT_FB) != 0)
                {
                    BasisDebug.LogError($"{Tag} Runtime reported a non-recoverable passthrough error.", BasisDebug.LogTag.Device);
                }
                return r;
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"{Tag} xrPollEvent hook failed: {e}", BasisDebug.LogTag.Device);
                return r;
            }
        }

        protected override bool OnInstanceCreate(ulong instance)
        {
            IsSupported = OpenXRRuntime.IsExtensionEnabled(ExtensionString);
            BasisDebug.Log($"{Tag} OnInstanceCreate: {ExtensionString} enabled = {IsSupported}", BasisDebug.LogTag.Device);
            if (!IsSupported)
            {
                BasisDebug.LogWarning($"{Tag} Extension NOT enabled — tick the 'Basis Passthrough' OpenXR feature for Android in Project Settings > XR > OpenXR.", BasisDebug.LogTag.Device);
                return base.OnInstanceCreate(instance);
            }

            d_getProc = Marshal.GetDelegateForFunctionPointer<Type_xrGetInstanceProcAddr>(xrGetInstanceProcAddr);
            d_createPassthrough = Load<Type_xrCreatePassthroughFB>(instance, "xrCreatePassthroughFB");
            d_destroyPassthrough = Load<Type_xrDestroyPassthroughFB>(instance, "xrDestroyPassthroughFB");
            d_startPassthrough = Load<Type_xrPassthroughStartFB>(instance, "xrPassthroughStartFB");
            d_pausePassthrough = Load<Type_xrPassthroughPauseFB>(instance, "xrPassthroughPauseFB");
            d_createLayer = Load<Type_xrCreatePassthroughLayerFB>(instance, "xrCreatePassthroughLayerFB");
            d_destroyLayer = Load<Type_xrDestroyPassthroughLayerFB>(instance, "xrDestroyPassthroughLayerFB");
            d_resumeLayer = Load<Type_xrPassthroughLayerResumeFB>(instance, "xrPassthroughLayerResumeFB");
            d_pauseLayer = Load<Type_xrPassthroughLayerPauseFB>(instance, "xrPassthroughLayerPauseFB");
            return base.OnInstanceCreate(instance);
        }

        static T Load<T>(ulong instance, string name) where T : Delegate
        {
            if (d_getProc.Invoke(instance, name, out IntPtr p) == 0 && p != IntPtr.Zero)
            {
                return Marshal.GetDelegateForFunctionPointer<T>(p);
            }
            BasisDebug.LogError($"{Tag} Failed to resolve {name}", BasisDebug.LogTag.Device);
            return null;
        }

        protected override void OnSessionCreate(ulong session)
        {
            s_Session = session;
        }

        protected override void OnSessionStateChange(int oldState, int newState)
        {
            if (newState == XR_SESSION_STATE_STOPPING)
            {
                s_SessionStopped = true;
            }
            if (newState != XR_SESSION_STATE_FOCUSED)
            {
                return;
            }
            if (s_SessionStopped)
            {
                s_SessionStopped = false;
                BasisDebug.Log($"{Tag} Session focused again after a stop, re-applying display settings.", BasisDebug.LogTag.Device);
                BasisDeviceManagement.RaiseXRSessionResumed();
            }
            if (!IsSupported)
            {
                return;
            }
            if (!s_LayerCreated)
            {
                CreatePassthrough();
            }
            else
            {
                BasisPassthroughController.NotifyRuntimeReady();
            }
        }

        static bool CreateHandles(out ulong passthrough, out ulong layer)
        {
            passthrough = 0;
            layer = 0;
            if (d_createPassthrough == null || d_createLayer == null)
            {
                BasisDebug.LogError($"{Tag} Cannot create passthrough, function pointers missing.", BasisDebug.LogTag.Device);
                return false;
            }

            XrPassthroughCreateInfoFB createInfo = new XrPassthroughCreateInfoFB
            {
                type = XR_TYPE_PASSTHROUGH_CREATE_INFO_FB,
                next = IntPtr.Zero,
                flags = 0,
            };
            int r = d_createPassthrough.Invoke(s_Session, ref createInfo, out passthrough);
            if (r != 0)
            {
                BasisDebug.LogError($"{Tag} xrCreatePassthroughFB failed: {r}", BasisDebug.LogTag.Device);
                passthrough = 0;
                return false;
            }

            XrPassthroughLayerCreateInfoFB layerInfo = new XrPassthroughLayerCreateInfoFB
            {
                type = XR_TYPE_PASSTHROUGH_LAYER_CREATE_INFO_FB,
                next = IntPtr.Zero,
                passthrough = passthrough,
                flags = 0,
                purpose = XR_PASSTHROUGH_LAYER_PURPOSE_RECONSTRUCTION_FB,
            };
            r = d_createLayer.Invoke(s_Session, ref layerInfo, out layer);
            if (r != 0)
            {
                BasisDebug.LogError($"{Tag} xrCreatePassthroughLayerFB failed: {r}", BasisDebug.LogTag.Device);
                d_destroyPassthrough?.Invoke(passthrough);
                passthrough = 0;
                layer = 0;
                return false;
            }
            return true;
        }

        static void CreatePassthrough()
        {
            if (!CreateHandles(out s_Passthrough, out s_Layer))
            {
                return;
            }

            XrCompositionLayerPassthroughFB underlay = new XrCompositionLayerPassthroughFB
            {
                type = XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_FB,
                next = IntPtr.Zero,
                flags = 0,
                space = 0,
                layerHandle = s_Layer,
            };
            if (s_UnderlayPtr == IntPtr.Zero)
            {
                s_UnderlayPtr = Marshal.AllocHGlobal(Marshal.SizeOf<XrCompositionLayerPassthroughFB>());
            }
            Marshal.StructureToPtr(underlay, s_UnderlayPtr, false);
            if (s_FrameEndInfoPtr == IntPtr.Zero)
            {
                s_FrameEndInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<XrFrameEndInfo>());
            }
            EnsureLayersCapacity(8);

            s_LayerCreated = true;
            s_RetryArmed = true;
            BasisDebug.Log($"{Tag} Passthrough created (handle={s_Passthrough}, layer={s_Layer}).", BasisDebug.LogTag.Device);
            BasisPassthroughController.NotifyRuntimeReady();
        }

        static void Recreate()
        {
            if (!IsSupported || !s_LayerCreated || s_Session == 0)
            {
                return;
            }
            if (!CreateHandles(out ulong passthrough, out ulong layer))
            {
                s_Inject = false;
                return;
            }
            RetireNow();
            s_RetiredPassthrough = s_Passthrough;
            s_RetiredLayer = s_Layer;
            s_RetireTicks = RETIRE_TICKS;
            s_Passthrough = passthrough;
            s_Layer = layer;
            Marshal.WriteInt64(s_UnderlayPtr, UNDERLAY_LAYER_HANDLE_OFFSET, (long)layer);
            BasisDebug.Log($"{Tag} Passthrough recreated (handle={s_Passthrough}, layer={s_Layer}).", BasisDebug.LogTag.Device);
            BasisPassthroughController.NotifyRuntimeReady();
        }

        static void RetireNow()
        {
            if (s_RetiredLayer != 0)
            {
                d_destroyLayer?.Invoke(s_RetiredLayer);
            }
            if (s_RetiredPassthrough != 0)
            {
                d_destroyPassthrough?.Invoke(s_RetiredPassthrough);
            }
            s_RetiredLayer = 0;
            s_RetiredPassthrough = 0;
            s_RetireTicks = 0;
        }

        public static void MainThreadTick()
        {
            if (s_RetireTicks > 0 && --s_RetireTicks == 0)
            {
                RetireNow();
            }
            if (s_ReinitRequested)
            {
                s_ReinitRequested = false;
                s_RestoredNotify = false;
                BasisDebug.Log($"{Tag} Runtime requested passthrough re-initialisation.", BasisDebug.LogTag.Device);
                Recreate();
            }
            else if (s_RestoredNotify)
            {
                s_RestoredNotify = false;
                BasisPassthroughController.NotifyRuntimeReady();
            }
        }

        static void EnsureLayersCapacity(int count)
        {
            if (s_LayersPtr != IntPtr.Zero && count <= s_LayersCapacity)
            {
                return;
            }
            if (s_LayersPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(s_LayersPtr);
            }
            s_LayersCapacity = Mathf.Max(count, 8);
            s_LayersPtr = Marshal.AllocHGlobal(IntPtr.Size * s_LayersCapacity);
        }

        /// <summary>Starts/resumes or pauses passthrough and toggles the per-frame underlay submission.</summary>
        public static void SetActive(bool on)
        {
            if (!IsSupported || !s_LayerCreated)
            {
                s_Inject = false;
                BasisDebug.Log($"{Tag} SetActive({on}) ignored, IsSupported={IsSupported}, layerCreated={s_LayerCreated}.", BasisDebug.LogTag.Device);
                return;
            }
            if (on)
            {
                int startResult = d_startPassthrough?.Invoke(s_Passthrough) ?? -1;
                int resumeResult = d_resumeLayer?.Invoke(s_Layer) ?? -1;
                s_Inject = startResult == 0;
                BasisDebug.Log($"{Tag} SetActive(true): xrPassthroughStartFB={startResult}, xrPassthroughLayerResumeFB={resumeResult} (0 == success, inject={s_Inject}).", BasisDebug.LogTag.Device);
                if (s_Inject)
                {
                    s_RetryArmed = true;
                }
                else if (s_RetryArmed)
                {
                    s_RetryArmed = false;
                    BasisDebug.LogWarning($"{Tag} Start failed, recreating passthrough once.", BasisDebug.LogTag.Device);
                    Recreate();
                }
            }
            else
            {
                s_Inject = false;
                d_pauseLayer?.Invoke(s_Layer);
                d_pausePassthrough?.Invoke(s_Passthrough);
                BasisDebug.Log($"{Tag} SetActive(false): passthrough paused.", BasisDebug.LogTag.Device);
            }
        }

        [MonoPInvokeCallback(typeof(Type_xrEndFrame))]
        static int HookEndFrame(ulong session, IntPtr frameEndInfo)
        {
            try
            {
                return d_originalEndFrame != null ? d_originalEndFrame.Invoke(session, InjectUnderlay(frameEndInfo)) : 0;
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"{Tag} xrEndFrame hook failed: {e}", BasisDebug.LogTag.Device);
                return XR_ERROR_RUNTIME_FAILURE;
            }
        }

        static IntPtr InjectUnderlay(IntPtr frameEndInfo)
        {
            try
            {
                if (!s_LoggedFirstFrame)
                {
                    s_LoggedFirstFrame = true;
                    BasisDebug.Log($"{Tag} xrEndFrame hook is being called by the runtime.", BasisDebug.LogTag.Device);
                }
                if (!s_Inject || !s_LayerCreated || frameEndInfo == IntPtr.Zero)
                {
                    return frameEndInfo;
                }

                XrFrameEndInfo info = Marshal.PtrToStructure<XrFrameEndInfo>(frameEndInfo);
                uint count = info.layerCount;
                EnsureLayersCapacity((int)count + 1);

                Marshal.WriteIntPtr(s_LayersPtr, 0, s_UnderlayPtr);
                for (int i = 0; i < count; i++)
                {
                    IntPtr layerPtr = info.layers != IntPtr.Zero ? Marshal.ReadIntPtr(info.layers, i * IntPtr.Size) : IntPtr.Zero;
                    if (layerPtr != IntPtr.Zero && (uint)Marshal.ReadInt32(layerPtr, 0) == XR_TYPE_COMPOSITION_LAYER_PROJECTION)
                    {
                        long flags = Marshal.ReadInt64(layerPtr, LAYER_FLAGS_OFFSET);
                        Marshal.WriteInt64(layerPtr, LAYER_FLAGS_OFFSET, flags | PROJECTION_ALPHA_FLAGS);
                    }
                    Marshal.WriteIntPtr(s_LayersPtr, (i + 1) * IntPtr.Size, layerPtr);
                }

                if ((s_FrameLog++ % 300) == 0)
                {
                    BasisDebug.Log($"{Tag} EndFrame inject: {count} app layers -> submitting {count + 1} (passthrough underlay at index 0).", BasisDebug.LogTag.Device);
                }

                info.layerCount = count + 1;
                info.layers = s_LayersPtr;
                Marshal.StructureToPtr(info, s_FrameEndInfoPtr, false);
                return s_FrameEndInfoPtr;
            }
            catch (Exception e)
            {
                s_Inject = false;
                BasisDebug.LogError($"{Tag} Underlay injection failed, submitting the frame without passthrough: {e}", BasisDebug.LogTag.Device);
                return frameEndInfo;
            }
        }

        protected override void OnSessionDestroy(ulong session)
        {
            SetActive(false);
            RetireNow();
            s_ReinitRequested = false;
            s_RestoredNotify = false;
            s_SessionStopped = true;
            if (s_LayerCreated)
            {
                d_destroyLayer?.Invoke(s_Layer);
                d_destroyPassthrough?.Invoke(s_Passthrough);
            }
            s_LayerCreated = false;
            s_Layer = 0;
            s_Passthrough = 0;
            s_Session = 0;
            FreeBuffers();
            BasisPassthroughController.NotifyRuntimeLost();
        }

        protected override void OnInstanceDestroy(ulong instance)
        {
            IsSupported = false;
        }

        static void FreeBuffers()
        {
            if (s_UnderlayPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(s_UnderlayPtr);
                s_UnderlayPtr = IntPtr.Zero;
            }
            if (s_FrameEndInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(s_FrameEndInfoPtr);
                s_FrameEndInfoPtr = IntPtr.Zero;
            }
            if (s_LayersPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(s_LayersPtr);
                s_LayersPtr = IntPtr.Zero;
                s_LayersCapacity = 0;
            }
        }
    }
}
