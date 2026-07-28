using NUnit.Framework;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Regression tests for the Auto height-mode decision
    /// (<see cref="BasisCalibrationMath.AutoHeightModePicksArmSpan"/>): trust the LONGER of the
    /// player's two body measurements. Both metrics under-measure easily (calibrating while seated
    /// or slouched reads the eye height 25-35% short; bent arms read the span short) but neither
    /// can over-measure past the real body — so the larger implied body height is the trustworthy
    /// measurement. Eye height is preferred inside a tolerance band (stabler, carries the
    /// standing-eye corrections); the span wins only when the eye reading is implausibly short
    /// against the measured reach.
    ///
    /// The field report that fixed the first (avatar-ratio-based) version of this rule: calibrated
    /// sitting in a chair with arms out — eye read ~1.2 m while the span read ~1.7 m, and Auto took
    /// the broken eye height. That scenario is the headline case below.
    /// </summary>
    public class BasisAutoHeightModeTests
    {
        [Test]
        public void SeatedCalibrationWithArmsOut_PicksArmSpan()
        {
            // The field report: physically seated (standing mode), arms fully out. Eye 1.2 m implies
            // a ~1.29 m body; span 1.7 m implies a 1.7 m body — far outside the tolerance band, so
            // the eye measurement is untrustworthy and the span must win.
            Assert.That(BasisCalibrationMath.AutoHeightModePicksArmSpan(1.2f, 1.7f), Is.True,
                "a seated calibration with arms out must scale by the (correct) arm span, not the (short) eye height.");
        }

        [Test]
        public void NormalStandingCalibration_PrefersEyeHeight()
        {
            // Standing, proportional body: eye 1.65 m implies ~1.77 m; span 1.72 m implies 1.72 m.
            // Both measurements agree, so the stabler eye-height pair wins.
            Assert.That(BasisCalibrationMath.AutoHeightModePicksArmSpan(1.65f, 1.72f), Is.False);
        }

        [Test]
        public void NormalLongArmedPlayer_StaysInsideTheBand_KeepsEyeHeight()
        {
            // Anatomical variation must not flip the mode: a genuinely long-armed standing player
            // (eye 1.65 m → implied 1.77 m; span 1.85 m → implied 1.85 m, ~4.4% over) stays inside
            // the 8% preference band.
            Assert.That(BasisCalibrationMath.AutoHeightModePicksArmSpan(1.65f, 1.85f), Is.False,
                "normal long arms are not evidence the eye height is broken.");
        }

        [Test]
        public void BentArmsShortSpan_KeepsEyeHeight()
        {
            // The inverse failure: span under-measured (arms bent at calibration). The eye reading
            // implies the taller body, so it stays in charge.
            Assert.That(BasisCalibrationMath.AutoHeightModePicksArmSpan(1.65f, 1.1f), Is.False);
        }

        [Test]
        public void DegenerateInputs_DisqualifyThatMetric()
        {
            Assert.That(BasisCalibrationMath.AutoHeightModePicksArmSpan(0f, 1.7f), Is.True,
                "no eye measurement at all: the span is the only valid metric.");
            Assert.That(BasisCalibrationMath.AutoHeightModePicksArmSpan(1.65f, 0f), Is.False,
                "no span measurement: eye height is the only valid metric.");
            Assert.That(BasisCalibrationMath.AutoHeightModePicksArmSpan(0f, 0f), Is.False);
        }

        [Test]
        public void BandBoundary_IsWhereSpanImpliedExceedsEyeImpliedByTheBand()
        {
            // Pin the switch point so a band retune is a deliberate, test-visible change:
            // span flips the mode exactly when span/SpanToHeightRatio exceeds
            // (eye/EyeToHeightRatio) * AutoModeEyePreferenceBand.
            float eye = 1.5f;
            float boundarySpan = BasisCalibrationMath.ImpliedHeightFromEye(eye)
                * BasisCalibrationMath.AutoModeEyePreferenceBand
                * BasisCalibrationMath.SpanToHeightRatio;
            Assert.That(BasisCalibrationMath.AutoHeightModePicksArmSpan(eye, boundarySpan * 0.99f), Is.False);
            Assert.That(BasisCalibrationMath.AutoHeightModePicksArmSpan(eye, boundarySpan * 1.01f), Is.True);
        }
    }
}
