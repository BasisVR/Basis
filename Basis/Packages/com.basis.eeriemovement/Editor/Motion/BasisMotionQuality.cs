using System;
using UnityEngine;
namespace Basis.IK.Motion
{
    public struct BasisMotionQualitySummary
    {
        public bool Ok;
        public string Error, Label;
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
        public bool[] PopFrames;
        // --- fidelity against a reference track (the human, or a golden run). NaN if none given.
        public float ShapeDistance;    // spectral SHAPE mismatch -- catches what smoothness rewards
        public float MeanErrFracLimb;
        public override string ToString() => $"{Label}: jerk={JerkPerLimb:F0}/L/s3 sparc={Sparc:F2} jitter={JitterFracLimb * 100f:F3}%L@{JitterHz:F0}Hz " + $"pops={Pops} v95={SpeedP95PerLimb:F2}/L/s" + (float.IsNaN(ShapeDistance) ? "" : $" shape={ShapeDistance:F3} err={MeanErrFracLimb * 100f:F2}%L");
    }
    public static class BasisMotionQuality
    {
        public const float PopRatio = 8f, PopMinFracLimb = 0.01f;
        public static BasisMotionQualitySummary Analyze(Vector3[] joint, float limbLength, float dt, string label, Vector3[] reference = null)
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

            Vector3[] vel = BasisMotionSignal.Derivative(intended, dt), acc = BasisMotionSignal.Derivative(vel, dt);
            Vector3[] jerk = BasisMotionSignal.Derivative(acc, dt);
            float[] speed = BasisMotionSignal.Magnitude(vel);

            s.JerkPerLimb = BasisMotionSignal.Rms(jerk) / limbLength;
            s.Sparc = BasisMotionSpectrum.Sparc(speed, dt);
            s.SpeedP95PerLimb = BasisMotionSignal.Quantile(speed, 0.95f) / limbLength;
            s.SpeedMaxPerLimb = BasisMotionSignal.Quantile(speed, 1f) / limbLength;

            s.JitterFracLimb = BasisMotionSignal.Rms(residual) / limbLength;
            s.JitterHz = BasisMotionSpectrum.DominantAbove(BasisMotionSignal.Magnitude(BasisMotionSignal.Derivative(joint, dt)), dt, BasisMotionSignal.JitterBandHz);

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
        static void PopStats(Vector3[] p, float limbLength, out int pops, out float worst, out int worstFrame, out bool[] popFrames)
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
