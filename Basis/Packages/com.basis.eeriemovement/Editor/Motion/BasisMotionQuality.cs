using System;
using UnityEngine;

namespace Basis.IK.Motion
{
    /// <summary>Per-joint motion-quality metrics. Every field is either dimensionless or normalised by
    /// limb length, so the same numbers apply to a 0.6 m chibi and a 2.4 m giant -- the same doctrine
    /// as the Froude gait law. A metric in raw metres would gate one avatar size and lie about the rest.</summary>
    public struct BasisMotionQualitySummary
    {
        public bool Ok;
        public string Error;
        public string Label;
        public int Frames;
        public float Dt;

        // --- naturalness. TWO-SIDED: too little is as wrong as too much (see BasisMotionQuality docs).
        public float JerkPerLimb;      // RMS jerk of the INTENDED (6 Hz) motion, limb-lengths / s^3
        public float Sparc;            // spectral arc length of the speed profile; min-jerk = -1.40
        public float SpeedP95PerLimb;  // limb-lengths / s
        public float SpeedMaxPerLimb;

        // --- jitter. One-sided ceiling: a human simply does not buzz.
        public float JitterFracLimb;   // RMS of everything above 8 Hz, as a fraction of limb length
        public float JitterHz;         // where that energy sits. Diagnostic: Nyquist = feedback loop.

        // --- discontinuity. One-sided ceiling.
        public int Pops;               // single-frame jumps far beyond the motion's own scale AND big enough to see
        public float WorstPopRatio;    // worst frame-step / median frame-step
        public int WorstPopFrame;

        /// <summary>Which frames popped. Needed because comparing pop COUNTS against a reference does not
        /// answer the question anyone is actually asking: a solver that pops on exactly the frames the HUMAN
        /// popped on has invented nothing, and one that pops the same NUMBER of times somewhere else has
        /// invented all of them. Only a frame-by-frame comparison can tell those apart. Index i = the step
        /// from frame i to i+1.</summary>
        public bool[] PopFrames;

        // --- fidelity against a reference track (the human, or a golden run). NaN if none given.
        public float ShapeDistance;    // spectral SHAPE mismatch -- catches what smoothness rewards
        public float MeanErrFracLimb;

        public override string ToString() =>
            $"{Label}: jerk={JerkPerLimb:F0}/L/s3 sparc={Sparc:F2} jitter={JitterFracLimb * 100f:F3}%L@{JitterHz:F0}Hz " +
            $"pops={Pops} v95={SpeedP95PerLimb:F2}/L/s" +
            (float.IsNaN(ShapeDistance) ? "" : $" shape={ShapeDistance:F3} err={MeanErrFracLimb * 100f:F2}%L");
    }

    /// <summary>
    /// Scores how a joint MOVED, as opposed to where it ended up.
    ///
    /// Every other IK gate in this project asks "is this pose right". None of them ask "is this motion
    /// right", and the two are genuinely independent: a solved elbow can be 2 cm accurate on every
    /// single frame and still buzz at 30 Hz, sit on a plateau, or arrive 150 ms late. Position accuracy
    /// cannot see any of that, because each frame, taken alone, looks fine. The badness only exists
    /// BETWEEN frames.
    ///
    /// ============================================================================================
    /// THE ONE THING TO UNDERSTAND BEFORE ADDING A METRIC HERE:
    ///
    ///     SMOOTHNESS METRICS ARE ONE-SIDED. THEY PUNISH ROUGH AND *REWARD* MUSH.
    ///
    /// Measured against the CMU corpus, degrading a real human elbow by over-smoothing it with a
    /// 150 ms filter makes it score BETTER than the human on every smoothness metric there is:
    ///
    ///         metric        real human      +150ms over-smooth     +plateau     +linearised
    ///         jerk/L          1171                 95                 244            237
    ///         SPARC          -3.41               -2.62              -2.34          -2.89
    ///
    /// A gate of the form `smoothness <= threshold` therefore hands a passing grade to exactly the
    /// laggy, robotic, dead-feeling motion we are trying to eliminate, and FAILS the real human. It is
    /// worse than no gate, because it actively pushes the solver the wrong way.
    ///
    /// So: JerkPerLimb is gated as a FLOOR as well as a ceiling. A living arm has a characteristic
    /// amount of jerk in it. Too little means something upstream ate the motion.
    ///
    /// And smoothness alone is never enough -- it must be paired with ShapeDistance (which catches
    /// energy in the wrong bands) and with a lag measurement from BasisMotionResponse, or the cheapest
    /// way to pass every gate is to smooth harder, forever.
    /// ============================================================================================
    /// </summary>
    public static class BasisMotionQuality
    {
        /// <summary>A "pop" is a single-frame step this many times the motion's own MEDIAN step. Median,
        /// not mean -- with a mean, one pop inflates the very baseline it is being measured against, and
        /// a big enough pop hides itself.</summary>
        public const float PopRatio = 8f;

