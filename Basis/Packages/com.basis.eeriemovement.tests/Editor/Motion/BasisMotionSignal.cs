using System;
using UnityEngine;
namespace Basis.IK.Motion
{
    public static class BasisMotionSignal
    {
        public const float MotionBandHz = 6f, JitterBandHz = 8f;
        // ------------------------------------------------------------------ derivatives
        public static Vector3[] Derivative(Vector3[] p, float dt)
        {
            if (p == null || p.Length < 2) return Array.Empty<Vector3>();
            float inv2 = 1f / (2f * dt);
            var d = new Vector3[p.Length];
            d[0] = (p[1] - p[0]) / dt;
            d[p.Length - 1] = (p[p.Length - 1] - p[p.Length - 2]) / dt;
            for (int i = 1; i < p.Length - 1; i++) d[i] = (p[i + 1] - p[i - 1]) * inv2;
            return d;
        }
        public static float[] Derivative(float[] x, float dt)
        {
            if (x == null || x.Length < 2) return Array.Empty<float>();
            float inv2 = 1f / (2f * dt);
            var d = new float[x.Length];
            d[0] = (x[1] - x[0]) / dt;
            d[x.Length - 1] = (x[x.Length - 1] - x[x.Length - 2]) / dt;
            for (int i = 1; i < x.Length - 1; i++) d[i] = (x[i + 1] - x[i - 1]) * inv2;
            return d;
        }
        public static float[] AngularSpeedDeg(Quaternion[] q, float dt)
        {
            if (q == null || q.Length < 2) return Array.Empty<float>();
            var w = new float[q.Length];
            for (int i = 1; i < q.Length; i++)
            {
                Quaternion a = q[i - 1], b = q[i];
                if (Quaternion.Dot(a, b) < 0f) b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
                (Quaternion.Inverse(a) * b).ToAngleAxis(out float deg, out Vector3 _);
                if (deg > 180f) deg = 360f - deg;
                w[i] = deg / dt;
            }
            if (q.Length > 1) w[0] = w[1];
            return w;
        }
        // ------------------------------------------------------------------ filtering
        public static float[] LowPass(float[] x, float dt, float cutoffHz)
        {
            if (x == null || x.Length < 8) return (float[])(x?.Clone() ?? Array.Empty<float>());
            float fs = 1f / dt;
            if (cutoffHz >= fs * 0.5f) return (float[])x.Clone();

            // Bilinear-transformed 2nd-order Butterworth. DC gain is exactly 1 by construction:
            // (b0+b1+b2) / (1+a1+a2) == 4w^2 / 4w^2.
            double w = Math.Tan(Math.PI * cutoffHz / fs), w2 = w * w, den = 1.0 + Math.Sqrt(2.0) * w + w2;
            double b0 = w2 / den, b1 = 2.0 * b0, b2 = b0;
            double a1 = 2.0 * (w2 - 1.0) / den, a2 = (1.0 - Math.Sqrt(2.0) * w + w2) / den;

            // Odd extension. A plain zero-pad would slam the filter with a step at each end and ring
            // for tens of samples; an ODD extension continues the signal's own slope across the
            // boundary (2*x0 - x[k]), which is what a moving limb actually does.
            int pad = Math.Min(x.Length - 1, (int)(3f * fs / Math.Max(cutoffHz, 0.01f)));
            int n = x.Length, m = n + 2 * pad;
            var buf = new double[m];
            for (int i = 0; i < pad; i++) buf[i] = 2.0 * x[0] - x[pad - i];
            for (int i = 0; i < n; i++) buf[pad + i] = x[i];
            for (int i = 0; i < pad; i++) buf[pad + n + i] = 2.0 * x[n - 1] - x[n - 2 - i];

            Biquad(buf, b0, b1, b2, a1, a2);
            Array.Reverse(buf);
            Biquad(buf, b0, b1, b2, a1, a2);
            Array.Reverse(buf);

            var outv = new float[n];
            for (int i = 0; i < n; i++) outv[i] = (float)buf[pad + i];
            return outv;
        }
        static void Biquad(double[] v, double b0, double b1, double b2, double a1, double a2)
        {
            double x1 = v.Length > 0 ? v[0] : 0, x2 = x1, y1 = x1, y2 = x1;
            for (int i = 0; i < v.Length; i++)
            {
                double x0 = v[i], y0 = b0 * x0 + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                v[i] = y0;
                x2 = x1; x1 = x0;
                y2 = y1; y1 = y0;
            }
        }
        public static Vector3[] LowPass(Vector3[] p, float dt, float cutoffHz)
        {
            if (p == null || p.Length == 0) return Array.Empty<Vector3>();
            var xs = new float[p.Length];
            var ys = new float[p.Length];
            var zs = new float[p.Length];
            for (int i = 0; i < p.Length; i++) { xs[i] = p[i].x; ys[i] = p[i].y; zs[i] = p[i].z; }
            xs = LowPass(xs, dt, cutoffHz);
            ys = LowPass(ys, dt, cutoffHz);
            zs = LowPass(zs, dt, cutoffHz);
            var o = new Vector3[p.Length];
            for (int i = 0; i < p.Length; i++) o[i] = new Vector3(xs[i], ys[i], zs[i]);
            return o;
        }
        public static void Split(Vector3[] p, float dt, out Vector3[] intended, out Vector3[] residual, float cutoffHz = MotionBandHz)
        {
            intended = LowPass(p, dt, cutoffHz);
            residual = new Vector3[p.Length];
            for (int i = 0; i < p.Length; i++) residual[i] = p[i] - intended[i];
        }
        // ------------------------------------------------------------------ statistics
        public static float[] Magnitude(Vector3[] v)
        {
            var m = new float[v.Length];
            for (int i = 0; i < v.Length; i++) m[i] = v[i].magnitude;
            return m;
        }
        public static float Rms(Vector3[] v, int from = 0, int to = -1)
        {
            if (to < 0) to = v.Length;
            double s = 0; int n = 0;
            for (int i = from; i < to; i++) { s += (double)v[i].sqrMagnitude; n++; }
            return n == 0 ? 0f : (float)Math.Sqrt(s / n);
        }
        public static float Rms(float[] x, int from = 0, int to = -1)
        {
            if (to < 0) to = x.Length;
            double s = 0; int n = 0;
            for (int i = from; i < to; i++) { s += (double)x[i] * x[i]; n++; }
            return n == 0 ? 0f : (float)Math.Sqrt(s / n);
        }
        public static float Quantile(float[] x, float q)
        {
            if (x == null || x.Length == 0) return float.NaN;
            var c = (float[])x.Clone();
            Array.Sort(c);
            float pos = Mathf.Clamp01(q) * (c.Length - 1);
            int lo = Mathf.FloorToInt(pos), hi = Mathf.Min(lo + 1, c.Length - 1);
            return Mathf.Lerp(c[lo], c[hi], pos - lo);
        }
        public static float[] JointAngleDeg(Vector3[] a, Vector3[] b, Vector3[] c)
        {
            int n = Mathf.Min(a.Length, Mathf.Min(b.Length, c.Length));
            var ang = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 u = a[i] - b[i], w = c[i] - b[i];
                float lu = u.magnitude, lw = w.magnitude;
                ang[i] = (lu < 1e-9f || lw < 1e-9f) ? float.NaN : Mathf.Acos(Mathf.Clamp(Vector3.Dot(u / lu, w / lw), -1f, 1f)) * Mathf.Rad2Deg;
            }
            return ang;
        }
        public static Vector3[] MinJerk(Vector3 from, Vector3 to, int frames)
        {
            var p = new Vector3[frames];
            for (int i = 0; i < frames; i++)
            {
                float t = frames == 1 ? 1f : i / (float)(frames - 1);
                float s = 10f * t * t * t - 15f * t * t * t * t + 6f * t * t * t * t * t;
                p[i] = Vector3.LerpUnclamped(from, to, s);
            }
            return p;
        }
    }
}
