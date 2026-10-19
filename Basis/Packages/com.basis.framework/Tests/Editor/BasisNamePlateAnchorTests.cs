using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.UI.NamePlate;
using NUnit.Framework;

namespace Basis.Tests.NamePlate
{
    /// <summary>
    /// Pins where a remote player's nameplate sits vertically.
    ///
    /// The placement used to be <c>hips.y + 1.2 * (liveScale / modelScale)</c>: a fixed 1.2 metres
    /// above the HIPS, with the only per-avatar term being a runtime-resize ratio that is 1.0 for
    /// every avatar nobody rescaled. No part of it read the avatar's head, so the height the plate
    /// actually cleared the head by was 1.2 minus that avatar's hips→crown distance — passable on
    /// the ~1.75 m human the constant was tuned on, half a metre of empty air over a chibi, and
    /// NEGATIVE on anything tall, which renders the plate inside the avatar's chest. That is the
    /// "some avatars are higher or lower" report: the error is exactly each avatar's deviation from
    /// one particular body plan, so it reads as random per avatar rather than uniformly wrong.
    ///
    /// The replacement measures each avatar's own crown at registration
    /// (<see cref="BasisNamePlateAnchorMath"/>) and adds a proportional gap. These tests are written
    /// against a corpus of body plans rather than a single rig, because a single rig is precisely
    /// what let the old constant look correct for years.
    /// </summary>
    [NoAutoStaticsCleanup]
    public sealed class BasisNamePlateAnchorTests
    {
        /// <summary>Plate world scale for a default viewer (0.02 × size 1 × viewer scale 1), and the
        /// panel half-height that follows from it — 9 cm, the distance the plate hangs below the
        /// point it is anchored at.</summary>
        private const float DefaultPlateScale = 0.02f;
        private const float DefaultPanelHalfHeight = BasisNamePlateAnchorMath.PanelHalfHeightUnits * DefaultPlateScale;

        /// <summary>A body plan in root-relative rendered metres (heights above the feet).</summary>
        private readonly struct Body
        {
            public readonly string Name;
            public readonly float Hips;
            public readonly float Head;      // head bone: top of the neck, not the top of the skull
            public readonly float Eye;       // BasisAvatar.AvatarEyePosition.x; 0 = not authored
            public readonly float Bounds;    // highest renderer bounds top; NaN = nothing measurable
            public readonly float VisualTop; // what a viewer reads as the top of this avatar's head

            public Body(string name, float hips, float head, float eye, float bounds, float visualTop)
            {
                Name = name;
                Hips = hips;
                Head = head;
                Eye = eye;
                Bounds = bounds;
                VisualTop = visualTop;
            }

            public bool HasBounds => !float.IsNaN(Bounds);
        }

        private static readonly Body[] Corpus =
        {
            new Body("human 1.75m",          0.95f, 1.52f, 1.63f, 1.77f, 1.77f),
            new Body("human 1.50m short",    0.81f, 1.30f, 1.40f, 1.52f, 1.52f),
            new Body("human 2.00m tall",     1.09f, 1.74f, 1.86f, 2.02f, 2.02f),
            new Body("anime big head",       0.82f, 1.26f, 1.40f, 1.60f, 1.60f),
            new Body("anime + tall hair",    0.82f, 1.26f, 1.40f, 1.74f, 1.74f),
            new Body("chibi 1.20m",          0.50f, 0.72f, 0.92f, 1.22f, 1.22f),
            new Body("chibi + twin tails",   0.50f, 0.72f, 0.92f, 1.34f, 1.34f),
            new Body("super-deformed 0.90m", 0.36f, 0.50f, 0.70f, 0.92f, 0.92f),
            new Body("tiny 0.35m",           0.15f, 0.21f, 0.27f, 0.36f, 0.36f),
            new Body("dragon 3.50m",         1.90f, 3.05f, 3.25f, 3.52f, 3.52f),
            new Body("dragon + horns",       1.90f, 3.05f, 3.25f, 3.88f, 3.88f),
            new Body("giant 5.00m",          2.72f, 4.35f, 4.66f, 5.05f, 5.05f),
            new Body("human + top hat",      0.95f, 1.52f, 1.63f, 2.02f, 2.02f),
            new Body("no authored eye",      0.95f, 1.52f, 1.63f, 1.77f, 1.77f),
            new Body("no eye, no bounds",    0.95f, 1.52f, 0f,    float.NaN, 1.77f),
            new Body("chibi, no bounds",     0.50f, 0.72f, 0.92f, float.NaN, 1.22f),
        };

