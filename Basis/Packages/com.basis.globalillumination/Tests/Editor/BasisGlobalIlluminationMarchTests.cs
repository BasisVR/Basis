using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// What the screen space march can and cannot find.
    ///
    /// The plain march spends Ray Steps steps uniformly over Max Ray Length, so at the shipped default its
    /// FIRST sample already sits most of a metre along the ray. Anything nearer than that is only found
    /// when the per-pixel jitter happens to drop a step on it - which is the near field, where a bounce is
    /// brightest and where a player is most likely to notice it missing. The hierarchical march spends its
    /// fine steps inside whichever coarse cell could hold a hit instead of spreading them over the whole
    /// ray, so its stride near the origin is about a texel.
    /// </summary>
    public class BasisGlobalIlluminationMarchTests
    {
        private static readonly Vector3 CameraOrigin = new Vector3(0f, 1.15f, -2.5f);
        private static readonly Vector3 CameraAim = new Vector3(0f, 0.15f, 0.35f);

        /// <summary>A thin bright slat stood on the floor. Its base is the contact the near field is read at.</summary>
        private static readonly Vector3 SlatCentre = new Vector3(0f, 0.45f, 0.6f);

        private BasisGlobalIlluminationRenderHarness harness;

        [SetUp]
        public void SetUp()
        {
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            BasisGlobalIlluminationEmitter.Registered.Clear();
            harness = new BasisGlobalIlluminationRenderHarness();
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
        }

        [TearDown]
        public void TearDown()
        {
            harness?.Dispose();
            harness = null;
        }

        private void BuildRoom()
        {
            harness.AddSun(Quaternion.Euler(60f, -30f, 0f), 0.25f);

            Material surface = harness.CreateLitMaterial(new Color(0.85f, 0.85f, 0.85f), Color.black);
            harness.AddBox(Vector3.zero, new Vector3(20f, 0.2f, 20f), surface);
            harness.AddBox(new Vector3(0f, 2f, 5f), new Vector3(20f, 6f, 0.2f), surface);

            // Six centimetres thick and standing on the floor, so the light it throws onto the boards
            // beside it lives entirely within the first stride of the plain march.
            Material slat = harness.CreateLitMaterial(Color.black, new Color(18f, 0.5f, 0.5f));
            harness.AddBox(SlatCentre, new Vector3(1.6f, 0.9f, 0.06f), slat);

            harness.Camera.transform.position = CameraOrigin;
            harness.Camera.transform.rotation = Quaternion.LookRotation(CameraAim - CameraOrigin, Vector3.up);
            // Only what the march itself brought back: with the environment on, most rays miss and return a
            // smooth convolved cubemap that swamps the difference being measured.
            harness.Settings.fallback.value = BasisGlobalIlluminationFallback.None;
            harness.Settings.emitters.value = false;
        }

        private RectInt FloorPatch(float nearZ, float farZ)
        {
            Vector3 a = harness.Camera.WorldToScreenPoint(new Vector3(-0.5f, 0.101f, nearZ));
            Vector3 b = harness.Camera.WorldToScreenPoint(new Vector3(0.5f, 0.101f, farZ));
            int xMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(a.x, b.x)), 1, BasisGlobalIlluminationRenderHarness.Width - 2);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(a.x, b.x)), 1, BasisGlobalIlluminationRenderHarness.Width - 2);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(a.y, b.y)), 1, BasisGlobalIlluminationRenderHarness.Height - 2);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(a.y, b.y)), 1, BasisGlobalIlluminationRenderHarness.Height - 2);
            return new RectInt(xMin, yMin, Mathf.Max(3, xMax - xMin), Mathf.Max(3, yMax - yMin));
        }

        /// <summary>The boards immediately in front of the slat, where the bounce is a contact.</summary>
        private RectInt Contact() { return FloorPatch(SlatCentre.z - 0.42f, SlatCentre.z - 0.10f); }

        /// <summary>Open floor a good way from it, which both marches have every chance of reaching.</summary>
        private RectInt FarField() { return FloorPatch(-0.9f, -0.3f); }

        /// <summary>
        /// Gathers the probe with the march under test, holding every other quality knob still so the only
        /// thing that varies between readings is how the ray is walked.
        /// </summary>
        private Color Measure(RectInt region, bool hierarchical, int steps)
        {
            harness.Settings.mode.value = BasisGlobalIlluminationMode.ScreenSpace;
            harness.Settings.hierarchicalMarch.value = hierarchical;
            harness.Settings.overrideQualityCounts.value = true;
            harness.Settings.rayCount.value = 2;
            harness.Settings.bounces.value = 1;
            harness.Settings.rayMaxSteps.value = steps;
            harness.SetDebugView(BasisGlobalIlluminationDebugView.Indirect);
            return harness.Converged(region);
        }

        /// <summary>
        /// The plain march given an enormous step budget: the SAME estimator, gathering the same colour off
        /// the same buffer, just no longer short of steps.
        ///
        /// The traced gather is the wrong reference for this and it took a run to see why. The two modes do
        /// not shade a hit the same way - one relights the surface from the scene's lights, the other reads
        /// the colour already drawn there - so their absolute levels are not comparable, and a test that
        /// compares them is measuring that difference rather than the march. Converging the plain march
        /// instead holds everything constant except the one thing in question.
        /// </summary>
        private Color Reference(RectInt region)
        {
            return Measure(region, false, BasisGlobalIlluminationVolume.RayStepsMax);
        }

        [Test]
        public void TheHierarchicalMarchFindsTheContactBounceThePlainMarchStridesOver()
        {
            BuildRoom();
            RectInt contact = Contact();
            RectInt far = FarField();

            const int ShippedSteps = 20;
            Color plainContact = Measure(contact, false, ShippedSteps);
            Color hierarchicalContact = Measure(contact, true, ShippedSteps);
            Color plainFar = Measure(far, false, ShippedSteps);
            Color hierarchicalFar = Measure(far, true, ShippedSteps);
            Color referenceContact = Reference(contact);
            Color referenceFar = Reference(far);

            // Red, because red is what the slat emits: measuring luma would let a change in the grey the
            // sun already puts on those boards stand in for a change in the bounce.
            float plainNear = plainContact.r, sharpNear = hierarchicalContact.r;
            float plainDistant = plainFar.r, sharpDistant = hierarchicalFar.r;

            float referenceNear = referenceContact.r, referenceDistant = referenceFar.r;

            Debug.Log($"[BasisGI] march contact bounce: plain@{ShippedSteps}={plainNear:F4} hierarchical={sharpNear:F4} " +
                      $"plain@{BasisGlobalIlluminationVolume.RayStepsMax}={referenceNear:F4}\n" +
                      $"[BasisGI] march far field:      plain@{ShippedSteps}={plainDistant:F4} hierarchical={sharpDistant:F4} " +
                      $"plain@{BasisGlobalIlluminationVolume.RayStepsMax}={referenceDistant:F4}\n" +
                      harness.Describe());

            Assert.Greater(sharpNear, 0.002f,
                "the hierarchical march gathered no red at the slat's base, so either it found nothing or the probe is off the contact");

            // The claim being tested, stated as an error against a converged run of the same estimator: the
            // hierarchical march spends its steps where a hit can actually be, so it should land nearer to
            // what the plain march reaches only with six times the budget.
            float plainError = Mathf.Abs(plainNear - referenceNear);
            float sharpError = Mathf.Abs(sharpNear - referenceNear);
            Assert.Less(sharpError, plainError,
                $"the hierarchical march is FURTHER from a converged march than the shipped one is, so its " +
                $"extra light is an artefact of how it walks rather than light the plain march was missing: " +
                $"converged {referenceNear:F4}, plain {plainNear:F4} (off by {plainError:F4}), " +
                $"hierarchical {sharpNear:F4} (off by {sharpError:F4})");

            float plainFarError = Mathf.Abs(plainDistant - referenceDistant);
            float sharpFarError = Mathf.Abs(sharpDistant - referenceDistant);
            Assert.LessOrEqual(sharpFarError, Mathf.Max(plainFarError, 0.02f),
                $"the same, in the open where the plain march is least starved: converged {referenceDistant:F4}, " +
                $"plain {plainDistant:F4} (off by {plainFarError:F4}), hierarchical {sharpDistant:F4} " +
                $"(off by {sharpFarError:F4})");
        }
    }
}
