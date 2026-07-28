using Basis.IK.Mocap;
using UnityEngine;

namespace Basis.IK.Motion
{
    // There are TWO BasisMotionClip types in this project: the mocap one, and a ScriptableObject in the SDK
    // sitting in the GLOBAL namespace. `Basis Framework` references BasisSDK, so both are visible here -- and
    // C# resolves a global-namespace MEMBER ahead of a using-imported type, so an unqualified BasisMotionClip
    // would silently bind to the SDK's ScriptableObject. Alias it and the question never arises.
    // (BasisMocapAccuracy.cs is immune only because it is declared INSIDE Basis.IK.Mocap.)
    //
    // KEEP THIS ALIAS INSIDE THE NAMESPACE. At file scope it lives in the global namespace -- which is exactly
    // where the SDK's BasisMotionClip is -- so it collides with the very type it exists to disambiguate
    // (CS0576: "namespace <global> contains a definition conflicting with alias"). Hoisting it up to the other
    // usings looks tidier and does not compile.
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;

    public struct BasisMocapMotionSummary
    {
        public bool Ok;
        public string Error;
        public string Clip;
        public BasisMocapHintSource Hint;
        public int Frames;

        public BasisMotionQualitySummary HumanElbow, SolvedElbow;
        public BasisMotionQualitySummary HumanKnee, SolvedKnee;

        /// <summary>Solved jerk / human jerk. 1.0 means the solver's elbow is as ALIVE as the human's.
        /// Far below 1 means something upstream ate the motion (over-smoothing, a plateau, a lerped
        /// path). Far above 1 means the solver is adding movement the human never made.</summary>
        public float ElbowJerkRatio, KneeJerkRatio;

        /// <summary>How much buzz above 8 Hz the solver adds on top of the human's, as a fraction of limb
        /// length. The human's own figure is the mocap's noise floor (~0.2% of limb), so this is an
        /// EXCESS, not an absolute.</summary>
        public float ElbowJitterExcess, KneeJitterExcess;

        /// <summary>Spectral shape mismatch vs the human's own joint. Catches the failures that every
        /// smoothness metric REWARDS.</summary>
        public float ElbowShape, KneeShape;

        public int ElbowPopExcess, KneePopExcess;

        /// <summary>
        /// ⭐ POPS THE SOLVER ACTUALLY INVENTED: frames where OUR joint popped and the HUMAN'S DID NOT.
        ///
        /// This is what *PopExcess above was always meant to mean and does not. PopExcess subtracts COUNTS, so
        /// it cannot tell "popped on the same frames the human did" (invented nothing) from "popped the same
        /// number of times, somewhere else entirely" (invented all of them). Measured on the corpus, 69% of the
        /// knee's flagged frames are frames where the HUMAN'S OWN knee jumps too -- a fast motion, a foot
        /// plant, or the BVH's own rest-to-motion discontinuity on frame 1, which is worth a quarter of a metre
        /// in a single step and lands in BOTH tracks. The solver was being charged for the corpus's noise.
        /// </summary>
        public int ElbowPopsInvented, KneePopsInvented;

        /// <summary>Jitter of the elbow HINT itself, at two stages of the lookup path, as a fraction of arm
        /// length. This is a LOCALISER, not a gate: if HintRaw is clean and HintFlared is not, the chicken-wing
        /// flare is inventing the buzz; if both are dirty, the table (or its input frame) is; if both are clean,
        /// the two-bone solve is amplifying a clean hint. NaN unless hint == Lookup.</summary>
        public float HintRawJitter, HintFlaredJitter;

        /// <summary>Flare internals. FlareEngageJitter = RMS above 8 Hz of the engagement scalar itself (it is
        /// already 0..1, so this is dimensionless); FlareDownProjP05 = 5th-percentile of the down-pole
        /// projection, which is sin(angle of the forearm off vertical) -- near zero means the axis the whole
        /// swivel is measured from has collapsed, and everything built on it is noise.</summary>
        public float FlareEngageJitter, FlareDownProjP05, FlareDownProjMin;

        /// <summary>MEAN engagement. The flare's own docs say it must be ~0 on normal reaches ("0 is an exact
        /// no-op so normal reaches are untouched"). If this is not ~0 on a walk, it is firing when it should not.</summary>
        public float FlareEngageMean;

