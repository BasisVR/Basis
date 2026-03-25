using System;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    [Serializable]
    public class OneEuroFilterVector3 : OneEuroFilter
    {
        private LowPassFilter _xX = new LowPassFilter();
        private LowPassFilter _xY = new LowPassFilter();
        private LowPassFilter _xZ = new LowPassFilter();

        private LowPassFilter _dxX = new LowPassFilter();
        private LowPassFilter _dxY = new LowPassFilter();
        private LowPassFilter _dxZ = new LowPassFilter();

        public OneEuroFilterVector3() { }
        public OneEuroFilterVector3(float minCutoff, float beta, float dCutoff) : base(minCutoff, beta, dCutoff) { }

        public Vector3 Filter(Vector3 x, double timestamp)
        {
            float dt = ComputeDt(timestamp);

            // Derivative of the signal
            Vector3 dx = new Vector3(
                (_xX.Last() - x.x) / dt,
                (_xY.Last() - x.y) / dt,
                (_xZ.Last() - x.z) / dt
            );

            // Filter the derivative
            float ad = Alpha(dCutoff, dt);
            dx = new Vector3(
                _dxX.Filter(dx.x, ad),
                _dxY.Filter(dx.y, ad),
                _dxZ.Filter(dx.z, ad)
            );

            // Dynamic cutoff
            float cutoff = minCutoff + beta * dx.magnitude;

            // Filter the signal
            float a = Alpha(cutoff, dt);
            return new Vector3(
                _xX.Filter(x.x, a),
                _xY.Filter(x.y, a),
                _xZ.Filter(x.z, a)
            );
        }
    }
}
