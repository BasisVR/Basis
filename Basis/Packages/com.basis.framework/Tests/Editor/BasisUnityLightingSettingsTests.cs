#if BASIS_HAS_GI && !UNITY_ANDROID
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Basis.Scripts.Rendering;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityLighting = Basis.Scripts.Rendering.BasisUnityLightingSettings;

namespace Basis.Tests.Graphics
{
    public class BasisUnityLightingSettingsTests
    {
        private const string EnglishTable = "Packages/com.basis.framework/BasisUI/Localization/Languages/en.json";
        private const string RowSource = "Packages/com.basis.framework/BasisUI/Menus/Main Menu Providers/SettingsProviderParts/SettingsProviderUnityLighting.cs";
        private const string RendererRoot = "Assets/Basis/Settings/Unity Rendering Defaults/";
        private readonly List<Action> restore = new List<Action>();
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            foreach (FieldInfo field in typeof(UnityLighting).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                object setting = field.GetValue(null);
                if (setting is BasisSettingsBinding<bool> toggle)
                {
                    bool old = toggle.RawValue;
                    toggle.SetValueWithoutNotify(toggle.DefaultValue.GetDefault());
                    restore.Add(() => toggle.SetValueWithoutNotify(old));
                }
                else if (setting is BasisRangedSetting ranged)
                {
                    float old = ranged.Binding.RawValue;
                    ranged.Binding.SetValueWithoutNotify(ranged.Binding.DefaultValue.GetDefault());
                    restore.Add(() => ranged.Binding.SetValueWithoutNotify(old));
                }
                else if (Choice(setting) is BasisSettingsBinding<string> choice)
                {
                    string old = choice.RawValue;
                    choice.SetValueWithoutNotify(choice.DefaultValue.GetDefault());
                    restore.Add(() => choice.SetValueWithoutNotify(old));
                }
            }
        }

        [TearDown]
        public void TearDown()
        {
            for (int index = restore.Count - 1; index >= 0; index--) { restore[index](); }
            restore.Clear();
            foreach (UnityEngine.Object target in owned) { UnityEngine.Object.DestroyImmediate(target); }
            owned.Clear();
        }

