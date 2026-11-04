using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    public class BasisGlobalIlluminationWorldTests
    {
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
            RestoreAmbient();
            harness?.Dispose();
            harness = null;
            UnityEditor.ShaderUtil.allowAsyncCompilation = previousAsync;
            BasisGlobalIlluminationPass.ReleaseWorld();
        }

        [Test]
        public void ClipmapLevelsSnapToTheTopCascadeAndContainTheViewer()
        {
            const float voxel = 0.2f;
            foreach (Vector3 viewer in new[] { Vector3.zero, new Vector3(13.37f, -2.1f, 250.6f), new Vector3(-0.01f, 0.01f, -77f) })
            {
                for (int level = 0; level < BasisGlobalIlluminationPass.WorldMaxLevels; level++)
                {
                    Vector4 box = BasisGlobalIlluminationPass.WorldLevelBox(viewer, level, voxel);
                    float size = box.w * BasisGlobalIlluminationPass.WorldVoxels;
                    Assert.AreEqual(voxel * (1 << level), box.w, 1e-5f);
                    float snap = box.w * 2f * BasisGlobalIlluminationPass.WorldSnapProbes;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        float ratio = box[axis] / snap;
                        Assert.AreEqual(Mathf.Round(ratio), ratio, 1e-3f, $"level {level} axis {axis} is not on the top cascade's grid, so its probes would move with the viewer");
                        Assert.Greater(viewer[axis], box[axis] + size * 0.25f, $"level {level} axis {axis} puts the viewer near its edge");
                        Assert.Less(viewer[axis], box[axis] + size * 0.75f, $"level {level} axis {axis} puts the viewer near its edge");
                    }
                }
            }
        }

        [Test]
        public void WorldIntervalsAreContiguousAndGrowFourfold()
        {
            const float voxel = 0.2f;
            Assert.AreEqual(0f, BasisGlobalIlluminationPass.WorldInterval(0, voxel).x, 1e-6f);
            for (int cascade = 1; cascade < BasisGlobalIlluminationPass.WorldCascades; cascade++)
            {
                Vector2 below = BasisGlobalIlluminationPass.WorldInterval(cascade - 1, voxel);
                Vector2 above = BasisGlobalIlluminationPass.WorldInterval(cascade, voxel);
                Assert.AreEqual(below.y, above.x, 1e-4f, $"cascade {cascade} does not start where {cascade - 1} ended");
                Assert.AreEqual((below.y - below.x) * 4f, above.y - above.x, 1e-3f);
            }
            float first = BasisGlobalIlluminationPass.WorldInterval(0, voxel).y;
            Assert.GreaterOrEqual(first, BasisGlobalIlluminationPass.CascadePenumbra * voxel * 2f - 1e-5f, "the first world interval is shorter than the penumbra bound for its probe spacing");
        }

        [Test]
        public void EveryAxisProjectionCoversTheWholeLevel()
        {
            Vector4 box = BasisGlobalIlluminationPass.WorldLevelBox(new Vector3(3f, 1f, -4f), 1, 0.2f);
            float size = box.w * BasisGlobalIlluminationPass.WorldVoxels;
            Vector3 min = new Vector3(box.x, box.y, box.z);
            for (int axis = 0; axis < 3; axis++)
            {
                Matrix4x4 matrix = BasisGlobalIlluminationPass.VoxelAxisMatrix(box, axis);
                Vector4 low = matrix * new Vector4(min.x, min.y, min.z, 1f);
                Vector4 high = matrix * new Vector4(min.x + size, min.y + size, min.z + size, 1f);
                Assert.AreEqual(-1f, low.x, 1e-4f, $"axis {axis}"); Assert.AreEqual(-1f, low.y, 1e-4f, $"axis {axis}");
                Assert.AreEqual(1f, high.x, 1e-4f, $"axis {axis}"); Assert.AreEqual(1f, high.y, 1e-4f, $"axis {axis}");
                Assert.AreEqual(0.5f, low.z, 1e-6f); Assert.AreEqual(1f, low.w, 1e-6f);
            }
        }

        [Test]
        public void TheWorldSettingsAreHeldInsideTheirRangesAndSurviveACopy()
        {
            BasisGlobalIlluminationSettings settings = new BasisGlobalIlluminationSettings { worldLevels = 9, worldVoxelSize = 5f, worldSpace = false };
            settings.Clamp();
            Assert.AreEqual(BasisGlobalIlluminationSettings.WorldLevelsMax, settings.worldLevels);
            Assert.AreEqual(BasisGlobalIlluminationSettings.WorldVoxelSizeMax, settings.worldVoxelSize);
            Assert.LessOrEqual(BasisGlobalIlluminationSettings.WorldLevelsMax, BasisGlobalIlluminationPass.WorldMaxLevels);
            BasisGlobalIlluminationSettings copy = settings.Clone();
            Assert.IsFalse(copy.worldSpace);
            Assert.AreEqual(settings.worldLevels, copy.worldLevels);
            Assert.AreEqual(settings.worldVoxelSize, copy.worldVoxelSize);
            Assert.IsTrue(new BasisGlobalIlluminationSettings().worldSpace, "world space ships on");
        }

        private void Open()
        {
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            BasisGlobalIlluminationEmitter.Registered.Clear();
            harness = new BasisGlobalIlluminationRenderHarness();
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
        }

        private RectInt Probe(Vector3 worldPoint, int radius)
        {
            Vector3 screen = harness.Camera.WorldToScreenPoint(worldPoint);
            return new RectInt(Mathf.RoundToInt(screen.x) - radius, Mathf.RoundToInt(screen.y) - radius, radius * 2, radius * 2);
        }

        private Color Indirect(RectInt probe, int warmup)
        {
            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            Color sample = harness.Converged(probe, warmup);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            return sample;
        }

        private static bool Finite(Color colour)
        {
            return !(float.IsNaN(colour.r) || float.IsNaN(colour.g) || float.IsNaN(colour.b) || float.IsInfinity(colour.r) || float.IsInfinity(colour.g) || float.IsInfinity(colour.b));
        }

        [Test]
        public void LightFromBehindTheCameraReachesTheScreen()
        {
            Open();
            harness.AddSun(Quaternion.Euler(52f, -24f, 0f), 0.2f);
            Material grey = harness.CreateLitMaterial(new Color(0.8f, 0.8f, 0.8f), Color.black);
            harness.AddBox(Vector3.zero, new Vector3(14f, 0.2f, 14f), grey);
            harness.AddBox(new Vector3(0f, 2f, 3.2f), new Vector3(14f, 5f, 0.2f), grey);
            harness.AddBox(new Vector3(0f, 0.6f, -3.4f), new Vector3(1.6f, 1.0f, 0.4f), harness.CreateLitMaterial(Color.black, new Color(24f, 0.4f, 0.4f)));
            harness.Camera.transform.position = new Vector3(0f, 1.6f, -2.2f);
            harness.Settings.worldSpace = true;
            harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, -0.5f, -1f), Vector3.up);
            for (int frame = 0; frame < 12; frame++) { harness.Render(); }
            harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, -0.35f, 1f), Vector3.up);
            Assert.Less(harness.Camera.WorldToViewportPoint(new Vector3(0f, 0.6f, -3.4f)).z, 0f, "the red block has to be behind the camera for this to test anything");
            RectInt floor = Probe(new Vector3(0f, 0.101f, -1.0f), 6);

            harness.Settings.bounceFeedback = 0f;
            Color world = Indirect(floor, 24);
            harness.Settings.worldSpace = false;
            Color screenOnly = Indirect(floor, 24);

            Debug.Log($"[BasisGI] light from behind the camera: screen only {screenOnly} | with world cascades {world} | {harness.Describe()}");
            Assert.IsTrue(Finite(world), $"the world cascades went non-finite: {world}");
            float screenTint = screenOnly.r - screenOnly.g, worldTint = world.r - world.g;
            Assert.Greater(worldTint, screenTint + 0.005f, $"the red block behind the camera never reached the floor in view through the world cascades: {screenOnly} without, {world} with");
        }

        [Test]
        public void EveryWorldLayoutRendersFinite()
        {
            Open();
            harness.AddSun(Quaternion.Euler(52f, -24f, 0f), 0.5f);
            Material grey = harness.CreateLitMaterial(new Color(0.8f, 0.8f, 0.8f), Color.black);
            harness.AddBox(Vector3.zero, new Vector3(14f, 0.2f, 14f), grey);
            harness.AddBox(new Vector3(0f, 2f, 3.2f), new Vector3(14f, 5f, 0.2f), grey);
            harness.AddBox(new Vector3(-0.7f, 0.6f, 0.55f), new Vector3(0.45f, 0.45f, 0.45f), harness.CreateLitMaterial(Color.black, new Color(16f, 0.5f, 0.5f)));
            harness.Camera.transform.position = new Vector3(0.1f, 1.7f, -2.6f);
            harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(-0.1f, 0.3f, 0.6f) - harness.Camera.transform.position, Vector3.up);
            RectInt probe = Probe(new Vector3(-0.05f, 0.101f, 0.55f), 6);
            harness.Settings.worldSpace = true;

            for (int levels = BasisGlobalIlluminationSettings.WorldLevelsMin; levels <= BasisGlobalIlluminationSettings.WorldLevelsMax; levels++)
            {
                foreach (float voxel in new[] { BasisGlobalIlluminationSettings.WorldVoxelSizeMin, 0.2f, BasisGlobalIlluminationSettings.WorldVoxelSizeMax })
                {
                    harness.Settings.worldLevels = levels;
                    harness.Settings.worldVoxelSize = voxel;
                    Color sample = Indirect(probe, 8);
                    Debug.Log($"[BasisGI] world {levels} levels at {voxel} m: {sample}");
                    Assert.IsTrue(Finite(sample), $"{levels} levels at {voxel} m went non-finite: {sample}");
                    Assert.Greater(sample.r - sample.g, -0.02f, $"{levels} levels at {voxel} m lost the block's red");
                }
            }
        }

        [Test]
        public void TheClipmapFollowsAWalkingCamera()
        {
            Open();
            harness.AddSun(Quaternion.Euler(52f, -24f, 0f), 0.5f);
            Material grey = harness.CreateLitMaterial(new Color(0.8f, 0.8f, 0.8f), Color.black);
            harness.AddBox(Vector3.zero, new Vector3(40f, 0.2f, 40f), grey);
            harness.AddBox(new Vector3(-0.7f, 0.6f, 0.55f), new Vector3(0.45f, 0.45f, 0.45f), harness.CreateLitMaterial(Color.black, new Color(16f, 0.5f, 0.5f)));
            harness.Settings.worldSpace = true;
            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            for (int step = 0; step < 40; step++)
            {
                harness.Camera.transform.position = new Vector3(0.1f + step * 0.25f, 1.7f, -2.6f);
                harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(-0.7f, 0.3f, 0.55f) - harness.Camera.transform.position, Vector3.up);
                harness.Render();
            }
            Color sample = harness.RenderAndSample(Probe(new Vector3(-0.05f, 0.101f, 0.55f), 4));
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            Debug.Log($"[BasisGI] world after a walk: {sample}");
            Assert.IsTrue(Finite(sample), $"the walk left the world cascades non-finite: {sample}");
        }

        private UnityEngine.Rendering.AmbientMode ambientMode;
        private Color ambientLight;
        private float reflectionIntensity;
        private bool ambientStored;

        private void OpenDark()
        {
            Open();
            ambientMode = RenderSettings.ambientMode;
            ambientLight = RenderSettings.ambientLight;
            reflectionIntensity = RenderSettings.reflectionIntensity;
            ambientStored = true;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.reflectionIntensity = 0f;
            DynamicGI.UpdateEnvironment();
        }

        private void RestoreAmbient()
        {
            if (!ambientStored) { return; }
            ambientStored = false;
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientLight = ambientLight;
            RenderSettings.reflectionIntensity = reflectionIntensity;
        }

        private void Room(Material material, Vector3 size)
        {
            const float thickness = 0.3f;
            harness.AddBox(new Vector3(0f, -thickness / 2f, 0f), new Vector3(size.x + 2f * thickness, thickness, size.z + 2f * thickness), material);
            harness.AddBox(new Vector3(0f, size.y + thickness / 2f, 0f), new Vector3(size.x + 2f * thickness, thickness, size.z + 2f * thickness), material);
            harness.AddBox(new Vector3(-size.x / 2f - thickness / 2f, size.y / 2f, 0f), new Vector3(thickness, size.y, size.z), material);
            harness.AddBox(new Vector3(size.x / 2f + thickness / 2f, size.y / 2f, 0f), new Vector3(thickness, size.y, size.z), material);
            harness.AddBox(new Vector3(0f, size.y / 2f, size.z / 2f + thickness / 2f), new Vector3(size.x, size.y, thickness), material);
            harness.AddBox(new Vector3(0f, size.y / 2f, -size.z / 2f - thickness / 2f), new Vector3(size.x, size.y, thickness), material);
        }

        private void Look(Vector3 from, Vector3 at)
        {
            harness.Camera.transform.position = from;
            harness.Camera.transform.rotation = Quaternion.LookRotation(at - from, Vector3.up);
        }

        private Color Whole(int warmup)
        {
            return Indirect(new RectInt(4, 4, BasisGlobalIlluminationRenderHarness.Width - 8, BasisGlobalIlluminationRenderHarness.Height - 8), warmup);
        }

        [Test]
        public void AnEyeIsOnlySkippedInStereoWithTheSettingOn()
        {
            RenderTextureDescriptor mono = new RenderTextureDescriptor(64, 64) { dimension = UnityEngine.Rendering.TextureDimension.Tex2D, volumeDepth = 1 };
            RenderTextureDescriptor stereo = new RenderTextureDescriptor(64, 64) { dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray, volumeDepth = 2 };
            Assert.AreEqual(-1f, BasisGlobalIlluminationPass.SkipEyeFor(true, mono, 3, false));
            Assert.AreEqual(-1f, BasisGlobalIlluminationPass.SkipEyeFor(false, stereo, 3, false));
            Assert.AreEqual(-1f, BasisGlobalIlluminationPass.SkipEyeFor(true, stereo, 3, true));
            Assert.AreEqual(1f, BasisGlobalIlluminationPass.SkipEyeFor(true, stereo, 3, false));
            Assert.AreEqual(0f, BasisGlobalIlluminationPass.SkipEyeFor(true, stereo, 4, false));
        }

        [Test]
        public void ARedRoomBouncesRedLightThroughItsVoxels()
        {
            OpenDark();
            Material red = harness.CreateLitMaterial(new Color(0.8f, 0.08f, 0.08f), Color.black);
            Room(red, new Vector3(6f, 3f, 6f));
            harness.AddBox(new Vector3(0f, 2.9f, 0f), new Vector3(1.5f, 0.05f, 1.5f), harness.CreateLitMaterial(Color.black, new Color(25f, 25f, 25f)));
            Look(new Vector3(0f, 1.5f, -2.5f), new Vector3(0f, 1.2f, 2.5f));
            harness.Settings.worldSpace = true;
            harness.Settings.bounceFeedback = 0.6f;
            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            RectInt whole = new RectInt(4, 4, BasisGlobalIlluminationRenderHarness.Width - 8, BasisGlobalIlluminationRenderHarness.Height - 8);
            harness.Settings.surfaceColour = false;
            BasisGlobalIlluminationPass.ReleaseWorld();
            Color grey = harness.Converged(whole, 64, 8);
            harness.Settings.surfaceColour = true;
            BasisGlobalIlluminationPass.ReleaseWorld();
            Color known = harness.Converged(whole, 64, 8);
            float greyTint = grey.r / Mathf.Max(grey.g, 1e-4f), knownTint = known.r / Mathf.Max(known.g, 1e-4f);
            Debug.Log($"[BasisGI] red room indirect: grey voxels {grey} tint {greyTint:F2} | known colour {known} tint {knownTint:F2}");
            Assert.Greater(knownTint, greyTint * 1.05f, "the voxels did not pick up the colour of what they hold");
        }

        [Test]
        public void ThePyramidDoesNotChangeWhatTheRaysSee()
        {
            OpenDark();
            Material white = harness.CreateLitMaterial(new Color(0.75f, 0.75f, 0.75f), Color.black);
            Room(white, new Vector3(10f, 4f, 10f));
            harness.AddBox(new Vector3(-3f, 2f, 3f), new Vector3(0.03f, 2f, 2f), harness.CreateLitMaterial(Color.black, new Color(25f, 2f, 2f)));
            harness.AddBox(new Vector3(3f, 2f, 3f), new Vector3(2f, 0.03f, 2f), harness.CreateLitMaterial(Color.black, new Color(2f, 25f, 2f)));
            harness.AddBox(new Vector3(0f, 2f, 1f), new Vector3(4f, 3f, 0.03f), white);
            Look(new Vector3(0f, 1.5f, -4.5f), new Vector3(0f, 1.2f, 0f));
            harness.Settings.worldSpace = true;
            try
            {
                Whole(48);
                Color without = Color.black, with = Color.black;
                for (int round = 0; round < 3; round++)
                {
                    BasisGlobalIlluminationPass.WorldPyramid = false;
                    Whole(16);
                    without += Whole(4) / 3f;
                    BasisGlobalIlluminationPass.WorldPyramid = true;
                    Whole(16);
                    with += Whole(4) / 3f;
                }
                Debug.Log($"[BasisGI] pyramid: marched every voxel {without} | skipped empty blocks {with}");
                Assert.AreEqual(without.r, with.r, 0.015f, "red moved");
                Assert.AreEqual(without.g, with.g, 0.015f, "green moved");
                Assert.AreEqual(without.b, with.b, 0.015f, "blue moved");
            }
            finally
            {
                BasisGlobalIlluminationPass.WorldPyramid = true;
            }
        }

        [Test]
        public void AFreshSceneDoesNotInheritTheLastScenesColour()
        {
            OpenDark();
            Material white = harness.CreateLitMaterial(new Color(0.75f, 0.75f, 0.75f), Color.black);
            Room(white, new Vector3(6f, 3f, 6f));
            GameObject block = harness.AddBox(new Vector3(0f, 1.5f, 2f), new Vector3(2f, 2f, 0.4f), harness.CreateLitMaterial(Color.black, new Color(30f, 0f, 0f)));
            Look(new Vector3(0f, 1.5f, -2f), new Vector3(0f, 1.5f, 2f));
            harness.Settings.worldSpace = true;
            Color lit = Whole(32);
            block.GetComponent<MeshRenderer>().sharedMaterial = white;
            UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            System.Reflection.MethodInfo unloaded = typeof(BasisGlobalIlluminationPass).GetMethod("WorldSceneUnloaded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            unloaded.Invoke(null, new object[] { scene });
            Color after = Whole(72);
            Debug.Log($"[BasisGI] scene reset: lit {lit} | after {after}");
            Assert.Greater(lit.r - lit.g, 0.05f, "the block never lit the room, so this test proves nothing");
            Assert.Less(after.r - after.g, 0.02f, $"the old scene's light survived the reset: {after}");
        }

        [Test]
        public void ASealedRoomDoesNotLetTheSkyIn()
        {
            OpenDark();
            Room(harness.CreateLitMaterial(new Color(0.75f, 0.75f, 0.75f), Color.black), new Vector3(6f, 3f, 6f));
            Look(new Vector3(-2f, 1.5f, -2f), new Vector3(1f, 1f, 2f));
            harness.Settings.worldSpace = true;
            Color world = Whole(48);
            harness.Settings.worldSpace = false;
            Color screen = Whole(48);
            Debug.Log($"[BasisGI] sealed room: world {world} | screen only {screen}");
            Assert.Greater(screen.b, 0.2f, "the screen only gather no longer reaches the sky, so this test proves nothing");
            Assert.Less(world.b, screen.b * 0.2f, $"the sky reached inside a sealed room through the world cascades: {world}, screen only {screen}");
        }

        [Test]
        public void LightDoesNotCrossAWall()
        {
            OpenDark();
            Material white = harness.CreateLitMaterial(new Color(0.75f, 0.75f, 0.75f), Color.black);
            Room(white, new Vector3(10f, 3f, 6f));
            harness.AddBox(new Vector3(0f, 1.5f, 0f), new Vector3(0.2f, 3f, 6f), white);
            Look(new Vector3(-3f, 1.5f, -2f), new Vector3(0f, 1f, 1f));
            harness.Settings.worldSpace = true;
            Color dark = Whole(48);
            harness.AddBox(new Vector3(3f, 1.5f, 0f), new Vector3(1f, 1f, 1f), harness.CreateLitMaterial(Color.black, new Color(40f, 0f, 0f)));
            Color lit = Whole(48);
            Debug.Log($"[BasisGI] behind a wall: empty {dark} | bright block {lit}");
            Assert.Less(lit.r - dark.r, 0.02f, $"a block behind a wall lit the room in front of it: {dark} became {lit}");
        }

        [Test]
        public void TheSeenColourSurvivesTheClipmapScrolling()
        {
            OpenDark();
            Material white = harness.CreateLitMaterial(new Color(0.75f, 0.75f, 0.75f), Color.black);
            harness.AddSun(Quaternion.Euler(60f, 30f, 0f), 1f);
            harness.AddBox(Vector3.zero, new Vector3(60f, 0.2f, 60f), white);
            harness.AddBox(new Vector3(1.5f, 1.2f, -0.6f), new Vector3(6f, 2.4f, 0.4f), harness.CreateLitMaterial(Color.black, new Color(40f, 0.2f, 0.2f)));
            harness.Settings.worldSpace = true;
            harness.Settings.quality = BasisGlobalIlluminationQuality.High;
            Look(new Vector3(0f, 1.6f, 1.5f), new Vector3(0f, 0.8f, -1f));
            for (int frame = 0; frame < 24; frame++) { harness.Render(); }
            Vector3 start = new Vector3(0f, 1.6f, 1.5f);
            Vector4 before = BasisGlobalIlluminationPass.WorldLevelBox(start, 0, harness.Settings.worldVoxelSize);
            Vector3 end = start;
            for (int step = 0; step < 32; step++)
            {
                end += new Vector3(0.1f, 0f, 0f);
                Look(end, end + new Vector3(0f, -0.5f, 1f));
                harness.Render();
            }
            Assert.AreNotEqual(before, BasisGlobalIlluminationPass.WorldLevelBox(end, 0, harness.Settings.worldVoxelSize), "the walk has to scroll the clipmap for this to test anything");
            Look(end, end + new Vector3(0f, -0.5f, 1f));
            RectInt floor = Probe(end + new Vector3(-1f, -1.5f, 1.6f), 6);
            Color sample = Indirect(floor, 6);
            harness.Settings.worldSpace = false;
            Color screen = Indirect(floor, 24);
            Debug.Log($"[BasisGI] after scrolling: {sample} | screen only {screen}");
            Assert.Greater(sample.r - sample.g, screen.r - screen.g + 0.005f, $"the bright block lost its colour when the clipmap scrolled: {sample}, screen only {screen}");
        }

        [Test]
        public void TheCascadesAgreeWithRayTracingOnAClosedRoom()
        {
            OpenDark();
            harness.Render();
            if (!harness.RayTracingAvailable) { Assert.Ignore("This GPU cannot run the ray traced mode."); }
            harness.Resize(512, 512);
            Material white = harness.CreateLitMaterial(new Color(0.75f, 0.75f, 0.75f), Color.black);
            Room(white, new Vector3(6f, 4f, 8f));
            harness.AddBox(new Vector3(0f, 3.95f, 0f), new Vector3(2.4f, 0.06f, 2.4f), harness.CreateLitMaterial(Color.black, new Color(14f, 13f, 12f)));
            harness.AddBox(new Vector3(-2.9f, 2f, 0f), new Vector3(0.2f, 4f, 8f), harness.CreateLitMaterial(new Color(0.8f, 0.1f, 0.1f), Color.black));
            Light light = harness.AddSun(Quaternion.Euler(90f, 0f, 0f), 1f);
            light.shadows = LightShadows.None;
            Look(new Vector3(0f, 2f, -3.6f), new Vector3(0f, 1.6f, 1f));
            RectInt whole = new RectInt(16, 16, 480, 480);
            RectInt floor = Probe(new Vector3(0f, 0.02f, 0f), 16);
            RectInt nearRed = Probe(new Vector3(-2.2f, 0.02f, 0f), 16);

            harness.Settings.quality = BasisGlobalIlluminationQuality.High;
            harness.Settings.worldSpace = true;
            harness.Settings.bounceFeedback = 0f;
            Color single = Indirect(whole, 96), singleFloor = Indirect(floor, 8);
            harness.Settings.bounceFeedback = 0.6f;
            Color cascades = Indirect(whole, 128), cascadesFloor = Indirect(floor, 8), cascadesRed = Indirect(nearRed, 8);
            harness.Settings.mode = BasisGlobalIlluminationDiffuseMode.RayTraced;
            harness.ResetRayTracing();
            Color traced = Indirect(whole, 160), tracedFloor = Indirect(floor, 8), tracedRed = Indirect(nearRed, 8);
            harness.Settings.overrideQualityCounts = true;
            harness.Settings.bounces = 1;
            harness.Settings.rayCount = 4;
            Color tracedOne = Indirect(whole, 160), tracedOneFloor = Indirect(floor, 8);
            harness.Settings.overrideQualityCounts = false;
            Assert.IsTrue(harness.RayTracingRan, "the reference never traced");
            float ratio = BasisGlobalIlluminationRenderHarness.Luma(cascades) / Mathf.Max(1e-4f, BasisGlobalIlluminationRenderHarness.Luma(traced));
            float singleRatio = BasisGlobalIlluminationRenderHarness.Luma(single) / Mathf.Max(1e-4f, BasisGlobalIlluminationRenderHarness.Luma(tracedOne));
            Debug.Log($"[BasisGI] cascades vs ray traced: many bounces whole {cascades} / {traced} ratio {ratio:F2}, floor {cascadesFloor} / {tracedFloor}, near the red wall {cascadesRed} / {tracedRed}");
            Debug.Log($"[BasisGI] cascades vs ray traced: one bounce whole {single} / {tracedOne} ratio {singleRatio:F2}, floor {singleFloor} / {tracedOneFloor}");
            Assert.Greater(ratio, 0.5f, "the cascades gathered far less light than the ray traced reference");
            Assert.Less(ratio, 2f, "the cascades gathered far more light than the ray traced reference");
        }

        [Test]
        public void ARoughFloorReflectsTheRoomInsteadOfTheSky()
        {
            Open();
            Material white = harness.CreateLitMaterial(new Color(0.75f, 0.75f, 0.75f), Color.black);
            Room(white, new Vector3(6f, 3f, 6f));
            harness.AddBox(new Vector3(0f, 1.5f, 2.9f), new Vector3(4f, 2f, 0.1f), harness.CreateLitMaterial(Color.black, new Color(30f, 1f, 1f)));
            Material metal = harness.CreateLitMaterial(Color.white, Color.black);
            metal.SetFloat("_Metallic", 1f);
            metal.SetFloat("_Smoothness", 0.45f);
            harness.AddBox(new Vector3(0f, 0.01f, 0f), new Vector3(5.8f, 0.02f, 5.8f), metal);
            Look(new Vector3(0f, 2.6f, -2.8f), new Vector3(0f, 0.6f, 2.9f));
            harness.Settings.worldSpace = true;
            RectInt floor = Probe(new Vector3(0f, 0.02f, 0.9f), 6);
            harness.Settings.worldReflections = false;
            Color probe = harness.Converged(floor, 48);
            harness.Settings.worldReflections = true;
            Color world = harness.Converged(floor, 48);
            Debug.Log($"[BasisGI] rough floor reflection: probe {probe} | world cascades {world}");
            Assert.Greater(probe.b, 0.05f, "the reflection probe shows no sky, so this test proves nothing");
            Assert.Greater(world.r / Mathf.Max(world.b, 1e-4f), probe.r / Mathf.Max(probe.b, 1e-4f) * 1.5f, $"the floor still reflects the sky instead of the red wall: {world}, probe {probe}");
            Assert.Greater(world.r / Mathf.Max(world.g, 1e-4f), probe.r / Mathf.Max(probe.g, 1e-4f) * 1.3f, $"the floor does not reflect the red wall in front of it: {world}, probe {probe}");
        }

        [Test]
        public void TheWrappedVolumesArePowersOfTwo()
        {
            Assert.AreEqual(0, BasisGlobalIlluminationPass.WorldVoxels & (BasisGlobalIlluminationPass.WorldVoxels - 1), "the shaders wrap voxel slots with a bit mask");
            Assert.AreEqual(0, BasisGlobalIlluminationPass.WorldProbes & (BasisGlobalIlluminationPass.WorldProbes - 1), "the shaders wrap probe slots with a bit mask");
        }

        [Test]
        public void TheUpdateIntervalFollowsQuality()
        {
            BasisGlobalIlluminationSettings settings = new BasisGlobalIlluminationSettings { quality = BasisGlobalIlluminationQuality.Low };
            Assert.Greater(settings.ResolvedWorldInterval(), 1);
            settings.quality = BasisGlobalIlluminationQuality.High;
            Assert.AreEqual(1, settings.ResolvedWorldInterval());
            settings.quality = BasisGlobalIlluminationQuality.Low;
            settings.overrideQualityCounts = true;
            Assert.AreEqual(1, settings.ResolvedWorldInterval(), "a capture has nothing to accumulate over");
        }
    }
}