        /// <summary>
        /// ...AND it must actually be big enough to SEE. A pop must clear this fraction of the limb length in
        /// a single frame, as well as clearing PopRatio.
        ///
        /// ⚠ THE RATIO ALONE IS A LIAR, AND IT WAS LYING. It is scale-RELATIVE, so a joint that barely moves has
        /// a near-zero median and ordinary motion sails past 8x. Measured on the corpus (BasisKneePopLocaliser):
        /// of the knee's 170 flagged frames, 27 sat on tracks whose median step was under a thousandth of a leg,
        /// where the denominator has collapsed and the metric is measuring nothing but its own noise floor. That
        /// is how the knee scored an identical "36 excess pops" whether it was handed no hint at all, the old
        /// lookup, or the fitted model -- a number that does not move when its supposed cause moves was never
        /// measuring that cause.
        ///
        /// 1% of a limb in ONE frame is ~1.2 limb-lengths/second of instantaneous speed at 120 Hz: that is a
        /// discontinuity. Below it there is nothing a user could see, so there is nothing to count. The teleport
        /// BasisMotionAnalyzerTests plants is 13% of its limb and still trips this comfortably.
        /// </summary>
        public const float PopMinFracLimb = 0.01f;

        public static BasisMotionQualitySummary Analyze(
            Vector3[] joint, float limbLength, float dt, string label, Vector3[] reference = null)
        {
            var s = new BasisMotionQualitySummary
            {
                Label = label,
                Dt = dt,
                Frames = joint?.Length ?? 0,
                ShapeDistance = float.NaN,
                MeanErrFracLimb = float.NaN,
            };

            if (joint == null || joint.Length < 32)
            {
                s.Error = $"need >= 32 frames to measure motion, got {joint?.Length ?? 0}";
                return s;
            }
            if (!(limbLength > 1e-6f))
            {
                s.Error = $"limb length must be positive, got {limbLength}";
                return s;
            }
            if (!(dt > 1e-6f))
            {
                s.Error = $"dt must be positive, got {dt}";
                return s;
            }

            // Intended motion vs the buzz on top of it. Everything below depends on this split: run a
            // derivative on the raw path and you measure the instrument, not the arm.
            BasisMotionSignal.Split(joint, dt, out Vector3[] intended, out Vector3[] residual);

            Vector3[] vel = BasisMotionSignal.Derivative(intended, dt);
            Vector3[] acc = BasisMotionSignal.Derivative(vel, dt);
            Vector3[] jerk = BasisMotionSignal.Derivative(acc, dt);
            float[] speed = BasisMotionSignal.Magnitude(vel);

            s.JerkPerLimb = BasisMotionSignal.Rms(jerk) / limbLength;
            s.Sparc = BasisMotionSpectrum.Sparc(speed, dt);
            s.SpeedP95PerLimb = BasisMotionSignal.Quantile(speed, 0.95f) / limbLength;
            s.SpeedMaxPerLimb = BasisMotionSignal.Quantile(speed, 1f) / limbLength;

            s.JitterFracLimb = BasisMotionSignal.Rms(residual) / limbLength;
            s.JitterHz = BasisMotionSpectrum.DominantAbove(
                BasisMotionSignal.Magnitude(BasisMotionSignal.Derivative(joint, dt)),
                dt, BasisMotionSignal.JitterBandHz);

            PopStats(joint, limbLength, out s.Pops, out s.WorstPopRatio, out s.WorstPopFrame, out s.PopFrames);

            if (reference != null && reference.Length == joint.Length)
            {
                Vector3[] refIntended = BasisMotionSignal.LowPass(reference, dt, BasisMotionSignal.MotionBandHz);
                float[] refSpeed = BasisMotionSignal.Magnitude(BasisMotionSignal.Derivative(refIntended, dt));
                s.ShapeDistance = BasisMotionSpectrum.ShapeDistance(refSpeed, speed, dt);

                double e = 0;
                for (int i = 0; i < joint.Length; i++) e += Vector3.Distance(joint[i], reference[i]);
                s.MeanErrFracLimb = (float)(e / joint.Length) / limbLength;
            }

            s.Ok = true;
            return s;
        }

        static void PopStats(Vector3[] p, float limbLength, out int pops, out float worst, out int worstFrame,
                             out bool[] popFrames)
        {
            pops = 0; worst = 0f; worstFrame = -1;
            int n = p.Length - 1;
            popFrames = new bool[Mathf.Max(n, 0)];
            if (n < 8) return;

            var d = new float[n];
            for (int i = 0; i < n; i++) d[i] = Vector3.Distance(p[i + 1], p[i]);

            float med = BasisMotionSignal.Quantile(d, 0.5f);
            if (med <= 1e-9f) med = 1e-9f;

            // Both conditions, not either: far beyond the motion's own scale AND big enough to see. See
            // PopMinFracLimb -- the ratio on its own was counting the noise floor of a stationary joint.
            float floor = PopMinFracLimb * Mathf.Max(limbLength, 1e-6f);

            for (int i = 0; i < n; i++)
            {
                float r = d[i] / med;
                if (r > PopRatio && d[i] > floor)
                {
                    pops++;
                    popFrames[i] = true;
                }
                if (r > worst) { worst = r; worstFrame = i; }
            }
        }
    }
}
