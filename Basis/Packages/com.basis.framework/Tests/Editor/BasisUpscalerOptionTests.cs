using Basis.BasisUI;
using NUnit.Framework;
using UnityEngine.Rendering;

namespace Basis.Tests.Rendering
{
    public class BasisUpscalerOptionTests
    {
        [Test]
        public void EveryTemporalOption_HasALabelAndAnUpscalerId()
        {
            Assert.That(SMModuleAntialiasingURP.TemporalUpscalerLabelKeys.Length, Is.EqualTo(SMModuleAntialiasingURP.TemporalUpscalerOptions.Length));
            foreach (string option in SMModuleAntialiasingURP.TemporalUpscalerOptions)
            {
                Assert.That(SMModuleAntialiasingURP.TemporalUpscalerId(option), Is.Not.Null, option);
                Assert.That(SMModuleAntialiasingURP.IsTemporalUpscalerOption(option.ToLowerInvariant()), Is.True, $"{option} has to survive the settings system lowercasing it.");
            }
        }

        [Test]
        public void MsaaAndSpatialOptions_AreNotTemporal()
        {
            foreach (string option in new[] { "Off", "MSAA 2X", "MSAA 4X", "MSAA 8X", "Linear", "Point", "FSR", "STP" })
            {
                Assert.That(SMModuleAntialiasingURP.IsTemporalUpscalerOption(option), Is.False, option);
            }
        }

        [Test]
        public void EveryQualityOption_HasALabel()
        {
            Assert.That(SMModuleAntialiasingURP.UpscalerQualityLabelKeys.Length, Is.EqualTo(SMModuleAntialiasingURP.UpscalerQualityOptions.Length));
            Assert.That(SMModuleAntialiasingURP.UpscalerQualityOptions, Does.Contain("Quality"));
            Assert.That(SMModuleAntialiasingURP.UpscalerQualityOptions, Does.Contain(BasisSettingsDefaults.UpscalerQuality.DefaultValue.GetDefault()));
        }

        [Test]
        public void EveryDlssModelOption_HasALabelAndIncludesTheDefault()
        {
            Assert.That(SMModuleAntialiasingURP.DlssModelLabelKeys.Length, Is.EqualTo(SMModuleAntialiasingURP.DlssModelOptions.Length));
            Assert.That(SMModuleAntialiasingURP.DlssModelOptions, Does.Contain(BasisSettingsDefaults.DlssModel.DefaultValue.GetDefault()));
        }

        [Test]
        public void OnlyDlss_IsTheDlssOption()
        {
            Assert.That(SMModuleAntialiasingURP.IsDlssOption("dlss"), Is.True);
            foreach (string option in new[] { "FSR 2", "FSR 3", "FSR 4", "FSR", "MSAA 2X", "Off", null })
            {
                Assert.That(SMModuleAntialiasingURP.IsDlssOption(option), Is.False, option ?? "null");
            }
        }

        [TestCase("NVIDIA GeForce RTX 4090", true)]
        [TestCase("NVIDIA GeForce RTX 4060 Laptop GPU", true)]
        [TestCase("NVIDIA GeForce RTX 5070 Ti", true)]
        [TestCase("NVIDIA RTX 6000 Ada Generation", true)]
        [TestCase("NVIDIA RTX 2000 Ada Generation Laptop GPU", true)]
        [TestCase("NVIDIA RTX PRO 6000 Blackwell Workstation Edition", true)]
        [TestCase("NVIDIA GeForce RTX 3090", false)]
        [TestCase("NVIDIA GeForce RTX 2080 SUPER", false)]
        [TestCase("Quadro RTX 8000", false)]
        [TestCase("NVIDIA RTX A6000", false)]
        [TestCase("NVIDIA GeForce GTX 1080 Ti", false)]
        [TestCase("AMD Radeon RX 7900 XTX", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void Rtx40OrNewer_MatchesAdaAndLaterOnly(string deviceName, bool expected)
        {
            Assert.That(SMModuleAntialiasingURP.IsRtx40OrNewer(deviceName), Is.EqualTo(expected));
        }

        [Test]
        public void UpscalerSharpness_DefaultsInsideItsSliderRange()
        {
            float sharpness = BasisSettingsDefaults.UpscalerSharpness.DefaultValue.GetDefault();
            Assert.That(sharpness, Is.InRange(BasisSettingsDefaults.UPSCALER_SHARPNESS_MIN, BasisSettingsDefaults.UPSCALER_SHARPNESS_MAX));
            Assert.That(sharpness, Is.GreaterThan(0f));
        }

#if ENABLE_UPSCALER_FRAMEWORK && UNITY_EDITOR_WIN
        [Test]
        public void EveryTemporalOption_IsRegisteredWithUnity()
        {
            foreach (string option in SMModuleAntialiasingURP.TemporalUpscalerOptions)
            {
                string upscalerId = SMModuleAntialiasingURP.TemporalUpscalerId(option);
                Assert.That(UpscalerRegistry.s_RegisteredUpscalers.ContainsKey(upscalerId), Is.True, $"{option} maps to {upscalerId}, which Unity has not registered. com.unity.modules.amd and com.unity.modules.nvidia have to stay in Packages/manifest.json.");
            }
        }

        [Test]
        public void OnlyFsr3AndFsr4_AreSkippedWhenThisDeviceCannotRunThem()
        {
            Assert.That(UnityEngine.Rendering.Universal.UniversalRenderPipeline.skipUpscaler, Is.Not.Null, "SMModuleAntialiasingURP installs the skip hook on editor load.");
            foreach (string upscalerId in new[] { "nvidia.dlss4", "amd.fsr2", "amd.fsr1", "unity.stp" })
            {
                Assert.That(SMModuleAntialiasingURP.IsUpscalerUnavailable(upscalerId), Is.False, upscalerId);
            }
            if (UnityEngine.SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.That(SMModuleAntialiasingURP.IsUpscalerUnavailable("amd.fsr3"), Is.True, "FSR3 is Direct3D12 only.");
                Assert.That(SMModuleAntialiasingURP.IsUpscalerUnavailable("amd.fsr4"), Is.True, "FSR4 is Direct3D12 only.");
            }
        }
#endif
    }
}
