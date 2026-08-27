using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// End to end: a grey room with one small bright red emissive block, rendered through the project's own
    /// renderer, read back a pixel at a time. These are the only tests here that can see whether light
    /// actually arrives and whether it holds still once it has.
    ///
    /// Every measurement is taken through the effect's own debug view rather than the composited image.
    /// A probe on a lit floor is mostly floor: measuring a bounce through the composite's multiply hides
    /// both how much of it there is and how far it moved between frames.
    /// </summary>
    public class BasisGlobalIlluminationRenderTests
    {
        // A block rather than a panel: a thin slab stands edge on to the floor and lights almost none of
        // it, which measures the harness rather than the effect.
        private static readonly Vector3 BlockCentre = new Vector3(-0.7f, 0.6f, 0.55f);
        private static readonly Vector3 BlockSize = new Vector3(0.45f, 0.45f, 0.45f);
        private static readonly Vector3 NearProbe = new Vector3(-0.05f, 0.101f, 0.55f);
        private static readonly Vector3 FarProbe = new Vector3(1.05f, 0.101f, 0.55f);

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

        /// <summary>A grey room lit by one dim sun, so anything red at a probe came from the bounce.</summary>
        private void BuildRoom()
        {
            harness.AddSun(Quaternion.Euler(52f, -24f, 0f), 0.5f);

            Material surface = harness.CreateLitMaterial(new Color(0.8f, 0.8f, 0.8f), Color.black);
            harness.AddBox(new Vector3(0f, 0f, 0f), new Vector3(14f, 0.2f, 14f), surface);
            harness.AddBox(new Vector3(0f, 2f, 3.2f), new Vector3(14f, 5f, 0.2f), surface);
            harness.AddBox(new Vector3(-3.4f, 2f, 0f), new Vector3(0.2f, 5f, 9f), surface);

            harness.Camera.transform.position = new Vector3(0.1f, 1.7f, -2.6f);
            harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(-0.1f, 0.3f, 0.6f) - harness.Camera.transform.position, Vector3.up);
        }

        private Material AddEmissiveBlock(Color emission)
        {
            Material block = harness.CreateLitMaterial(Color.black, emission);
            harness.AddBox(BlockCentre, BlockSize, block);
            return block;
        }

        // Wide enough that the reading is an average over many traced texels rather than a handful.
        // Interleaved gradient noise decorrelates neighbouring pixels, so area is what converges it.
        private RectInt Probe(Vector3 worldPoint, int radius = 7)
        {
            Vector3 screen = harness.Camera.WorldToScreenPoint(worldPoint);
            return new RectInt(Mathf.RoundToInt(screen.x) - radius, Mathf.RoundToInt(screen.y) - radius, radius * 2, radius * 2);
        }

        private static bool OnScreen(RectInt probe)
        {
            return probe.xMin >= 0 && probe.yMin >= 0
                && probe.xMax <= BasisGlobalIlluminationRenderHarness.Width
                && probe.yMax <= BasisGlobalIlluminationRenderHarness.Height;
        }

        /// <summary>The effect's own output at the probe, settled and averaged.</summary>
        private Color Indirect(RectInt probe, int warmup = 24)
        {
            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            Color sample = harness.Converged(probe, warmup);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            return sample;
        }

        /// <summary>How red a reading is, against its own other channels rather than against a baseline.</summary>
        private static float Tint(Color colour) { return colour.r - colour.g; }

        [Test]
        public void TheProbesAndTheBlockAreAllOnScreen()
        {
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));

            Assert.IsTrue(OnScreen(Probe(NearProbe)), $"the near floor probe {Probe(NearProbe)} fell outside the target");
            Assert.IsTrue(OnScreen(Probe(FarProbe)), $"the far floor probe {Probe(FarProbe)} fell outside the target");
            Assert.IsTrue(OnScreen(Probe(BlockCentre)), $"the emissive block {Probe(BlockCentre)} fell outside the target, so the screen space gather could never find it");

            harness.Settings.enable.value = false;
            harness.Render();
            Color block = harness.Sample(Probe(BlockCentre, 2));
            Color floor = harness.Sample(Probe(NearProbe));
            Debug.Log($"[BasisGI] block={block} floor={floor}");
            Assert.Greater(block.r, floor.r + 0.3f, $"the emissive block rendered at {block} against a floor at {floor}, so it is not emitting and the bounce tests would have nothing to gather");
            Assert.Greater(Tint(block), Tint(floor) + 0.2f, $"the emissive block rendered at {block}, which is not red");
        }

        [Test]
        public void TheEffectReachesTheImageAtAll()
        {
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            RectInt probe = Probe(NearProbe);

            harness.Settings.enable.value = false;
            for (int frame = 0; frame < 4; frame++) { harness.Render(); }
            Color off = harness.RenderAndSample(probe);

            harness.Settings.enable.value = true;
            for (int frame = 0; frame < 16; frame++) { harness.Render(); }
            Color on = harness.RenderAndSample(probe);

            float difference = Mathf.Abs(on.r - off.r) + Mathf.Abs(on.g - off.g) + Mathf.Abs(on.b - off.b);
            Debug.Log($"[BasisGI] reach: off={off} on={on} diff={difference:F5} | {harness.Describe()}");
            Assert.Greater(difference, 1e-4f,
                $"turning the effect on changed nothing at all, so it never ran: off={off}, on={on}. {harness.Describe()}");
        }

        [Test]
        public void AnEmissiveBlockBouncesItsColourOntoTheFloor()
        {
            BuildRoom();
            Material block = AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            RectInt probe = Probe(NearProbe);

            Color lit = Indirect(probe);
            BasisGlobalIlluminationRenderHarness.SetEmission(block, Color.black);
            Color dark = Indirect(probe);

            Debug.Log($"[BasisGI] bounce: lit={lit} dark={dark} tint {Tint(dark):F4} -> {Tint(lit):F4}");
            Assert.Greater(Tint(lit), Tint(dark) + 0.01f,
                $"the floor beside a bright red block gathered no red from it: lit={lit}, dark={dark}");
        }

        [Test]
        public void TheBounceFallsOffWithDistanceFromTheBlock()
        {
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));

            float near = Tint(Indirect(Probe(NearProbe)));
            float far = Tint(Indirect(Probe(FarProbe)));

            Debug.Log($"[BasisGI] falloff: near={near:F4} far={far:F4}");
            Assert.Greater(near, far,
                $"the floor a metre further from the block gathered as much red as the floor beside it ({near:F4} against {far:F4}), so the bounce is not being placed, it is being smeared");
        }

        [Test]
        public void AnEmissiveBounceHoldsStillUnderCameraJitter()
        {
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            RectInt probe = Probe(NearProbe);

            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            Color[] samples = harness.RenderJitteredRun(probe, 30);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);

            float mean = BasisGlobalIlluminationRenderHarness.Mean(samples, BasisGlobalIlluminationRenderHarness.Red);
            float swing = BasisGlobalIlluminationRenderHarness.RelativeFrameToFrameSwing(samples, BasisGlobalIlluminationRenderHarness.Red);
            float range = BasisGlobalIlluminationRenderHarness.Range(samples, BasisGlobalIlluminationRenderHarness.Red);

            Debug.Log($"[BasisGI] emissive flicker: mean={mean:F4} swing={swing:F4} range={range:F4}");
            Assert.Greater(mean, 0.002f, "the probe gathered nothing, so this run measured the stability of black");
            Assert.Less(swing, 0.03f,
                $"the bounce off a bright emissive block swung {swing:P1} of its own level between consecutive frames while the scene stood still - that is the flicker (mean {mean:F4}, range {range:F4})");
        }

        [Test]
        public void AnAnalyticEmitterHoldsStillUnderCameraJitter()
        {
            BuildRoom();
            harness.AddEmitter(BlockCentre, Color.red, 8f, 0.25f, 10f);
            RectInt probe = Probe(NearProbe);

            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            Color[] samples = harness.RenderJitteredRun(probe, 30);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);

            float mean = BasisGlobalIlluminationRenderHarness.Mean(samples, BasisGlobalIlluminationRenderHarness.Red);
            float swing = BasisGlobalIlluminationRenderHarness.RelativeFrameToFrameSwing(samples, BasisGlobalIlluminationRenderHarness.Red);
            float range = BasisGlobalIlluminationRenderHarness.Range(samples, BasisGlobalIlluminationRenderHarness.Red);

            Debug.Log($"[BasisGI] emitter flicker: mean={mean:F4} swing={swing:F4} range={range:F4}");
            Assert.Greater(mean, 0.002f, "the probe gathered nothing from the emitter, so this run measured the stability of black");
            Assert.Less(swing, 0.03f,
                $"a registered emitter's light swung {swing:P1} of its own level between consecutive frames while the scene stood still (mean {mean:F4}, range {range:F4})");
        }

        /// <summary>
        /// An emitter walled off from the probe has to stay walled off, and it has to stop being walled off
        /// gradually if at all. The emitter, the wall and the probe never move relative to each other; only
        /// the camera does, dollying forward until the emitter leaves the frame and then passes behind the
        /// near plane. What the shadow is worth is judged against what the same emitter delivers with the
        /// wall removed, so the numbers mean something regardless of how bright the emitter is.
        ///
        /// A screen space shadow can only test what the camera drew, so once the wall itself leaves the
        /// frame there is nothing left to test against and some light does get through - that is the honest
        /// floor of the technique, and it is why emitters exist in the first place. What must not happen is
        /// a step: the light arriving all at once, with nothing on screen to explain it. The rate is
        /// therefore measured per metre the camera travelled rather than per sample of this sweep, which is
        /// the only form of the question that does not change answer when the sweep is made finer.
        /// </summary>
        [Test]
        public void AWalledOffEmitterStaysWalledOffWhenItLeavesTheView()
        {
            BuildRoom();
            Material surface = harness.CreateLitMaterial(new Color(0.8f, 0.8f, 0.8f), Color.black);
            GameObject wall = harness.AddBox(new Vector3(-0.1f, 0.85f, 0.55f), new Vector3(0.12f, 1.5f, 1.6f), surface);

            Vector3 probePoint = new Vector3(0.95f, 0.101f, 0.55f);
            Vector3 emitterPoint = new Vector3(-1.15f, 0.9f, 0.55f);
            BasisGlobalIlluminationEmitter emitter = harness.AddEmitter(emitterPoint, Color.red, 45f, 0.45f, 12f);

            const int steps = 13;
            const float travel = 3.7f;
            float[] leak = new float[steps];
            float unobstructed = 0f;

            for (int step = 0; step < steps; step++)
            {
                float z = Mathf.Lerp(-2.4f, -2.4f + travel, step / (float)(steps - 1));
                harness.Camera.transform.position = new Vector3(0.95f, 1.7f, z);
                harness.Camera.transform.rotation = Quaternion.LookRotation(probePoint - harness.Camera.transform.position, Vector3.up);

                RectInt probe = Probe(probePoint, 5);
                if (!OnScreen(probe)) { leak[step] = float.NaN; continue; }

                emitter.enabled = true;
                float withEmitter = Tint(Indirect(probe, 14));
                emitter.enabled = false;
                float withoutEmitter = Tint(Indirect(probe, 14));
                leak[step] = withEmitter - withoutEmitter;

                if (step == 0)
                {
                    wall.SetActive(false);
                    emitter.enabled = true;
                    float open = Tint(Indirect(probe, 14));
                    emitter.enabled = false;
                    unobstructed = open - withoutEmitter;
                    wall.SetActive(true);
                }
            }
            emitter.enabled = true;

            float worstStep = 0f;
            int worstStepIndex = -1;
            float previous = float.NaN;
            float inViewTotal = 0f;
            int inViewCount = 0;
            System.Text.StringBuilder trace = new System.Text.StringBuilder();
            for (int step = 0; step < steps; step++)
            {
                trace.Append(step).Append('=').Append(leak[step].ToString("F4")).Append(' ');
                if (float.IsNaN(leak[step])) { continue; }
                if (!float.IsNaN(previous))
                {
                    float delta = Mathf.Abs(leak[step] - previous);
                    if (delta > worstStep) { worstStep = delta; worstStepIndex = step; }
                }
                previous = leak[step];
                // The first half of the dolly is the part where the wall is still comfortably in frame.
                if (step * 2 < steps) { inViewTotal += leak[step]; inViewCount++; }
            }

            float metresPerStep = travel / (steps - 1);
            float ratePerMetre = worstStep / Mathf.Max(unobstructed, 1e-5f) / metresPerStep;
            float inViewLeak = inViewCount > 0 ? inViewTotal / inViewCount : 0f;

            Debug.Log($"[BasisGI] emitter leak by step: {trace}| unobstructed={unobstructed:F4} inView={inViewLeak:F4} rate={ratePerMetre:F2}/m");
            Assert.Greater(unobstructed, 0.02f,
                $"the emitter delivered {unobstructed:F4} with nothing in the way, so this run had no shadow to measure");
            Assert.Less(inViewLeak, unobstructed * 0.15f,
                $"with the wall in frame the emitter still put {inViewLeak / unobstructed:P0} of its unobstructed light on the floor, so it is not being shadowed. Per step: {trace}");
            Assert.Less(ratePerMetre, 2.5f,
                $"the emitter's light arrived at {ratePerMetre:F1} times its unobstructed value per metre the camera travelled, worst at step {worstStepIndex} - that is a pop rather than a fade. Per step: {trace}");
        }

        private void SkipWithoutRayTracing()
        {
            if (!harness.RayTracingAvailable)
            {
                Assert.Ignore("This GPU cannot run the ray traced mode, so the screen space gather is all there is to test.");
            }
        }

        /// <summary>Switches to the traced gather and rebuilds the structure so it sees the scene as it is now.</summary>
        private void UseRayTracing()
        {
            harness.Settings.mode.value = BasisGlobalIlluminationMode.RayTraced;
            harness.ResetRayTracing();
        }

        [Test]
        public void TheRayTracedModeActuallyTraces()
        {
            SkipWithoutRayTracing();
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            UseRayTracing();

            for (int frame = 0; frame < 6; frame++) { harness.Render(); }
            Debug.Log($"[BasisGI] ray traced: {harness.Describe()}");
            Assert.IsTrue(harness.RayTracingRan,
                $"the ray traced mode fell back to the screen space gather, so every traced test below would be measuring the wrong path. {harness.Describe()}");
        }

        [Test]
        public void TheRayTracedModeBouncesAnEmissiveBlock()
        {
            SkipWithoutRayTracing();
            BuildRoom();
            Material block = AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            UseRayTracing();
            RectInt probe = Probe(NearProbe);

            Color lit = Indirect(probe, 30);
            if (!harness.RayTracingRan) { Assert.Ignore("The ray traced mode fell back to screen space on this GPU."); }

            BasisGlobalIlluminationRenderHarness.SetEmission(block, Color.black);
            harness.ResetRayTracing();
            Color dark = Indirect(probe, 30);

            Debug.Log($"[BasisGI] traced bounce: lit={lit} dark={dark} tint {Tint(dark):F4} -> {Tint(lit):F4}");
            Assert.Greater(Tint(lit), Tint(dark) + 0.01f,
                $"the traced gather found no red on an emissive block it can see from any angle: lit={lit}, dark={dark}");
        }

        [Test]
        public void TheRayTracedModeHoldsStillUnderCameraJitter()
        {
            SkipWithoutRayTracing();
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            UseRayTracing();
            RectInt probe = Probe(NearProbe);

            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            Color[] samples = harness.RenderJitteredRun(probe, 30, 20);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            if (!harness.RayTracingRan) { Assert.Ignore("The ray traced mode fell back to screen space on this GPU."); }

            float mean = BasisGlobalIlluminationRenderHarness.Mean(samples, BasisGlobalIlluminationRenderHarness.Red);
            float swing = BasisGlobalIlluminationRenderHarness.RelativeFrameToFrameSwing(samples, BasisGlobalIlluminationRenderHarness.Red);
            float range = BasisGlobalIlluminationRenderHarness.Range(samples, BasisGlobalIlluminationRenderHarness.Red);

            Debug.Log($"[BasisGI] traced flicker: mean={mean:F4} swing={swing:F4} range={range:F4}");
            Assert.Greater(mean, 0.002f, "the probe gathered nothing, so this run measured the stability of black");
            Assert.Less(swing, 0.06f,
                $"the traced bounce swung {swing:P1} of its own level between consecutive frames while the scene stood still (mean {mean:F4}, range {range:F4})");
        }

        /// <summary>
        /// A room with more lights than the shadow ray budget has to keep bouncing all of them. Resampling
        /// is what makes that affordable: without it the light list has to be short, and a light dropping
        /// out of a short list as the player moves is a step change with nothing on screen to explain it.
        /// </summary>
        [Test]
        public void TheRayTracedModeCarriesMoreLightsThanItShadowRays()
        {
            SkipWithoutRayTracing();
            BuildRoom();
            UseRayTracing();

            for (int index = 0; index < 20; index++)
            {
                GameObject host = harness.Own(new GameObject("BasisGIRoomLight"));
                host.transform.position = new Vector3(-2.5f + index * 0.26f, 1.4f, 0.2f);
                Light light = host.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = index == 0 ? Color.red : Color.white;
                light.intensity = index == 0 ? 40f : 0.4f;
                light.range = 6f;
                light.shadows = LightShadows.None;
            }
            harness.ResetRayTracing();

            RectInt probe = Probe(NearProbe);
            Color lit = Indirect(probe, 30);
            if (!harness.RayTracingRan) { Assert.Ignore("The ray traced mode fell back to screen space on this GPU."); }

            Debug.Log($"[BasisGI] many lights: {lit} tint={Tint(lit):F4} | {harness.Describe()}");
            Assert.Greater(Tint(lit), 0.004f,
                $"the one bright red light in a room of twenty never reached the bounce, so the light budget dropped it: {lit}");
        }
    }
}
