using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[DisallowMultipleRendererFeature("Basis Global Illumination")]
[Tooltip("Screen space global illumination: rays are marched through the depth buffer and the camera colour at the hit is gathered as indirect light, so no GBuffer or albedo is required.")]
public sealed class BasisGlobalIlluminationFeature : ScriptableRendererFeature
{
    public const string ShaderName = "Hidden/Basis/GlobalIllumination";
    public const string RayStagesShaderName = "Hidden/Basis/GlobalIlluminationRT";
    public const string ShaderRoot = "Packages/com.basis.globalillumination/Shaders/";

    public static Func<Camera, bool> CameraFilter;
    public static bool KeepRenderingWithDebugger;

    [SerializeField, HideInInspector] private Shader m_Shader;
    [SerializeField, HideInInspector] private Shader m_RayStagesShader;
    [SerializeField, HideInInspector] private RayTracingShader m_RayTraceShader;
    [SerializeField, HideInInspector] private ComputeShader m_RayTraceCompute;
    [Tooltip("Run the ray traced mode on the compute ray tracing backend when the GPU has no hardware ray tracing. It is a software BVH traversal, so it is much slower - off means the mode falls back to screen space instead.")]
    [SerializeField] private bool m_RayTracingComputeFallback = false;
    [Tooltip("Render in reflection probe captures. Realtime probes pay for the effect once per face.")]
    [SerializeField] private bool m_ReflectionProbes = false;
    [Tooltip("Render in mirror reflections. Off leaves a mirror showing the room without any bounce light, which does not match what the same room looks like directly. Each mirror camera pays for its own gather, so a world with a large mirror pays roughly twice.")]
    [SerializeField] private bool m_Mirrors = true;
    [Tooltip("Render while a Rendering Debugger lighting view is active. Off keeps the individual lighting previews clean.")]
    [SerializeField] private bool m_RenderingDebugger = false;
    [Tooltip("Request URP's depth-normals prepass so the volume's Normals Texture source has data. Costs an extra prepass, and surfaces whose shader has no DepthNormals pass still read nothing - the reconstructed source needs none of this.")]
    [SerializeField] private bool m_NormalsPrepass = false;

    private Material m_Material;
    private Material m_RayStagesMaterial;
    private BasisGlobalIlluminationPass m_Pass;
    private BasisGlobalIlluminationPass.SpecularPass m_SpecularPass;
    private BasisGlobalIlluminationDebugView m_DebugView;

    public bool ReflectionProbes { get { return m_ReflectionProbes; } set { m_ReflectionProbes = value; } }
    public bool Mirrors { get { return m_Mirrors; } set { m_Mirrors = value; } }
    public bool RenderingDebugger { get { return m_RenderingDebugger; } set { m_RenderingDebugger = value; } }
    public bool NormalsPrepass { get { return m_NormalsPrepass; } set { m_NormalsPrepass = value; } }
    public bool RayTracingComputeFallback { get { return m_RayTracingComputeFallback; } set { m_RayTracingComputeFallback = value; } }
    public Material Material => m_Material;
    public Material RayStagesMaterial => m_RayStagesMaterial;
    public BasisGlobalIlluminationPass Pass => m_Pass;

    /// <summary>Whether this GPU can run the ray traced mode at all. False falls the volume back to screen space.</summary>
    public bool RayTracingAvailable
    {
        get
        {
            if (m_RayStagesMaterial == null) { return false; }
            if (BasisGlobalIlluminationRayContext.HardwareSupported) { return m_RayTraceShader != null; }
            return m_RayTracingComputeFallback && BasisGlobalIlluminationRayContext.ComputeSupported && m_RayTraceCompute != null;
        }
    }

    /// <summary>
    /// Which visualisation the pass draws instead of the effect.
    ///
    /// Every view other than None makes the pass REPLACE the camera image rather than composite into it,
    /// and the replacement is dominated by terms the player's settings do not scale. Intensity, saturation,
    /// tint, the fallbacks and the emitters then move the frame by a few percent of a flat grey, which is
    /// indistinguishable from those settings being broken - and is exactly how it gets reported. Leaving it
    /// on is easy because the result still looks like a plausibly lit room, so it says so once, loudly.
    /// </summary>
    public BasisGlobalIlluminationDebugView DebugView
    {
        get { return m_DebugView; }
        set
        {
            if (value != m_DebugView)
            {
                if (value != BasisGlobalIlluminationDebugView.None)
                {
                    Debug.LogWarning($"[BasisGI] Debug view '{value}' is on. The global illumination pass now replaces the camera image with a visualisation instead of compositing into it, so intensity, saturation, tint, the fallbacks and the emitters will all look like they do nothing until it is set back to Off.");
                }
                else if (m_DebugView != BasisGlobalIlluminationDebugView.None)
                {
                    Debug.Log("[BasisGI] Debug view off; the effect is compositing into the camera image again.");
                }
            }

            m_DebugView = value;
            if (m_Pass != null) { m_Pass.DebugView = value; }
        }
    }

    public override void Create()
    {
        ResolveShader();
        m_Material = m_Shader != null ? CoreUtils.CreateEngineMaterial(m_Shader) : null;
        m_RayStagesMaterial = m_RayStagesShader != null ? CoreUtils.CreateEngineMaterial(m_RayStagesShader) : null;
        m_Pass = new BasisGlobalIlluminationPass(m_Material);
        m_Pass.SetRayTracing(m_RayStagesMaterial, m_RayTraceShader, m_RayTraceCompute, m_RayTracingComputeFallback);
        m_Pass.DebugView = m_DebugView;
        m_SpecularPass = new BasisGlobalIlluminationPass.SpecularPass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_Pass == null || m_Material == null) { return; }

