using System.Reflection;
using UnityEngine;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph.Drawing.Controls;
using UnityEditor.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;

namespace UnityEditor.ShaderGraph
{
    /// <summary>
    /// ShaderGraph node for fetching scene depth using input attachments.
    /// This node reads depth directly from tile memory at the current fragment position.
    /// </summary>   
    [SRPFilter(typeof(UniversalRenderPipeline))]
    [Title("Input", "Scene", "Fetch Scene Depth")]
    sealed class FetchSceneDepthNode : CodeFunctionNode, IMayRequireDepthTexture
    {
        const string kOutputSlotName = "Out";
        public const int OutputSlotId = 0;

        [SerializeField]
        private DepthSamplingMode m_DepthSamplingMode = DepthSamplingMode.Linear01;

        [EnumControl("Sampling Mode")]
        public DepthSamplingMode depthSamplingMode
        {
            get { return m_DepthSamplingMode; }
            set
            {
                if (m_DepthSamplingMode == value)
                    return;

                m_DepthSamplingMode = value;
                Dirty(ModificationScope.Graph);
            }
        }

        private static ShaderKeyword DepthAsInputAttachment;

        private static ShaderKeyword DepthAsInputAttachmentMSAA;

        public FetchSceneDepthNode()
        {
            name = "Fetch Scene Depth";
            synonyms = new string[] { "input attachment", "framebuffer fetch", "tile memory" };

            DepthAsInputAttachment = new ShaderKeyword()
            {
                displayName = "Depth As Input Attachment",
                keywordType = KeywordType.Enum,
                keywordDefinition = KeywordDefinition.MultiCompile,
                keywordScope = KeywordScope.Global,
                keywordStages = KeywordShaderStage.Fragment,
                entries = new List<KeywordEntry>
                {
                    new KeywordEntry() { displayName = "Off", referenceName = "" },
                    new KeywordEntry() { displayName = "Depth As Input Attachment", referenceName = "DEPTH_AS_INPUT_ATTACHMENT" },
                    new KeywordEntry() { displayName = "Depth As Input Attachment MSAA", referenceName = "DEPTH_AS_INPUT_ATTACHMENT_MSAA" },
                },
            };

            UpdateNodeAfterDeserialization();
        }

        public override bool hasPreview { get { return false; } }

        public override void CollectShaderKeywords(KeywordCollector keywords, GenerationMode generationMode)
        {
            base.CollectShaderKeywords(keywords, generationMode);

            // Add keywords requires by this node
            keywords.AddShaderKeyword(DepthAsInputAttachment);
        }

        protected override MethodInfo GetFunctionToConvert()
        {
            switch (m_DepthSamplingMode)
            {
                case DepthSamplingMode.Raw:
                    return GetType().GetMethod("Unity_FetchSceneDepth_Raw", BindingFlags.Static | BindingFlags.NonPublic);
                case DepthSamplingMode.Eye:
                    return GetType().GetMethod("Unity_FetchSceneDepth_Eye", BindingFlags.Static | BindingFlags.NonPublic);
                case DepthSamplingMode.Linear01:
                default:
                    return GetType().GetMethod("Unity_FetchSceneDepth_Linear01", BindingFlags.Static | BindingFlags.NonPublic);
            }
        }

        static string Unity_FetchSceneDepth_Linear01(
            [Slot(0, Binding.ClipPosition, true, ShaderStageCapability.Fragment)] Vector4 clipPos,
            [Slot(1, Binding.None, ShaderStageCapability.Fragment)] out Vector1 Out)
        {
            return
@"
{
    #if defined(_DEPTH_AS_INPUT_ATTACHMENT) || defined(_DEPTH_AS_INPUT_ATTACHMENT_MSAA)
        // Fetch depth from tile memory using input attachment
        float rawDepth = shadergraph_LWFetchSceneDepth(clipPos.xy);
        Out = Linear01Depth(rawDepth, _ZBufferParams);
    #else
        // There is explicitly no fallback here.
        Out = 0;
    #endif
}
";
        }

        static string Unity_FetchSceneDepth_Raw(
            [Slot(0, Binding.ClipPosition, true, ShaderStageCapability.Fragment)] Vector4 clipPos,
            [Slot(1, Binding.None, ShaderStageCapability.Fragment)] out Vector1 Out)
        {
            return
@"
{
    #if defined(_DEPTH_AS_INPUT_ATTACHMENT) || defined(_DEPTH_AS_INPUT_ATTACHMENT_MSAA)
        // Fetch raw depth from tile memory using input attachment
        Out = shadergraph_LWFetchSceneDepth(clipPos.xy);
    #else
        // There is explicitly no fallback here.
        Out = 0;
    #endif
}
";
        }

        static string Unity_FetchSceneDepth_Eye(
            [Slot(0, Binding.ClipPosition, true, ShaderStageCapability.Fragment)] Vector4 clipPos,
            [Slot(1, Binding.None, ShaderStageCapability.Fragment)] out Vector1 Out)
        {
            return
@"
{
    #if defined(_DEPTH_AS_INPUT_ATTACHMENT) || defined(_DEPTH_AS_INPUT_ATTACHMENT_MSAA)
        // Fetch depth from tile memory using input attachment
        float rawDepth = shadergraph_LWFetchSceneDepth(clipPos.xy);
        {
            Out = LinearEyeDepth(rawDepth, _ZBufferParams);
        }
    #else
        {
            // There is explicitly no fallback here.
            Out = 0;
        }
    #endif
}
";
        }

        public bool RequiresDepthTexture(ShaderStageCapability stageCapability)
        {
            return true;
        }

        public override void ValidateNode()
        {
            base.ValidateNode();

            owner.AddValidationError(objectId,
                "Fetch Scene Depth uses input attachments to read depth directly from tile memory. " +
                "For texture-based depth sampling, use 'Sample Scene Depth' node instead.",
                Rendering.ShaderCompilerMessageSeverity.Warning);
        }
    }
}
