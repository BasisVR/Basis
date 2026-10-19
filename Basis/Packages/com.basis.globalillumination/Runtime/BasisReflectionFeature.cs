using Unity.Scripting.LifecycleManagement;
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[AutoStaticsCleanup]
[DisallowMultipleRendererFeature("Basis Reflections")]
[Tooltip("Reflections traced against the depth buffer, or against the scene itself on a ray tracing GPU, and published for URP's lit shaders to use in place of the reflection probe.")]
public sealed partial class BasisReflectionFeature : ScriptableRendererFeature
{
    public static Func<Camera, bool> CameraFilter;

    [SerializeField, HideInInspector] private Shader m_Shader;
    [SerializeField, HideInInspector] private Shader m_RayStagesShader;
    [SerializeField, HideInInspector] private RayTracingShader m_RayTraceShader;
    [SerializeField, HideInInspector] private ComputeShader m_RayTraceCompute;
    [SerializeField] private bool m_RayTracingComputeFallback = false;
    [SerializeField] private bool m_ReflectionProbes = false;
    [SerializeField] private bool m_Mirrors = true;
    [SerializeField] private bool m_RenderingDebugger = false;
    [SerializeField] private bool m_NormalsPrepass = false;

    private Material m_Material;
    private Material m_RayStagesMaterial;
    private BasisGlobalIlluminationPass m_DepthPyramid;
    private BasisGlobalIlluminationPass.SpecularPass m_SpecularPass;
    private BasisGlobalIlluminationPass.SpecularColorCapturePass m_ColorCapturePass;

    public bool ReflectionProbes { get { return m_ReflectionProbes; } set { m_ReflectionProbes = value; } }
    public bool Mirrors { get { return m_Mirrors; } set { m_Mirrors = value; } }
    public bool RenderingDebugger { get { return m_RenderingDebugger; } set { m_RenderingDebugger = value; } }
    public bool NormalsPrepass { get { return m_NormalsPrepass; } set { m_NormalsPrepass = value; } }
    public bool RayTracingComputeFallback { get { return m_RayTracingComputeFallback; } set { m_RayTracingComputeFallback = value; } }
    public Material Material => m_Material;

    public bool RayTracingAvailable
    {
        get
        {
            if (m_RayStagesMaterial == null) { return false; }
            if (BasisGlobalIlluminationRayContext.HardwareSupported) { return m_RayTraceShader != null; }
            return m_RayTracingComputeFallback && BasisGlobalIlluminationRayContext.ComputeSupported && m_RayTraceCompute != null;
        }
    }

    public override void Create()
    {
        BasisGlobalIlluminationFeature.ResolveShaders(this, ref m_Shader, ref m_RayStagesShader, ref m_RayTraceShader, ref m_RayTraceCompute);
        CoreUtils.Destroy(m_Material);
        CoreUtils.Destroy(m_RayStagesMaterial);
        m_Material = m_Shader != null ? CoreUtils.CreateEngineMaterial(m_Shader) : null;
        m_RayStagesMaterial = m_RayStagesShader != null ? CoreUtils.CreateEngineMaterial(m_RayStagesShader) : null;
        m_DepthPyramid = new BasisGlobalIlluminationPass(m_Material);
        m_SpecularPass = new BasisGlobalIlluminationPass.SpecularPass();
        m_ColorCapturePass = new BasisGlobalIlluminationPass.SpecularColorCapturePass();
    }

    public bool ShouldRender(Camera camera, CameraType cameraType, bool postProcessEnabled)
    {
        return isActive && BasisGlobalIlluminationFeature.ShouldRender(camera, cameraType, postProcessEnabled, m_ReflectionProbes, m_Mirrors, m_RenderingDebugger, CameraFilter);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_SpecularPass == null || m_Material == null) { return; }

        ref CameraData cameraData = ref renderingData.cameraData;
        if (!ShouldRender(cameraData.camera, cameraData.cameraType, cameraData.postProcessEnabled)) { return; }

        BasisGlobalIlluminationSettings settings = BasisGlobalIlluminationSettings.Current;
        if (!settings.SpecularActive()) { return; }

        bool rayTracingAvailable = RayTracingAvailable;
        bool wantsNormals = m_NormalsPrepass && settings.normalSource == BasisGlobalIlluminationNormalSource.NormalsTexture;

        // Reflections have to be published before the opaque draws that consume them, so they are a separate
        // pass at a separate injection point rather than a stage of the diffuse gather. See SpecularPass.
        m_SpecularPass.Setup(m_Material, m_RayStagesMaterial, m_RayTraceShader, m_RayTraceCompute, m_RayTracingComputeFallback, rayTracingAvailable, m_DepthPyramid);
        m_SpecularPass.UseNormalsTexture = wantsNormals;
        ScriptableRenderPassInput inputs = ScriptableRenderPassInput.Depth;
        if (wantsNormals) { inputs |= ScriptableRenderPassInput.Normal; }
        m_SpecularPass.ConfigureInput(inputs);
        renderer.EnqueuePass(m_SpecularPass);

        // The screen space backend reads the previous frame's colour, so a pass at the other end of the
        // frame has to have written it. The ray traced backend relights its hits instead and the copy
        // would be a dead cost there, which is why this is enqueued per backend rather than always.
        if (m_ColorCapturePass != null && BasisGlobalIlluminationPass.SpecularPass.ScreenSpaceReflections(settings, rayTracingAvailable))
        {
            m_ColorCapturePass.Setup(m_Material);
            renderer.EnqueuePass(m_ColorCapturePass);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        m_DepthPyramid?.Dispose();
        m_DepthPyramid = null;
        m_SpecularPass = null;
        m_ColorCapturePass = null;
        CoreUtils.Destroy(m_Material);
        m_Material = null;
        CoreUtils.Destroy(m_RayStagesMaterial);
        m_RayStagesMaterial = null;
    }
}
