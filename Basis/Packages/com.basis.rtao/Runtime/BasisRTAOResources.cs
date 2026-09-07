using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Rendering.RTAO
{
    [CreateAssetMenu(menuName = "Basis/Rendering/Ray Traced Ambient Occlusion Resources", fileName = "BasisRTAOResources")]
    public sealed class BasisRTAOResources : ScriptableObject
    {
        [SerializeField] private Shader prepassShader;
        [SerializeField] private Shader compositeShader;
        [SerializeField] private ComputeShader denoiseShader;
        [SerializeField] private ComputeShader screenSpaceShader;
        [SerializeField] private RayTracingShader hardwareTraceShader;
        [SerializeField] private ComputeShader computeTraceShader;

        public Shader PrepassShader => prepassShader;
        public Shader CompositeShader => compositeShader;
        public ComputeShader DenoiseShader => denoiseShader;
        public ComputeShader ScreenSpaceShader => screenSpaceShader;
        public RayTracingShader HardwareTraceShader => hardwareTraceShader;
        public ComputeShader ComputeTraceShader => computeTraceShader;

        public bool IsComplete(BasisRTAOBackend backend)
        {
            return string.IsNullOrEmpty(DescribeMissing(backend));
        }

        public string DescribeMissing(BasisRTAOBackend backend)
        {
            string missing = string.Empty;
            if (prepassShader == null)
                missing += " prepassShader";
            if (compositeShader == null)
                missing += " compositeShader";
            if (denoiseShader == null)
                missing += " denoiseShader";
            if (backend == BasisRTAOBackend.Hardware && hardwareTraceShader == null)
                missing += " hardwareTraceShader";
            if (backend == BasisRTAOBackend.ComputeBvh && computeTraceShader == null)
                missing += " computeTraceShader";
            if (backend == BasisRTAOBackend.ScreenSpace && screenSpaceShader == null)
                missing += " screenSpaceShader";
            return missing.Trim();
        }

#if UNITY_EDITOR
        public void PopulateFromPackage()
        {
            const string root = "Packages/com.basis.rtao/Shaders/";
            if (prepassShader == null)
                prepassShader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(root + "BasisRTAOPrepass.shader");
            if (compositeShader == null)
                compositeShader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(root + "BasisRTAOComposite.shader");
            if (denoiseShader == null)
                denoiseShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "BasisRTAODenoise.compute");
            if (screenSpaceShader == null)
                screenSpaceShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "BasisRTAOScreenSpace.compute");
            if (hardwareTraceShader == null)
                hardwareTraceShader = UnityEditor.AssetDatabase.LoadAssetAtPath<RayTracingShader>(root + "BasisRTAO.raytrace");
            if (computeTraceShader == null)
                computeTraceShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "BasisRTAO.compute");
        }

        public bool HasEveryReference()
        {
            return prepassShader != null && compositeShader != null && denoiseShader != null
                && screenSpaceShader != null && hardwareTraceShader != null && computeTraceShader != null;
        }
#endif
    }
}
