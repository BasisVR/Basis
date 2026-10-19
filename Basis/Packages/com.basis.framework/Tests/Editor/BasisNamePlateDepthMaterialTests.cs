using Basis.Scripts.UI.NamePlate;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Tests.IK
{
    public class BasisNamePlateDepthMaterialTests
    {
        const string TransparentPath = "Packages/com.basis.sdk/Materials/TransParentNamePlateMaterial.mat";
        const string OpaquePath = "Packages/com.basis.sdk/Materials/OpaqueNamePlateMaterial.mat";
        const string FontPath = "Packages/com.basis.sdk/Fonts/Poppins-Regular SDF NamePlate.asset";
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        Material created;
        [TearDown]
        public void TearDown()
        {
            if (created != null) Object.DestroyImmediate(created);
        }
        static Shader PanelShader() => Shader.Find(BasisNamePlateDepthMaterials.PanelShaderName);
        static Shader TextShader() => Shader.Find(BasisNamePlateDepthMaterials.TextShaderName);
        static Material Load(string path)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Assert.IsNotNull(material, path + " is missing");
            return material;
        }
        [Test]
        public void ShadersParseAndDeclareTheObjectKeyword()
        {
            Shader panel = PanelShader();
            Shader text = TextShader();
            Assert.IsNotNull(panel);
            Assert.IsNotNull(text);
            Assert.IsFalse(ShaderUtil.ShaderHasError(panel), "panel shader has errors");
            Assert.IsFalse(ShaderUtil.ShaderHasError(text), "text shader has errors");
            CollectionAssert.Contains(panel.keywordSpace.keywordNames, BasisNamePlateDepthMaterials.ObjectKeyword);
            CollectionAssert.Contains(panel.keywordSpace.keywordNames, "BASIS_NAMEPLATE_GPU");
            CollectionAssert.Contains(text.keywordSpace.keywordNames, BasisNamePlateDepthMaterials.ObjectKeyword);
        }
        [Test]
        public void TransparentBubbleKeepsItsBlendQueueAndColor()
        {
            Material source = Load(TransparentPath);
            created = BasisNamePlateDepthMaterials.CreateSurface(PanelShader(), source);
            Assert.IsTrue(BasisNamePlateDepthMaterials.IsDepthMaterial(created));
            Assert.AreEqual(source.renderQueue, created.renderQueue);
            Assert.AreEqual(source.GetColor(BaseColorId), created.GetColor(BaseColorId));
            Assert.AreEqual(source.GetFloat("_SrcBlend"), created.GetFloat("_SrcBlend"));
            Assert.AreEqual(source.GetFloat("_DstBlend"), created.GetFloat("_DstBlend"));
            Assert.AreEqual(0f, created.GetFloat("_ZWrite"));
            Assert.AreEqual(0f, created.GetFloat("_Sheen"));
        }
        [Test]
        public void OpaqueBubbleStaysSolidButLeavesTheOpaquePass()
        {
            Material source = Load(OpaquePath);
            created = BasisNamePlateDepthMaterials.CreateSurface(PanelShader(), source);
            Assert.Greater(created.renderQueue, (int)RenderQueue.GeometryLast, "a depth-primed opaque pass would drop a shader with no DepthOnly pass");
            Assert.AreEqual(1f, created.GetColor(BaseColorId).a);
            Assert.AreEqual((float)BlendMode.One, created.GetFloat("_SrcBlend"));
            Assert.AreEqual((float)BlendMode.Zero, created.GetFloat("_DstBlend"));
            Assert.AreEqual(1f, created.GetFloat("_ZWrite"));
        }
        [Test]
        public void TextCloneKeepsTheAtlasAndUnderlay()
        {
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            Assert.IsNotNull(font, FontPath + " is missing");
            Material source = font.material;
            created = BasisNamePlateDepthMaterials.CreateText(TextShader(), source);
            Assert.AreSame(TextShader(), created.shader);
            Assert.IsTrue(BasisNamePlateDepthMaterials.IsDepthMaterial(created));
            Assert.AreSame(source.GetTexture(MainTexId), created.GetTexture(MainTexId));
            Assert.AreEqual(source.IsKeywordEnabled("UNDERLAY_ON"), created.IsKeywordEnabled("UNDERLAY_ON"));
            Assert.IsFalse(BasisNamePlateDepthMaterials.IsDepthMaterial(source));
        }
        [Test]
        public void DepthPullFollowsThePlateScale()
        {
            int id = Shader.PropertyToID("_BasisNamePlateDepthPull");
            float before = Shader.GetGlobalFloat(id);
            try
            {
                BasisNamePlateDepthMaterials.UpdateDepthPull(0.02f);
                Assert.AreEqual(BasisNamePlateDepthMaterials.PullUnits * 0.02f, Shader.GetGlobalFloat(id), 1e-6f);
                BasisNamePlateDepthMaterials.UpdateDepthPull(0.01f);
                Assert.AreEqual(BasisNamePlateDepthMaterials.PullUnits * 0.01f, Shader.GetGlobalFloat(id), 1e-6f);
            }
            finally
            {
                BasisNamePlateDepthMaterials.Dispose();
                Shader.SetGlobalFloat(id, before);
            }
        }
        [Test]
        public void ConvertedMaterialsPassThroughUnchanged()
        {
            created = BasisNamePlateDepthMaterials.CreateSurface(PanelShader(), Load(TransparentPath));
            Assert.AreSame(created, BasisNamePlateDepthMaterials.Surface(created));
            Assert.AreSame(created, BasisNamePlateDepthMaterials.Text(created));
            Assert.IsNull(BasisNamePlateDepthMaterials.Surface(null));
            Assert.IsNull(BasisNamePlateDepthMaterials.Text(null));
            Assert.DoesNotThrow(() => BasisNamePlateDepthMaterials.Apply(null));
        }
    }
}
