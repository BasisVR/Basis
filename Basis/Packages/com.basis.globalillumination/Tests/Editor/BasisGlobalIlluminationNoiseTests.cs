using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// What the gather looks like rather than where it landed: how much grain a viewer is left with once
    /// every stage of the denoiser has had its turn, and how much of it each stage actually removed.
    ///
    /// The flicker tests next door watch one probe move between frames. This watches one frame across
    /// pixels, which is the other half of the same question and the one a still player complains about.
    /// Each stage is measured by switching the ones after it off, so a stage that is not earning its
    /// frame time says so in the numbers rather than being taken on faith.
    /// </summary>
    public class BasisGlobalIlluminationNoiseTests
    {
        private static readonly Vector3 BlockCentre = new Vector3(-0.7f, 0.6f, 0.55f);
        private static readonly Vector3 BlockSize = new Vector3(0.45f, 0.45f, 0.45f);

        // Floor to the right of the emissive block, so the region holds no silhouette of its own: an edge
        // in the frame reads to the estimator exactly like grain does.
        private static readonly Vector3 RegionNear = new Vector3(0.15f, 0.101f, 0.30f);
        private static readonly Vector3 RegionFar = new Vector3(1.30f, 0.101f, 1.05f);

        private BasisGlobalIlluminationRenderHarness harness;

        [SetUp]
        public void SetUp()
        {
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            BasisGlobalIlluminationEmitter.Registered.Clear();
            harness = new BasisGlobalIlluminationRenderHarness();
        }

        [TearDown]
        public void TearDown()
        {
            harness?.Dispose();
            harness = null;
        }

        private void BuildRoom()
        {
            harness.AddSun(Quaternion.Euler(52f, -24f, 0f), 0.5f);

            Material surface = harness.CreateLitMaterial(new Color(0.8f, 0.8f, 0.8f), Color.black);
            harness.AddBox(new Vector3(0f, 0f, 0f), new Vector3(14f, 0.2f, 14f), surface);
            harness.AddBox(new Vector3(0f, 2f, 3.2f), new Vector3(14f, 5f, 0.2f), surface);
            harness.AddBox(new Vector3(-3.4f, 2f, 0f), new Vector3(0.2f, 5f, 9f), surface);

            Material block = harness.CreateLitMaterial(Color.black, new Color(16f, 0.5f, 0.5f));
            harness.AddBox(BlockCentre, BlockSize, block);

            harness.Camera.transform.position = new Vector3(0.1f, 1.7f, -2.6f);
            harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(-0.1f, 0.3f, 0.6f) - harness.Camera.transform.position, Vector3.up);
        }

        /// <summary>The patch of open floor the grain is read off, as pixels of the target.</summary>
        private RectInt Region()
        {
            Vector3 near = harness.Camera.WorldToScreenPoint(RegionNear);
            Vector3 far = harness.Camera.WorldToScreenPoint(RegionFar);
            int xMin = Mathf.RoundToInt(Mathf.Min(near.x, far.x));
            int xMax = Mathf.RoundToInt(Mathf.Max(near.x, far.x));
            int yMin = Mathf.RoundToInt(Mathf.Min(near.y, far.y));
            int yMax = Mathf.RoundToInt(Mathf.Max(near.y, far.y));
            return new RectInt(xMin, yMin, Mathf.Max(4, xMax - xMin), Mathf.Max(4, yMax - yMin));
        }

        /// <summary>Grain in the effect's own output, with the stages after the one being measured switched off.</summary>
        private BasisGlobalIlluminationRenderHarness.Grain Grain(bool temporal, float smoothing, bool wide)
        {
            harness.Settings.temporalFilter.value = temporal;
            harness.Settings.smoothing.value = smoothing;
            harness.Settings.wideBlur.value = wide;
            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            BasisGlobalIlluminationRenderHarness.Grain grain = harness.MeasuredGrain(
                Region(), BasisGlobalIlluminationRenderHarness.Luma);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            return grain;
        }

        private string Table(out BasisGlobalIlluminationRenderHarness.Grain full)
        {
            BasisGlobalIlluminationRenderHarness.Grain raw = Grain(false, 0f, false);
            BasisGlobalIlluminationRenderHarness.Grain temporal = Grain(true, 0f, false);
            BasisGlobalIlluminationRenderHarness.Grain narrow = Grain(true, 1f, false);
            full = Grain(true, 1f, true);
            return $"trace[{raw}] +temporal[{temporal}] +blur[{narrow}] +wide[{full}]";
        }

        private void SkipWithoutRayTracing()
        {
            if (!harness.RayTracingAvailable)
            {
                Assert.Ignore("This GPU cannot run the ray traced mode, so the screen space gather is all there is to test.");
            }
        }

        [Test]
        public void TheRegionIsOpenFloorAndNotSky()
        {
            BuildRoom();
            RectInt region = Region();
            Assert.IsTrue(region.xMin >= 1 && region.yMin >= 1
                && region.xMax <= BasisGlobalIlluminationRenderHarness.Width - 1
                && region.yMax <= BasisGlobalIlluminationRenderHarness.Height - 1,
                $"the grain region {region} fell outside the target, so every reading below is of the frame's edge");
            Assert.Greater(region.width * region.height, 400,
                $"the grain region {region} is too small to estimate a deviation from");

            harness.Settings.enable.value = false;
            harness.Render();
            Color floor = harness.Sample(region);
            Debug.Log($"[BasisGI] grain region {region} reads {floor} with the effect off");
            Assert.Greater(floor.g, 0.01f, $"the grain region is not on lit floor: it reads {floor} with the effect off");
        }

        [Test]
        public void TheScreenSpaceGatherIsNotGrainy()
        {
            BuildRoom();
            string table = Table(out BasisGlobalIlluminationRenderHarness.Grain full);

            Debug.Log($"[BasisGI] screen space grain: {table}");
            Assert.Greater(full.Level, 0.002f, "the region gathered nothing, so this run measured the grain of black");
            Assert.Less(full.Relative, 0.06f,
                $"the denoised screen space gather still carries {full.Relative:P1} grain on open floor. {table}");
        }

        [Test]
        public void TheRayTracedGatherIsNotGrainy()
        {
            SkipWithoutRayTracing();
            BuildRoom();
            harness.Settings.mode.value = BasisGlobalIlluminationMode.RayTraced;
            harness.ResetRayTracing();

            string table = Table(out BasisGlobalIlluminationRenderHarness.Grain full);
            if (!harness.RayTracingRan) { Assert.Ignore("The ray traced mode fell back to screen space on this GPU."); }

            Debug.Log($"[BasisGI] ray traced grain: {table}");
            Assert.Greater(full.Level, 0.002f, "the region gathered nothing, so this run measured the grain of black");
            Assert.Less(full.Relative, 0.06f,
                $"the denoised traced gather still carries {full.Relative:P1} grain on open floor. {table}");
        }

        /// <summary>
        /// Every stage has to be worth its frame time. A stage that leaves the grain where it found it is
        /// either misconfigured or filtering something other than what it was pointed at, and either way
        /// the numbers say so before anybody has to look at a frame.
        /// </summary>
        [Test]
        public void EveryDenoiserStageRemovesGrain()
        {
            BuildRoom();
            BasisGlobalIlluminationRenderHarness.Grain raw = Grain(false, 0f, false);
            BasisGlobalIlluminationRenderHarness.Grain temporal = Grain(true, 0f, false);
            BasisGlobalIlluminationRenderHarness.Grain blurred = Grain(true, 1f, true);

            Debug.Log($"[BasisGI] stage contribution: trace[{raw}] +temporal[{temporal}] +blur[{blurred}]");
            Assert.Greater(raw.Level, 0.002f, "the region gathered nothing, so this run measured the grain of black");
            Assert.Less(temporal.Relative, raw.Relative * 0.8f,
                $"the temporal filter left the grain at {temporal.Relative:P1} of a signal that arrived at {raw.Relative:P1}");
            Assert.Less(blurred.Relative, temporal.Relative * 0.8f,
                $"the spatial filter left the grain at {blurred.Relative:P1} of a signal that reached it at {temporal.Relative:P1}");
        }
    }
}
