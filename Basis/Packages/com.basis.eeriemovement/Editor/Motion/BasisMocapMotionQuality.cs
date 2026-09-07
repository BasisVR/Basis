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
        public string Error, Clip;
        public BasisMocapHintSource Hint;
        public int Frames;
        public BasisMotionQualitySummary HumanElbow, SolvedElbow;
        public BasisMotionQualitySummary HumanKnee, SolvedKnee;
        public float ElbowJerkRatio, KneeJerkRatio;
        public float ElbowJitterExcess, KneeJitterExcess;
        public float ElbowShape, KneeShape;
        public int ElbowPopExcess, KneePopExcess;
        public int ElbowPopsInvented, KneePopsInvented;
        public float HintRawJitter, HintFlaredJitter;
        public float FlareEngageJitter, FlareDownProjP05, FlareDownProjMin;
        public float FlareEngageMean, ElbowErrFracArm, KneeErrFracLeg;
        public override string ToString() => $"{Clip}/{Hint}: elbow jerk x{ElbowJerkRatio:F2} jitter+{ElbowJitterExcess * 100f:F2}%L " + $"shape {ElbowShape:F3} pops+{ElbowPopExcess} | knee jerk x{KneeJerkRatio:F2} " + $"jitter+{KneeJitterExcess * 100f:F2}%L shape {KneeShape:F3} pops+{KneePopExcess}";
    }
    public static class BasisMocapMotionQuality
    {
        // ---------------------------------------------------------------------------------------------
        // PROVISIONAL THRESHOLDS. Deliberately loose: they catch "obviously not a human arm", nothing
        // subtler. Following the same discipline the accuracy layer landed under -- "there is no honest
        // threshold until the number exists" -- these get tightened into a ratchet once
        // BasisMocapMotionQualityTests has printed the real solver's numbers across the whole corpus.
        // Tighten them from measured data, not from taste.
        // ---------------------------------------------------------------------------------------------
        public const float MinJerkRatio = 0.35f, MaxJerkRatio = 3.0f, MaxJitterExcess = 0.005f;
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
            s.SolvedElbow = BasisMotionQuality.Analyze(tracks.SolvedElbow, tracks.ArmLen, dt, "solved.elbow", reference: tracks.TruthElbow);
            s.HumanKnee = BasisMotionQuality.Analyze(tracks.TruthKnee, tracks.LegLen, dt, "human.knee");
            s.SolvedKnee = BasisMotionQuality.Analyze(tracks.SolvedKnee, tracks.LegLen, dt, "solved.knee", reference: tracks.TruthKnee);

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
        static int PopsInvented(bool[] solved, bool[] human)
        {
            if (solved == null) return 0;
            int n = human == null ? solved.Length : Mathf.Min(solved.Length, human.Length), invented = 0;
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
                return (false, $"elbow moves like a puppet: jerk is {s.ElbowJerkRatio:F2}x the human's " + $"(floor {MinJerkRatio}) -- something upstream is eating the motion");
            if (s.ElbowJerkRatio > MaxJerkRatio)
                return (false, $"elbow is busier than a human's: jerk {s.ElbowJerkRatio:F2}x (ceiling {MaxJerkRatio})");

            if (s.KneeJerkRatio < MinJerkRatio)
                return (false, $"knee moves like a puppet: jerk {s.KneeJerkRatio:F2}x the human's (floor {MinJerkRatio})");
            if (s.KneeJerkRatio > MaxJerkRatio)
                return (false, $"knee is busier than a human's: jerk {s.KneeJerkRatio:F2}x (ceiling {MaxJerkRatio})");

            if (s.ElbowJitterExcess > MaxJitterExcess)
                return (false, $"elbow buzzes: {s.ElbowJitterExcess * 100f:F2}% of arm length above 8 Hz beyond " + $"the human's, at {s.SolvedElbow.JitterHz:F0} Hz (ceiling {MaxJitterExcess * 100f:F1}%)");
            if (s.KneeJitterExcess > MaxJitterExcess)
                return (false, $"knee buzzes: {s.KneeJitterExcess * 100f:F2}% of leg length above 8 Hz beyond the " + $"human's, at {s.SolvedKnee.JitterHz:F0} Hz (ceiling {MaxJitterExcess * 100f:F1}%)");

            // ShapeDistance is deliberately NOT gated -- see ReportOnlyShapeDistance. It is carried in the
            // summary and printed, but it does not fail a build until the 02_01 anomaly is understood.

            return (true, s.ToString());
        }
    }
}