        ref CameraData cameraData = ref renderingData.cameraData;
        if (!ShouldRender(cameraData.camera, cameraData.cameraType, cameraData.postProcessEnabled)) { return; }

        BasisGlobalIlluminationSettings settings = BasisGlobalIlluminationSettings.Current;
        if (!settings.IsActive()) { return; }

        // Reflections have to be published before the opaque draws that consume them, so they are a separate
        // pass at a separate injection point rather than another stage of the one below. See SpecularPass.
        if (settings.SpecularActive() && m_SpecularPass != null)
        {
            m_SpecularPass.Setup(m_Material, m_RayStagesMaterial, m_RayTraceShader, m_RayTraceCompute, m_RayTracingComputeFallback, RayTracingAvailable);
            m_SpecularPass.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(m_SpecularPass);
        }

        if (!settings.DiffuseActive()) { return; }

        bool wantsNormals = m_NormalsPrepass && settings.normalSource == BasisGlobalIlluminationNormalSource.NormalsTexture;
        // Motion is asked for only when the temporal filter is going to reproject through it. URP renders
        // a whole extra pass to produce that texture, and a frame that will not read it should not pay for
        // one - whereas a frame that will read it must declare the need here, because a pass that is never
        // requested is never scheduled and the texture arrives invalid.
        bool wantsMotion = settings.temporalFilter && settings.motionVectors;
        ScriptableRenderPassInput inputs = ScriptableRenderPassInput.Depth;
        if (wantsNormals) { inputs |= ScriptableRenderPassInput.Normal; }
        if (wantsMotion) { inputs |= ScriptableRenderPassInput.Motion; }

        m_Pass.UseNormalsTexture = wantsNormals;
        m_Pass.UseMotionVectors = wantsMotion;
        m_Pass.DebugView = m_DebugView;
        m_Pass.SetMaterial(m_Material);
        m_Pass.SetRayTracing(m_RayStagesMaterial, m_RayTraceShader, m_RayTraceCompute, m_RayTracingComputeFallback);
        m_Pass.RayTracingAvailable = RayTracingAvailable;
        m_Pass.ConfigureInput(inputs);
        renderer.EnqueuePass(m_Pass);
    }

    /// <summary>
    /// Whether <paramref name="camera"/> is one of the cameras a mirror renders its reflection from.
    ///
    /// Read off the additional camera data rather than the camera type: a mirror is an ordinary Game
    /// camera pointed at a reflected pose, not a CameraType.Reflection, which is the type Unity uses for
    /// reflection PROBE captures. TryGetComponent rather than GetUniversalAdditionalCameraData, because
    /// that helper adds the component when it is missing and this is asked on every camera in the frame.
    /// </summary>
    public static bool IsMirrorReflection(Camera camera)
    {
        return camera != null
            && camera.TryGetComponent(out UniversalAdditionalCameraData data)
            && data.isMirrorReflectionCamera;
    }

    public bool ShouldRender(Camera camera, CameraType cameraType, bool postProcessEnabled)
    {
        if (!isActive) { return false; }
        if (!SupportsPlatform()) { return false; }
        if (cameraType == CameraType.Preview) { return false; }
        if (cameraType == CameraType.Reflection && !m_ReflectionProbes) { return false; }

        bool mirror = IsMirrorReflection(camera);
        if (mirror && !m_Mirrors) { return false; }
        // A mirror is exempt from the post processing requirement, and the exemption is the whole reason
        // mirrors have never shown a bounce. Mirrors ship with Render Post Processing OFF - it is a sensible
        // default for a camera that renders the room a second time - and this effect is not part of that
        // stack: it composites before transparents, off the depth buffer, and needs nothing the post stack
        // provides. Gating it on that toggle meant a mirror showed the room unlit next to a direct view of
        // the same room lit, and no author would have connected the two settings.
        if (!postProcessEnabled && !mirror) { return false; }
        if (!m_RenderingDebugger && !KeepRenderingWithDebugger && DebugManager.instance.isAnyDebugUIActive) { return false; }
        Func<Camera, bool> filter = CameraFilter;
        return filter == null || camera == null || filter(camera);
    }

    public static bool SupportsPlatform()
    {
        return !Application.isMobilePlatform && SystemInfo.graphicsShaderLevel >= 35;
    }

    /// <summary>
    /// Resolves the shaders the feature owns, in the editor only. Shader.Find knows nothing about ray tracing
    /// kernels so those come from the package path. Anything newly resolved dirties the renderer asset,
    /// because the serialised reference is the only thing that carries a shader into a player build.
    /// </summary>
    private void ResolveShader()
    {
#if UNITY_EDITOR
        bool resolved = false;
        if (m_Shader == null) { m_Shader = Shader.Find(ShaderName); resolved |= m_Shader != null; }
        if (m_RayStagesShader == null) { m_RayStagesShader = Shader.Find(RayStagesShaderName); resolved |= m_RayStagesShader != null; }
        if (m_RayTraceShader == null)
        {
            m_RayTraceShader = UnityEditor.AssetDatabase.LoadAssetAtPath<RayTracingShader>(ShaderRoot + "BasisGlobalIlluminationRT.raytrace");
            resolved |= m_RayTraceShader != null;
        }
        if (m_RayTraceCompute == null)
        {
            m_RayTraceCompute = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderRoot + "BasisGlobalIlluminationRT.compute");
            resolved |= m_RayTraceCompute != null;
        }
        if (resolved && !UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) { UnityEditor.EditorUtility.SetDirty(this); }
#endif
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        m_Pass?.Dispose();
        m_Pass = null;
        m_SpecularPass = null;
        CoreUtils.Destroy(m_Material);
        m_Material = null;
        CoreUtils.Destroy(m_RayStagesMaterial);
        m_RayStagesMaterial = null;
    }
}
