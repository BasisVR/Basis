using UnityEngine;

namespace HVR.Basis.Comms.OSC
{
    internal sealed class BasisEyeLidPlausibility
    {
        public const float ClosedBelow = 0.3f;
        public const float ClosedTimeConstant = 4f;
        public const float AcceptClosedShare = 0.25f;
        public const float RejectClosedShare = 0.5f;
        public const float SampleStaleSeconds = 1f;
        public const float SourceSilenceSeconds = 3f;

        private float _lastFaceSampleTime = float.NegativeInfinity;
        private float _lastLidSampleTime = float.NegativeInfinity;
        private float _lidLeft, _lidRight;
        private float _closedShare = 1f;

        public bool Valid { get; private set; }

        public void FaceSample(float now) => _lastFaceSampleTime = now;

        public void LidSample(float now, bool left, bool right, float lid)
        {
            _lastFaceSampleTime = now;
            _lastLidSampleTime = now;
            if (left) _lidLeft = lid;
            if (right) _lidRight = lid;
        }

        public bool Tick(float now, float deltaTime)
        {
            if (now - _lastFaceSampleTime > SourceSilenceSeconds)
            {
                _closedShare = 1f;
                Valid = false;
                return Valid;
            }

            if (now - _lastLidSampleTime < SampleStaleSeconds)
            {
                float closed = Mathf.Max(_lidLeft, _lidRight) < ClosedBelow ? 1f : 0f;
                _closedShare += (closed - _closedShare) * Mathf.Clamp01(deltaTime / ClosedTimeConstant);
                if (!Valid && _closedShare < AcceptClosedShare) Valid = true;
                else if (Valid && _closedShare > RejectClosedShare) Valid = false;
            }

            return Valid;
        }
    }
}