        /// <summary>Mean elbow POSITION error as a fraction of arm length. Carried here so naturalness and
        /// accuracy can be read off the same row -- a change that buys smoothness by giving up accuracy is not
        /// a win, and this is the only place both numbers appear together.</summary>
        public float ElbowErrFracArm;

        /// <summary>Mean KNEE position error as a fraction of leg length.</summary>
        public float KneeErrFracLeg;

        public override string ToString() =>
            $"{Clip}/{Hint}: elbow jerk x{ElbowJerkRatio:F2} jitter+{ElbowJitterExcess * 100f:F2}%L " +
            $"shape {ElbowShape:F3} pops+{ElbowPopExcess} | knee jerk x{KneeJerkRatio:F2} " +
            $"jitter+{KneeJitterExcess * 100f:F2}%L shape {KneeShape:F3} pops+{KneePopExcess}";
    }

    /// <summary>
    /// "Is it human? Is it natural?" -- answered by comparing the solver's joint motion against A REAL
    /// HUMAN DOING THE SAME MOTION, frame for frame.
    ///
    /// This is what makes the question answerable instead of a matter of taste. Every metric here is a
    /// DIFFERENTIAL against ground truth, so there are no invented thresholds to argue about: the
    /// question is not "is 1200 units of jerk a lot" but "did OUR elbow move like THAT elbow did,
    /// given the same hand to follow".
    ///
    /// The joints scored are the ones the SOLVER INVENTS -- the elbow and the knee. The hand and the
    /// foot are commanded by trackers; if those are wrong it is a wiring bug, and the accuracy gate
    /// already catches it. Nothing tells the elbow where to go, so the elbow is where naturalness
    /// lives or dies.
    ///
    /// WHY THE JERK RATIO IS THE FLAGSHIP, AND WHY IT IS TWO-SIDED:
    /// Over-smooth a real human elbow and it scores BETTER than the human on every smoothness metric
    /// there is (measured: jerk 1171 -> 95, SPARC -3.41 -> -2.62). A one-sided gate would therefore
    /// reward the mushy, laggy, robotic motion we are trying to eliminate, and fail the actual human.
    /// So the gate is a BAND: the solver's elbow must be roughly as alive as the human's -- not calmer,
    /// not busier. See BasisMotionQuality for the full argument.
    /// </summary>
    public static class BasisMocapMotionQuality
    {
        // ---------------------------------------------------------------------------------------------
        // PROVISIONAL THRESHOLDS. Deliberately loose: they catch "obviously not a human arm", nothing
        // subtler. Following the same discipline the accuracy layer landed under -- "there is no honest
        // threshold until the number exists" -- these get tightened into a ratchet once
        // BasisMocapMotionQualityTests has printed the real solver's numbers across the whole corpus.
        // Tighten them from measured data, not from taste.
        // ---------------------------------------------------------------------------------------------

        /// <summary>Below this the solved joint is measurably deader than the human's -- over-smoothed,
        /// plateaued, or lerped. Measured across the whole corpus the shipped solver runs 0.93-1.53, so
        /// nothing is anywhere near this today; it is here to stop a future "fix" that damps the life out
        /// of the arm to make some other gate go green.</summary>
        public const float MinJerkRatio = 0.35f;

        /// <summary>Above this the solver is inventing motion the human never made.</summary>
        public const float MaxJerkRatio = 3.0f;

        /// <summary>
        /// Excess RMS above 8 Hz, as a fraction of limb length -- THE gate. The human's own residual is
        /// ~0.2% of limb, and most of that is the mocap rig's optical noise rather than the human.
        /// A real elbow does not buzz: physiological tremor is 0.1-0.3 mm.
        ///
        /// 0.5% of a 0.6 m arm is 3 mm of RMS buzz. Handed the TRUE elbow the solver achieves 0.00-0.28%,
        /// so this is a target the solver is demonstrably capable of hitting -- it is not an aspiration.
        /// </summary>
        public const float MaxJitterExcess = 0.005f;

