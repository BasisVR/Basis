using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// Whether a mirror shows the same lit room the player is standing in.
    ///
    /// It did not, and for a reason no author could have found: mirrors ship with Render Post Processing
    /// OFF - a sensible default for a camera that draws the room a second time - and this effect refused
    /// to run on any camera with that toggle off. It is not part of the post stack; it composites before
    /// transparents off the depth buffer and needs nothing that stack provides. The result was a mirror
    /// rendering the room with no bounce light at all, next to a direct view of the same room with it.
    /// </summary>
    public class BasisGlobalIlluminationMirrorTests
    {
        private BasisGlobalIlluminationRenderHarness harness;
        private UniversalAdditionalCameraData cameraData;
        private bool previousMirrors;

        [SetUp]
        public void SetUp()
        {
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            BasisGlobalIlluminationEmitter.Registered.Clear();
            harness = new BasisGlobalIlluminationRenderHarness();
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            cameraData = harness.Camera.GetUniversalAdditionalCameraData();
            previousMirrors = harness.Feature != null && harness.Feature.Mirrors;
        }

        [TearDown]
        public void TearDown()
        {
            if (cameraData != null) { cameraData.isMirrorReflectionCamera = false; }
            if (harness?.Feature != null) { harness.Feature.Mirrors = previousMirrors; }
            harness?.Dispose();
            harness = null;
            cameraData = null;
        }

        /// <summary>A grey room with one red emissive slat, so the bounce onto the floor beside it is red.</summary>
        private void BuildRoom()
        {
            harness.AddSun(Quaternion.Euler(60f, -30f, 0f), 0.25f);

            Material surface = harness.CreateLitMaterial(new Color(0.85f, 0.85f, 0.85f), Color.black);
            harness.AddBox(Vector3.zero, new Vector3(20f, 0.2f, 20f), surface);
            harness.AddBox(new Vector3(0f, 2f, 5f), new Vector3(20f, 6f, 0.2f), surface);

            Material slat = harness.CreateLitMaterial(Color.black, new Color(18f, 0.5f, 0.5f));
            harness.AddBox(new Vector3(0f, 0.45f, 0.6f), new Vector3(1.6f, 0.9f, 0.06f), slat);

            Vector3 origin = new Vector3(0f, 1.15f, -2.5f);
            harness.Camera.transform.position = origin;
            harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0.15f, 0.35f) - origin, Vector3.up);
            harness.Settings.fallback = BasisGlobalIlluminationFallback.None;
        }

        /// <summary>The floor in front of the slat, where the red bounce lands.</summary>
        private RectInt Floor()
        {
            Vector3 a = harness.Camera.WorldToScreenPoint(new Vector3(-0.5f, 0.101f, 0.18f));
            Vector3 b = harness.Camera.WorldToScreenPoint(new Vector3(0.5f, 0.101f, 0.5f));
            int xMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(a.x, b.x)), 1, BasisGlobalIlluminationRenderHarness.Width - 2);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(a.x, b.x)), 1, BasisGlobalIlluminationRenderHarness.Width - 2);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(a.y, b.y)), 1, BasisGlobalIlluminationRenderHarness.Height - 2);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(a.y, b.y)), 1, BasisGlobalIlluminationRenderHarness.Height - 2);
            return new RectInt(xMin, yMin, Mathf.Max(3, xMax - xMin), Mathf.Max(3, yMax - yMin));
        }

        /// <summary>
        /// What the floor reads with post processing OFF throughout, so the only thing varying between
        /// readings is whether the effect ran. Comparing a post-processed frame against a non-post-processed
        /// one would move the image for reasons that have nothing to do with the bounce.
        /// </summary>
        private Color Read(RectInt region, bool asMirror, bool mirrorsAllowed)
        {
            cameraData.renderPostProcessing = false;
            cameraData.isMirrorReflectionCamera = asMirror;
            if (harness.Feature != null) { harness.Feature.Mirrors = mirrorsAllowed; }
            return harness.Converged(region);
        }

        [Test]
        public void AMirrorShowsTheBounceEvenWithItsPostProcessingOff()
        {
            BuildRoom();
            RectInt floor = Floor();

            Color ordinary = Read(floor, asMirror: false, mirrorsAllowed: true);
            Color mirror = Read(floor, asMirror: true, mirrorsAllowed: true);
            Color mirrorDenied = Read(floor, asMirror: true, mirrorsAllowed: false);

            Debug.Log($"[BasisGI] mirror gate, post processing off throughout: " +
                      $"ordinary camera red={ordinary.r:F4}  mirror red={mirror.r:F4}  " +
                      $"mirror with Mirrors off red={mirrorDenied.r:F4}\n{harness.Describe()}");

            // Red, because red is what the slat emits. An ordinary camera with post processing off still
            // gets no bounce - that gate is deliberate for everything which is not a mirror.
            Assert.Greater(mirror.r, ordinary.r * 1.02f,
                $"a mirror reflection camera gathered no more bounce than an ordinary camera with post " +
                $"processing off, so the exemption is not reaching it: ordinary {ordinary.r:F4}, " +
                $"mirror {mirror.r:F4}");

            Assert.That(mirrorDenied.r, Is.EqualTo(ordinary.r).Within(0.02f * Mathf.Max(ordinary.r, 0.01f)),
                $"switching Mirrors off left the mirror still rendering the effect, so the setting cannot " +
                $"turn it back off: ordinary {ordinary.r:F4}, mirror denied {mirrorDenied.r:F4}");
        }

        [Test]
        public void AMirrorIsRecognisedByItsCameraDataRatherThanItsType()
        {
            BuildRoom();
            cameraData.isMirrorReflectionCamera = false;
            Assert.IsFalse(BasisGlobalIlluminationFeature.IsMirrorReflection(harness.Camera),
                "an ordinary camera was taken for a mirror");

            cameraData.isMirrorReflectionCamera = true;
            Assert.IsTrue(BasisGlobalIlluminationFeature.IsMirrorReflection(harness.Camera),
                "a camera flagged as a mirror reflection was not recognised as one");

            // A mirror is a Game camera pointed at a reflected pose. CameraType.Reflection means a
            // reflection PROBE capture, which is a different thing with its own toggle, and conflating the
            // two would have mirrors follow the probe setting.
            Assert.AreEqual(CameraType.Game, harness.Camera.cameraType,
                "the mirror test camera is not a Game camera, so this no longer proves the two are distinct");
        }

        [Test]
        public void NullIsNotAMirror()
        {
            Assert.IsFalse(BasisGlobalIlluminationFeature.IsMirrorReflection(null));
        }
    }
}
