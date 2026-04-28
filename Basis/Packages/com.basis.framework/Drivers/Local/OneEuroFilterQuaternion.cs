using System;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
        [Serializable]
        public class IKOneEuroFilterQuaternion
        {
            public float minCutoff;
            public float beta;
            public float dCutoff;

            private bool hasPrev;
            private Quaternion prev;
            private readonly OneEuroFilterVector3 vecFilter;

            public IKOneEuroFilterQuaternion(float minCutoff, float beta, float dCutoff)
            {
                this.minCutoff = minCutoff;
                this.beta = beta;
                this.dCutoff = dCutoff;
                vecFilter = new OneEuroFilterVector3(minCutoff, beta, dCutoff);
            }

            public void Reset() => hasPrev = false;
        }
}