        /// <summary>Height of the plate's BOTTOM EDGE above the avatar's crown — the clear air a
        /// viewer actually sees — for an avatar registered and rendered at scale 1.</summary>
        private static float GapAboveCrown(in Body b, float panelHalfHeight = DefaultPanelHalfHeight)
        {
            float model = BasisNamePlateAnchorMath.MeasureHeightAboveHipsModelUnits(
                b.Hips, b.Head, b.Eye, b.Bounds, b.HasBounds, rootScaleY: 1f);
            float anchor = BasisNamePlateAnchorMath.AnchorWorldY(b.Hips, model, panelHalfHeight);
            return (anchor - panelHalfHeight) - b.VisualTop;
        }

        /// <summary>The pre-fix placement, kept so the regression witnesses below measure the real
        /// thing rather than a description of it.</summary>
        private static float LegacyGapAboveCrown(in Body b)
        {
            float anchor = b.Hips + BasisNamePlateAnchorMath.LegacyHeightAboveHips;
            return (anchor - DefaultPanelHalfHeight) - b.VisualTop;
        }

        [Test]
        public void PlateClearsTheCrownOnEveryBodyPlan()
        {
            foreach (Body b in Corpus)
            {
                float gap = GapAboveCrown(b);
                Assert.That(gap, Is.GreaterThan(0f),
                    $"{b.Name}: the plate's bottom edge is {-gap * 100f:0.#} cm INSIDE the avatar's head");
            }
        }

        [Test]
        public void PlateStaysCloseEnoughToReadAsTheirs()
        {
            // The other half of the contract: a plate that clears the head by a third of the
            // avatar's own height reads as floating, and on a crowded instance it drifts into the
            // row of plates behind it.
            foreach (Body b in Corpus)
            {
                float gap = GapAboveCrown(b);
                float fraction = gap / b.VisualTop;
                Assert.That(fraction, Is.LessThan(0.15f),
                    $"{b.Name}: the plate sits {gap * 100f:0.#} cm ({fraction * 100f:0.#}% of its height) above the head");
            }
        }

        [Test]
        public void TheFixedHeightItReplacedSankIntoTallAvatarsAndFloatedOffShortOnes()
        {
            // Regression witness. Documents what the 1.2 m constant actually did, so the fix cannot
            // be quietly reverted and the size of the error is on record.
            Body dragon = Find("dragon 3.50m");
            Assert.That(LegacyGapAboveCrown(dragon), Is.LessThan(-0.4f),
                "the old constant should bury a 3.5 m avatar's plate ~half a metre inside it");
            Assert.That(GapAboveCrown(dragon), Is.GreaterThan(0f), "the fix should lift it clear");

            Body chibi = Find("chibi 1.20m");
            Assert.That(LegacyGapAboveCrown(chibi) / chibi.VisualTop, Is.GreaterThan(0.3f),
                "the old constant should strand a chibi's plate a third of its own height overhead");
            Assert.That(GapAboveCrown(chibi) / chibi.VisualTop, Is.LessThan(0.1f), "the fix should bring it down");

            // And the body plan it WAS tuned on stays acceptable — a fix that only repairs the
            // extremes by wrecking the common case is not a fix.
            Body human = Find("human 1.75m");
            Assert.That(LegacyGapAboveCrown(human), Is.GreaterThan(0f));
            Assert.That(GapAboveCrown(human), Is.GreaterThan(0f));
        }

        [Test]
        public void CrownTracksHeadSizeRatherThanStature()
        {
            // Two avatars of the SAME height whose heads are wildly different fractions of it. Any
            // rule written against stature or against the torso gets one of them wrong; measuring
            // the head against the eye is what separates them.
            var human = new Body("human", 0.68f, 1.09f, 1.17f, float.NaN, 1.25f);
            var bigHead = new Body("big head", 0.52f, 0.75f, 0.95f, float.NaN, 1.25f);

            float humanCrown = BasisNamePlateAnchorMath.EstimateCrownAboveRoot(human.Hips, human.Head, human.Eye, 0f, false);
            float bigHeadCrown = BasisNamePlateAnchorMath.EstimateCrownAboveRoot(bigHead.Hips, bigHead.Head, bigHead.Eye, 0f, false);

            Assert.That(bigHeadCrown - bigHead.Head, Is.GreaterThan((humanCrown - human.Head) * 2f),
                "the big-headed rig's crown should sit far further above its head bone");
            // Both land near the same real crown despite hips and head bones 16 cm apart.
            Assert.That(humanCrown, Is.EqualTo(1.25f).Within(0.1f));
            Assert.That(bigHeadCrown, Is.EqualTo(1.25f).Within(0.15f));
        }