        /// <summary>
        /// ⚠ REPORTED, NOT GATED -- deliberately.
        ///
        /// Spectral shape distance is an excellent DEGRADATION detector (over-smoothing 0.160 vs added
        /// jitter 0.001, measured on identical source motion) and it is genuinely informative here: on the
        /// buzzy clips it separates the three hint sources cleanly (69_70: Lookup 0.494, None 0.130,
        /// TruthJoint 0.000). But as an ABSOLUTE cross-trajectory gate it is not yet trustworthy: on 02_01
        /// it scores the TRUE elbow (0.662) as moving less like the human than a crude lookup guess (0.568),
        /// which is impossible. Every other clip in the corpus puts TruthJoint at 0.000-0.058, so the metric
        /// is right 19 times out of 20 and I do not understand the twentieth.
        ///
        /// The suspicion is band-edge sensitivity: with 5 hard-edged bands, a narrowband motion sitting near
        /// a boundary can dump its energy across the edge under a small trajectory change. Overlapping or
        /// smoothly-weighted bands would likely fix it.
        ///
        /// A metric I cannot explain does not get to fail builds. HintSources_Compared_ForMotionQuality
        /// exists to keep this honest and is currently what is telling us not to trust it -- exactly its job.
        /// Understand the 02_01 case, then promote this back to a gate.
        /// </summary>
        public const float ReportOnlyShapeDistance = 0.20f;

        public static BasisMocapMotionSummary Run(BasisMotionClip clip, BasisMocapHintSource hint)
        {
            var s = new BasisMocapMotionSummary { Hint = hint };
            if (clip == null || clip.FrameCount < 64) { s.Error = "clip too short to measure motion"; return s; }
            s.Clip = clip.Name;
            s.Frames = clip.FrameCount;

            var tracks = new BasisMocapAccuracy.BasisMocapTracks();
            BasisMocapAccuracySummary acc = BasisMocapAccuracy.Run(clip, hint, null, tracks);
            if (!acc.Ok) { s.Error = acc.Error; return s; }
            s.ElbowErrFracArm = acc.ElbowMeanFracArm;
            s.KneeErrFracLeg = acc.KneeMeanFracLeg;

            float dt = tracks.Dt;

            s.HumanElbow = BasisMotionQuality.Analyze(tracks.TruthElbow, tracks.ArmLen, dt, "human.elbow");
            s.SolvedElbow = BasisMotionQuality.Analyze(tracks.SolvedElbow, tracks.ArmLen, dt, "solved.elbow",
                                                       reference: tracks.TruthElbow);
            s.HumanKnee = BasisMotionQuality.Analyze(tracks.TruthKnee, tracks.LegLen, dt, "human.knee");
            s.SolvedKnee = BasisMotionQuality.Analyze(tracks.SolvedKnee, tracks.LegLen, dt, "solved.knee",
                                                      reference: tracks.TruthKnee);

            if (!s.HumanElbow.Ok || !s.SolvedElbow.Ok || !s.HumanKnee.Ok || !s.SolvedKnee.Ok)
            {
                s.Error = s.HumanElbow.Error ?? s.SolvedElbow.Error ?? s.HumanKnee.Error ?? s.SolvedKnee.Error;
                return s;
            }

            s.ElbowJerkRatio = Ratio(s.SolvedElbow.JerkPerLimb, s.HumanElbow.JerkPerLimb);
            s.KneeJerkRatio = Ratio(s.SolvedKnee.JerkPerLimb, s.HumanKnee.JerkPerLimb);

            s.ElbowJitterExcess = s.SolvedElbow.JitterFracLimb - s.HumanElbow.JitterFracLimb;
            s.KneeJitterExcess = s.SolvedKnee.JitterFracLimb - s.HumanKnee.JitterFracLimb;

            s.ElbowShape = s.SolvedElbow.ShapeDistance;
            s.KneeShape = s.SolvedKnee.ShapeDistance;

            s.ElbowPopExcess = Mathf.Max(0, s.SolvedElbow.Pops - s.HumanElbow.Pops);
            s.KneePopExcess = Mathf.Max(0, s.SolvedKnee.Pops - s.HumanKnee.Pops);

            s.ElbowPopsInvented = PopsInvented(s.SolvedElbow.PopFrames, s.HumanElbow.PopFrames);
            s.KneePopsInvented = PopsInvented(s.SolvedKnee.PopFrames, s.HumanKnee.PopFrames);

            s.HintRawJitter = float.NaN;
            s.HintFlaredJitter = float.NaN;
            s.FlareEngageJitter = float.NaN;
            s.FlareDownProjP05 = float.NaN;
            s.FlareDownProjMin = float.NaN;
            if ((hint == BasisMocapHintSource.Lookup || hint == BasisMocapHintSource.LookupNoFlare) && tracks.HintRaw != null)
            {
                BasisMotionQualitySummary raw = BasisMotionQuality.Analyze(tracks.HintRaw, tracks.ArmLen, dt, "hint.raw");
                BasisMotionQualitySummary fla = BasisMotionQuality.Analyze(tracks.HintFlared, tracks.ArmLen, dt, "hint.flared");
                if (raw.Ok) s.HintRawJitter = raw.JitterFracLimb;
                if (fla.Ok) s.HintFlaredJitter = fla.JitterFracLimb;

                // The engagement scalar is already dimensionless 0..1, so its residual above 8 Hz IS its jitter.
                float[] eng = tracks.FlareEngage;
                float[] lo = BasisMotionSignal.LowPass(eng, dt, BasisMotionSignal.MotionBandHz);
                var res = new float[eng.Length];
                for (int i = 0; i < eng.Length; i++) res[i] = eng[i] - lo[i];
                s.FlareEngageJitter = BasisMotionSignal.Rms(res);

                double sum = 0; foreach (float e in eng) sum += e;
                s.FlareEngageMean = (float)(sum / System.Math.Max(1, eng.Length));

                s.FlareDownProjP05 = BasisMotionSignal.Quantile(tracks.FlareDownProj, 0.05f);
                s.FlareDownProjMin = BasisMotionSignal.Quantile(tracks.FlareDownProj, 0f);
            }

            s.Ok = true;
            return s;
        }

