using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Test.GlobalSettingsMigration
{
    class LightFalloffSettingsMigrationTest : GlobalSettingsMigrationTestBase
    {
        UniversalRenderPipelineGlobalSettings m_NewInstance;
        LightFalloffMode m_PreviousLightFalloffMode;

        [SetUp]
        public void SetUpAsset()
        {
#pragma warning disable 618 // Type or member is obsolete
            m_PreviousLightFalloffMode = m_Asset.m_ObsoleteLightFalloffMode;
#pragma warning restore 618
        }

        [TearDown]
        public void TearDownAsset()
        {
#pragma warning disable 618 // Type or member is obsolete
            m_Asset.m_ObsoleteLightFalloffMode = m_PreviousLightFalloffMode;
#pragma warning restore 618
            EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>(m_Instance);

            if (m_NewInstance != null)
                Object.DestroyImmediate(m_NewInstance);
        }

        URPLightFalloffSettings RegisterNewGlobalSettings()
        {
            m_NewInstance = ScriptableObject.CreateInstance<UniversalRenderPipelineGlobalSettings>();
            EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>(m_NewInstance);
            return GraphicsSettings.GetRenderPipelineSettings<URPLightFalloffSettings>();
        }

        [Test]
        public void QuadraticURPAsset_MigratesToQuadratic_AndClearsTheObsoleteFalloffMode()
        {
#pragma warning disable 618 // Type or member is obsolete
            m_Asset.m_ObsoleteLightFalloffMode = LightFalloffMode.Quadratic;

            var settings = RegisterNewGlobalSettings();

            Assert.IsNotNull(settings, "URPLightFalloffSettings must be added to the new Global Settings asset");
            Assert.AreEqual(LightFalloffMode.Quadratic, settings.lightFalloffMode,
                "A URP Asset that used the Quadratic falloff before URPLightFalloffSettings existed must migrate the project to Quadratic to preserve its lighting");
            Assert.AreEqual(LightFalloffMode.InverseSquared, m_Asset.m_ObsoleteLightFalloffMode,
                "The migrated URP Asset must stop storing Quadratic, otherwise the version 16 step sets Quadratic again when the project is upgraded");
#pragma warning restore 618
        }

        [Test]
        public void GlobalSettingsVersionStaysAt14()
        {
            Assert.AreEqual(14, UniversalRenderPipelineGlobalSettings.k_LastVersion,
                "6000.7 must keep the Global Settings version at 14. 7000.0 added the shadow normal bias migration as version 15, and an asset at a higher version skips it when the project is upgraded");
        }
    }
}