        [Test]
        public void RendererBoundsRaiseTheCrownForHeadgearButAreCappedForNonHeadGeometry()
        {
            const float hips = 0.95f, head = 1.52f, eye = 1.63f;
            float bare = BasisNamePlateAnchorMath.EstimateCrownAboveRoot(hips, head, eye, 0f, false);

            // A top hat is real head geometry and has to lift the plate.
            float hatted = BasisNamePlateAnchorMath.EstimateCrownAboveRoot(hips, head, eye, 2.02f, true);
            Assert.That(hatted, Is.GreaterThan(bare + 0.15f), "a 25 cm hat should raise the crown");

            // Wings spread in T-pose reach above the head too, and must not follow all the way.
            float winged = BasisNamePlateAnchorMath.EstimateCrownAboveRoot(hips, head, eye, 2.60f, true);
            Assert.That(winged, Is.LessThan(2.05f), "non-head geometry a metre overhead should be capped out");

            // Bounds that come in LOW (a hidden or mis-authored head mesh) must never pull the plate
            // down into the face.
            float shrunk = BasisNamePlateAnchorMath.EstimateCrownAboveRoot(hips, head, eye, 1.55f, true);
            Assert.That(shrunk, Is.EqualTo(bare).Within(1e-4f), "small bounds should not lower the crown");
        }

        [Test]
        public void MissingEyeFallsBackToBoundsAndThenToProportions()
        {
            const float hips = 0.50f, head = 0.72f; // chibi: bounds are the only real information
            float fromBounds = BasisNamePlateAnchorMath.EstimateCrownAboveRoot(hips, head, 0f, 1.22f, true);
            Assert.That(fromBounds, Is.EqualTo(1.22f).Within(0.05f),
                "with no eye the bounds should be trusted for the head's size");

            float fromProportions = BasisNamePlateAnchorMath.EstimateCrownAboveRoot(hips, head, 0f, 0f, false);
            Assert.That(fromProportions, Is.GreaterThan(head), "the last-resort estimate must still clear the head bone");

            // An eye at or below the head bone is not eye data, it is a default-valued field.
            float degenerate = BasisNamePlateAnchorMath.EstimateCrownAboveRoot(hips, head, head - 0.1f, 0f, false);
            Assert.That(degenerate, Is.EqualTo(fromProportions).Within(1e-4f));
        }

        [Test]
        public void HeightIsStoredInModelUnitsSoAResizeIsOneMultiply()
        {
            // The measurement is taken in rendered metres and stored pre-scale, exactly as the
            // eye/mouth anchors are (BasisRemoteBoneMath.HeadAnchorOffset), so that the per-frame
            // multiply by the live root scale does not apply the avatar's own scale twice.
            //
            // The inputs do not change with the model scale — TposeWorld and AvatarEyePosition are
            // both RENDERED metres, so a model authored at 1/100 and blown up by its root reads the
            // same heights as one authored at 1:1. The root scale is the only thing that differs, so
            // the stored value has to differ by exactly that factor.
            Body b = Find("human 1.75m");
            const float modelScale = 2.35f;

            float atOne = BasisNamePlateAnchorMath.MeasureHeightAboveHipsModelUnits(
                b.Hips, b.Head, b.Eye, b.Bounds, true, rootScaleY: 1f);
            float atScale = BasisNamePlateAnchorMath.MeasureHeightAboveHipsModelUnits(
                b.Hips, b.Head, b.Eye, b.Bounds, true, rootScaleY: modelScale);

            Assert.That(atScale * modelScale, Is.EqualTo(atOne).Within(1e-3f),
                "the stored height should be model units, so liveScale × stored is the rendered height");
        }

        [Test]
        public void ARuntimeResizeKeepsThePlateTheSameDistanceUpTheAvatar()
        {
            // What the job does with the stored value: NamePlateHeightAboveHips = stored × live root
            // scale. A player who scales themselves up should carry their plate with them, still
            // clearing their (now larger) head by the same proportion.
            Body b = Find("human 1.75m");
            float stored = BasisNamePlateAnchorMath.MeasureHeightAboveHipsModelUnits(
                b.Hips, b.Head, b.Eye, b.Bounds, true, rootScaleY: 1f);

            float reference = float.NaN;
            foreach (float liveScale in new[] { 0.5f, 1f, 1.5f, 3f })
            {
                float hipsWorld = b.Hips * liveScale;
                float crownWorld = b.VisualTop * liveScale;
                float bottom = BasisNamePlateAnchorMath.AnchorWorldY(hipsWorld, stored * liveScale, DefaultPanelHalfHeight)
                               - DefaultPanelHalfHeight;
                float fraction = (bottom - crownWorld) / crownWorld;

                Assert.That(bottom, Is.GreaterThan(crownWorld), $"resized ×{liveScale}: the plate sank into the avatar");
                if (float.IsNaN(reference))
                {
                    reference = fraction;
                }
                Assert.That(fraction, Is.EqualTo(reference).Within(1e-3f),
                    $"resized ×{liveScale}: the plate moved to a different fraction of the avatar's height");
            }
        }

