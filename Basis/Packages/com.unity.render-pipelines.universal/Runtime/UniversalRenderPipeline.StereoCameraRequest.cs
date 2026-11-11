using System;
using System.Reflection;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Scripting.LifecycleManagement;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // Basis addition, not part of stock URP.
    //
    // Renders one camera into a two-slice texture array with single-pass instanced stereo, using two
    // caller-supplied views. It reuses the XR code path of the renderer (stereo constants, texture
    // array targets, instancing) without going through the XR display, so the two views can be
    // anything: Basis mirrors pass the two reflected eyes.
    //
    // XRView and XRPass.AddView are internal to the core package and core does not grant this assembly
    // access. The bridge below reaches them once through reflection, checks that the local copy of the
    // view layout still matches core's, and reports the request as unsupported when anything differs,
    // so callers fall back instead of rendering garbage after a core update.
    public sealed partial class UniversalRenderPipeline
    {
        /// <summary>
        /// Render request for one camera drawn as two views into a two-slice texture array.
        /// Submit with <see cref="RenderPipeline.SubmitRenderRequest{RequestData}"/> while
        /// <see cref="SupportsStereoCameraRequest"/> is true.
        /// </summary>
        public class StereoCameraRequest
        {
            /// <summary>Single-sampled Tex2DArray render texture with two slices and a depth buffer.</summary>
            public RenderTexture destination;
            /// <summary>World to camera matrix of slice 0.</summary>
            public Matrix4x4 viewLeft;
            /// <summary>World to camera matrix of slice 1.</summary>
            public Matrix4x4 viewRight;
            /// <summary>Projection of slice 0. May carry an oblique near plane.</summary>
            public Matrix4x4 projectionLeft;
            /// <summary>Projection of slice 1. May carry an oblique near plane.</summary>
            public Matrix4x4 projectionRight;
            /// <summary>Projection the skybox uses for slice 0, without any oblique near plane.</summary>
            public Matrix4x4 skyboxProjectionLeft;
            /// <summary>Projection the skybox uses for slice 1, without any oblique near plane.</summary>
            public Matrix4x4 skyboxProjectionRight;
            /// <summary>Sample count of the intermediate attachments that resolve into the destination.</summary>
            public int msaaSamples = 1;

            internal RenderTexture createInfoTarget;
            internal XRPassCreateInfo createInfo;
        }

        /// <summary>
        /// True when <see cref="StereoCameraRequest"/> can render: an XR display is running with single-pass
        /// instancing and the bridge into the core XR pass is intact. Culling uses the camera's own
        /// culling matrix, which has to cover both views.
        /// </summary>
        public static bool SupportsStereoCameraRequest
        {
            get
            {
#if ENABLE_VR && ENABLE_XR_MODULE
                return asset != null && XRSystem.displayActive && TextureXR.slices >= 2 && !SystemInfo.supportsMultiview && StereoRequestBridge.Ready;
#else
                return false;
#endif
            }
        }

#if ENABLE_VR && ENABLE_XR_MODULE
        // Field for field copy of UnityEngine.Experimental.Rendering.XRView. Order and types must match.
        struct StereoRequestView
        {
            public Matrix4x4 projMatrix;
            public Matrix4x4 viewMatrix;
            public Matrix4x4 prevViewMatrix;
            public Rect viewport;
            public Mesh occlusionMesh;
            public Mesh visibleMesh;
            public int textureArraySlice;
            public Vector2 eyeCenterUV;
            public bool isPrevViewMatrixValid;
        }

        delegate void StereoRequestAddView(XRPass pass, ref StereoRequestView view);

        static class StereoRequestViewBridge<TView> where TView : struct
        {
            [NoAutoStaticsCleanup] public static Action<XRPass, TView> addView;

            public static void Add(XRPass pass, ref StereoRequestView view)
            {
                addView(pass, UnsafeUtility.As<StereoRequestView, TView>(ref view));
            }
        }

        static class StereoRequestBridge
        {
            const BindingFlags k_Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            [NoAutoStaticsCleanup] static bool s_Initialized;
            [NoAutoStaticsCleanup] static bool s_Ready;
            [NoAutoStaticsCleanup] static StereoRequestAddView s_AddView;
            [NoAutoStaticsCleanup] static FieldInfo s_RenderTarget;
            [NoAutoStaticsCleanup] static FieldInfo s_RenderTargetDesc;
            [NoAutoStaticsCleanup] static FieldInfo s_ScaledWidth;
            [NoAutoStaticsCleanup] static FieldInfo s_ScaledHeight;
            [NoAutoStaticsCleanup] static FieldInfo s_LayoutType;
            [NoAutoStaticsCleanup] static FieldInfo s_UVScales;
            [NoAutoStaticsCleanup] static FieldInfo s_UVOffsets;

            public static bool Ready
            {
                get
                {
                    if (!s_Initialized)
                    {
                        s_Initialized = true;
                        try
                        {
                            s_Ready = Initialize();
                        }
                        catch (Exception e)
                        {
                            s_Ready = false;
                            Debug.LogWarning($"StereoCameraRequest is unavailable, the core XR pass could not be bridged: {e.Message}");
                        }
                    }
                    return s_Ready;
                }
            }

            static bool Initialize()
            {
                Type viewType = typeof(XRPass).Assembly.GetType("UnityEngine.Experimental.Rendering.XRView");
                MethodInfo addView = typeof(XRPass).GetMethod("AddView", k_Instance);
                if (viewType == null || addView == null || !LayoutMatches(viewType, typeof(StereoRequestView)))
                {
                    Debug.LogWarning("StereoCameraRequest is unavailable, the core XRView layout changed.");
                    return false;
                }

                Type createInfo = typeof(XRPassCreateInfo);
                s_RenderTarget = createInfo.GetField("renderTarget", k_Instance);
                s_RenderTargetDesc = createInfo.GetField("renderTargetDesc", k_Instance);
                s_ScaledWidth = createInfo.GetField("renderTargetScaledWidth", k_Instance);
                s_ScaledHeight = createInfo.GetField("renderTargetScaledHeight", k_Instance);
                s_LayoutType = createInfo.GetField("xrLayoutType", k_Instance);
                s_UVScales = createInfo.GetField("uvScales", k_Instance);
                s_UVOffsets = createInfo.GetField("uvOffsets", k_Instance);
                if (s_RenderTarget == null || s_RenderTargetDesc == null || s_ScaledWidth == null || s_ScaledHeight == null
                    || s_LayoutType == null || s_UVScales == null || s_UVOffsets == null)
                {
                    Debug.LogWarning("StereoCameraRequest is unavailable, the core XRPassCreateInfo fields changed.");
                    return false;
                }

                Type bridge = typeof(StereoRequestViewBridge<>).MakeGenericType(viewType);
                Type addViewDelegate = typeof(Action<,>).MakeGenericType(typeof(XRPass), viewType);
                bridge.GetField("addView").SetValue(null, Delegate.CreateDelegate(addViewDelegate, addView));
                s_AddView = (StereoRequestAddView)Delegate.CreateDelegate(typeof(StereoRequestAddView), bridge.GetMethod("Add"));
                return true;
            }

            static bool LayoutMatches(Type real, Type copy)
            {
                if (UnsafeUtility.SizeOf(real) != UnsafeUtility.SizeOf(copy))
                    return false;

                FieldInfo[] realFields = real.GetFields(k_Instance);
                if (realFields.Length != copy.GetFields(k_Instance).Length)
                    return false;

                for (int i = 0; i < realFields.Length; i++)
                {
                    FieldInfo copyField = copy.GetField(realFields[i].Name, k_Instance);
                    if (copyField == null || copyField.FieldType != realFields[i].FieldType
                        || UnsafeUtility.GetFieldOffset(copyField) != UnsafeUtility.GetFieldOffset(realFields[i]))
                        return false;
                }
                return true;
            }

            public static XRPassCreateInfo CreateInfo(RenderTexture destination)
            {
                RenderTextureDescriptor desc = destination.descriptor;
                object boxed = new XRPassCreateInfo();
                s_RenderTarget.SetValue(boxed, new RenderTargetIdentifier(destination, 0, CubemapFace.Unknown, -1));
                s_RenderTargetDesc.SetValue(boxed, desc);
                s_ScaledWidth.SetValue(boxed, desc.width);
                s_ScaledHeight.SetValue(boxed, desc.height);
                s_LayoutType.SetValue(boxed, XRLayoutType.SinglePassStereo);
                s_UVScales.SetValue(boxed, Vector4.one);
                s_UVOffsets.SetValue(boxed, Vector4.zero);
                return (XRPassCreateInfo)boxed;
            }

            public static void AddView(XRPass pass, in Matrix4x4 projection, in Matrix4x4 view, Rect viewport, int slice)
            {
                FrustumPlanes planes = projection.decomposeProjection;
                float left = Math.Abs(planes.left), right = Math.Abs(planes.right), top = Math.Abs(planes.top), bottom = Math.Abs(planes.bottom);
                StereoRequestView data = new StereoRequestView
                {
                    projMatrix = projection,
                    viewMatrix = view,
                    prevViewMatrix = view,
                    viewport = viewport,
                    textureArraySlice = slice,
                    eyeCenterUV = new Vector2(left / (right + left), top / (top + bottom)),
                    isPrevViewMatrixValid = false,
                };
                s_AddView(pass, ref data);
            }
        }
#endif

        static void RenderStereoCameraRequest(ScriptableRenderContext context, Camera camera, StereoCameraRequest request)
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            RenderTexture destination = request.destination;
            if (destination == null || destination.dimension != TextureDimension.Tex2DArray || destination.volumeDepth != 2)
            {
                Debug.LogError("StereoCameraRequest needs a Tex2DArray destination with two slices.");
                return;
            }
            if (!SupportsStereoCameraRequest)
                return;

            if (!ReferenceEquals(request.createInfoTarget, destination))
            {
                request.createInfo = StereoRequestBridge.CreateInfo(destination);
                request.createInfoTarget = destination;
            }

            RenderTexture originalTarget = camera.targetTexture;
            camera.targetTexture = destination;
            destination.IncrementUpdateCount();

            XRPassUniversal pass = null;
            try
            {
                using (UnityEngine.Pool.ListPool<Camera>.Get(out var cameras))
                {
                    cameras.Add(camera);

                    using (new ContextRenderingScope(context, cameras))
                    using (new CameraRenderingScope(context, camera))
                    {
                        camera.gameObject.TryGetComponent<UniversalAdditionalCameraData>(out var additionalCameraData);
                        if (additionalCameraData == null || additionalCameraData.renderType == CameraRenderType.Base)
                        {
                            pass = (XRPassUniversal)XRPassUniversal.Create(request.createInfo);
                            pass.cullFromCamera = true;
                            pass.hasSkyboxProjections = true;
                            pass.skyboxProjection0 = request.skyboxProjectionLeft;
                            pass.skyboxProjection1 = request.skyboxProjectionRight;

                            Rect viewport = new Rect(0, 0, destination.width, destination.height);
                            StereoRequestBridge.AddView(pass, request.projectionLeft, request.viewLeft, viewport, 0);
                            StereoRequestBridge.AddView(pass, request.projectionRight, request.viewRight, viewport, 1);

                            var renderer = GetRenderer(camera, additionalCameraData);
                            var cameraData = CreateCameraData(renderer.frameData, camera, additionalCameraData);
                            cameraData.xr = pass;
                            cameraData.xrRendering = false;
                            UpdateCameraData(cameraData, pass);

                            if (!(renderer is UniversalRenderer { useTileOnlyMode: true }))
                            {
                                int msaaSamples = 1;
                                if (camera.allowMSAA && renderer.supportedRenderingFeatures.msaa && request.msaaSamples > 1)
                                {
                                    RenderTextureDescriptor msaaDesc = cameraData.cameraTargetDescriptor;
                                    msaaDesc.msaaSamples = request.msaaSamples;
                                    msaaSamples = SystemInfo.GetRenderTextureSupportedMSAASampleCount(msaaDesc);
                                }
                                cameraData.cameraTargetDescriptor.msaaSamples = msaaSamples;
                            }
                            cameraData.cameraTargetDescriptor.useDynamicScale = false;

                            InitializeAdditionalCameraData(camera, additionalCameraData, true, true, cameraData);
                            RenderSingleCamera(context, cameraData);
                        }
                    }
                }
            }
            finally
            {
                pass?.Release();
                camera.targetTexture = originalTarget;
            }
#endif
        }
    }
}