        [Test]
        public void EveryKeyIsUniqueLowercaseAndMarkedUnity()
        {
            HashSet<string> keys = new HashSet<string>();
            foreach (FieldInfo field in typeof(UnityLighting).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!(field.GetValue(null) is IBasisSettingsBinding setting)) { continue; }
                Assert.AreEqual(setting.BindingKey.ToLowerInvariant(), setting.BindingKey, field.Name);
                StringAssert.StartsWith("unity", setting.BindingKey, field.Name);
                Assert.IsTrue(keys.Add(setting.BindingKey), "duplicate key " + setting.BindingKey);
            }
            Assert.Greater(keys.Count, 60);
        }

        [Test]
        public void EveryRangeHoldsItsDefaultAndEveryChoiceLinesUp()
        {
            foreach (FieldInfo field in typeof(UnityLighting).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                object setting = field.GetValue(null);
                if (setting is BasisRangedSetting ranged)
                {
                    float value = ranged.Binding.DefaultValue.GetDefault();
                    Assert.That(value, Is.InRange(ranged.Min, ranged.Max), field.Name);
                }
                else if (Choice(setting) != null)
                {
                    string[] options = (string[])setting.GetType().GetField("Options").GetValue(setting);
                    Array values = (Array)setting.GetType().GetField("Values").GetValue(setting);
                    Assert.AreEqual(options.Length, values.Length, field.Name);
                }
            }
        }

        [Test]
        public void AmbientOcclusionDefaultsAreUnitysHighPresetAppliedAfterOpaqueWithBlueNoise()
        {
            ScreenSpaceAmbientOcclusionVolumeOverride expected = Own(ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusionVolumeOverride>());
            expected.quality = ScreenSpaceAmbientOcclusionQuality.High;
            expected.method = ScreenSpaceAmbientOcclusionNoiseMethod.BlueNoise;
            expected.afterOpaque = true;
            expected.useComputeShader = SystemInfo.supportsComputeShaders;
            expected.normalQuality = ScreenSpaceAmbientOcclusionNormalQuality.High;
            expected.sampleCount = ScreenSpaceAmbientOcclusionSampleCount.High;
            expected.directionCount = 4;
            expected.stepCount = 6;
            ScreenSpaceAmbientOcclusionVolumeOverride applied = Own(ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusionVolumeOverride>());
            UnityLighting.Apply(applied, singlePassStereo: false);
            AssertSameParameters(expected, applied);
            Assert.IsTrue(applied.afterOpaque, "after opaque reaches every shader, including ones that never read Unity's occlusion texture");
            UnityLighting.Apply(applied, singlePassStereo: true);
            Assert.IsFalse(applied.useComputeShader);
        }

        [Test]
        public void SwitchingAQualityPresetToCustomKeepsTheDefaultLook()
        {
            ScreenSpaceAmbientOcclusionVolumeOverride presetAo = Own(ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusionVolumeOverride>());
            SurfaceCacheGIVolumeOverride presetGi = Own(ScriptableObject.CreateInstance<SurfaceCacheGIVolumeOverride>());
            ScreenSpaceReflectionVolumeSettings presetSsr = Own(ScriptableObject.CreateInstance<ScreenSpaceReflectionVolumeSettings>());
            UnityLighting.Apply(presetAo, singlePassStereo: false);
            UnityLighting.Apply(presetGi);
            UnityLighting.Apply(presetSsr);
            UnityLighting.SsaoQuality.Binding.SetValueWithoutNotify(UnityLighting.Custom);
            UnityLighting.SurfaceCacheQuality.Binding.SetValueWithoutNotify(UnityLighting.Custom);
            UnityLighting.SsrPreset.Binding.SetValueWithoutNotify(UnityLighting.Custom);
            ScreenSpaceAmbientOcclusionVolumeOverride customAo = Own(ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusionVolumeOverride>());
            SurfaceCacheGIVolumeOverride customGi = Own(ScriptableObject.CreateInstance<SurfaceCacheGIVolumeOverride>());
            ScreenSpaceReflectionVolumeSettings customSsr = Own(ScriptableObject.CreateInstance<ScreenSpaceReflectionVolumeSettings>());
            UnityLighting.Apply(customAo, singlePassStereo: false);
            UnityLighting.Apply(customGi);
            UnityLighting.Apply(customSsr);

            Assert.AreEqual(presetAo.sampleCount, customAo.sampleCount);
            Assert.AreEqual(presetAo.downsample, customAo.downsample);
            Assert.AreEqual(presetAo.blurQuality, customAo.blurQuality);
            Assert.AreEqual(presetAo.depthSource, customAo.depthSource);
            Assert.AreEqual(presetAo.normalQuality, customAo.normalQuality);
            Assert.AreEqual(presetAo.directionCount, customAo.directionCount);
            Assert.AreEqual(presetAo.stepCount, customAo.stepCount);
            AssertSameParameters(presetGi, customGi);
            AssertSameParameters(presetSsr, customSsr);
        }

        [Test]
        public void ComputeShaderAutoSkipsSinglePassStereoAndOnOffForceIt()
        {
            bool supported = SystemInfo.supportsComputeShaders;
            int auto = UnityLighting.SsaoComputeShader.Read("Auto");
            Assert.AreEqual("Auto", UnityLighting.SsaoComputeShader.Binding.DefaultValue.GetDefault());
            Assert.AreEqual(supported, UnityLighting.UsesComputeShader(auto, singlePassStereo: false));
            Assert.IsFalse(UnityLighting.UsesComputeShader(auto, singlePassStereo: true));
            Assert.AreEqual(supported, UnityLighting.UsesComputeShader(UnityLighting.SsaoComputeShader.Read("On"), singlePassStereo: true));
            Assert.IsFalse(UnityLighting.UsesComputeShader(UnityLighting.SsaoComputeShader.Read("Off"), singlePassStereo: false));
        }

        [Test]
        public void UnitysComputeAmbientOcclusionStillDrawsOneViewOnly()
        {
            const string shader = "Packages/com.unity.render-pipelines.universal/Shaders/Utils/GTAO.compute";
            if (!File.Exists(shader)) { Assert.Ignore($"{shader} is not present."); }
            StringAssert.DoesNotContain("UNITY_XR_ASSIGN_VIEW_INDEX", File.ReadAllText(shader), "Unity's compute GTAO reads the eye index now, so Auto can stop keeping single-pass stereo cameras on the fragment shader");
        }

        [Test]
        public void GlobalIlluminationDefaultsAreUnitysUltraPresetWithBouncePatchAllocation()
        {
            SurfaceCacheGIVolumeOverride expected = Own(ScriptableObject.CreateInstance<SurfaceCacheGIVolumeOverride>());
            expected.ApplyPreset(SurfaceCacheGIVolumeOverride.PresetQuality.Ultra);
            expected.lightTransportBouncePatchAllocation.value = true;
            SurfaceCacheGIVolumeOverride applied = Own(ScriptableObject.CreateInstance<SurfaceCacheGIVolumeOverride>());
            UnityLighting.Apply(applied);
            AssertSameParameters(expected, applied);
        }

        [Test]
        public void ReflectionDefaultsAreUnitysBestQualityPresetWithTransparentsAndContactHardening()
        {
            ScreenSpaceReflectionVolumeSettings expected = Own(ScriptableObject.CreateInstance<ScreenSpaceReflectionVolumeSettings>());
            expected.resolution.value = ScreenSpaceReflectionVolumeSettings.Resolution.Full;
            expected.mode.value = ScreenSpaceReflectionVolumeSettings.ReflectionMode.OpaquesAndTransparents;
            expected.contactHardeningScale.value = 1f;
            ScreenSpaceReflectionVolumeSettings applied = Own(ScriptableObject.CreateInstance<ScreenSpaceReflectionVolumeSettings>());
            UnityLighting.Apply(applied);
            AssertSameParameters(expected, applied);
        }

        [Test]
        public void ReflectionPresetsMatchUnitysOwnTable()
        {
            FieldInfo table = typeof(ScreenSpaceReflectionVolumeSettings).GetField("k_PerformancePresets", BindingFlags.NonPublic | BindingFlags.Static);
            if (table == null) { Assert.Ignore("URP no longer exposes k_PerformancePresets; check the preset copy in BasisUnityLightingSettings by hand."); }
            IList presets = (IList)table.GetValue(null);
            Assert.AreEqual(UnityLighting.SsrPreset.Options.Length - 1, presets.Count, "Unity added or removed a reflection preset");
            for (int index = 0; index < presets.Count; index++)
            {
                object preset = presets[index];
                UnityLighting.SsrPreset.Binding.SetValueWithoutNotify(UnityLighting.SsrPreset.Options[index]);
                ScreenSpaceReflectionVolumeSettings applied = Own(ScriptableObject.CreateInstance<ScreenSpaceReflectionVolumeSettings>());
                UnityLighting.Apply(applied);
                string name = UnityLighting.SsrPreset.Options[index];
                Assert.AreEqual(Field(preset, "resolution"), applied.resolution.value, name);
                Assert.AreEqual(Field(preset, "upscalingMethod"), applied.upscalingMethod.value, name);
                Assert.AreEqual(Field(preset, "marchingMethod"), applied.marchingMethod.value, name);
                Assert.AreEqual(Field(preset, "hitRefinementSteps"), applied.hitRefinementSteps.value, name);
                Assert.AreEqual(Field(preset, "finalThicknessMultiplier"), applied.finalThicknessMultiplier.value, name);
                Assert.AreEqual(Field(preset, "maxRayLength"), applied.maxRayLength.value, name);
                Assert.AreEqual(Field(preset, "maxRaySteps"), applied.maxRaySteps.value, name);
                Assert.AreEqual(Field(preset, "objectThickness"), applied.objectThickness.value, name);
            }
        }

        [Test]
        public void CustomQualityUsesTheCustomValuesAndPresetsIgnoreThem()
        {
            ScreenSpaceAmbientOcclusionVolumeOverride ao = Own(ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusionVolumeOverride>());
            UnityLighting.SsaoSampleCount.Binding.SetValueWithoutNotify("High");
            UnityLighting.SsaoQuality.Binding.SetValueWithoutNotify("Custom");
            UnityLighting.Apply(ao, singlePassStereo: false);
            Assert.AreEqual(ScreenSpaceAmbientOcclusionSampleCount.High, ao.sampleCount);
            UnityLighting.SsaoQuality.Binding.SetValueWithoutNotify("Low");
            UnityLighting.Apply(ao, singlePassStereo: false);
            Assert.AreEqual(ScreenSpaceAmbientOcclusionSampleCount.Low, ao.sampleCount);

            SurfaceCacheGIVolumeOverride gi = Own(ScriptableObject.CreateInstance<SurfaceCacheGIVolumeOverride>());
            UnityLighting.SurfaceCacheSampleCount.Binding.SetValueWithoutNotify(7f);
            UnityLighting.SurfaceCacheQuality.Binding.SetValueWithoutNotify("Custom");
            UnityLighting.Apply(gi);
            Assert.AreEqual(7, gi.lightTransportSampleCount.value);
            UnityLighting.SurfaceCacheQuality.Binding.SetValueWithoutNotify("Ultra");
            UnityLighting.Apply(gi);
            SurfaceCacheGIVolumeOverride ultra = Own(ScriptableObject.CreateInstance<SurfaceCacheGIVolumeOverride>());
            ultra.ApplyPreset(SurfaceCacheGIVolumeOverride.PresetQuality.Ultra);
            Assert.AreEqual(ultra.lightTransportSampleCount.value, gi.lightTransportSampleCount.value);
            Assert.IsTrue(gi.enabled.value, "a world volume that disabled Surface Cache would otherwise black out indirect light");
        }

        [Test]
        public void ContactHardeningOnlyRunsWhileItsToggleIsOn()
        {
            ScreenSpaceReflectionVolumeSettings ssr = Own(ScriptableObject.CreateInstance<ScreenSpaceReflectionVolumeSettings>());
            UnityLighting.SsrContactHardeningScale.Binding.SetValueWithoutNotify(2f);
            UnityLighting.SsrContactHardening.SetValueWithoutNotify(false);
            UnityLighting.Apply(ssr);
            Assert.AreEqual(0f, ssr.contactHardeningScale.value);
            UnityLighting.SsrContactHardening.SetValueWithoutNotify(true);
            UnityLighting.Apply(ssr);
            Assert.AreEqual(2f, ssr.contactHardeningScale.value);
        }

        [Test]
        public void LinearMarchingFollowsThePresetUntilCustom()
        {
            Assert.IsTrue(UnityLighting.SsrUsesLinearMarching("Fast", "Hierarchical"));
            Assert.IsFalse(UnityLighting.SsrUsesLinearMarching("High Quality", "Linear"));
            Assert.IsTrue(UnityLighting.SsrUsesLinearMarching("Custom", "Linear"));
            Assert.IsFalse(UnityLighting.SsrUsesLinearMarching("Custom", "Hierarchical"));
        }

        [Test]
        public void EveryUnityRowHasALabelAndATooltip()
        {
            if (!File.Exists(EnglishTable) || !File.Exists(RowSource)) { Assert.Ignore("Source files are not present."); }
            string table = File.ReadAllText(EnglishTable);
            string source = File.ReadAllText(RowSource);
            MatchCollection rows = Regex.Matches(source, "rows, \"([A-Za-z]+\\.[A-Za-z]+)\"");
            MatchCollection labels = Regex.Matches(source, "Root \\+ \"([A-Za-z.]+)\"");
            Assert.Greater(rows.Count, 60);
            Assert.Greater(labels.Count, 30);
            foreach (Match row in rows)
            {
                StringAssert.Contains("\"key\": \"settings.graphics.unity." + row.Groups[1].Value + "\"", table);
                StringAssert.Contains("\"key\": \"settings.graphics.unity." + row.Groups[1].Value + ".tooltip\"", table);
            }
            foreach (Match label in labels)
            {
                StringAssert.Contains("\"key\": \"settings.graphics.unity." + label.Groups[1].Value + "\"", table);
            }
        }

        [TestCase("DesktopRenderer.asset")]
        [TestCase("DirectToScreenRenderer.asset")]
        public void TheSettingsHookRunsBeforeUnitysFeatures(string file)
        {
            ScriptableRendererData data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererRoot + file);
            if (data == null) { Assert.Ignore($"{RendererRoot + file} is not present."); }
            int hook = data.rendererFeatures.FindIndex(feature => feature is BasisUnityLightingFeature);
            Assert.GreaterOrEqual(hook, 0, "the settings hook is missing");
            Assert.IsTrue(data.rendererFeatures[hook].isActive);
            for (int index = 0; index < data.rendererFeatures.Count; index++)
            {
                if (BasisLightingSolutions.IsUnityLightingFeature(data.rendererFeatures[index]))
                {
                    Assert.Less(hook, index, data.rendererFeatures[index].name + " reads the volume stack before Basis writes it");
                }
            }
        }

        private T Own<T>(T target) where T : UnityEngine.Object
        {
            owned.Add(target);
            return target;
        }

        private static BasisSettingsBinding<string> Choice(object setting)
        {
            return setting is IBasisSettingsBinding ? setting.GetType().GetField("Binding")?.GetValue(setting) as BasisSettingsBinding<string> : null;
        }

        private static object Field(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        }

        private static void AssertSameParameters(VolumeComponent expected, VolumeComponent actual)
        {
            foreach (FieldInfo field in expected.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!typeof(VolumeParameter).IsAssignableFrom(field.FieldType)) { continue; }
                PropertyInfo value = field.FieldType.GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
                Assert.AreEqual(value.GetValue(field.GetValue(expected)), value.GetValue(field.GetValue(actual)), expected.GetType().Name + "." + field.Name);
            }
        }
    }
}
#endif
