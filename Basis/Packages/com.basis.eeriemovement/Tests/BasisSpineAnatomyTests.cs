using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// THE RULER, NOT THE THING BEING MEASURED. These pin BasisSpineAnatomyCore's maths: the swing-twist
    /// decomposition, the elliptical cone, the saturation, the identity, the equivariance. The corpus tests
    /// next door (BasisSpineAnatomyCorpusTests) ask the other question -- whether the ENVELOPE is right --
    /// against 35,081 frames of real humans.
    ///
    /// A guard is only worth having if it CANNOT be escaped and DOES NOT fire on legal poses. Both of those
    /// are properties, not tolerances, and both are tested here by exhaustion rather than by example.
    /// </summary>
    public sealed class BasisSpineAnatomyTests
    {
        const float k_Tol = 0.05f;

        // A T-posed avatar standing upright: +X right, +Y up, +Z forward, parent identity.
        static BasisSpineRestFrame Upright() => BasisSpineAnatomy.BuildRestFrame(
            new Vector3(0f, 1.0f, 0f), new Vector3(0f, 1.2f, 0f),
            Quaternion.identity, Quaternion.identity, Vector3.right);

        static void Read(Quaternion local, in BasisSpineRestFrame f, out float flex, out float lat, out float axial)
            => BasisSpineAnatomyCore.Decompose(local * BasisSpineAnatomyCore.Conj(f.RestLocalRot), f,
                out flex, out lat, out axial);

        // ---------------------------------------------------------------------------------------------
        // THE FRAME

        [Test]
        public void TheRestFrame_IsOrthonormal_AndAgreesWithUnitysAxes()
        {
            BasisSpineRestFrame f = Upright();
            Assert.IsTrue(f.Valid);
            Assert.AreEqual(1f, f.Right.magnitude, 1e-4f, "right is not unit");
            Assert.AreEqual(1f, f.Up.magnitude, 1e-4f, "up is not unit");
            Assert.AreEqual(1f, f.Forward.magnitude, 1e-4f, "forward is not unit");
            Assert.AreEqual(0f, Vector3.Dot(f.Right, f.Up), 1e-4f, "right and up are not orthogonal");
            Assert.AreEqual(0f, Vector3.Dot(f.Up, f.Forward), 1e-4f, "up and forward are not orthogonal");
            Assert.AreEqual(0f, Vector3.Dot(f.Forward, f.Right), 1e-4f, "forward and right are not orthogonal");
        }

        [Test]
        public void PositiveFlexion_BendsTheBoneForward()
        {
            BasisSpineRestFrame f = Upright();
            Vector3 tip = BasisSpineAnatomyCore.Recompose(30f, 0f, 0f, f) * Vector3.up;
            // A sign error here is the difference between a spine that bows and a spine that arches.
            Assert.Greater(tip.z, 0.4f, $"+flexion must tip the bone FORWARD (+Z). Got {tip}");
            Assert.Greater(tip.y, 0f, "a 30 deg flexion must not invert the bone");
        }

        [Test]
        public void PureSideBend_HasNoForwardComponent()
        {
            BasisSpineRestFrame f = Upright();
            Vector3 tip = BasisSpineAnatomyCore.Recompose(0f, 30f, 0f, f) * Vector3.up;
            Assert.AreEqual(0f, tip.z, 1e-4f, $"a pure side-bend leaked into flexion: {tip}");
        }

        [Test]
        public void ADegenerateBone_YieldsAnInvalidFrame_AndTheGuardDeclines()
        {
            var zeroLen = BasisSpineAnatomy.BuildRestFrame(Vector3.zero, Vector3.zero,
                Quaternion.identity, Quaternion.identity, Vector3.right);
            Assert.IsFalse(zeroLen.Valid, "a zero-length bone must not produce a frame");

            // a bone lying along the body's right is not a spine bone: decline rather than invent an axis
            var sideways = BasisSpineAnatomy.BuildRestFrame(Vector3.zero, Vector3.right,
                Quaternion.identity, Quaternion.identity, Vector3.right);
            Assert.IsFalse(sideways.Valid, "a bone parallel to the body's right must not produce a frame");

            Quaternion wild = BasisSpineAnatomyCore.Recompose(90f, 90f, 90f, Upright());
            Assert.AreEqual(wild, BasisSpineAnatomyCore.Clamp(wild, zeroLen, BasisSpineAnatomy.Rom(BasisSpineSegment.Lumbar)),
                "an invalid frame must be a no-op, never a guess");
        }

        // ---------------------------------------------------------------------------------------------
        // THE DECOMPOSITION

        [Test]
        public void Decompose_ExactlyInverts_Recompose_OverTheWholeRange()
        {
            BasisSpineRestFrame f = Upright();
            var rng = new System.Random(20260715);
            float worst = 0f;
            string worstAt = "";
            for (int i = 0; i < 100000; i++)
            {
                float a = (float)(rng.NextDouble() * 160.0 - 80.0);
                float b = (float)(rng.NextDouble() * 160.0 - 80.0);
                float c = (float)(rng.NextDouble() * 160.0 - 80.0);
                Read(BasisSpineAnatomyCore.Recompose(a, b, c, f), f, out float a2, out float b2, out float c2);
                float e = Mathf.Max(Mathf.Abs(a - a2), Mathf.Max(Mathf.Abs(b - b2), Mathf.Abs(c - c2)));
                if (e > worst) { worst = e; worstAt = $"({a:F1},{b:F1},{c:F1}) -> ({a2:F1},{b2:F1},{c2:F1})"; }
            }
            // Everything downstream reads these three numbers. If they are not the numbers that were put in,
            // every limit in the table is being applied to something other than what it names.
            Assert.Less(worst, k_Tol, $"round-trip broke by {worst:F4} deg at {worstAt}");
        }

        // ---------------------------------------------------------------------------------------------
        // THE IDENTITY

        [TestCase(0f, 0f, 0f)]
        [TestCase(10f, 0f, 0f)]
        [TestCase(40f, 0f, 0f)]
        [TestCase(0f, 10f, 0f)]
        [TestCase(0f, 0f, 8f)]
        [TestCase(30f, 8f, 5f)]
        [TestCase(-15f, 0f, 0f)]
        [TestCase(49f, 0f, 10f)]
        public void ALegalPose_IsReturnedBitForBit(float flex, float lat, float axial)
        {
            BasisSpineRestFrame f = Upright();
            Quaternion q = BasisSpineAnatomyCore.Recompose(flex, lat, axial, f);
            Quaternion c = BasisSpineAnatomyCore.Clamp(q, f, BasisSpineAnatomy.Rom(BasisSpineSegment.Lumbar),
                out BasisSpineClampInfo info);

            Assert.IsFalse(info.Touched, "the guard fired on a pose a human makes every day");
            // BIT for bit -- not "within a tolerance". A guard that perturbs legal poses to fix illegal ones
            // is the wrong trade, and a re-normalised round-trip would drift the spine every single frame.
            Assert.IsTrue(c.x == q.x && c.y == q.y && c.z == q.z && c.w == q.w,
                $"a legal pose was perturbed by {Quaternion.Angle(q, c):F5} deg");
        }

        // ---------------------------------------------------------------------------------------------
        // THE ENVELOPE

        [TestCase(BasisSpineSegment.Lumbar)]
        [TestCase(BasisSpineSegment.LowerThoracic)]
        [TestCase(BasisSpineSegment.UpperThoracic)]
        [TestCase(BasisSpineSegment.Cervical)]
        public void NoRotationOnEarth_CanEscapeTheEnvelope(BasisSpineSegment segment)
        {
            BasisSpineRestFrame f = Upright();
            BasisSpineRom rom = BasisSpineAnatomy.Rom(segment);
            var rng = new System.Random(7 + (int)segment);
            float worstSwing = 0f, worstAxial = 0f;

            for (int i = 0; i < 120000; i++)
            {
                // uniformly random over SO(3): deliberately including poses no spine could ever reach, and
                // poses the CCD could produce if it were left to its own devices -- which, before this, it was.
                Quaternion q = new Quaternion(
                    (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
                float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (n < 1e-6f) continue;
                q = new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);

                Read(BasisSpineAnatomyCore.Clamp(q, f, rom), f, out float flex, out float lat, out float axial);

                float lim = flex >= 0f ? rom.FlexDeg : rom.ExtDeg;
                float r = Mathf.Sqrt((flex / lim) * (flex / lim) + (lat / rom.LatDeg) * (lat / rom.LatDeg));
                worstSwing = Mathf.Max(worstSwing, r);
                worstAxial = Mathf.Max(worstAxial, Mathf.Abs(axial) / rom.AxialDeg);
            }

            float bound = BasisSpineAnatomyCore.OvershootAsymptote + 0.02f;
            Assert.Less(worstSwing, bound, $"{segment}: swing escaped to {worstSwing:F3}x its limit");
            Assert.Less(worstAxial, bound, $"{segment}: axial rotation escaped to {worstAxial:F3}x its limit");
        }

        [Test]
        public void TheLumbarSpine_CannotTwistLikeAThoracicSpine()
        {
            // THE headline defect. The shipped clamp limited the spine's TWIST with the LATERAL BEND limit
            // (BasisSpineBendCore.ClampAsymmetric: `e.y = Clamp(e.y, -maxLat, maxLat)`) -- there was no axial
            // limit anywhere in the codebase to clamp it with -- and then the CCD ran afterwards with none at
            // all. Fujii's in-vivo MRI puts L1-S1 axial rotation at 7.7 deg/side.
            BasisSpineRestFrame f = Upright();
            BasisSpineRom rom = BasisSpineAnatomy.Rom(BasisSpineSegment.Lumbar);
            float ceiling = rom.AxialDeg * BasisSpineAnatomyCore.OvershootAsymptote;

            foreach (float ask in new[] { 20f, 25f, 45f, 90f, 179f })
            {
                Quaternion q = BasisSpineAnatomyCore.Recompose(0f, 0f, ask, f);
                Read(BasisSpineAnatomyCore.Clamp(q, f, rom), f, out _, out _, out float got);
                Assert.LessOrEqual(Mathf.Abs(got), ceiling + 0.01f,
                    $"asked the lumbar spine for {ask} deg of twist and it gave {got:F1} deg");
            }

            // and the thoracic spine, which really does twist, must NOT be clamped to the lumbar's budget
            BasisSpineRom thoracic = BasisSpineAnatomy.Rom(BasisSpineSegment.LowerThoracic);
            Assert.Greater(thoracic.AxialDeg, rom.AxialDeg,
                "the thoracic spine twists more than the lumbar -- that is the whole point of the table");
        }

        [Test]
        public void TheSwingEnvelopeIsAnEllipse_NotABox()
        {
            // A box clamp accepts max-flexion AND max-side-bend simultaneously. You cannot bend fully forward
            // and fully sideways at the same time; the facets run out. Clamping euler components
            // independently -- which is what shipped -- is exactly a box.
            BasisSpineRestFrame f = Upright();
            BasisSpineRom rom = BasisSpineAnatomy.Rom(BasisSpineSegment.Lumbar);

            Quaternion corner = BasisSpineAnatomyCore.Recompose(rom.FlexDeg, rom.LatDeg, 0f, f);
            Quaternion c = BasisSpineAnatomyCore.Clamp(corner, f, rom, out BasisSpineClampInfo info);
            Read(c, f, out float flex, out float lat, out _);

            Assert.IsTrue(info.SwingClamped, "the box corner sits outside the ellipse and must be rejected");
            Assert.Less(flex, rom.FlexDeg - 1f, "flexion was not pulled back");
            Assert.Less(lat, rom.LatDeg - 0.3f, "side-bend was not pulled back");

            // and it pulls STRAIGHT BACK along the bend, never sideways: the swing's direction is preserved
            // exactly, so the guard cannot introduce a bend the user never asked for.
            float asked = Mathf.Atan2(rom.LatDeg, rom.FlexDeg);
            float got = Mathf.Atan2(lat, flex);
            Assert.AreEqual(asked, got, 0.01f, "the guard changed the DIRECTION of the bend, not just its size");
        }

        // ---------------------------------------------------------------------------------------------
        // NO POPS

        [Test]
        public void TheGuardCanOnlySlowTheBone_NeverOvershootIt()
        {
            // The pop test. A HARD clamp steps its derivative from 1 to 0 at the limit; that step is the pop.
            // This guard's slope is 1 at the handover and eases from there, so the bone arrives at its limit
            // instead of striking it.
            //
            // Measured in ANATOMICAL ANGLES on purpose. Quaternion.Angle is acos-based and catastrophically
            // ill-conditioned near identity -- it reports ~0.01 deg of noise on a 0.1 deg step, which is bigger
            // than the effect. It flagged a "pop" at 5.9 deg of flexion, where the guard is a bit-exact
            // identity and CANNOT have popped. A metric that lies is worse than no metric.
            BasisSpineRestFrame f = Upright();
            BasisSpineRom rom = BasisSpineAnatomy.Rom(BasisSpineSegment.Lumbar);

            const float step = 0.05f;
            float prev = 0f;
            bool first = true;
            for (int k = 0; k * step <= 130f; k++)
            {
                float ask = k * step;
                Read(BasisSpineAnatomyCore.Clamp(BasisSpineAnatomyCore.Recompose(ask, 0f, 0f, f), f, rom), f,
                    out float got, out _, out _);
                if (!first)
                {
                    float slope = (got - prev) / step;
                    Assert.LessOrEqual(slope, 1.001f,
                        $"the guard AMPLIFIED motion at {ask:F1} deg (slope {slope:F3}) -- it can only ever slow it");
                    Assert.GreaterOrEqual(slope, -1e-3f,
                        $"guarded flexion went BACKWARDS at {ask:F1} deg (slope {slope:F3})");
                }
                prev = got;
                first = false;
            }
        }

        [Test]
        public void TheHandoverIsSmooth_SlopeOneInside_NoWallOutside()
        {
            BasisSpineRestFrame f = Upright();
            BasisSpineRom rom = BasisSpineAnatomy.Rom(BasisSpineSegment.Lumbar);

            float Guarded(float ask)
            {
                Read(BasisSpineAnatomyCore.Clamp(BasisSpineAnatomyCore.Recompose(ask, 0f, 0f, f), f, rom), f,
                    out float got, out _, out _);
                return got;
            }
            float Slope(float at) => (Guarded(at + 0.01f) - Guarded(at - 0.01f)) / 0.02f;

            float inside = Slope(rom.FlexDeg - 1f);
            float outside = Slope(rom.FlexDeg + 1f);
            Assert.Greater(inside, 0.98f, $"slope just inside the limit is {inside:F3}; it must be 1 (the identity)");
            Assert.Greater(outside, 0.5f, $"slope just outside the limit is {outside:F3}; a hard clamp would give 0");
        }

        // ---------------------------------------------------------------------------------------------
        // SPACE CONFORMANCE

        [Test]
        public void TheGuardIsEquivariant_RotatingTheWholeBodyChangesNothing()
        {
            // A joint's LOCAL rotation does not care which way the avatar is facing, so neither may its
            // guard. If any world axis has leaked in, this fails -- and this project has shipped exactly that
            // bug before (a spine clamp that enforced a body-frame length along GRAVITY, which buried the
            // hips 55 cm when the user lay down).
            BasisSpineRestFrame flat = Upright();
            BasisSpineRom rom = BasisSpineAnatomy.Rom(BasisSpineSegment.Lumbar);
            var rng = new System.Random(99);

            for (int i = 0; i < 3000; i++)
            {
                Quaternion Q = Quaternion.Euler(
                    (float)(rng.NextDouble() * 360), (float)(rng.NextDouble() * 360), (float)(rng.NextDouble() * 360));
                BasisSpineRestFrame rotated = BasisSpineAnatomy.BuildRestFrame(
                    Q * new Vector3(0f, 1.0f, 0f), Q * new Vector3(0f, 1.2f, 0f), Q, Q, Q * Vector3.right);

                float fl = (float)(rng.NextDouble() * 200 - 100);
                float la = (float)(rng.NextDouble() * 200 - 100);
                float ax = (float)(rng.NextDouble() * 200 - 100);

                Read(BasisSpineAnatomyCore.Clamp(BasisSpineAnatomyCore.Recompose(fl, la, ax, flat), flat, rom),
                    flat, out float fA, out float lA, out float aA);
                Read(BasisSpineAnatomyCore.Clamp(BasisSpineAnatomyCore.Recompose(fl, la, ax, rotated), rotated, rom),
                    rotated, out float fB, out float lB, out float aB);

                Assert.AreEqual(fA, fB, k_Tol, "flexion changed when the avatar turned around");
                Assert.AreEqual(lA, lB, k_Tol, "side-bend changed when the avatar turned around");
                Assert.AreEqual(aA, aB, k_Tol, "axial rotation changed when the avatar turned around");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // SAFETY

        [Test]
        public void ANaNPose_IsPassedThroughAndNeverManufacturedInto_ARealPose()
        {
            // A NaN transform PERSISTS in Unity: write one into a bone and the spine never recovers, even
            // once good data returns. `!(x > eps)` catches NaN where `x < eps` waves it straight through.
            BasisSpineRestFrame f = Upright();
            Quaternion nan = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);
            Quaternion c = BasisSpineAnatomyCore.Clamp(nan, f, BasisSpineAnatomy.Rom(BasisSpineSegment.Lumbar));
            Assert.IsTrue(float.IsNaN(c.x), "a NaN was laundered into a finite pose -- the guard must decline, not invent");
        }

        [Test]
        public void Saturate_IsTheIdentityBelowSoft_AndAsymptoticAbove()
        {
            Assert.AreEqual(5f, BasisSpineAnatomyCore.Saturate(5f, 10f, 12f), 0f, "below soft must be EXACT");
            Assert.AreEqual(10f, BasisSpineAnatomyCore.Saturate(10f, 10f, 12f), 1e-5f);
            for (float x = 10f; x < 1000f; x += 7f)
            {
                float y = BasisSpineAnatomyCore.Saturate(x, 10f, 12f);
                Assert.Less(y, 12f, $"Saturate({x}) = {y} reached its asymptote");
                Assert.GreaterOrEqual(y, 10f, $"Saturate({x}) = {y} fell below the soft threshold");
            }
        }

        [Test]
        public void TheRomTable_IsOrderedTheWayARealSpineIs()
        {
            BasisSpineRom lumbar = BasisSpineAnatomy.Rom(BasisSpineSegment.Lumbar);
            BasisSpineRom lowThor = BasisSpineAnatomy.Rom(BasisSpineSegment.LowerThoracic);
            BasisSpineRom upThor = BasisSpineAnatomy.Rom(BasisSpineSegment.UpperThoracic);
            BasisSpineRom cerv = BasisSpineAnatomy.Rom(BasisSpineSegment.Cervical);

            // These orderings are anatomy, not tuning. If a future edit inverts one, it is a bug, and the
            // number that shipped for years -- one ROM shared by every segment -- fails every line of this.
            Assert.Greater(lumbar.FlexDeg, lowThor.FlexDeg, "the lumbar spine is the trunk's flexion hinge");
            Assert.Greater(lowThor.FlexDeg, upThor.FlexDeg, "the ribs stiffen the upper thorax against flexion");
            Assert.Less(lumbar.AxialDeg, lowThor.AxialDeg, "the lumbar facets are near-sagittal: they will not twist");
            Assert.Less(lumbar.AxialDeg, upThor.AxialDeg, "the lumbar spine twists less than the thorax, not more");
            Assert.Greater(cerv.AxialDeg, lowThor.AxialDeg * 2f, "the neck is by far the freest segment in twist");

            foreach (BasisSpineSegment s in new[] { BasisSpineSegment.Lumbar, BasisSpineSegment.LowerThoracic,
                                                    BasisSpineSegment.UpperThoracic, BasisSpineSegment.Cervical })
            {
                BasisSpineRom r = BasisSpineAnatomy.Rom(s);
                Assert.Greater(r.FlexDeg, 0f, $"{s} flexion");
                Assert.Greater(r.ExtDeg, 0f, $"{s} extension");
                Assert.Greater(r.LatDeg, 0f, $"{s} lateral");
                Assert.Greater(r.AxialDeg, 0f, $"{s} axial");
            }
        }
    }
}