        static float Ratio(float solved, float human) => human > 1e-6f ? solved / human : float.NaN;

        /// <summary>Frames where the SOLVED joint popped and the HUMAN'S did not -- i.e. the discontinuities the
        /// solver is responsible for, as opposed to the ones it faithfully reproduced from the source data.
        /// A pop the human also made is the solver doing its job, and charging it for that is how a metric ends
        /// up reporting the same number no matter what you change.</summary>
        static int PopsInvented(bool[] solved, bool[] human)
        {
            if (solved == null) return 0;
            int n = human == null ? solved.Length : Mathf.Min(solved.Length, human.Length);
            int invented = 0;
            for (int i = 0; i < n; i++)
            {
                if (solved[i] && (human == null || !human[i])) invented++;
            }
            return invented;
        }

        public static (bool pass, string reason) Gate(in BasisMocapMotionSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);

            // Jerk band, elbow. Checked FIRST and from BOTH sides, because the failure this catches --
            // motion that is too DEAD -- is the one every other metric here would happily call a pass.
            if (s.ElbowJerkRatio < MinJerkRatio)
                return (false, $"elbow moves like a puppet: jerk is {s.ElbowJerkRatio:F2}x the human's " +
                               $"(floor {MinJerkRatio}) -- something upstream is eating the motion");
            if (s.ElbowJerkRatio > MaxJerkRatio)
                return (false, $"elbow is busier than a human's: jerk {s.ElbowJerkRatio:F2}x (ceiling {MaxJerkRatio})");

            if (s.KneeJerkRatio < MinJerkRatio)
                return (false, $"knee moves like a puppet: jerk {s.KneeJerkRatio:F2}x the human's (floor {MinJerkRatio})");
            if (s.KneeJerkRatio > MaxJerkRatio)
                return (false, $"knee is busier than a human's: jerk {s.KneeJerkRatio:F2}x (ceiling {MaxJerkRatio})");

            if (s.ElbowJitterExcess > MaxJitterExcess)
                return (false, $"elbow buzzes: {s.ElbowJitterExcess * 100f:F2}% of arm length above 8 Hz beyond " +
                               $"the human's, at {s.SolvedElbow.JitterHz:F0} Hz (ceiling {MaxJitterExcess * 100f:F1}%)");
            if (s.KneeJitterExcess > MaxJitterExcess)
                return (false, $"knee buzzes: {s.KneeJitterExcess * 100f:F2}% of leg length above 8 Hz beyond the " +
                               $"human's, at {s.SolvedKnee.JitterHz:F0} Hz (ceiling {MaxJitterExcess * 100f:F1}%)");

            // ShapeDistance is deliberately NOT gated -- see ReportOnlyShapeDistance. It is carried in the
            // summary and printed, but it does not fail a build until the 02_01 anomaly is understood.

            return (true, s.ToString());
        }
    }
}
