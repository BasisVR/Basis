using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// What survives the per-quality budgets, and what happens at the edge of one. An emitter or a light
    /// that drops out of the budget between one frame and the next takes all of its light with it, and a
    /// step change in a pixel with nothing on screen to explain it is what a player calls a flicker.
    /// </summary>
    public class BasisGlobalIlluminationBudgetTests
    {
        private readonly List<GameObject> spawned = new List<GameObject>();
        private readonly List<BasisGlobalIlluminationEmitter> ranked = new List<BasisGlobalIlluminationEmitter>();
        private GameObject cameraHost;
        private Camera camera;

        [SetUp]
        public void SetUp()
        {
            BasisGlobalIlluminationEmitter.Registered.Clear();
            cameraHost = new GameObject("BasisGIBudgetCamera");
            camera = cameraHost.AddComponent<Camera>();
        }

        [TearDown]
        public void TearDown()
        {
            for (int index = 0; index < spawned.Count; index++)
            {
                if (spawned[index] != null) { Object.DestroyImmediate(spawned[index]); }
            }
            spawned.Clear();
            ranked.Clear();
            if (cameraHost != null) { Object.DestroyImmediate(cameraHost); }
            BasisGlobalIlluminationEmitter.Registered.Clear();
        }

        private BasisGlobalIlluminationEmitter Emitter(Vector3 position, Color colour, float intensity = 1f, float range = 40f)
        {
            GameObject host = new GameObject("BasisGIBudgetEmitter");
            host.transform.position = position;
            spawned.Add(host);
            BasisGlobalIlluminationEmitter emitter = host.AddComponent<BasisGlobalIlluminationEmitter>();
            emitter.Color = colour;
            emitter.Intensity = intensity;
            emitter.Radius = 0.25f;
            emitter.Range = range;
            emitter.Register();
            return emitter;
        }

        private Light PointLight(Vector3 position, float intensity = 1f, float range = 20f)
        {
            GameObject host = new GameObject("BasisGIBudgetLight");
            host.transform.position = position;
            spawned.Add(host);
            Light light = host.AddComponent<Light>();
            light.type = LightType.Point;
            light.intensity = intensity;
            light.range = range;
            light.color = Color.white;
            return light;
        }

        [Test]
        public void RankKeepsTheNearestEmittersAndReportsTheCount()
        {
            for (int index = 0; index < 6; index++) { Emitter(new Vector3(0f, 0f, 2f + index * 3f), Color.white); }
            camera.transform.position = Vector3.zero;

            BasisGlobalIlluminationEmitter.Selection selection = BasisGlobalIlluminationEmitter.Rank(ranked, Vector3.zero, 3);
            Assert.AreEqual(3, selection.Count);
            for (int slot = 1; slot < selection.Count; slot++)
            {
                Assert.GreaterOrEqual(ranked[slot].WorldPosition.z, ranked[slot - 1].WorldPosition.z - 1e-3f,
                    "the ranking put a further emitter ahead of a nearer one");
            }
        }

        [Test]
        public void NothingIsFadedWhenEveryEmitterFits()
        {
            Emitter(new Vector3(0f, 0f, 2f), Color.white);
            Emitter(new Vector3(0f, 0f, 5f), Color.white);

            BasisGlobalIlluminationEmitter.Selection selection = BasisGlobalIlluminationEmitter.Rank(ranked, Vector3.zero, 8);
            Assert.AreEqual(2, selection.Count);
            Assert.AreEqual(1f, selection.BoundaryWeight, 1e-4f, "no emitter was dropped, so none should have been faded");
            Assert.AreEqual(1f, selection.WeightAt(0), 1e-4f);
            Assert.AreEqual(1f, selection.WeightAt(1), 1e-4f);
        }

        [Test]
        public void TheEmitterAtTheEdgeOfTheBudgetIsFadedOutBeforeItIsDisplaced()
        {
            // Two identical emitters equidistant from the viewer: whichever one the budget keeps is decided
            // by nothing more than list order, so it must be worth nothing by the time that decision is made.
            Emitter(new Vector3(-2f, 0f, 0f), Color.white);
            Emitter(new Vector3(2f, 0f, 0f), Color.white);

            BasisGlobalIlluminationEmitter.Selection selection = BasisGlobalIlluminationEmitter.Rank(ranked, Vector3.zero, 1);
            Assert.AreEqual(1, selection.Count);
            Assert.Less(selection.BoundaryWeight, 0.02f,
                $"a tie at the edge of the budget still uploaded {selection.BoundaryWeight:P0} of an emitter's light, so the swap between them is a step");
        }

        [Test]
        public void SwappingTheBoundaryEmitterIsContinuous()
        {
            // One red and one blue emitter, a budget of one, and a viewer walking from the red one to the
            // blue one. What gets uploaded has to cross over smoothly: without the fade the red channel
            // holds full value right up to the crossing and then drops to nothing in a single step.
            Emitter(new Vector3(-4f, 0f, 0f), Color.red, 2f);
            Emitter(new Vector3(4f, 0f, 0f), Color.blue, 2f);

            const int steps = 41;
            float[] red = new float[steps];
            for (int step = 0; step < steps; step++)
            {
                float x = Mathf.Lerp(-3f, 3f, step / (float)(steps - 1));
                camera.transform.position = new Vector3(x, 0f, 0f);
                BasisGlobalIlluminationPass.GatherEmitters(camera, 1);
                red[step] = BasisGlobalIlluminationPass.EmitterRadianceAt(0).x;
            }

            float peak = 0f;
            for (int step = 0; step < steps; step++) { peak = Mathf.Max(peak, red[step]); }
            float worst = 0f;
            int worstStep = 0;
            for (int step = 1; step < steps; step++)
            {
                float delta = Mathf.Abs(red[step] - red[step - 1]);
                if (delta > worst) { worst = delta; worstStep = step; }
            }

            Assert.Greater(peak, 0f, "no emitter was ever uploaded, so this measured nothing");
            Assert.Less(worst / peak, 0.25f,
                $"the uploaded emitter jumped {worst / peak:P0} of its own peak in one step of the viewer at step {worstStep} - that is the swap being visible");
        }

        [Test]
        public void BothModesKeepTheSameEmitters()
        {
            for (int index = 0; index < 8; index++)
            {
                Emitter(new Vector3(index * 1.7f - 6f, 0f, 3f + index), Color.white, 1f + index * 0.25f);
            }
            Vector3 viewer = new Vector3(0.4f, 0f, 0f);
            camera.transform.position = viewer;

            List<BasisGlobalIlluminationEmitter> screenSpace = new List<BasisGlobalIlluminationEmitter>();
            BasisGlobalIlluminationEmitter.Selection first = BasisGlobalIlluminationEmitter.Rank(screenSpace, viewer, 4);
            BasisGlobalIlluminationEmitter.Selection second = BasisGlobalIlluminationEmitter.Rank(ranked, viewer, 4);

            Assert.AreEqual(first.Count, second.Count);
            for (int slot = 0; slot < first.Count; slot++)
            {
                Assert.AreSame(screenSpace[slot], ranked[slot],
                    "the two modes ranked the same emitters differently, so switching mode would change which lights a world has");
            }
        }

        [Test]
        public void EmittersSurviveASceneWithMoreLightsThanTheBudget()
        {
            const int budget = 6;
            for (int index = 0; index < budget + 4; index++) { PointLight(new Vector3(index * 2f, 0f, 4f), 3f); }
            Emitter(new Vector3(0f, 1f, 1f), Color.red, 5f);
            Emitter(new Vector3(1f, 1f, 1f), Color.green, 5f);

            using (BasisGlobalIlluminationRayLights lights = new BasisGlobalIlluminationRayLights())
            {
                foreach (GameObject host in spawned)
                {
                    Light light = host != null ? host.GetComponent<Light>() : null;
                    if (light != null) { lights.AddScannedForTest(light); }
                }

                BasisGlobalIlluminationRayLightSettings settings = BasisGlobalIlluminationRayLightSettings.Default;
                settings.limit = budget;
                int total = lights.GatherForTest(settings, Vector3.zero);

                Assert.AreEqual(budget, total, "the budget was not filled");

                int emitterSlots = 0;
                for (int slot = 0; slot < total; slot++)
                {
                    // Emitters are uploaded with their sphere radius in spot.w; scene lights leave it zero.
                    if (lights.At(slot).spot.w > 0f) { emitterSlots++; }
                }
                Assert.GreaterOrEqual(emitterSlots, 2,
                    "a scene with more lights than the budget dropped every emitter, so a world's own light sources vanish exactly where they were placed to help");
            }
        }

        [Test]
        public void EmittersGiveTheirReservedSlotsBackWhenThereAreNone()
        {
            const int budget = 6;
            for (int index = 0; index < budget + 4; index++) { PointLight(new Vector3(index * 2f, 0f, 4f), 3f); }

            using (BasisGlobalIlluminationRayLights lights = new BasisGlobalIlluminationRayLights())
            {
                foreach (GameObject host in spawned)
                {
                    Light light = host != null ? host.GetComponent<Light>() : null;
                    if (light != null) { lights.AddScannedForTest(light); }
                }

                BasisGlobalIlluminationRayLightSettings settings = BasisGlobalIlluminationRayLightSettings.Default;
                settings.limit = budget;
                Assert.AreEqual(budget, lights.GatherForTest(settings, Vector3.zero),
                    "with no emitters registered the whole budget belongs to the scene lights");
            }
        }

        [Test]
        public void TheLightAtTheEdgeOfTheBudgetIsFadedOutBeforeItIsDisplaced()
        {
            Light near = PointLight(new Vector3(-3f, 0f, 0f), 4f);
            Light far = PointLight(new Vector3(3f, 0f, 0f), 4f);
            List<Light> candidates = new List<Light> { near, far };

            Assert.Less(BasisGlobalIlluminationRayLights.BoundaryWeight(candidates, Vector3.zero, 1), 0.02f,
                "a tie at the edge of the light budget still uploaded a light at full brightness, so the swap between them is a step");
            Assert.AreEqual(1f, BasisGlobalIlluminationRayLights.BoundaryWeight(candidates, Vector3.zero, 2), 1e-4f,
                "nothing was dropped, so nothing should have been faded");
        }

        [Test]
        public void DirectionalLightsAreNotFadedAtTheEdgeOfTheBudget()
        {
            GameObject host = new GameObject("BasisGIBudgetSun");
            spawned.Add(host);
            Light sun = host.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1f;

            GameObject second = new GameObject("BasisGIBudgetSecondSun");
            spawned.Add(second);
            Light other = second.AddComponent<Light>();
            other.type = LightType.Directional;
            other.intensity = 1f;

            // A directional light's rank cannot change as the viewer moves, so there is no swap to smooth
            // over - and fading it would simply delete the sun.
            List<Light> candidates = new List<Light> { sun, other };
            Assert.AreEqual(1f, BasisGlobalIlluminationRayLights.BoundaryWeight(candidates, Vector3.zero, 1), 1e-4f);
        }

        [Test]
        public void UnusedEmitterSlotsAreCleared()
        {
            Emitter(new Vector3(0f, 0f, 2f), Color.white);
            camera.transform.position = Vector3.zero;

            Assert.AreEqual(1, BasisGlobalIlluminationPass.GatherEmitters(camera, 8));
            Assert.AreEqual(Vector4.zero, BasisGlobalIlluminationPass.EmitterRadianceAt(1),
                "a stale emitter left in an unused slot would keep lighting the scene after it was gone");
            Assert.AreEqual(Vector4.zero, BasisGlobalIlluminationPass.EmitterSphereAt(BasisGlobalIlluminationPass.MaxEmitters - 1));
        }
    }
}
