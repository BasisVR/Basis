using System;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// Common global resources for mip pyramid generator (color, depth).
    /// </summary>
    [Serializable]
    [SupportedOnRenderPipeline]
    [Categorization.CategoryInfo(Name = "R: Mip Generator Resources", Order = 1000), HideInInspector]
    internal sealed class MipGenRenderPipelineRuntimeResources : IRenderPipelineResources
    {
        /// <summary>
        /// Version of the SSR Resources
        /// </summary>
        public int version => 0;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => true;

        [SerializeField, ResourcePath("Runtime/MipGen/Shaders/ColorPyramidPS.shader")]
        private Shader m_ColorPyramidPS;

        public Shader colorPyramidPS
        {
            get => m_ColorPyramidPS;
            set => this.SetValueAndNotify(ref m_ColorPyramidPS, value);
        }

        [SerializeField, ResourcePath("Runtime/MipGen/Shaders/ColorPyramid.compute")]
        public ComputeShader m_ColorPyramidCS;
        public ComputeShader colorPyramidCS
        {
            get => m_ColorPyramidCS;
            set => this.SetValueAndNotify(ref m_ColorPyramidCS, value);
        }

        [SerializeField, ResourcePath("Runtime/MipGen/Shaders/DepthPyramid.compute")]
        private ComputeShader m_DepthPyramidCS;

        public ComputeShader depthPyramidCS
        {
            get => m_DepthPyramidCS;
            set => this.SetValueAndNotify(ref m_DepthPyramidCS, value);
        }
    }
}
