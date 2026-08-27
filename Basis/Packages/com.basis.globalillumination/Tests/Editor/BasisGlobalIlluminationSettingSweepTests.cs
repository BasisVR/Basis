using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// Every setting the player can reach, moved from one end of its range to the other, with the rendered
    /// image measured either side. A setting that is wired through the volume and still changes nothing on
    /// screen is indistinguishable from a broken one, and only a render can tell the two apart.
    ///
    /// Both modes are swept, because a setting can be alive in the screen space gather and inert in the
    /// traced one - which is exactly what a player switching to Ray Traced would report as half the panel
    /// having stopped working.
    ///
    /// The scene is built so that no setting is untestable by accident: there is an environment for a
    /// missed ray to fall back to, something standing between the emitter and the probe for the occlusion
    /// test to find, and the baseline runs at half resolution so the upsample has work to do. Each reading
    /// carries both a level and a spatial contrast, because a blur that is working changes the second
    /// without touching the first.
    /// </summary>
    public class BasisGlobalIlluminationSettingSweepTests
    {
        private static readonly Vector3 BlockCentre = new Vector3(-0.7f, 0.6f, 0.55f);
        private static readonly Vector3 BlockSize = new Vector3(0.45f, 0.45f, 0.45f);
        private static readonly Vector3 EmitterPoint = new Vector3(1.5f, 0.75f, 0.5f);
        private static readonly Vector3 NearProbe = new Vector3(-0.05f, 0.101f, 0.55f);
        // A second probe straddling the emissive block's silhouette. Anything that only shows itself at a
        // depth discontinuity - the bilateral upsample above all - is invisible in the middle of a floor.
        private static readonly Vector3 EdgeProbe = new Vector3(-0.94f, 0.55f, 0.55f);
        // A third probe tucked into the floor-to-wall corner. Near field obscurance needs something close by
        // to occlude against, and in the middle of an open floor there is nothing - which reads as the
        // obscurance slider doing nothing when what it actually has is nothing to do.
        private static readonly Vector3 CornerProbe = new Vector3(0.4f, 0.101f, 2.85f);

        private BasisGlobalIlluminationRenderHarness harness;
        private RectInt[] probes;

        private sealed class Setting
        {
            public string Name;
            public Action<BasisGlobalIlluminationVolume> Low;
            public Action<BasisGlobalIlluminationVolume> High;
            /// <summary>False where the setting only drives one of the two gathers by design.</summary>
            public bool ScreenSpace = true;
            public bool RayTraced = true;
        }

        [SetUp]
        public void SetUp()
        {
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            BasisGlobalIlluminationEmitter.Registered.Clear();
            harness = new BasisGlobalIlluminationRenderHarness();

            harness.AddSun(Quaternion.Euler(52f, -24f, 0f), 0.5f);
            Material surface = harness.CreateLitMaterial(new Color(0.8f, 0.8f, 0.8f), Color.black);
            harness.AddBox(new Vector3(0f, 0f, 0f), new Vector3(14f, 0.2f, 14f), surface);
            harness.AddBox(new Vector3(0f, 2f, 3.2f), new Vector3(14f, 5f, 0.2f), surface);
            // The side wall's colour is in a base map, so folding a map into the traced albedo has something
            // to fold. Every other surface keeps its colour in the base colour.
            harness.AddBox(new Vector3(-3.4f, 2f, 0f), new Vector3(0.2f, 5f, 9f), harness.CreateTexturedMaterial(new Color(0.9f, 0.25f, 0.15f)));
            harness.AddBox(BlockCentre, BlockSize, harness.CreateLitMaterial(Color.black, new Color(16f, 0.5f, 0.5f)));

            // A green emitter with a wall between it and the probe, so the occlusion test has something to
            // find and turning it off is visible.
            harness.AddBox(new Vector3(0.75f, 0.75f, 0.5f), new Vector3(0.12f, 1.3f, 1.4f), surface);
            harness.AddEmitter(EmitterPoint, Color.green, 24f, 0.35f, 12f);
            // A second emitter with nothing in the way, so turning emitters off is measurable even while the
            // first one is being used to prove the occlusion test works.
            harness.AddEmitter(new Vector3(-0.15f, 0.9f, -0.35f), Color.blue, 14f, 0.3f, 10f);

            harness.Camera.transform.position = new Vector3(0.1f, 1.7f, -2.6f);
            harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(-0.1f, 0.3f, 0.6f) - harness.Camera.transform.position, Vector3.up);

            probes = new[] { Rect(NearProbe, 7), Rect(EdgeProbe, 5), Rect(CornerProbe, 5) };

            // The pipeline creates its renderers, and the feature its material, on the first render. Asking
            // the feature what it can do before that has ever happened gets an answer about an object that
            // has not been initialised yet - which is how the ray traced sweep came to skip itself on a GPU
            // that runs it perfectly well.
            for (int frame = 0; frame < 4; frame++) { harness.Render(); }
        }

        private RectInt Rect(Vector3 worldPoint, int radius)
        {
            Vector3 screen = harness.Camera.WorldToScreenPoint(worldPoint);
            return new RectInt(Mathf.RoundToInt(screen.x) - radius, Mathf.RoundToInt(screen.y) - radius, radius * 2, radius * 2);
        }

        [TearDown]
        public void TearDown()
        {
            harness?.Dispose();
            harness = null;
        }

        /// <summary>
        /// The state every sweep starts from. Half resolution rather than full, so the bilateral upsample is
        /// actually running and can be turned off; everything else mid-range so both ends of each sweep have
        /// somewhere to go.
        /// </summary>
        private static void Baseline(BasisGlobalIlluminationVolume v)
        {
            v.enable.value = true;
            v.intensity.value = 1f;
            v.saturation.value = 1f;
            v.tint.value = Color.white;
            v.obscuranceIntensity.value = 0.5f;
            v.obscuranceRadius.value = 0.5f;
            v.maxRayLength.value = 16f;
            v.fadeDistance.value = 120f;
            v.quality.value = BasisGlobalIlluminationQuality.Medium;
            v.overrideQualityCounts.value = false;
            v.smoothing.value = 1f;
            v.wideBlur.value = true;
            v.resolution.value = BasisGlobalIlluminationResolution.Half;
            v.temporalFilter.value = true;
            v.temporalResponse.value = 0.15f;
            v.neighbourhoodClamp.value = true;
            v.bilateralUpsample.value = true;
            v.fireflyClamp.value = 6f;
            v.fallback.value = BasisGlobalIlluminationFallback.ReflectionProbe;
            v.fallbackIntensity.value = 1f;
            v.emitters.value = true;
            v.emitterIntensity.value = 1f;
            v.emitterOcclusion.value = true;
            v.rayReuse.value = true;
            v.bounces.value = 1;
            v.rayTracedLights.value = true;
            v.rayTracedLightIntensity.value = 1f;
            v.rayTracedShadows.value = true;
            v.rayTracedEmissiveSurfaces.value = true;
            v.rayTracedTextureAlbedo.value = true;
            v.rayTracedNormalBias.value = 0.02f;
        }

        private static List<Setting> Settings()
        {
            return new List<Setting>
            {
                new Setting { Name = "intensity",          Low = v => v.intensity.value = 0.1f,  High = v => v.intensity.value = 4f },
                new Setting { Name = "saturation",         Low = v => v.saturation.value = 0.1f, High = v => v.saturation.value = 2f },
                new Setting { Name = "tint",               Low = v => v.tint.value = Color.white, High = v => v.tint.value = new Color(0.2f, 0.2f, 1f) },
                new Setting { Name = "obscurance",         Low = v => v.obscuranceIntensity.value = 0.05f, High = v => v.obscuranceIntensity.value = 1f },
                new Setting { Name = "obscuranceRadius",   Low = v => v.obscuranceRadius.value = 0.05f, High = v => v.obscuranceRadius.value = 4f },
                new Setting { Name = "maxRayLength",       Low = v => v.maxRayLength.value = 1f, High = v => v.maxRayLength.value = 64f },
                new Setting { Name = "fadeDistance",       Low = v => v.fadeDistance.value = 1.5f, High = v => v.fadeDistance.value = 120f },
                new Setting { Name = "smoothing",          Low = v => v.smoothing.value = 0f,    High = v => v.smoothing.value = 2f },
                new Setting { Name = "wideBlur",           Low = v => v.wideBlur.value = false,  High = v => v.wideBlur.value = true },
                new Setting { Name = "temporalFilter",     Low = v => v.temporalFilter.value = false, High = v => v.temporalFilter.value = true },
                new Setting { Name = "temporalResponse",   Low = v => v.temporalResponse.value = 0.05f, High = v => v.temporalResponse.value = 1f },
                new Setting { Name = "quality",            Low = v => v.quality.value = BasisGlobalIlluminationQuality.Low, High = v => v.quality.value = BasisGlobalIlluminationQuality.Ultra },
                new Setting { Name = "resolution",         Low = v => v.resolution.value = BasisGlobalIlluminationResolution.Quarter, High = v => v.resolution.value = BasisGlobalIlluminationResolution.Half },
                new Setting { Name = "fallback",           Low = v => v.fallback.value = BasisGlobalIlluminationFallback.None, High = v => v.fallback.value = BasisGlobalIlluminationFallback.Sky },
                new Setting { Name = "fallbackIntensity",  Low = v => v.fallbackIntensity.value = 0f, High = v => v.fallbackIntensity.value = 4f },
                new Setting { Name = "emitters",           Low = v => v.emitters.value = false,  High = v => v.emitters.value = true },
                new Setting { Name = "emitterIntensity",   Low = v => v.emitterIntensity.value = 0.1f, High = v => v.emitterIntensity.value = 8f },
                new Setting { Name = "emitterOcclusion",   Low = v => v.emitterOcclusion.value = false, High = v => v.emitterOcclusion.value = true, RayTraced = false },
                new Setting { Name = "rayReuse",           Low = v => v.rayReuse.value = false,  High = v => v.rayReuse.value = true, RayTraced = false },
                new Setting { Name = "neighbourhoodClamp", Low = v => v.neighbourhoodClamp.value = false, High = v => v.neighbourhoodClamp.value = true },
                new Setting { Name = "bilateralUpsample",  Low = v => v.bilateralUpsample.value = false, High = v => v.bilateralUpsample.value = true },
                new Setting { Name = "fireflyClamp",       Low = v => v.fireflyClamp.value = 1f, High = v => v.fireflyClamp.value = 32f },
                new Setting { Name = "bounces",            Low = v => { v.overrideQualityCounts.value = true; v.bounces.value = 1; }, High = v => { v.overrideQualityCounts.value = true; v.bounces.value = 4; }, ScreenSpace = false },
                new Setting { Name = "rayTracedLights",    Low = v => v.rayTracedLights.value = false, High = v => v.rayTracedLights.value = true, ScreenSpace = false },
                new Setting { Name = "rayTracedLightIntensity", Low = v => v.rayTracedLightIntensity.value = 0f, High = v => v.rayTracedLightIntensity.value = 4f, ScreenSpace = false },
                new Setting { Name = "rayTracedShadows",   Low = v => v.rayTracedShadows.value = false, High = v => v.rayTracedShadows.value = true, ScreenSpace = false },
                new Setting { Name = "rayTracedEmissive",  Low = v => v.rayTracedEmissiveSurfaces.value = false, High = v => v.rayTracedEmissiveSurfaces.value = true, ScreenSpace = false },
                new Setting { Name = "rayTracedAlbedo",    Low = v => v.rayTracedTextureAlbedo.value = false, High = v => v.rayTracedTextureAlbedo.value = true, ScreenSpace = false },
                new Setting { Name = "rayTracedNormalBias", Low = v => v.rayTracedNormalBias.value = 0f, High = v => v.rayTracedNormalBias.value = 0.5f, ScreenSpace = false },
            };
        }

        private BasisGlobalIlluminationRenderHarness.Reading[] Measure(BasisGlobalIlluminationMode mode)
        {
            if (mode != BasisGlobalIlluminationMode.RayTraced) { return harness.ConvergedReadings(probes, 26, 8); }

            // The traced gather has to be given longer. Its structure and light list are rebuilt between
            // measurements - the scene is only rescanned on a timer, and in edit mode the frame counter that
            // drives that timer barely moves - so it starts each run from nothing, and a run too short to
            // settle shows up as a repeatability floor wide enough to hide half the panel behind.
            harness.ResetRayTracing();
            return harness.ConvergedReadings(probes, 48, 16);
        }

        /// <summary>
        /// How far apart two readings are, taking whichever of the three metrics moved most, over whichever
        /// probe moved most. A setting only has to show itself somewhere to be alive.
        /// </summary>
        private static float Difference(BasisGlobalIlluminationRenderHarness.Reading[] a, BasisGlobalIlluminationRenderHarness.Reading[] b)
        {
            float worst = 0f;
            for (int index = 0; index < a.Length && index < b.Length; index++)
            {
                float level = Mathf.Abs(a[index].Level.r - b[index].Level.r)
                    + Mathf.Abs(a[index].Level.g - b[index].Level.g)
                    + Mathf.Abs(a[index].Level.b - b[index].Level.b);
                float contrast = Mathf.Abs(a[index].Contrast - b[index].Contrast) * 3f;
                float swing = Mathf.Abs(a[index].Swing - b[index].Swing) * 3f;
                float pixel = a[index].PixelDifference(b[index]);
                worst = Mathf.Max(worst, Mathf.Max(Mathf.Max(level, pixel), Mathf.Max(contrast, swing)));
            }
            return worst;
        }

        private void Sweep(BasisGlobalIlluminationMode mode, List<string> dead, StringBuilder report)
        {
            List<Setting> settings = Settings();
            harness.Settings.mode.value = mode;

            // What the same measurement taken twice disagrees by. Anything a setting moves the image by less
            // than this cannot be told apart from the gather's own noise, so it is the floor to judge against.
            Baseline(harness.Settings);
            BasisGlobalIlluminationRenderHarness.Reading[] control = Measure(mode);
            BasisGlobalIlluminationRenderHarness.Reading[] controlAgain = Measure(mode);
            float floor = Mathf.Max(0.004f, Difference(control, controlAgain) * 2f);
            report.Append($"  repeatability floor {floor:F4}  level {control[0].Level} contrast {control[0].Contrast:F4} swing {control[0].Swing:F4}\n");

            for (int index = 0; index < settings.Count; index++)
            {
                Setting setting = settings[index];
                bool applies = mode == BasisGlobalIlluminationMode.ScreenSpace ? setting.ScreenSpace : setting.RayTraced;

                Baseline(harness.Settings);
                setting.Low(harness.Settings);
                BasisGlobalIlluminationRenderHarness.Reading[] low = Measure(mode);

                Baseline(harness.Settings);
                setting.High(harness.Settings);
                BasisGlobalIlluminationRenderHarness.Reading[] high = Measure(mode);

                float delta = Difference(low, high);
                bool moved = delta > floor;
                report.Append($"  {setting.Name,-24} delta {delta:F4} {(moved ? "moved" : "DEAD ")}{(applies ? "" : " (n/a in this mode)")}\n");
                if (applies && !moved) { dead.Add($"{setting.Name} (delta {delta:F4} against floor {floor:F4})"); }
            }

            Baseline(harness.Settings);
        }

        [Test]
        public void EveryProbeIsOnScreen()
        {
            string[] names = { "near", "edge", "corner" };
            for (int index = 0; index < probes.Length; index++)
            {
                RectInt rect = probes[index];
                Assert.IsTrue(rect.xMin >= 0 && rect.yMin >= 0
                    && rect.xMax <= BasisGlobalIlluminationRenderHarness.Width
                    && rect.yMax <= BasisGlobalIlluminationRenderHarness.Height,
                    $"the {names[index]} probe {rect} fell outside the target, so every sweep below would be reading nothing there");
            }
        }

        [Test]
        public void EverySettingChangesTheScreenSpaceImage()
        {
            List<string> dead = new List<string>();
            StringBuilder report = new StringBuilder("[BasisGI] screen space setting sweep\n");
            Sweep(BasisGlobalIlluminationMode.ScreenSpace, dead, report);
            Debug.Log(report.ToString());
            Assert.IsEmpty(dead, "screen space settings that changed nothing on screen: " + string.Join(", ", dead));
        }

        [Test]
        public void EverySettingChangesTheRayTracedImage()
        {
            StringBuilder report = new StringBuilder("[BasisGI] ray traced setting sweep\n");
            report.Append($"  {harness.Describe()}\n");
            if (!harness.RayTracingAvailable)
            {
                Debug.Log(report.ToString());
                Assert.Ignore("This GPU cannot run the ray traced mode.");
            }

            List<string> dead = new List<string>();
            Sweep(BasisGlobalIlluminationMode.RayTraced, dead, report);
            report.Append($"  traced={harness.RayTracingRan}\n");
            Debug.Log(report.ToString());

            if (!harness.RayTracingRan) { Assert.Ignore("The ray traced mode fell back to screen space on this GPU."); }
            Assert.IsEmpty(dead, "ray traced settings that changed nothing on screen: " + string.Join(", ", dead));
        }
    }
}
