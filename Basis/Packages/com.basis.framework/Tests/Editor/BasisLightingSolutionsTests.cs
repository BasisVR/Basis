#if BASIS_HAS_GI && !UNITY_ANDROID && !UNITY_WEBGL
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Basis.BasisUI;
using Basis.Scripts.Rendering;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Tests.Graphics
{
    public class BasisLightingSolutionsTests
    {
        private const string RendererRoot = "Assets/Basis/Settings/Unity Rendering Defaults/";
        private const string EnglishTable = "Packages/com.basis.framework/BasisUI/Localization/Languages/en.json";

        [TestCase("Unity", true)]
        [TestCase("unity", true)]
        [TestCase(" UNITY ", true)]
        [TestCase("Basis", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void SolutionNamesReadWhateverTheCase(string solution, bool unity)
        {
            Assert.AreEqual(unity, BasisLightingSolutions.IsUnity(solution));
        }

        [Test]
        public void EverySolutionDefaultsToBasis()
        {
            Assert.AreEqual(BasisLightingSolutions.SolutionBasis, BasisSettingsDefaults.AmbientOcclusionSolution.DefaultValue.GetDefault());
            Assert.AreEqual(BasisLightingSolutions.SolutionBasis, BasisSettingsDefaults.GlobalIlluminationSolution.DefaultValue.GetDefault());
            Assert.AreEqual(BasisLightingSolutions.SolutionBasis, BasisSettingsDefaults.ReflectionsSolution.DefaultValue.GetDefault());
            Assert.AreEqual("Screen Space", BasisSettingsDefaults.ReflectionsMode.DefaultValue.GetDefault());
        }

        [Test]
        public void EveryOptionHasALabel()
        {
            Assert.AreEqual(new[] { "Basis", "Unity" }, BasisLightingSolutions.SolutionOptions);
            Assert.AreEqual(BasisLightingSolutions.SolutionOptions.Length, BasisLightingSolutions.SolutionLabelKeys.Length);
        }

        [Test]
        public void EverySwitchThatDecidesASolutionIsWatched()
        {
            string[] keys =
            {
                BasisSettingsDefaults.UseRayTracedAmbientOcclusion.BindingKey,
                BasisSettingsDefaults.AmbientOcclusionSolution.BindingKey,
                BasisSettingsDefaults.UseGlobalIllumination.BindingKey,
                BasisSettingsDefaults.GlobalIlluminationSolution.BindingKey,
                BasisSettingsDefaults.GlobalIlluminationSpecular.BindingKey,
                BasisSettingsDefaults.ReflectionsSolution.BindingKey,
            };
            foreach (string key in keys)
            {
                Assert.IsTrue(BasisLightingSolutions.IsSolutionKey(key), key);
                Assert.IsTrue(BasisLightingSolutions.IsSolutionKey(key.ToUpperInvariant()), key);
            }
            Assert.IsFalse(BasisLightingSolutions.IsSolutionKey(BasisSettingsDefaults.GlobalIlluminationIntensity.BindingKey));
            Assert.IsFalse(BasisLightingSolutions.IsSolutionKey(null));
        }

        [Test]
        public void TheDesktopRendererCarriesBothSolutionsForEveryEffect()
        {
            List<ScriptableRendererFeature> features = Features("DesktopRenderer.asset");
            AssertOne<ScreenSpaceAmbientOcclusion>(features, false);
            AssertOne<SurfaceCacheGIRendererFeature>(features, false);
            AssertOne<ScreenSpaceReflectionRendererFeature>(features, false);
            AssertOne<BasisReflectionFeature>(features, true);
        }

        [Test]
        public void TheDirectToScreenRendererLeavesUnityReflectionsAndGlobalIlluminationOut()
        {
            List<ScriptableRendererFeature> features = Features("DirectToScreenRenderer.asset");
            AssertOne<ScreenSpaceAmbientOcclusion>(features, false);
            AssertOne<BasisReflectionFeature>(features, true);
            Assert.IsFalse(features.Exists(feature => feature is ScreenSpaceReflectionRendererFeature), "with Unity's reflections on every renderer, URP strips their off variant");
            Assert.IsFalse(features.Exists(feature => feature is SurfaceCacheGIRendererFeature), "each renderer with Surface Cache builds its own world, taking three of the eight transform tracking slots the engine leaves after the GPU Resident Drawer");
        }

        [Test]
        public void AScriptReloadMidPlayStillRestoresWhatTheRendererAuthored()
        {
            List<ScriptableRendererFeature> ambientOcclusion = new List<ScriptableRendererFeature>();
            if (QualitySettings.renderPipeline is UniversalRenderPipelineAsset asset)
            {
                foreach (ScriptableRendererData data in asset.rendererDataList)
                {
                    if (data == null) { continue; }
                    ambientOcclusion.AddRange(data.rendererFeatures.FindAll(feature => feature is ScreenSpaceAmbientOcclusion));
                }
            }
            if (ambientOcclusion.Count == 0)
            {
                Assert.Ignore("The active pipeline has no Unity ambient occlusion feature.");
            }
            List<bool> before = ambientOcclusion.ConvertAll(feature => feature.isActive);
            bool enabled = BasisSettingsDefaults.UseRayTracedAmbientOcclusion.RawValue;
            string solution = BasisSettingsDefaults.AmbientOcclusionSolution.RawValue;
            try
            {
                BasisSettingsDefaults.UseRayTracedAmbientOcclusion.SetValueWithoutNotify(true);
                BasisSettingsDefaults.AmbientOcclusionSolution.SetValueWithoutNotify(BasisLightingSolutions.SolutionUnity);
                BasisLightingSolutions.Apply();
                Assert.IsTrue(ambientOcclusion.TrueForAll(feature => feature.isActive));
                foreach (string field in new[] { "authored", "authoredAfterOpaque" })
                {
                    ((IDictionary)typeof(BasisLightingSolutions).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null)).Clear();
                }
                BasisLightingSolutions.Apply();
                BasisLightingSolutions.RestoreAuthored();
                for (int index = 0; index < ambientOcclusion.Count; index++)
                {
                    Assert.AreEqual(before[index], ambientOcclusion[index].isActive, "a reload forgets the switcher's memory, so it must not mistake the play state for the authored one");
                }
            }
            finally
            {
                BasisSettingsDefaults.UseRayTracedAmbientOcclusion.SetValueWithoutNotify(enabled);
                BasisSettingsDefaults.AmbientOcclusionSolution.SetValueWithoutNotify(solution);
                BasisLightingSolutions.RestoreAuthored();
                for (int index = 0; index < ambientOcclusion.Count; index++)
                {
                    ambientOcclusion[index].SetActive(before[index]);
                }
            }
        }

        [Test]
        public void TheBuildSwitchesEveryUnityFeatureOnAndPutsThemBack()
        {
            List<ScriptableRendererFeature> unity = new List<ScriptableRendererFeature>();
            for (int level = 0; level < QualitySettings.count; level++)
            {
                if (!(QualitySettings.GetRenderPipelineAssetAt(level) is UniversalRenderPipelineAsset asset)) { continue; }
                foreach (ScriptableRendererData data in asset.rendererDataList)
                {
                    if (data == null) { continue; }
                    foreach (ScriptableRendererFeature feature in data.rendererFeatures)
                    {
                        if (feature != null && BasisLightingSolutions.IsUnityLightingFeature(feature) && !unity.Contains(feature)) { unity.Add(feature); }
                    }
                }
            }
            if (unity.Count == 0)
            {
                Assert.Ignore("No quality level renders with a Unity lighting feature.");
            }
            List<bool> before = unity.ConvertAll(feature => feature.isActive);
            try
            {
                BasisLightingSolutions.ActivateForBuild();
                Assert.IsTrue(unity.TrueForAll(feature => feature.isActive), "a feature left off while the player builds has its shader variants and resources stripped");
            }
            finally
            {
                BasisLightingSolutions.RestoreAfterBuild();
            }
            for (int index = 0; index < unity.Count; index++)
            {
                Assert.AreEqual(before[index], unity[index].isActive, unity[index].name);
            }
        }

        [Test]
        public void EveryNewLabelIsInTheEnglishTable()
        {
            if (!File.Exists(EnglishTable))
            {
                Assert.Ignore($"{EnglishTable} is not present.");
            }
            string table = File.ReadAllText(EnglishTable);
            List<string> keys = new List<string>(BasisLightingSolutions.SolutionLabelKeys)
            {
                "settings.graphics.gi.solution", "settings.graphics.gi.solution.tooltip",
                "settings.graphics.rtao.solution", "settings.graphics.rtao.solution.tooltip",
                "settings.graphics.reflections.title", "settings.graphics.reflections.enable", "settings.graphics.reflections.enable.tooltip",
                "settings.graphics.reflections.solution", "settings.graphics.reflections.solution.tooltip",
                "settings.graphics.reflections.mode", "settings.graphics.reflections.mode.tooltip",
                "settings.graphics.rayTracing.unavailable",
                "settings.graphics.gi.mode.screenSpace.tooltip", "settings.graphics.gi.mode.rayTraced.tooltip",
                "settings.graphics.rtao.mode.screenSpace.tooltip", "settings.graphics.rtao.mode.rayTraced.tooltip",
            };
            foreach (string key in keys)
            {
                StringAssert.Contains("\"key\": \"" + key + "\"", table);
            }
        }

        private static List<ScriptableRendererFeature> Features(string file)
        {
            ScriptableRendererData data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererRoot + file);
            if (data == null)
            {
                Assert.Ignore($"{RendererRoot + file} is not present.");
            }
            return new List<ScriptableRendererFeature>(data.rendererFeatures);
        }

        private static void AssertOne<T>(List<ScriptableRendererFeature> features, bool active) where T : ScriptableRendererFeature
        {
            List<ScriptableRendererFeature> found = features.FindAll(feature => feature is T);
            Assert.AreEqual(1, found.Count, typeof(T).Name);
            Assert.AreEqual(active, found[0].isActive, typeof(T).Name + " active");
        }
    }
}
#endif
