using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Basis.Tests.GlobalIllumination
{
    public class BasisGlobalIlluminationCascadeTests
    {
        private static readonly Vector3 BlockCentre = new Vector3(-0.7f, 0.6f, 0.55f);
        private static readonly Vector3 BlockSize = new Vector3(0.45f, 0.45f, 0.45f);
        private static readonly Vector3 NearProbe = new Vector3(-0.05f, 0.101f, 0.55f);
        private static readonly Vector3 FarProbe = new Vector3(1.05f, 0.101f, 0.55f);
        private static readonly Vector3 OpenProbe = new Vector3(1.4f, 0.101f, -0.6f);

        private BasisGlobalIlluminationRenderHarness harness;
        private bool previousAsync;

        [SetUp]
        public void SetUp()
        {
            previousAsync = UnityEditor.ShaderUtil.allowAsyncCompilation;
            UnityEditor.ShaderUtil.allowAsyncCompilation = false;
        }

        [TearDown]
        public void TearDown()
        {
            harness?.Dispose();
            harness = null;
            UnityEditor.ShaderUtil.allowAsyncCompilation = previousAsync;
            BasisGlobalIlluminationPass.ReleaseWorld();
        }

        [Test]
        public void TheSolidAnglesCoverTheWholeSphere()
        {
            float[] angles = BasisGlobalIlluminationPass.ComputeCascadeSolidAngles();
            float total = 0f, smallest = float.MaxValue, largest = 0f;
            foreach (float angle in angles)
            {
                total += angle;
                smallest = Mathf.Min(smallest, angle);
                largest = Mathf.Max(largest, angle);
            }
            Debug.Log($"[BasisGI] cascade solid angles: total={total:F5} smallest={smallest:F4} largest={largest:F4}");
            Assert.AreEqual(4f * Mathf.PI, total, 1e-3f);
            Assert.Greater(smallest, 0f);
            Assert.Less(largest / smallest, 3f);
        }

        [Test]
        public void TheSolidAnglesMatchAMonteCarloCount()
        {
            float[] angles = BasisGlobalIlluminationPass.ComputeCascadeSolidAngles();
            int[] hits = new int[angles.Length];
            System.Random random = new System.Random(7);
            const int samples = 200000;
            for (int i = 0; i < samples; i++)
            {
                Vector3 direction;
                do { direction = new Vector3((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f); }
                while (direction.sqrMagnitude > 1f || direction.sqrMagnitude < 1e-6f);
                direction.Normalize();
                Vector2 oct = OctahedralEncode(direction);
                int x = Mathf.Clamp((int)((oct.x * 0.5f + 0.5f) * BasisGlobalIlluminationPass.CascadeDirections), 0, BasisGlobalIlluminationPass.CascadeDirections - 1);
                int y = Mathf.Clamp((int)((oct.y * 0.5f + 0.5f) * BasisGlobalIlluminationPass.CascadeDirections), 0, BasisGlobalIlluminationPass.CascadeDirections - 1);
                hits[x + y * BasisGlobalIlluminationPass.CascadeDirections]++;
            }
            for (int index = 0; index < angles.Length; index++)
            {
                float measured = hits[index] / (float)samples * 4f * Mathf.PI;
                Assert.AreEqual(angles[index], measured, 0.02f, $"texel {index}");
            }
        }

        private static Vector2 OctahedralEncode(Vector3 n)
        {
            n /= Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);
            if (n.z >= 0f) { return new Vector2(n.x, n.y); }
            return new Vector2((1f - Mathf.Abs(n.y)) * (n.x >= 0f ? 1f : -1f), (1f - Mathf.Abs(n.x)) * (n.y >= 0f ? 1f : -1f));
        }

        [Test]
        public void TheIntervalsAreContiguousAndReachTheWholeRay()
        {
            const float range = 16f;
            for (int cascades = 1; cascades <= BasisGlobalIlluminationPass.CascadeMax; cascades++)
            {
                Assert.AreEqual(0f, BasisGlobalIlluminationPass.CascadeInterval(0, cascades, range).x, 1e-6f);
                for (int cascade = 1; cascade < cascades; cascade++)
                {
                    Vector2 below = BasisGlobalIlluminationPass.CascadeInterval(cascade - 1, cascades, range);
                    Vector2 above = BasisGlobalIlluminationPass.CascadeInterval(cascade, cascades, range);
                    Assert.AreEqual(below.y, above.x, 1e-4f, $"gap between cascade {cascade - 1} and {cascade} of {cascades}");
                    Assert.AreEqual(4f * (below.y - below.x), above.y - above.x, 1e-3f, "each interval is four times the one below");
                }
                Assert.AreEqual(range, BasisGlobalIlluminationPass.CascadeInterval(cascades - 1, cascades, range).y, 1e-3f);
            }
        }

        [Test]
        public void ChildDirectionsNestInsideTheirParent()
        {
            for (int resolution = BasisGlobalIlluminationPass.CascadeDirections; resolution <= 32; resolution *= 2)
            {
                for (int y = 0; y < resolution; y++)
                {
                    for (int x = 0; x < resolution; x++)
                    {
                        Vector3 parent = BasisGlobalIlluminationPass.CascadeDirection(x, y, resolution);
                        Vector3 mean = Vector3.zero;
                        for (int child = 0; child < 4; child++)
                        {
                            Vector3 direction = BasisGlobalIlluminationPass.CascadeDirection(x * 2 + (child & 1), y * 2 + (child >> 1), resolution * 2);
                            mean += direction;
                            Assert.Greater(Vector3.Dot(parent, direction), Mathf.Cos(Mathf.PI * 2.2f / resolution), $"child {child} of {x},{y} at {resolution} wandered off its parent");
                        }
                        Assert.Greater(Vector3.Dot(parent, mean.normalized), Mathf.Cos(Mathf.PI * 0.75f / resolution));
                    }
                }
            }
        }

        [Test]
        public void NoJitterMeansAFixedGridAndNoRotation()
        {
            BasisGlobalIlluminationPass.CascadeJitter(5, 0f, 4, out Vector2 offset, out Quaternion rotation);
            Assert.AreEqual(Vector2.zero, offset);
            Assert.AreEqual(Quaternion.identity, rotation);

            Vector2 total = Vector2.zero;
            for (int frame = 0; frame < BasisGlobalIlluminationPass.CascadeJitterPeriod; frame++)
            {
                BasisGlobalIlluminationPass.CascadeJitter(frame, 1f, 4, out offset, out rotation);
                Assert.LessOrEqual(Mathf.Abs(offset.x), 2f);
                Assert.LessOrEqual(Mathf.Abs(offset.y), 2f);
                Assert.AreEqual(1f, Mathf.Abs(Quaternion.Dot(rotation, rotation)), 1e-4f);
                total += offset;
            }
            Assert.Less(total.magnitude / BasisGlobalIlluminationPass.CascadeJitterPeriod, 0.4f, "the grid offset should average out near the cell centre");
        }

        [Test]
        public void AHeadsetEyeKeepsEveryCascadeAndATinyImageDropsTheCoarsest()
        {
            Assert.AreEqual(6, BasisGlobalIlluminationPass.EffectiveCascadeCount(6, 4, 1008, 1120));
            Assert.AreEqual(5, BasisGlobalIlluminationPass.EffectiveCascadeCount(5, 4, 960, 540));
            Assert.Less(BasisGlobalIlluminationPass.EffectiveCascadeCount(6, 4, 96, 64), 6);
            Assert.GreaterOrEqual(BasisGlobalIlluminationPass.EffectiveCascadeCount(6, 8, 8, 8), 2);
        }

        [Test]
        public void TheQualityLadderSpendsMoreAtEveryStep()
        {
            BasisGlobalIlluminationSettings settings = new BasisGlobalIlluminationSettings();
            float previous = 0f;
            foreach (BasisGlobalIlluminationQuality quality in System.Enum.GetValues(typeof(BasisGlobalIlluminationQuality)))
            {
                settings.quality = quality;
                float spacing = settings.ResolvedCascadeSpacing();
                float cost = settings.ResolvedCascadeCount() * settings.ResolvedCascadeSteps() * 16f / (spacing * spacing);
                Assert.Greater(cost, previous, quality.ToString());
                previous = cost;
            }
        }

        private void Open()
        {
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            BasisGlobalIlluminationEmitter.Registered.Clear();
            harness = new BasisGlobalIlluminationRenderHarness();
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
        }

        private static void DenseLayout(BasisGlobalIlluminationSettings settings)
        {
            settings.cascadeOverride = true;
            settings.cascadeSpacing = BasisGlobalIlluminationSettings.CascadeSpacingMin;
            settings.cascadeCount = settings.ResolvedCascadeCount() - 1;
            settings.cascadeSteps = BasisGlobalIlluminationSettings.CascadeStepsMax;
        }

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

        private RectInt Probe(Vector3 worldPoint, int radius = 7)
        {
            Vector3 screen = harness.Camera.WorldToScreenPoint(worldPoint);
            return new RectInt(Mathf.RoundToInt(screen.x) - radius, Mathf.RoundToInt(screen.y) - radius, radius * 2, radius * 2);
        }

        private Color View(BasisGlobalIlluminationDebugView view, RectInt probe, int warmup = 24)
        {
            harness.SetDebugView(view);
            Color sample = harness.Converged(probe, warmup);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            return sample;
        }

        private static float Tint(Color colour) { return colour.r - colour.g; }

        private static bool Finite(Color colour)
        {
            return !(float.IsNaN(colour.r) || float.IsNaN(colour.g) || float.IsNaN(colour.b) || float.IsInfinity(colour.r) || float.IsInfinity(colour.g) || float.IsInfinity(colour.b));
        }

        [Test]
        public void TheCascadesReachTheImage()
        {
            Open();
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            RectInt probe = Probe(NearProbe);

            harness.Settings.enable = false;
            for (int frame = 0; frame < 4; frame++) { harness.Render(); }
            Color off = harness.RenderAndSample(probe);
            harness.Settings.enable = true;
            for (int frame = 0; frame < 16; frame++) { harness.Render(); }
            Color on = harness.RenderAndSample(probe);

            float difference = Mathf.Abs(on.r - off.r) + Mathf.Abs(on.g - off.g) + Mathf.Abs(on.b - off.b);
            Debug.Log($"[BasisGI] cascades reach: off={off} on={on} diff={difference:F5} | {harness.Describe()}");
            Assert.IsTrue(Finite(on), $"the composite went non-finite: {on}");
            Assert.Greater(difference, 1e-4f, $"turning the cascades on changed nothing at all: off={off}, on={on}. {harness.Describe()}");
        }

        [Test]
        public void TheCascadesBounceTheBlockOntoTheFloor()
        {
            Open();
            BuildRoom();
            Material block = AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            RectInt probe = Probe(NearProbe);

            Color lit = View(BasisGlobalIlluminationDebugView.Indirect, probe);
            BasisGlobalIlluminationRenderHarness.SetEmission(block, Color.black);
            Color dark = View(BasisGlobalIlluminationDebugView.Indirect, probe);

            Debug.Log($"[BasisGI] cascades bounce: lit={lit} dark={dark}");
            Assert.IsTrue(Finite(lit) && Finite(dark), $"non-finite indirect: lit={lit} dark={dark}");
            Assert.Greater(Tint(lit), Tint(dark) + 0.01f, $"the floor beside a bright red block gathered no red through the cascades: lit={lit}, dark={dark}");
        }

        [Test]
        public void TheCascadeBounceFallsOffWithDistance()
        {
            Open();
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            float near = Tint(View(BasisGlobalIlluminationDebugView.Indirect, Probe(NearProbe)));
            float far = Tint(View(BasisGlobalIlluminationDebugView.Indirect, Probe(FarProbe)));
            Debug.Log($"[BasisGI] cascades falloff: near={near:F4} far={far:F4}");
            Assert.Greater(near, far * 1.2f, $"the cascades put as much red a metre away as beside the block ({near:F4} against {far:F4})");
        }

        [Test]
        public void BothLayoutsKeepTheNearFarOrdering()
        {
            Open();
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            harness.Settings.smoothing = 0f;
            harness.Settings.bounceFeedback = 0f;
            Color near = View(BasisGlobalIlluminationDebugView.Indirect, Probe(NearProbe), 32);
            Color far = View(BasisGlobalIlluminationDebugView.Indirect, Probe(FarProbe), 32);
            harness.Settings.resolution = BasisGlobalIlluminationResolution.Full;
            DenseLayout(harness.Settings);
            Color denseNear = View(BasisGlobalIlluminationDebugView.Indirect, Probe(NearProbe), 32);
            Color denseFar = View(BasisGlobalIlluminationDebugView.Indirect, Probe(FarProbe), 32);
            Debug.Log($"[BasisGI] near/far: shipped {near} / {far}, dense {denseNear} / {denseFar}");
            Assert.Greater(Tint(near), Tint(far), "the shipped layout lost the near/far ordering");
            Assert.Greater(Tint(denseNear), Tint(denseFar), "the dense layout lost the near/far ordering");
        }

        [Test]
        public void TheCascadeBounceHoldsStillAtHeadsetResolution()
        {
            Open();
            harness.Resize(1024, 1024);
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            RectInt probe = Probe(NearProbe, 20);

            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            Color[] samples = harness.RenderJitteredRun(probe, 30, 48);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);

            float mean = BasisGlobalIlluminationRenderHarness.Mean(samples, BasisGlobalIlluminationRenderHarness.Red);
            float swing = BasisGlobalIlluminationRenderHarness.RelativeFrameToFrameSwing(samples, BasisGlobalIlluminationRenderHarness.Red);
            Debug.Log($"[BasisGI] cascades flicker: mean={mean:F4} swing={swing:F4}");
            Assert.Greater(mean, 0.002f, "the probe gathered nothing, so this run measured the stability of black");
            Assert.Less(swing, 0.015f, $"the cascades swung {swing:P1} between consecutive frames while the scene stood still");
        }

        [Test]
        public void TheCascadesAreNotGrainy()
        {
            Open();
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            RectInt probe = Probe(NearProbe, 10);

            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            BasisGlobalIlluminationRenderHarness.Grain grain = harness.MeasuredGrain(probe, BasisGlobalIlluminationRenderHarness.Luma);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);

            Debug.Log($"[BasisGI] cascades grain: {grain}");
            Assert.Greater(grain.Level, 0.002f, "the probe gathered nothing, so this run measured the grain of black");
            Assert.Less(grain.Swing, grain.Level * 0.02f + 0.0005f, $"the settled cascades still flicker: {grain}");
        }

        [Test]
        public void AWallSeenEdgeOnDoesNotLightItself()
        {
            Open();
            harness.AddSun(Quaternion.Euler(40f, -60f, 0f), 1.2f);
            Material grey = harness.CreateLitMaterial(new Color(0.75f, 0.75f, 0.75f), Color.black);
            Material wallMaterial = harness.CreateLitMaterial(new Color(0.75f, 0.75f, 0.75f), Color.black);
            harness.AddBox(Vector3.zero, new Vector3(14f, 0.2f, 14f), grey);
            harness.AddBox(new Vector3(0f, 2.5f, 3.2f), new Vector3(14f, 5f, 0.2f), grey);
            harness.AddBox(new Vector3(-3.4f, 2.5f, 0f), new Vector3(0.2f, 5f, 9f), wallMaterial);
            harness.Camera.fieldOfView = 70f;
            harness.Camera.transform.position = new Vector3(0.3f, 1.6f, -3.4f);
            harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(-0.3f, 1.0f, 1.2f) - harness.Camera.transform.position, Vector3.up);
            RectInt wall = Probe(new Vector3(-3.29f, 2.2f, 0.6f), 5);
            Assert.IsTrue(wall.xMin >= 0 && wall.yMin >= 0 && wall.xMax <= BasisGlobalIlluminationRenderHarness.Width && wall.yMax <= BasisGlobalIlluminationRenderHarness.Height, $"wall probe {wall} is off screen");
            harness.Settings.bounceFeedback = 0f;

            foreach (BasisGlobalIlluminationResolution resolution in new[] { BasisGlobalIlluminationResolution.Full, BasisGlobalIlluminationResolution.Half })
            {
                harness.Settings.resolution = resolution;
                wallMaterial.SetColor("_BaseColor", new Color(0.75f, 0.75f, 0.75f));
                Color control = View(BasisGlobalIlluminationDebugView.Indirect, wall);
                wallMaterial.SetColor("_BaseColor", new Color(0.1f, 0.8f, 0.15f));
                Color cascades = View(BasisGlobalIlluminationDebugView.Indirect, wall);
                float controlGreen = control.g - (control.r + control.b) * 0.5f, cascadesGreen = cascades.g - (cascades.r + cascades.b) * 0.5f;
                Debug.Log($"[BasisGI] cascades edge-on wall {resolution}: green wall {cascades} green {cascadesGreen:F4} | grey wall {control} green {controlGreen:F4}");
                Assert.Less(cascadesGreen, controlGreen + 0.03f, $"the green wall lit itself green through the cascades at {resolution}");
                Assert.Greater(BasisGlobalIlluminationRenderHarness.Luma(control), 0.01f, $"the edge-on wall received no light at all at {resolution}: {control}");
            }
        }

        [Test]
        public void TheFloorUnderTheBlockIsDarkerThanOpenFloor()
        {
            Open();
            BuildRoom();
            harness.Settings.obscuranceIntensity = 1f;
            AddEmissiveBlock(new Color(0f, 0f, 0f));
            Vector3 under = new Vector3(BlockCentre.x + BlockSize.x * 0.5f + 0.04f, 0.101f, BlockCentre.z);
            Color contact = View(BasisGlobalIlluminationDebugView.Obscurance, Probe(under, 3));
            Color open = View(BasisGlobalIlluminationDebugView.Obscurance, Probe(OpenProbe, 3));
            Debug.Log($"[BasisGI] cascades obscurance: contact={contact.r:F4} open={open.r:F4}");
            Assert.Greater(open.r - contact.r, 0.01f, "the cascades barely darkened the floor tucked against the block");
            Assert.Greater(open.r, 0.55f, "the cascades darkened open floor with nothing above it");
        }

        [Test]
        public void EveryQualityRendersFinite()
        {
            Open();
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            foreach (BasisGlobalIlluminationQuality quality in System.Enum.GetValues(typeof(BasisGlobalIlluminationQuality)))
            {
                harness.Settings.quality = quality;
                foreach (BasisGlobalIlluminationResolution resolution in new[] { BasisGlobalIlluminationResolution.Full, BasisGlobalIlluminationResolution.Half, BasisGlobalIlluminationResolution.Quarter })
                {
                    harness.Settings.resolution = resolution;
                    Color sample = View(BasisGlobalIlluminationDebugView.Indirect, Probe(NearProbe), 6);
                    Debug.Log($"[BasisGI] cascades {quality}/{resolution}: {sample}");
                    Assert.IsTrue(Finite(sample), $"{quality}/{resolution} went non-finite: {sample}");
                    Assert.Greater(Tint(sample), 0.005f, $"{quality}/{resolution} gathered no red");
                }
            }
        }

        [Test]
        public void TheOverrideLayoutsRenderFinite()
        {
            Open();
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            harness.Settings.cascadeOverride = true;
            foreach (int spacing in new[] { 1, 8 })
            {
                foreach (int count in new[] { BasisGlobalIlluminationSettings.CascadeCountMin, BasisGlobalIlluminationSettings.CascadeCountMax })
                {
                    foreach (int steps in new[] { BasisGlobalIlluminationSettings.CascadeStepsMin, BasisGlobalIlluminationSettings.CascadeStepsMax })
                    {
                        harness.Settings.cascadeSpacing = spacing;
                        harness.Settings.cascadeCount = count;
                        harness.Settings.cascadeSteps = steps;
                        Color sample = View(BasisGlobalIlluminationDebugView.Indirect, Probe(NearProbe), 6);
                        Debug.Log($"[BasisGI] cascades override spacing {spacing} count {count} steps {steps}: {sample}");
                        Assert.IsTrue(Finite(sample), $"spacing {spacing} count {count} steps {steps} went non-finite: {sample}");
                        Assert.Greater(Tint(sample), 0.002f, $"spacing {spacing} count {count} steps {steps} gathered no red");
                    }
                }
            }
        }

        [Test]
        public void TheBounceFeedbackCarriesLightFurtherAndSettles()
        {
            Open();
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            RectInt probe = Probe(new Vector3(-0.4f, 0.6f, 3.09f), 6);
            harness.Settings.temporalResponse = 0.25f;

            harness.Settings.bounceFeedback = 0f;
            Color single = View(BasisGlobalIlluminationDebugView.Indirect, probe, 64);
            harness.Settings.bounceFeedback = 1f;
            Color multi = View(BasisGlobalIlluminationDebugView.Indirect, probe, 64);
            Color later = View(BasisGlobalIlluminationDebugView.Indirect, probe, 64);
            Debug.Log($"[BasisGI] bounce feedback: single {single} multi {multi} later {later}");

            Assert.IsTrue(Finite(multi) && Finite(later), $"the feedback went non-finite: {multi} then {later}");
            float gain = BasisGlobalIlluminationRenderHarness.Luma(multi) / Mathf.Max(1e-4f, BasisGlobalIlluminationRenderHarness.Luma(single));
            Assert.Greater(gain, 1.02f, $"full feedback lit the far wall {gain:P0} as brightly as a single bounce did");
            float drift = Mathf.Abs(BasisGlobalIlluminationRenderHarness.Luma(later) - BasisGlobalIlluminationRenderHarness.Luma(multi)) / Mathf.Max(1e-4f, BasisGlobalIlluminationRenderHarness.Luma(multi));
            Assert.Less(drift, 0.03f, $"the feedback was still moving {drift:P1} after it should have settled: {multi} then {later}");
        }

        [Test]
        public void TheBounceFeedbackIsInertWithoutTheTemporalFilter()
        {
            Open();
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));
            RectInt probe = Probe(new Vector3(-0.4f, 0.6f, 3.09f), 6);
            harness.Settings.temporalFilter = false;

            harness.Settings.bounceFeedback = 0f;
            Color single = View(BasisGlobalIlluminationDebugView.Indirect, probe, 48);
            harness.Settings.bounceFeedback = 1f;
            Color feedback = View(BasisGlobalIlluminationDebugView.Indirect, probe, 48);
            Debug.Log($"[BasisGI] feedback without history: {single} vs {feedback}");
            float difference = Mathf.Abs(single.r - feedback.r) + Mathf.Abs(single.g - feedback.g) + Mathf.Abs(single.b - feedback.b);
            Assert.Less(difference, 0.04f, $"the feedback moved the image with no history to feed back: {single} vs {feedback}");
        }

        [Test]
        public void TheCascadeCostClimbsWithQuality()
        {
            Open();
            BuildRoom();
            AddEmissiveBlock(new Color(16f, 0.5f, 0.5f));

            RenderTexture large = harness.Own(new RenderTexture(2016, 2240, 24, RenderTextureFormat.DefaultHDR) { name = "BasisGICascadeCostTarget" });
            large.Create();
            RenderTexture original = harness.Camera.targetTexture;
            harness.Camera.targetTexture = large;
            Texture2D pixel = harness.Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            BasisGlobalIlluminationQuality[] qualities = (BasisGlobalIlluminationQuality[])System.Enum.GetValues(typeof(BasisGlobalIlluminationQuality));
            int configs = 1 + qualities.Length * 2 + 1;
            System.Collections.Generic.List<float>[] times = new System.Collections.Generic.List<float>[configs];
            for (int config = 0; config < configs; config++) { times[config] = new System.Collections.Generic.List<float>(); }

            void Apply(int config)
            {
                harness.Settings.enable = config > 0;
                harness.Settings.cascadeOverride = false;
                if (config == configs - 1)
                {
                    harness.Settings.quality = BasisGlobalIlluminationQuality.Ultra;
                    harness.Settings.bounceFeedback = 0.6f;
                    DenseLayout(harness.Settings);
                    return;
                }
                harness.Settings.quality = config > 0 ? qualities[(config - 1) / 2] : BasisGlobalIlluminationQuality.Medium;
                harness.Settings.bounceFeedback = config > 0 && (config - 1) % 2 == 1 ? 0.6f : 0f;
            }

            void Synchronised()
            {
                harness.Render();
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = large;
                pixel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                RenderTexture.active = previous;
            }

            float Low(int config)
            {
                times[config].Sort();
                return times[config][times[config].Count / 5];
            }

            try
            {
                for (int config = 0; config < configs; config++)
                {
                    Apply(config);
                    for (int frame = 0; frame < 8; frame++) { Synchronised(); }
                }
                for (int round = 0; round < 14; round++)
                {
                    for (int config = 0; config < configs; config++)
                    {
                        Apply(config);
                        Synchronised();
                        for (int frame = 0; frame < 3; frame++)
                        {
                            Stopwatch watch = Stopwatch.StartNew();
                            Synchronised();
                            times[config].Add((float)watch.Elapsed.TotalMilliseconds);
                        }
                    }
                }

                float off = Low(0);
                string report = $"[BasisGI] cost at 2016x2240, GI off {off:F2} ms";
                float lowest = float.MaxValue, highest = 0f, worstFeedback = 0f;
                for (int index = 0; index < qualities.Length; index++)
                {
                    float single = Low(1 + index * 2) - off, feedback = Low(2 + index * 2) - off;
                    report += $"\n  {qualities[index]}: +{single:F2} ms, with bounce feedback +{feedback:F2} ms";
                    if (index == 0) { lowest = single; }
                    if (index == qualities.Length - 1) { highest = single; }
                    worstFeedback = Mathf.Max(worstFeedback, feedback - single);
                }
                report += $"\n  dense override: +{Low(configs - 1) - off:F2} ms";
                Debug.Log(report);
                Assert.Less(lowest, highest + 0.3f, "Low cost more than Ultra: " + report);
                Assert.Less(worstFeedback, 0.5f, "the bounce feedback cost more than one extra full screen pass should: " + report);
            }
            finally
            {
                harness.Camera.targetTexture = original;
            }
        }
    }
}
