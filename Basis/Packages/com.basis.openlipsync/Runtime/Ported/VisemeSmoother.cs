using System;

namespace OpenLipSync.Inference
{
    /// <summary>
    /// Temporal smoother for viseme probabilities that does not push the mouth late.
    ///
    /// <para>The plain one-pole this replaces — <c>y[n] = a*y[n-1] + (1-a)*x[n]</c> — is a lag
    /// network. Its group delay is <c>a/(1-a)</c> frames, so the shipped default of a = 0.7 at
    /// the model's fixed 100 Hz hop held every viseme <b>23 ms behind the audio</b>, purely to
    /// reject frame-to-frame jitter. That delay is spent before the pipeline has even handed the
    /// weights to the avatar, and it is the single largest avoidable term in the lip-sync
    /// latency budget.</para>
    ///
    /// <para>Running the pole twice and extrapolating along the difference — <c>2*y1 - y2</c>,
    /// the standard double-EMA — cancels that delay exactly for a ramp input while keeping most
    /// of the noise rejection. At a = 0.7 the residual noise is 0.62x the raw signal against
    /// 0.42x for the plain pole: slightly less smoothing, 23 ms less lag. The trade is worth
    /// taking because the model already spends 20 ms of unavoidable lookahead, and lag is what
    /// reads as "wrong" on a face.</para>
    ///
    /// <para>Cost is one extra multiply-add per viseme per hop — 15 floats at 100 Hz per
    /// speaker, which is nothing next to the inference that produced them.</para>
    /// </summary>
    public sealed class VisemeSmoother
    {
        private readonly float[] _stage1;
        private readonly float[] _stage2;
        private readonly float[] _output;
        private float _alpha;

        public VisemeSmoother(int visemeCount, float alpha = 0.7f)
        {
            if (visemeCount <= 0) visemeCount = 1;
            _stage1 = new float[visemeCount];
            _stage2 = new float[visemeCount];
            _output = new float[visemeCount];
            _alpha = Sanitize(alpha);
            Reset();
        }

        /// <summary>
        /// Lag-compensated weights. Allocated once and only ever mutated in place, so callers
        /// may cache the reference.
        /// </summary>
        public float[] Output => _output;

        public int Length => _output.Length;

        /// <summary>
        /// Weight kept on the previous frame, 0..0.99. 0 is a straight pass-through; both poles
        /// then collapse onto the input and the lead term contributes nothing.
        /// </summary>
        public float Alpha
        {
            get => _alpha;
            set => _alpha = Sanitize(value);
        }

        private static float Sanitize(float alpha)
        {
            if (float.IsNaN(alpha)) return 0f;
            return alpha < 0f ? 0f : (alpha > 0.99f ? 0.99f : alpha);
        }

        /// <summary>
        /// Advances both poles by one mel frame and republishes <see cref="Output"/>.
        /// </summary>
        public void Step(float[] probabilities)
        {
            if (probabilities == null) return;

            int n = _output.Length;
            if (probabilities.Length < n) n = probabilities.Length;

            float a = _alpha;
            float inv = 1f - a;

            for (int i = 0; i < n; i++)
            {
                float s1 = _stage1[i] * a + probabilities[i] * inv;
                float s2 = _stage2[i] * a + s1 * inv;
                _stage1[i] = s1;
                _stage2[i] = s2;

                // s1 plus one step of its own trend. Overshoot on a hard onset is the whole
                // point of the lead term, but it must not leave probability space — these
                // become blendshape weights directly, and a negative one drives the shape
                // backwards.
                float d = s1 + s1 - s2;
                _output[i] = d < 0f ? 0f : (d > 1f ? 1f : d);
            }
        }

        /// <summary>
        /// Returns to the closed-mouth rest pose. Both poles are seeded to the same value so
        /// the first frame after a reset carries no phantom trend.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_stage1, 0, _stage1.Length);
            Array.Clear(_stage2, 0, _stage2.Length);
            Array.Clear(_output, 0, _output.Length);

            // Index 0 is "sil": start silent rather than with every shape at zero, which on a
            // rig whose rest pose is an open mouth is not the same thing.
            _stage1[0] = 1f;
            _stage2[0] = 1f;
            _output[0] = 1f;
        }
    }
}
