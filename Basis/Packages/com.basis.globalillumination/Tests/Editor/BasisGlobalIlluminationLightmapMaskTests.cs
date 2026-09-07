using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// The receiving half of lightmapped-world support: a surface whose bounce is already baked stands back
    /// from the composite, while everything dynamic keeps the full effect.
    ///
    /// Two calibration facts these tests are built around, both measured rather than assumed. First, in this
    /// harness the red bounce never reads as a positive red-minus-green tint - the blue environment fallback
    /// dominates the absolute value - so every assertion here is a DELTA between two configurations, the way
    /// the passing bounce tests measure. Second, whether the engine drives LIGHTMAP_ON for a lightmapIndex
    /// assigned at runtime in edit mode is an environment question, not a code question - so the pipeline is
    /// proven through <see cref="BasisGlobalIlluminationPass.LightmapMaskForcedValue"/>, which severs that
    /// one link, and the real-lightmap test first checks whether the environment honours the assignment at
    /// all and skips with a reason when it does not.
    /// </summary>
    public class BasisGlobalIlluminationLightmapMaskTests
    {
        private static readonly Vector3 BlockCentre = new Vector3(-0.7f, 0.6f, 0.55f);
        private static readonly Vector3 BlockSize = new Vector3(0.45f, 0.45f, 0.45f);
        private static readonly Vector3 FloorProbePoint = new Vector3(-0.05f, 0.101f, 0.55f);
        private static readonly Vector3 BoxTopProbePoint = new Vector3(0.75f, 0.521f, 0.55f);

        private BasisGlobalIlluminationRenderHarness harness;
        private LightmapData[] previousLightmaps;
        private Texture2D lightmapTexture;
        private MeshRenderer floorRenderer;

        [SetUp]
        public void SetUp()
        {
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            BasisGlobalIlluminationEmitter.Registered.Clear();
            harness = new BasisGlobalIlluminationRenderHarness();
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            previousLightmaps = LightmapSettings.lightmaps;
            LightmapSettings.lightmaps = new LightmapData[0];
            BasisGlobalIlluminationPass.LightmapMaskForcedValue = -1f;
        }

        [TearDown]
        public void TearDown()
        {
            BasisGlobalIlluminationPass.LightmapMaskForcedValue = -1f;
            LightmapSettings.lightmaps = previousLightmaps;
            previousLightmaps = null;
            if (lightmapTexture != null) { Object.DestroyImmediate(lightmapTexture); lightmapTexture = null; }
            floorRenderer = null;
            harness?.Dispose();
            harness = null;
        }

        /// <summary>The grey room, a red emissive block, and a dynamic grey box standing on the floor.</summary>
        private void BuildScene()
        {
            harness.AddSun(Quaternion.Euler(52f, -24f, 0f), 0.5f);

            Material surface = harness.CreateLitMaterial(new Color(0.8f, 0.8f, 0.8f), Color.black);
            GameObject floor = harness.AddBox(new Vector3(0f, 0f, 0f), new Vector3(14f, 0.2f, 14f), surface);
            floorRenderer = floor.GetComponent<MeshRenderer>();
            harness.AddBox(new Vector3(0f, 2f, 3.2f), new Vector3(14f, 5f, 0.2f), surface);
            harness.AddBox(new Vector3(-3.4f, 2f, 0f), new Vector3(0.2f, 5f, 9f), surface);
            harness.AddBox(new Vector3(0.75f, 0.31f, 0.55f), new Vector3(0.55f, 0.42f, 0.55f), surface);

            Material block = harness.CreateLitMaterial(Color.black, new Color(16f, 0.5f, 0.5f));
            harness.AddBox(BlockCentre, BlockSize, block);

            harness.Camera.transform.position = new Vector3(0.1f, 1.7f, -2.6f);
            harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(-0.1f, 0.3f, 0.6f) - harness.Camera.transform.position, Vector3.up);

            harness.Settings.enable = true;
            harness.Settings.mode = BasisGlobalIlluminationMode.ScreenSpace;
        }

        /// <summary>
        /// Marks the floor as carrying a real lightmap: a LightmapSettings entry plus the renderer's own
        /// index, which together are what make the engine draw it with LIGHTMAP_ON.
        /// </summary>
        private void LightmapTheFloor()
        {
            lightmapTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { name = "BasisGIMaskTestLightmap" };
            Color[] texels = { Color.black, Color.black, Color.black, Color.black };
            lightmapTexture.SetPixels(texels);
            lightmapTexture.Apply(false, false);
            LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = lightmapTexture } };
            floorRenderer.lightmapIndex = 0;
            floorRenderer.lightmapScaleOffset = new Vector4(1f, 1f, 0f, 0f);
        }

        private RectInt Probe(Vector3 worldPoint, int radius = 7)
        {
            Vector3 screen = harness.Camera.WorldToScreenPoint(worldPoint);
            return new RectInt(Mathf.RoundToInt(screen.x) - radius, Mathf.RoundToInt(screen.y) - radius, radius * 2, radius * 2);
        }

        private Color Indirect(RectInt probe)
        {
            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            Color sample = harness.Converged(probe);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            return sample;
        }

        private static float Level(Color colour) { return colour.r + colour.g + colour.b; }

        [Test]
        public void TheMaskKeywordFollowsTheSceneAndTheSetting()
        {
            BuildScene();
            if (harness.Feature == null || harness.Feature.Material == null) { Assert.Ignore("No live renderer feature to read the keyword from."); }
            Material material = harness.Feature.Material;

            // No lightmaps in the scene: the mask must not exist however low the receive floor is set,
            // because with the keyword on and nothing bound the sample reads zero and suppresses the
            // whole effect - the exact fail-dangerous shape the polarity work removed.
            harness.Settings.lightmappedReceive = 0f;
            harness.Render();
            Assert.IsFalse(material.IsKeywordEnabled("_BASISGI_LIGHTMAP_MASK"),
                "An unbaked scene must never bind the lightmap mask.");

            // The forced value stands in for a baked scene, so the pass records and the keyword follows.
            BasisGlobalIlluminationPass.LightmapMaskForcedValue = 0.5f;
            harness.Render();
            Assert.IsTrue(material.IsKeywordEnabled("_BASISGI_LIGHTMAP_MASK"),
                $"With the mask forced the pass must record and bind. {harness.Describe()}");

            BasisGlobalIlluminationPass.LightmapMaskForcedValue = -1f;
            harness.Render();
            Assert.IsFalse(material.IsKeywordEnabled("_BASISGI_LIGHTMAP_MASK"),
                "Clearing the forced value in an unbaked scene must take the mask away again.");

            LightmapTheFloor();
            harness.Render();
            Assert.IsTrue(material.IsKeywordEnabled("_BASISGI_LIGHTMAP_MASK"),
                $"A baked scene with a receive floor below one must render the mask. {harness.Describe()}");

            // One means the old behaviour exactly, so the pass and its keyword go away entirely.
            harness.Settings.lightmappedReceive = 1f;
            harness.Render();
            Assert.IsFalse(material.IsKeywordEnabled("_BASISGI_LIGHTMAP_MASK"),
                "A receive floor of one must skip the mask pass outright.");
        }

        [Test]
        public void TheMaskPipelineSuppressesWhatItDraws()
        {
            // Forced to zero, every opaque surface the pass keeps writes "lightmapped", so the whole
            // indirect image must collapse to the receive floor. This is the end-to-end proof of the draw,
            // the hand depth test, the mask sampling lining up with the composite, and the receive
            // arithmetic - everything except the LIGHTMAP_ON split, which the environment-gated test below
            // owns.
            BuildScene();
            RectInt floorProbe = Probe(FloorProbePoint);

            harness.Settings.lightmappedReceive = 1f;
            float full = Level(Indirect(floorProbe));

            harness.Settings.lightmappedReceive = 0f;
            BasisGlobalIlluminationPass.LightmapMaskForcedValue = 0f;
            float masked = Level(Indirect(floorProbe));
            BasisGlobalIlluminationPass.LightmapMaskForcedValue = -1f;

            Debug.Log($"[BasisGI] forced mask: floor level {full:F4} -> {masked:F4} | {harness.Describe()}");
            Assert.Greater(full, 0.01f, $"The unmasked indirect level read {full:F4}; there is nothing here for the mask to suppress.");
            Assert.Less(masked, full * 0.5f,
                $"With the mask forced to zero and the receive floor at zero the indirect must collapse; it read {masked:F4} against {full:F4} unmasked.");
        }

        [Test]
        public void ALightmappedFloorStandsBackFromTheBounceAndADynamicBoxDoesNot()
        {
            BuildScene();

            // First establish that this environment honours a runtime-assigned lightmap at all: with the
            // effect off, a floor whose GI term becomes a black lightmap has to render darker. If it does
            // not, the engine never drove LIGHTMAP_ON for the assignment and nothing downstream of it is
            // measurable here - that is an edit-mode limitation, not a defect, and the forced-value test
            // above still covers the pipeline itself.
            RectInt floorProbe = Probe(FloorProbePoint);
            RectInt boxProbe = Probe(BoxTopProbePoint);

            harness.Settings.enable = false;
            for (int frame = 0; frame < 3; frame++) { harness.Render(); }
            float rawBefore = Level(harness.Sample(floorProbe));
            LightmapTheFloor();
            for (int frame = 0; frame < 3; frame++) { harness.Render(); }
            float rawAfter = Level(harness.Sample(floorProbe));
            harness.Settings.enable = true;
            Debug.Log($"[BasisGI] lightmap darkening probe: {rawBefore:F4} -> {rawAfter:F4}");
            if (rawBefore - rawAfter < 0.02f)
            {
                Assert.Ignore("A runtime-assigned lightmapIndex does not drive LIGHTMAP_ON in this environment, so the split cannot be measured here. The forced-value test covers the mask pipeline; the keyword split needs play mode or a genuinely baked scene.");
            }

            harness.Settings.lightmappedReceive = 1f;
            float floorFull = Level(Indirect(floorProbe));
            float boxFull = Level(Indirect(boxProbe));

            harness.Settings.lightmappedReceive = 0f;
            float floorMasked = Level(Indirect(floorProbe));
            float boxMasked = Level(Indirect(boxProbe));

            Debug.Log($"[BasisGI] lightmap mask: floor {floorFull:F4} -> {floorMasked:F4}, box {boxFull:F4} -> {boxMasked:F4} | {harness.Describe()}");

            Assert.Greater(floorFull, 0.01f, $"The unmasked indirect level read {floorFull:F4}; there is nothing here to suppress.");
            Assert.Less(floorMasked, floorFull * 0.5f,
                $"A lightmapped floor at receive zero must lose its indirect; it read {floorMasked:F4} against {floorFull:F4} unmasked.");
            Assert.Greater(boxMasked, boxFull * 0.55f,
                $"The dynamic box is not lightmapped and must keep its indirect; it read {boxMasked:F4} against {boxFull:F4} unmasked.");
        }

        [Test]
        public void AnUnbakedSceneIsUntouchedByTheReceiveSetting()
        {
            BuildScene();
            RectInt floorProbe = Probe(FloorProbePoint);

            harness.Settings.lightmappedReceive = 1f;
            float full = Level(Indirect(floorProbe));

            harness.Settings.lightmappedReceive = 0f;
            float masked = Level(Indirect(floorProbe));

            Debug.Log($"[BasisGI] unbaked receive sweep: {full:F4} -> {masked:F4}");
            Assert.Greater(full, 0.01f, $"The scene put no measurable indirect on the floor ({full:F4}).");
            Assert.Greater(masked, full * 0.6f,
                $"With no lightmaps in the scene the receive setting must change nothing; the floor read {masked:F4} against {full:F4}.");
        }
    }
}
