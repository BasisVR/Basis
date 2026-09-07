using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// The screen space reflection backend, end to end: a bright emissive block standing on a polished
    /// metallic floor, rendered through the project's own renderer in Screen Space mode, and the floor is
    /// required to pick the block's colour up. Everything in between - the depth pyramid, the mirror march,
    /// the previous-frame colour capture, the reprojection, the shared denoise stages, the publish, and the
    /// lit shader consumption - has to work for that one number to move, which is the point: these are the
    /// reflections players on the default shipping mode actually get, and until this backend existed that
    /// mode had none at all.
    ///
    /// Measurements are taken through the composited image rather than a debug view, because the consumer
    /// IS the lit shader: a reflection that is traced, denoised and published but never reaches a surface
    /// is indistinguishable from no reflection, and that is exactly the failure that has happened before.
    /// </summary>
    public class BasisGlobalIlluminationScreenSpaceReflectionTests
    {
        private static readonly Vector3 BlockCentre = new Vector3(0f, 1.0f, 1.6f);
        private static readonly Vector3 BlockSize = new Vector3(1.8f, 2.0f, 0.3f);

        private BasisGlobalIlluminationRenderHarness harness;

        [SetUp]
        public void SetUp()
        {
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            BasisGlobalIlluminationEmitter.Registered.Clear();
            harness = new BasisGlobalIlluminationRenderHarness();
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            // The publish parameters are a global, and a global outlives the test that set it. The pass
            // zeroes them from OnCameraCleanup on every frame it runs, but a baseline measured before any
            // reflection pass has run this session must not inherit a live gate from an earlier one.
            Shader.SetGlobalVector("_BasisGISpecularParams", Vector4.zero);
        }

        [TearDown]
        public void TearDown()
        {
            harness?.Dispose();
            harness = null;
        }

        /// <summary>
        /// A dim grey room with one polished metallic floor and a bright red emissive block standing on it,
        /// seen from above at an angle shallow enough that the floor in front of the block reflects it.
        /// Metallic, because a dielectric floor only reflects strongly at grazing angles and the camera
        /// cannot get both the block and a grazing floor into a 192 by 128 frame; a metal floor reflects at
        /// the angle the frame actually has.
        /// </summary>
        private void BuildReflectiveRoom()
        {
            harness.AddSun(Quaternion.Euler(55f, -30f, 0f), 0.4f);

            Material floor = harness.CreateLitMaterial(new Color(0.6f, 0.6f, 0.6f), Color.black);
            floor.SetFloat("_Metallic", 1f);
            floor.SetFloat("_Smoothness", 0.95f);
            harness.AddBox(new Vector3(0f, 0f, 0f), new Vector3(16f, 0.2f, 16f), floor);

            Material block = harness.CreateLitMaterial(Color.black, new Color(24f, 1.2f, 1.2f));
            harness.AddBox(BlockCentre, BlockSize, block);

            harness.Camera.transform.position = new Vector3(0f, 1.7f, -2.6f);
            harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0.35f, 1.2f) - harness.Camera.transform.position, Vector3.up);

            harness.Settings.enable = true;
            harness.Settings.mode = BasisGlobalIlluminationMode.ScreenSpace;
            // Reflections only: the diffuse gather would also carry red onto the floor, and this suite is
            // about whether the SPECULAR path carries it. SpecularActive is independent of intensity.
            harness.Settings.intensity = 0f;
            harness.Settings.specular = true;
            harness.Settings.specularIntensity = 1f;
            harness.Settings.specularTemporal = true;
        }

        /// <summary>
        /// The strip of floor between the camera and the block, where its mirror image lands. Wide, because
        /// the exact band depends on the mirror geometry and a wide probe converges regardless; the average
        /// is diluted by pixels outside the band, which the assertions' margins already allow for.
        /// </summary>
        private static RectInt FloorBand()
        {
            return new RectInt(BasisGlobalIlluminationRenderHarness.Width / 4, 8,
                BasisGlobalIlluminationRenderHarness.Width / 2, BasisGlobalIlluminationRenderHarness.Height / 3);
        }

        /// <summary>How red a reading is against its own other channels, so exposure cancels out.</summary>
        private static float Tint(Color colour) { return colour.r - colour.g; }

        [Test]
        public void TheFloorReflectsTheEmissiveBlockWithoutRayTracing()
        {
            BuildReflectiveRoom();
            BasisGlobalIlluminationHistory.ReleaseAll();

            harness.Settings.specular = false;
            Color off = harness.Converged(FloorBand(), 12, 6);

            harness.Settings.specular = true;
            Color on = harness.Converged(FloorBand(), 24, 8);

            Debug.Log($"[BasisGI] SSR floor band on={on} off={off} :: {harness.Describe()}");
            Assert.IsFalse(float.IsNaN(on.r) || float.IsNaN(on.g) || float.IsNaN(on.b), $"the reflecting floor read {on}, which is not a colour");
            Assert.Greater(on.r, off.r + 0.04f,
                $"the floor read {on} with screen space reflections on against {off} with them off, so the emissive block's reflection never reached it ({harness.Describe()})");
            Assert.Greater(Tint(on), Tint(off) + 0.03f,
                $"the floor brightened from {off} to {on} without turning red, so whatever arrived was not the block's reflection");
        }

        [Test]
        public void TheFirstFrameFallsBackToTheProbeWithoutExploding()
        {
            BuildReflectiveRoom();
            // A cold start: no accumulation, no captured colour. The trace must answer with the fallback and
            // zero confidence rather than reading an uninitialised target - a wrong answer here is not a
            // subtly dim reflection, it is garbage smeared across every smooth surface on the first frame
            // after reflections switch on.
            BasisGlobalIlluminationHistory.ReleaseAll();

            Color first = harness.RenderAndSample(FloorBand());

            Debug.Log($"[BasisGI] SSR first frame floor band={first}");
            Assert.IsFalse(float.IsNaN(first.r) || float.IsNaN(first.g) || float.IsNaN(first.b), $"the first frame read {first}");
            Assert.Greater(first.r + first.g + first.b, 0.001f, $"the first frame read {first}: the floor went black, so the fallback path did not answer");
        }

        [Test]
        public void TheReflectionSurvivesTheCameraTurning()
        {
            BuildReflectiveRoom();
            BasisGlobalIlluminationHistory.ReleaseAll();

            harness.Settings.specular = false;
            Color off = harness.Converged(FloorBand(), 12, 6);

            harness.Settings.specular = true;
            for (int frame = 0; frame < 20; frame++) { harness.Render(); }

            // A camera that turns is the case the previous-frame reprojection exists for: every hit lands on
            // a part of last frame's image that is no longer where it was. The reflection is allowed to
            // shift and to soften; what it must not do is pop to nothing on any single frame, because that
            // reads as the whole floor flickering.
            Quaternion baseRotation = harness.Camera.transform.rotation;
            for (int frame = 0; frame < 8; frame++)
            {
                harness.Camera.transform.rotation = baseRotation * Quaternion.Euler(0f, (frame + 1) * 0.4f, 0f);
                Color turning = harness.RenderAndSample(FloorBand());
                Assert.Greater(turning.r, off.r + 0.02f,
                    $"on turning frame {frame} the floor read {turning} against an off baseline of {off}: the reflection vanished while the camera turned");
            }
            harness.Camera.transform.rotation = baseRotation;
        }

        /// <summary>
        /// The reported-from-the-field failure this guards against: a world with a perfectly visible
        /// skybox whose baked reflection environment is black - never generated, stripped from the bundle -
        /// reflected a confident black sky in every polished floor. The fix reads the rendered sky out of
        /// the previous frame's capture when the missed ray's direction lands on sky the screen can see.
        /// Green can ONLY arrive through that path here: the skybox is set without regenerating the baked
        /// environment, so the cubemap fallback still holds whatever the scene baked before - the exact
        /// divergence the field bug was made of.
        /// </summary>
        [Test]
        public void SkyTheScreenCanSeeReflectsIntoTheFloor()
        {
            harness.AddSun(Quaternion.Euler(55f, -30f, 0f), 0.4f);

            Material floor = harness.CreateLitMaterial(new Color(0.6f, 0.6f, 0.6f), Color.black);
            floor.SetFloat("_Metallic", 1f);
            floor.SetFloat("_Smoothness", 0.95f);
            harness.AddBox(new Vector3(0f, 0f, 0f), new Vector3(16f, 0.2f, 16f), floor);

            // Low and level, so the frame holds the horizon: sky above, grazing floor below. A grazing
            // floor's mirror rays leave at the same few degrees they arrived, which puts their vanishing
            // points just above the horizon - on screen, where the capture holds the rendered sky.
            harness.Camera.transform.position = new Vector3(0f, 0.6f, -5.5f);
            harness.Camera.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            harness.Camera.clearFlags = CameraClearFlags.Skybox;

            Cubemap greenSky = new Cubemap(4, TextureFormat.RGBA32, false);
            Color[] face = new Color[16];
            for (int texel = 0; texel < face.Length; texel++) { face[texel] = new Color(0f, 1f, 0f, 1f); }
            for (int side = 0; side < 6; side++) { greenSky.SetPixels(face, (CubemapFace)side); }
            greenSky.Apply();
            Material skybox = new Material(Shader.Find("Skybox/Cubemap"));
            skybox.SetTexture("_Tex", greenSky);
            Material previousSkybox = RenderSettings.skybox;
            RenderSettings.skybox = skybox;

            try
            {
                harness.Settings.enable = true;
                harness.Settings.mode = BasisGlobalIlluminationMode.ScreenSpace;
                harness.Settings.intensity = 0f;
                harness.Settings.specular = true;
                harness.Settings.specularIntensity = 1f;
                harness.Settings.specularTemporal = true;
                BasisGlobalIlluminationHistory.ReleaseAll();

                // The strip of far floor just under the horizon, where the grazing reflections land.
                RectInt horizonBand = new RectInt(BasisGlobalIlluminationRenderHarness.Width / 4,
                    BasisGlobalIlluminationRenderHarness.Height / 2 - 22,
                    BasisGlobalIlluminationRenderHarness.Width / 2, 18);

                harness.Settings.specular = false;
                Color off = harness.Converged(horizonBand, 12, 6);

                harness.Settings.specular = true;
                Color on = harness.Converged(horizonBand, 24, 8);

                Debug.Log($"[BasisGI] SSR sky band on={on} off={off} :: {harness.Describe()}");
                Assert.Greater(on.g - on.r, (off.g - off.r) + 0.02f,
                    $"the floor under the horizon read {on} with reflections on against {off} with them off: the green sky the screen itself shows never reached the reflection, which is the black-sky field bug");
            }
            finally
            {
                RenderSettings.skybox = previousSkybox;
                Object.DestroyImmediate(skybox);
                Object.DestroyImmediate(greenSky);
            }
        }

        [Test]
        public void ScreenSpaceIsChosenExactlyWhenTheModeOrTheHardwareSaysSo()
        {
            BasisGlobalIlluminationSettings settings = new BasisGlobalIlluminationSettings();

            settings.mode = BasisGlobalIlluminationMode.ScreenSpace;
            Assert.IsTrue(BasisGlobalIlluminationPass.SpecularPass.ScreenSpaceReflections(settings, true),
                "Screen Space mode must use the screen space backend even where ray tracing exists - the mode is the player's costing decision");
            Assert.IsTrue(BasisGlobalIlluminationPass.SpecularPass.ScreenSpaceReflections(settings, false));

            settings.mode = BasisGlobalIlluminationMode.RayTraced;
            Assert.IsFalse(BasisGlobalIlluminationPass.SpecularPass.ScreenSpaceReflections(settings, true),
                "Ray Traced mode with the hardware for it must keep the ray traced backend");
            Assert.IsTrue(BasisGlobalIlluminationPass.SpecularPass.ScreenSpaceReflections(settings, false),
                "Ray Traced mode without the hardware used to mean no reflections at all; it must fall back to screen space the way the diffuse gather falls back");
        }

        [Test]
        public void ThePriorColourStampOnlyTrustsARecentCapture()
        {
            // The stamp logic is pure, but the target it stamps is a real RTHandle, and allocating one
            // needs the same graphics device the render tests need.
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();

            BasisGlobalIlluminationHistory history = new BasisGlobalIlluminationHistory();
            Assert.IsFalse(history.PriorColorContiguous(10), "nothing has been captured, so nothing is recent");

            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(64, 64, RenderTextureFormat.ARGBHalf, 0);
            history.EnsurePriorColor(descriptor);
            Assert.IsFalse(history.PriorColorContiguous(10), "allocation is not capture: the target holds nothing until the capture pass writes it");

            history.RecordPriorColorFrame(10);
            Assert.IsTrue(history.PriorColorContiguous(11), "captured last frame");
            Assert.IsFalse(history.PriorColorContiguous(40), "a capture thirty frames old is not a frame to reproject into");

            // A rate limited camera - the handheld camera at a third of the display rate - captures every
            // third frame, and the window has to learn that stride rather than reject its whole feed.
            history.RecordPriorColorFrame(13);
            history.RecordPriorColorFrame(16);
            Assert.IsTrue(history.PriorColorContiguous(19), "a camera on a three frame stride is contiguous three frames later");

            // A resize means the new target holds nothing, whatever the stamp said a moment ago.
            descriptor.width = 128;
            history.EnsurePriorColor(descriptor);
            Assert.IsFalse(history.PriorColorContiguous(17), "a reallocated target holds nothing until the capture pass writes it again");

            history.RecordPriorColorFrame(18);
            Assert.IsTrue(history.PriorColorContiguous(19));
            history.Release();
            Assert.IsFalse(history.PriorColorContiguous(19), "released with the rest of the reflection state");
            Assert.IsNull(history.PriorColor);
        }
    }
}
