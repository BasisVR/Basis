using System;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// One Euro filter implementation based on: Casiez et al., "The One Euro Filter"
    /// Designed for real-time smoothing of jittery signals with minimal lag.
    /// This file includes Vector3 and Quaternion variants and integrates it into BasisLocalRigDriver.
    /// </summary>

    [Serializable]
    public class OneEuroFilter
    {
        // Base signal params
        [Range(0.01f, 10f)] public float minCutoff = 1.0f;  // Hz
        [Range(0f, 10f)] public float beta = 0.0f;          // speed coefficient
        [Range(0.01f, 10f)] public float dCutoff = 1.0f;    // derivative cutoff

        private bool _initialized;
        private double _lastTimestamp;

        protected LowPassFilter _x;
        protected LowPassFilter _dx;

        public OneEuroFilter() { }
        public OneEuroFilter(float minCutoff, float beta, float dCutoff)
        {
            this.minCutoff = minCutoff;
            this.beta = beta;
            this.dCutoff = dCutoff;
        }

        protected float Alpha(float cutoff, float dt)
        {
            // Exponential smoothing factor
            float tau = 1.0f / (2.0f * Mathf.PI * cutoff);
            return 1.0f / (1.0f + tau / Mathf.Max(dt, 1e-6f));
        }

        protected float ComputeDt(double timestamp)
        {
            if (!_initialized)
            {
                _initialized = true;
                _lastTimestamp = timestamp;
                return 1f / 90f; // safe default ~90 FPS
            }
            float dt = (float)math.max(timestamp - _lastTimestamp, 1e-6f);
            _lastTimestamp = timestamp;
            return dt;
        }

        protected class LowPassFilter
        {
            private bool _initialized;
            private float _hatX;
            public float Filter(float x, float alpha)
            {
                if (!_initialized)
                {
                    _initialized = true;
                    _hatX = x;
                }
                else
                {
                    _hatX = alpha * x + (1 - alpha) * _hatX;
                }
                return _hatX;
            }
            public float Last() => _hatX;
        }
    }
}
