using System;

namespace UnityEngine.Rendering.Universal
{
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    [Categorization.CategoryInfo(Name = "R: SSR Resources", Order = 1010)]
    [Categorization.ElementInfo(Order = 0)]
    [HideInInspector]
    class ScreenSpaceReflectionPersistentResources : IRenderPipelineResources
    {
        [SerializeField] [ResourcePath("Shaders/Utils/ComputeScreenSpaceReflection.shader")]
        Shader m_Shader;

        [SerializeField] [ResourcePath("Shaders/Utils/BlitFullPrecision.shader")]
        Shader m_BlitShader;

        public Shader Shader
        {
            get => m_Shader;
            set => this.SetValueAndNotify(ref m_Shader, value);
        }

        public Shader BlitShader
        {
            get => m_BlitShader;
            set => this.SetValueAndNotify(ref m_BlitShader, value);
        }

        public bool isAvailableInPlayerBuild => true;

        [SerializeField] [HideInInspector]
        int m_Version = 0;

        /// <summary>Current version of the resource container. Used only for upgrading a project.</summary>
        public int version => m_Version;
    }
}
