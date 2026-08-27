using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// What the temporal filter does with a world that is not standing still.
    ///
    /// Reprojecting through the previous view-projection alone carries the camera's motion and nothing
    /// else, so a pixel now sitting on a moving surface is walked back to whatever was behind that surface
    /// last frame. The depth test then rejects the history every single frame and the accumulation never
    /// starts - which, in a room full of avatars, makes the avatars the noisiest thing in it. These two
    /// tests are the two halves of the fix: that the motion vector path reprojects to the same place as
    /// the matrix where the two must agree, and that it beats the matrix where they must not.
    /// </summary>
    public class BasisGlobalIlluminationMotionTests
    {
        private static readonly Vector3 CameraOrigin = new Vector3(0f, 1.5f, -3.2f);
        private static readonly Vector3 CameraAim = new Vector3(0f, 0.6f, 0.3f);

        private BasisGlobalIlluminationRenderHarness harness;
        private GameObject mover;

        [SetUp]
        public void SetUp()
        {
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            BasisGlobalIlluminationEmitter.Registered.Clear();
            harness = new BasisGlobalIlluminationRenderHarness();
            // An inherited debug view replaces the composite instead of multiplying into it, and everything
            // measured through one reads as though half the settings do nothing. Pin it.
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
        }

        [TearDown]
        public void TearDown()
        {
            harness?.Dispose();
            harness = null;
            mover = null;
        }

        /// <summary>
        /// A grey room with a red emissive strip laid across the floor in front of one box, so the box's
        /// camera-facing side is lit almost entirely by the bounce off that strip. The strip is deliberately
        /// ON screen: this has to measure the screen space gather, which is what the shipped default runs,
        /// and that gather can only find light the camera already drew.
        /// </summary>
        private void BuildRoom()
        {
            harness.AddSun(Quaternion.Euler(52f, -24f, 0f), 0.35f);

            Material surface = harness.CreateLitMaterial(new Color(0.8f, 0.8f, 0.8f), Color.black);
            harness.AddBox(new Vector3(0f, 0f, 0f), new Vector3(16f, 0.2f, 16f), surface);
            harness.AddBox(new Vector3(0f, 2f, 4f), new Vector3(16f, 6f, 0.2f), surface);

            Material strip = harness.CreateLitMaterial(Color.black, new Color(14f, 0.4f, 0.4f));
            harness.AddBox(new Vector3(0f, 0.12f, -1.2f), new Vector3(7f, 0.04f, 0.7f), strip);

            mover = harness.AddBox(MoverCentre, new Vector3(2.4f, 1.2f, 1.2f), surface);
            // MeshRenderer defaults do not guarantee per object motion. Without this URP writes only the
            // camera's motion for it, both paths reproject identically, and the moving test below would be
            // measuring nothing while looking like it passed.
            foreach (Renderer renderer in mover.GetComponentsInChildren<Renderer>())
            {
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            }

            harness.Camera.transform.position = CameraOrigin;
            harness.Camera.transform.rotation = Quaternion.LookRotation(CameraAim - CameraOrigin, Vector3.up);
            harness.Settings.fallback.value = BasisGlobalIlluminationFallback.None;
        }

        /// <summary>
        /// How far the box travels per frame, in world metres, to put a given number of TRACED texels of
        /// motion on screen.
        ///
        /// This calibration is the whole reason the first version of this test measured nothing. What
        /// decides whether the two reprojections differ is not how fast something moves in metres, it is
        /// how many texels it crosses per frame - under about one, the matrix is wrong by less than the
        /// bilinear history fetch smooths over, and there is nothing to tell apart. This target is 192x128,
        /// where an avatar walking at 1.2 m/s crosses a THIRD of a traced texel per frame. At the 1080p a
        /// player actually runs it crosses nearly three. Fixing the speed in metres would therefore have
        /// tested a regime no player is ever in; fixing it in texels tests the one they are always in.
        /// </summary>
        private float MetresPerFrameForTexels(float tracedTexelsPerFrame)
        {
            float distance = Vector3.Distance(harness.Camera.transform.position, MoverCentre + new Vector3(0f, 0f, -0.6f));
            float worldPerPixel = 2f * distance * Mathf.Tan(harness.Camera.fieldOfView * 0.5f * Mathf.Deg2Rad)
                                  / BasisGlobalIlluminationRenderHarness.Height;
            int divisor = harness.Settings.ResolvedResolutionDivisor();
            return tracedTexelsPerFrame * divisor * worldPerPixel;
        }

        private static readonly Vector3 MoverCentre = new Vector3(0.12f, 0.6f, 0.6f);

        /// <summary>
        /// Where the box sits on a given frame, swinging either side of one point rather than travelling in
        /// a straight line. At the speed this has to run to be representative the box would cross the whole
        /// target in a handful of frames, and the probe would end up measuring the silhouette leaving it.
        /// A triangle wave gives the same per-frame displacement - which is all either reprojection sees -
        /// while keeping the box over the probe for the whole run.
        /// </summary>
        private Vector3 MoverAt(float step, float metresPerFrame, float halfPeriod = 4f)
        {
            float phase = Mathf.Repeat(step, halfPeriod * 2f);
            float offset = (phase < halfPeriod ? phase : halfPeriod * 2f - phase) - halfPeriod * 0.5f;
            return MoverCentre + new Vector3(offset * metresPerFrame, 0f, 0f);
        }

        /// <summary>
        /// A patch of the moving box's camera-facing side, taken at the middle of its travel and kept well
        /// inside the face so the box still covers it at both ends of the run. A probe that slid off the box
        /// would be measuring the silhouette crossing it, which reads exactly like the instability being
        /// looked for.
        /// </summary>
        private RectInt FrontFace()
        {
            Vector3 centre = MoverCentre + new Vector3(0f, 0f, -0.6f);
            Vector3 near = harness.Camera.WorldToScreenPoint(centre + new Vector3(-0.35f, -0.28f, 0f));
            Vector3 far = harness.Camera.WorldToScreenPoint(centre + new Vector3(0.35f, 0.28f, 0f));
            int xMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(near.x, far.x)), 1, BasisGlobalIlluminationRenderHarness.Width - 2);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(near.x, far.x)), 1, BasisGlobalIlluminationRenderHarness.Width - 2);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(near.y, far.y)), 1, BasisGlobalIlluminationRenderHarness.Height - 2);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(near.y, far.y)), 1, BasisGlobalIlluminationRenderHarness.Height - 2);
            return new RectInt(xMin, yMin, Mathf.Max(4, xMax - xMin), Mathf.Max(4, yMax - yMin));
        }

        /// <summary>
        /// Renders a run with the box travelling one step per frame and returns what the probe read on each
        /// measured frame. The warm-up is walked at the same pace, so the accumulation being measured is one
        /// that has been living with the motion rather than one that settled while still and was then
        /// disturbed.
        /// </summary>
        private Color[] MovingRun(RectInt region, bool motionVectors, float tracedTexelsPerFrame, int frames = 16, int warmup = 22)
        {
            // Read the effect's own output, not the frame it was composited into. The composite is a
            // multiply, so a probe on a lit box is mostly box: measured that way both reprojections read a
            // swing of 0.00% and the measurement says nothing at all about either of them.
            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            harness.Settings.motionVectors.value = motionVectors;
            float metresPerFrame = MetresPerFrameForTexels(tracedTexelsPerFrame);
            harness.Camera.transform.position = CameraOrigin;
            harness.Camera.transform.rotation = Quaternion.LookRotation(CameraAim - CameraOrigin, Vector3.up);

            Color[] samples = new Color[frames];
            for (int frame = 0; frame < warmup + frames; frame++)
            {
                mover.transform.position = MoverAt(frame, metresPerFrame);
                harness.Render();
                if (frame >= warmup) { samples[frame - warmup] = harness.Sample(region); }
            }
            return samples;
        }

        /// <summary>Renders a run in which only the camera moves, which is what the two reprojections have to agree on.</summary>
        private Color[] CameraRun(RectInt region, bool motionVectors, int frames = 16, int warmup = 22)
        {
            // Through the effect's own output, for the same reason as above - and here it also makes the
            // comparison strict, because a reprojection error that the composite would have buried under
            // the box's own shading has nowhere to hide in the raw indirect.
            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            harness.Settings.motionVectors.value = motionVectors;
            mover.transform.position = MoverCentre;

            Color[] samples = new Color[frames];
            for (int frame = 0; frame < warmup + frames; frame++)
            {
                // The camera alone moves, at a walking pace across the view. Both reprojections describe
                // this displacement exactly, so any disagreement between them is a defect in one of them.
                Vector3 position = CameraOrigin + new Vector3(frame * 0.010f, 0f, 0f);
                harness.Camera.transform.position = position;
                harness.Camera.transform.rotation = Quaternion.LookRotation(CameraAim - position, Vector3.up);
                harness.Render();
                if (frame >= warmup) { samples[frame - warmup] = harness.Sample(region); }
            }
            return samples;
        }

        /// <summary>
        /// Refuses to measure anything if this run cannot produce motion vectors in the first place.
        ///
        /// URP advances the previous-frame matrix that its motion vector pass is built on once per ENGINE
        /// frame. Driving a camera with Camera.Render() in a loop never advances that counter, so the pass
        /// keeps differencing against a matrix that never moves on and the texture holds a fixed vector
        /// with nothing to do with the scene. Measured 2026-08-27 on this harness: the counter moved by
        /// zero across twelve renders, `yield return null` inside a UnityTest did not help, and with
        /// nothing moving and the camera bolted down the texture still read about 0.009 in UV - roughly a
        /// pixel and a half - where every vector had to be exactly zero. Everything downstream of that is
        /// a measurement of the broken input.
        ///
        /// The check is a guard rather than a hard skip so that these keep their value: run them somewhere
        /// the engine really is ticking - play mode, or a build - and they measure the thing they claim to.
        /// </summary>
        private void SkipWithoutUsableMotionVectors()
        {
            int before = Time.frameCount;
            for (int frame = 0; frame < 4; frame++) { harness.Render(); }
            if (Time.frameCount == before)
            {
                Assert.Ignore("This run cannot produce motion vectors: the engine frame counter did not advance " +
                              "across four renders, so URP's motion vector pass is differencing against a matrix " +
                              "that never moves and its texture holds a fixed vector unrelated to the scene. " +
                              "Measuring a reprojection against that measures the harness, not the effect.");
            }
        }

        private static float Level(Color[] samples)
        {
            return BasisGlobalIlluminationRenderHarness.Mean(samples, BasisGlobalIlluminationRenderHarness.Luma);
        }

        private static float Swing(Color[] samples)
        {
            return BasisGlobalIlluminationRenderHarness.RelativeFrameToFrameSwing(samples, BasisGlobalIlluminationRenderHarness.Luma);
        }

        /// <summary>
        /// With only the camera moving, the two reprojections are describing the same displacement by two
        /// different routes and have to land on the same texel. This is the test that catches a sign or a
        /// flip: Unity's motion vectors are FORWARD vectors, already halved into UV space and already
        /// carrying the platform's v flip, so the previous position is uv minus the texel and nothing else.
        /// Get any part of that wrong and the history is fetched from the wrong place every frame, the depth
        /// test throws most of it away, and the level and the steadiness both move.
        /// </summary>
        [Test]
        public void MotionVectorsReprojectWhereTheMatrixDoesWhileOnlyTheCameraMoves()
        {
            BuildRoom();
            SkipWithoutUsableMotionVectors();
            RectInt region = FrontFace();

            // What the same measurement disagrees with itself by, so the comparison below is judged against
            // this run's own repeatability rather than against a number chosen in advance.
            float control = Level(CameraRun(region, false));
            float controlAgain = Level(CameraRun(region, false));
            float floor = Mathf.Max(0.004f, Mathf.Abs(control - controlAgain) * 2f);

            float withVectors = Level(CameraRun(region, true));
            float difference = Mathf.Abs(withVectors - control);

            Debug.Log($"[BasisGI] camera-only reprojection: matrix={control:F4} motionVectors={withVectors:F4} " +
                      $"difference={difference:F4} floor={floor:F4}\n{harness.Describe()}");

            Assert.Greater(control, 0.002f, "the probe gathered nothing, so this run compared two blacks");
            Assert.LessOrEqual(difference, floor * 3f,
                "reprojecting through motion vectors put the history somewhere the matrix did not, with only the " +
                "camera moving - the two describe the same displacement there, so this is a sign, a flip or a " +
                $"scale in the motion vector read: matrix {control:F4} against motion vectors {withVectors:F4}, " +
                $"floor {floor:F4}");
        }

        /// <summary>
        /// The point of the whole change. A surface that is moving reprojects, under the matrix, onto
        /// whatever was behind it, so its history is rejected on depth every frame and what reaches the
        /// screen is a raw one or two ray estimate - it flickers. Motion vectors follow the surface, the
        /// history is accepted, and the accumulation actually runs.
        /// </summary>
        [Test]
        public void AMovingSurfaceKeepsItsAccumulationWithMotionVectors()
        {
            BuildRoom();
            SkipWithoutUsableMotionVectors();
            RectInt region = FrontFace();

            // Swept rather than measured at one speed, because the answer genuinely depends on it and a
            // single number would hide that. Below roughly one traced texel per frame the matrix is wrong
            // by less than the history fetch smooths over and the two are indistinguishable; the speeds
            // that matter are the ones a player is actually looking at.
            float[] speeds = { 0.3f, 1.5f, 3f };
            System.Text.StringBuilder report = new System.Text.StringBuilder();
            float assertedMatrix = 0f, assertedVectors = 0f, assertedLevel = 0f, assertedVectorLevel = 0f;

            // The still image is the ground truth both paths are trying to reproduce. Reprojection error
            // does not show up as a level change - it shows up as the surface's own structure being smeared
            // across itself - so the number that matters is how far each path's spatial detail sits from
            // what the same surface holds when nothing is moving at all.
            Color[] stillRun = MovingRun(region, false, 0f);
            float stillStructure = harness.SpatialNoise(region, BasisGlobalIlluminationRenderHarness.Luma);
            float stillLevel = Level(stillRun);
            report.Append($"  still reference: level={stillLevel:F4} structure={stillStructure:F5}\n");

            for (int index = 0; index < speeds.Length; index++)
            {
                Color[] matrix = MovingRun(region, false, speeds[index]);
                float matrixStructure = harness.SpatialNoise(region, BasisGlobalIlluminationRenderHarness.Luma);
                Color[] vectors = MovingRun(region, true, speeds[index]);
                float vectorStructure = harness.SpatialNoise(region, BasisGlobalIlluminationRenderHarness.Luma);

                float matrixSwing = Swing(matrix), vectorSwing = Swing(vectors);
                float matrixLevel = Level(matrix), vectorLevel = Level(vectors);
                report.Append($"  {speeds[index]:F1} traced texels/frame: " +
                              $"matrix[level={matrixLevel:F4} swing={matrixSwing:P2} structure={matrixStructure:F5}] " +
                              $"motionVectors[level={vectorLevel:F4} swing={vectorSwing:P2} structure={vectorStructure:F5}]\n");

                // The 3 texel case is the one asserted on: that is a 1.2 m/s walk seen at 1080p, which is
                // the single most common thing in front of a player in this application.
                if (Mathf.Approximately(speeds[index], 3f))
                {
                    assertedMatrix = matrixSwing;
                    assertedVectors = vectorSwing;
                    assertedLevel = matrixLevel;
                    assertedVectorLevel = vectorLevel;
                }
            }

            Debug.Log($"[BasisGI] moving surface, by on-screen speed:\n{report}{harness.Describe()}");

            Assert.Greater(assertedLevel, 0.002f, "the probe gathered nothing off the moving box, so this run measured black");
            Assert.Greater(assertedVectorLevel, 0.002f, "the probe gathered nothing off the moving box with motion vectors on");

            // The bounce itself must survive the change - a reprojection that steadied the image by losing
            // the light would pass a swing test and be worthless.
            Assert.That(assertedVectorLevel, Is.EqualTo(assertedLevel).Within(0.35f * assertedLevel),
                "following the surface changed how much light reached it, which it must not: " +
                $"{assertedLevel:F4} became {assertedVectorLevel:F4}");

            Assert.Less(assertedVectors, assertedMatrix * 0.85f,
                "a surface moving at the speed a walking avatar crosses a 1080p frame is no steadier with " +
                "motion vectors than without them, so its history is still being rejected every frame: " +
                $"matrix swing {assertedMatrix:P2}, motion vectors {assertedVectors:P2}");
        }
    }
}
