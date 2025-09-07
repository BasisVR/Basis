using System;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    [Serializable]
    public class OneEuroFilterQuaternion : OneEuroFilter
    {
        // Represent quaternion as 4D vector and filter components independently (works well for small jitter).
        private LowPassFilter _xW = new LowPassFilter();
        private LowPassFilter _xX = new LowPassFilter();
        private LowPassFilter _xY = new LowPassFilter();
        private LowPassFilter _xZ = new LowPassFilter();

        private LowPassFilter _dxW = new LowPassFilter();
        private LowPassFilter _dxX = new LowPassFilter();
        private LowPassFilter _dxY = new LowPassFilter();
        private LowPassFilter _dxZ = new LowPassFilter();

        public OneEuroFilterQuaternion() { }
        public OneEuroFilterQuaternion(float minCutoff, float beta, float dCutoff) : base(minCutoff, beta, dCutoff) { }

        public Quaternion Filter(Quaternion q, float timestamp)
        {
            // Ensure normalized input
            if (q == Quaternion.identity == false)
                q.Normalize();

            float dt = ComputeDt(timestamp);

            // Derivative approximation (component-wise)
            Vector4 cur = new Vector4(q.x, q.y, q.z, q.w);
            Vector4 last = new Vector4(_xX.Last(), _xY.Last(), _xZ.Last(), _xW.Last());
            Vector4 d = (last - cur) / dt;

            // Filter derivative
            float ad = Alpha(dCutoff, dt);
            Vector4 dFiltered = new Vector4(
                _dxX.Filter(d.x, ad),
                _dxY.Filter(d.y, ad),
                _dxZ.Filter(d.z, ad),
                _dxW.Filter(d.w, ad)
            );

            // Dynamic cutoff based on angular speed magnitude
            float cutoff = minCutoff + beta * dFiltered.magnitude;

            float a = Alpha(cutoff, dt);
            Vector4 qFiltered = new Vector4(
                _xX.Filter(cur.x, a),
                _xY.Filter(cur.y, a),
                _xZ.Filter(cur.z, a),
                _xW.Filter(cur.w, a)
            );

            // Re-normalize
            Quaternion result = new Quaternion(qFiltered.x, qFiltered.y, qFiltered.z, qFiltered.w);
            result.Normalize();
            return result;
        }
    }
}
