using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// What the gather looks like rather than where it landed: how much grain a viewer is left with once
    /// every stage of the denoiser has had its turn, and how much of it each stage actually removed.
    ///
    /// The flicker tests next door watch one probe move between frames. This watches one frame across
    /// pixels, which is the other half of the same question and the one a still player complains about.
    ///
    /// Two things about how these are set up, both learned the hard way:
    ///
    /// The environment fallback is switched off. With it on, most rays in an open room miss and come back
    /// carrying a convolved cubemap - a smooth, almost constant term that dominates the reading and drags
    /// the measured grain down to a fraction of a percent no matter what the denoiser does. What is noisy
    /// here is the part that was gathered by tracing, so that is the part these measure.
    ///
    /// The camera drifts. A denoiser measured from a settled accumulation is being flattered: reprojection
    /// finds history for every pixel, and the spatial pass is barely asked to do anything. Walking pace is
    /// where a sparse gather actually shows.
    /// </summary>
    public class BasisGlobalIlluminationNoiseTests
    {
        private static readonly Vector3 BlockCentre = new Vector3(-0.7f, 0.6f, 0.55f);
        private static readonly Vector3 BlockSize = new Vector3(0.45f, 0.45f, 0.45f);

        // Open floor to the right of the emissive block, so the region holds no silhouette of its own: an
        // edge in the frame reads to the estimator exactly like grain does.
        private static readonly Vector3 RegionNear = new Vector3(0.0f, 0.101f, -1.0f);
        private static readonly Vector3 RegionFar = new Vector3(2.0f, 0.101f, 0.5f);

        // A shade over a metre a second at sixty frames, straight across the view, which is the direction
        // that disoccludes the most floor per metre travelled.
        private static readonly Vector3 WalkingPace = new Vector3(0.02f, 0f, 0f);

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

        /// <summary>A grey room with one bright emissive block, and nothing but the trace to light it.</summary>
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

            // Only what the rays themselves brought back. See the class note.
            harness.Settings.fallback.value = BasisGlobalIlluminationFallback.None;
        }

        /// <summary>The patch of open floor the grain is read off, as pixels of the target.</summary>
        private RectInt Region()
        {
            Vector3 near = harness.Camera.WorldToScreenPoint(RegionNear);
            Vector3 far = harness.Camera.WorldToScreenPoint(RegionFar);
            int xMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(near.x, far.x)), 1, BasisGlobalIlluminationRenderHarness.Width - 2);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(near.x, far.x)), 1, BasisGlobalIlluminationRenderHarness.Width - 2);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(near.y, far.y)), 1, BasisGlobalIlluminationRenderHarness.Height - 2);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(near.y, far.y)), 1, BasisGlobalIlluminationRenderHarness.Height - 2);
            return new RectInt(xMin, yMin, Mathf.Max(4, xMax - xMin), Mathf.Max(4, yMax - yMin));
        }

        /// <summary>
        /// Switches to the traced gather and waits for the structure to come back up.
        ///
        /// Rebuilding the tracer throws away the resolved average colour of every texture, and those come
        /// back through AsyncGPUReadback - whose callbacks a manual render loop never dispatches, so
        /// without the wait they land at a different frame each run and a traced reading does not mean the
        /// same thing twice.
        /// </summary>
        private void UseRayTracing()
        {
            harness.Settings.mode.value = BasisGlobalIlluminationMode.RayTraced;
            harness.ResetRayTracing();
            AsyncGPUReadback.WaitAllRequests();
        }

        private void SkipWithoutRayTracing()
        {
            if (!harness.RayTracingAvailable)
            {
                Assert.Ignore("This GPU cannot run the ray traced mode, so the screen space gather is all there is to test.");
            }
        }

        /// <summary>Grain in the effect's own output, with the stages after the one being measured switched off.</summary>
        private BasisGlobalIlluminationRenderHarness.Grain Grain(bool temporal, float smoothing, bool wide,
            BasisGlobalIlluminationDebugView view = BasisGlobalIlluminationDebugView.Indirect, bool moving = false)
        {
            harness.Settings.temporalFilter.value = temporal;
            harness.Settings.smoothing.value = smoothing;
            harness.Settings.wideBlur.value = wide;
            harness.SetDebugView(view);
            BasisGlobalIlluminationRenderHarness.Grain grain = harness.MeasuredGrain(
                Region(), BasisGlobalIlluminationRenderHarness.Luma,
                drift: moving ? WalkingPace : Vector3.zero);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            return grain;
        }

        private string StageTable(out BasisGlobalIlluminationRenderHarness.Grain full, bool moving = false)
        {
            BasisGlobalIlluminationRenderHarness.Grain raw = Grain(false, 0f, false, moving: moving);
            BasisGlobalIlluminationRenderHarness.Grain temporal = Grain(true, 0f, false, moving: moving);
            BasisGlobalIlluminationRenderHarness.Grain narrow = Grain(true, 1f, false, moving: moving);
            full = Grain(true, 1f, true, moving: moving);
            return $"trace[{raw}] +temporal[{temporal}] +blur[{narrow}] +wide[{full}]";
        }

        [Test]
        public void TheGrainRegionIsOpenLitFloor()
        {
            BuildRoom();
            RectInt region = Region();
            Assert.Greater(region.width * region.height, 900,
                $"the grain region {region} is too small to estimate a deviation from");

            harness.Settings.enable.value = false;
            harness.Render();
            Color floor = harness.Sample(region);
            float grain = harness.SpatialNoise(region, BasisGlobalIlluminationRenderHarness.Luma);
            Debug.Log($"[BasisGI] grain region {region} reads {floor} with the effect off, own grain {grain:F5}");

            Assert.Greater(floor.g, 0.01f, $"the grain region is not on lit floor: it reads {floor} with the effect off");
            Assert.Less(grain, 0.004f,
                $"the region is not flat with the effect off - it already carries {grain:F5} of its own structure, " +
                $"so anything measured on top of it is the scene, not the gather");
        }

        [Test]
        public void TheScreenSpaceGatherIsNotGrainy()
        {
            BuildRoom();
            string table = StageTable(out BasisGlobalIlluminationRenderHarness.Grain full);

            Debug.Log($"[BasisGI] screen space grain: {table}");
            Assert.Greater(full.Level, 0.002f, "the region gathered nothing, so this run measured the grain of black");
            Assert.Less(full.Relative, 0.0015f,
                $"the denoised screen space gather still carries {full.Relative:P2} grain on open floor. {table}");
        }

        [Test]
        public void TheRayTracedGatherIsNotGrainy()
        {
            SkipWithoutRayTracing();
            BuildRoom();
            UseRayTracing();

            string table = StageTable(out BasisGlobalIlluminationRenderHarness.Grain full);
            if (!harness.RayTracingRan) { Assert.Ignore("The ray traced mode fell back to screen space on this GPU."); }

            Debug.Log($"[BasisGI] ray traced grain: {table}");
            Assert.Greater(full.Level, 0.002f, "the region gathered nothing, so this run measured the grain of black");
            Assert.Less(full.Relative, 0.008f,
                $"the denoised traced gather still carries {full.Relative:P2} grain on open floor. {table}");
        }

        /// <summary>
        /// The obscurance channel is a separate signal that rides the same buffer, and at a couple of rays a
        /// pixel it is the coarsest thing in the frame - two rays can only report nothing, half or all. It
        /// is composited as a multiply, so its grain lands on every lit surface rather than only where the
        /// bounce reached.
        /// </summary>
        [Test]
        public void TheObscuranceIsNotGrainy()
        {
            BuildRoom();
            BasisGlobalIlluminationRenderHarness.Grain full = Grain(true, 1f, true, BasisGlobalIlluminationDebugView.Obscurance);

            Debug.Log($"[BasisGI] obscurance grain: {full}");
            Assert.Greater(full.Level, 0.02f, "the obscurance channel read as black, so this run measured nothing");
            Assert.Less(full.Relative, 0.06f,
                $"the obscurance channel carries {full.Relative:P1} grain, and it multiplies every lit surface. {full}");
        }

        /// <summary>
        /// The same measurement with the camera walking. This is the one that matters: a denoiser measured
        /// from a settled accumulation is being flattered, because reprojection finds history for every
        /// pixel and the spatial pass is never asked for anything.
        /// </summary>
        [Test]
        public void TheGatherStaysSmoothWhileTheCameraMoves()
        {
            BuildRoom();
            BasisGlobalIlluminationRenderHarness.Grain still = Grain(true, 1f, true);
            BasisGlobalIlluminationRenderHarness.Grain moving = Grain(true, 1f, true, moving: true);

            Debug.Log($"[BasisGI] screen space walking: still[{still}] moving[{moving}]");
            Assert.Greater(moving.Level, 0.002f, "the region gathered nothing while moving, so this run measured black");
            Assert.Less(moving.Relative, 0.0025f,
                $"walking pace left {moving.Relative:P2} grain against {still.Relative:P2} standing still. still[{still}] moving[{moving}]");
        }

        [Test]
        public void TheTracedGatherStaysSmoothWhileTheCameraMoves()
        {
            SkipWithoutRayTracing();
            BuildRoom();
            UseRayTracing();

            BasisGlobalIlluminationRenderHarness.Grain still = Grain(true, 1f, true);
            BasisGlobalIlluminationRenderHarness.Grain moving = Grain(true, 1f, true, moving: true);
            if (!harness.RayTracingRan) { Assert.Ignore("The ray traced mode fell back to screen space on this GPU."); }

            Debug.Log($"[BasisGI] ray traced walking: still[{still}] moving[{moving}]");
            Assert.Greater(moving.Level, 0.002f, "the region gathered nothing while moving, so this run measured black");
            Assert.Less(moving.Relative, 0.01f,
                $"walking pace left {moving.Relative:P2} grain against {still.Relative:P2} standing still. still[{still}] moving[{moving}]");
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