        [Test]
        public void GapGrowsWithTheAvatarButIsClampedAtBothEnds()
        {
            Assert.That(BasisNamePlateAnchorMath.ClearanceGap(1.75f),
                Is.GreaterThan(BasisNamePlateAnchorMath.ClearanceGap(0.9f)),
                "a taller avatar should get a proportionally larger gap");

            Assert.That(BasisNamePlateAnchorMath.ClearanceGap(0.1f), Is.EqualTo(BasisNamePlateAnchorMath.MinGap).Within(1e-5f),
                "a doll-sized avatar must not get a gap thinner than the clamp");
            Assert.That(BasisNamePlateAnchorMath.ClearanceGap(40f), Is.EqualTo(BasisNamePlateAnchorMath.MaxGap).Within(1e-5f),
                "a building-sized avatar must not get a gap that runs away with its height");
        }

        [Test]
        public void AnchorLeavesThePlatesBottomEdgeWhereTheMeasurementPutIt()
        {
            // The plate's origin is the centre of its panel and its size follows the VIEWER's scale,
            // so the measured clearance is only real if the half-height is added back at placement.
            // Same avatar, three viewer plate scales: the bottom edge must not move.
            Body b = Find("human 1.75m");
            float model = BasisNamePlateAnchorMath.MeasureHeightAboveHipsModelUnits(
                b.Hips, b.Head, b.Eye, b.Bounds, true, rootScaleY: 1f);

            float reference = float.NaN;
            foreach (float plateScale in new[] { 0.01f, 0.02f, 0.05f })
            {
                float halfHeight = BasisNamePlateAnchorMath.PanelHalfHeightUnits * plateScale;
                float bottom = BasisNamePlateAnchorMath.AnchorWorldY(b.Hips, model, halfHeight) - halfHeight;
                if (float.IsNaN(reference))
                {
                    reference = bottom;
                }
                Assert.That(bottom, Is.EqualTo(reference).Within(1e-4f),
                    $"the plate's bottom edge moved when the viewer's plate scale changed to {plateScale}");
            }

            // And without that term the plate would hang its whole lower half into the head: at the
            // default scale that is 9 cm, most of the measured gap.
            Assert.That(DefaultPanelHalfHeight, Is.GreaterThan(BasisNamePlateAnchorMath.MinGap));
        }

        [Test]
        public void DegenerateRigsFallBackInsteadOfProducingNonsense()
        {
            foreach (float bad in new[] { float.NaN, float.PositiveInfinity })
            {
                float height = BasisNamePlateAnchorMath.MeasureHeightAboveHipsModelUnits(
                    hipsAboveRoot: 0.95f, headAboveRoot: 1.52f, eyeAboveRoot: bad,
                    boundsTopAboveRoot: bad, hasBounds: true, rootScaleY: 1f);
                Assert.That(height, Is.GreaterThan(0f), "a poisoned input must not produce a NaN plate height");
                Assert.That(float.IsNaN(height) || float.IsInfinity(height), Is.False);
            }

            // A rig whose head bone sits at or below its hips (broken mapping) still has to place
            // the plate somewhere sane rather than below the avatar.
            float collapsed = BasisNamePlateAnchorMath.MeasureHeightAboveHipsModelUnits(
                hipsAboveRoot: 1f, headAboveRoot: 0.9f, eyeAboveRoot: 0f, boundsTopAboveRoot: 0f,
                hasBounds: false, rootScaleY: 1f);
            Assert.That(collapsed, Is.GreaterThan(0f));

            // A zero / negative root scale is the other way registration can arrive broken.
            float zeroScale = BasisNamePlateAnchorMath.MeasureHeightAboveHipsModelUnits(
                0.95f, 1.52f, 1.63f, 1.77f, true, rootScaleY: 0f);
            Assert.That(zeroScale, Is.GreaterThan(0f));
        }

        private static Body Find(string name)
        {
            foreach (Body b in Corpus)
            {
                if (b.Name == name)
                {
                    return b;
                }
            }
            Assert.Fail($"corpus is missing '{name}'");
            return default;
        }
    }
}
