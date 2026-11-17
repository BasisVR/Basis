namespace Basis.Scripts.Networking.Receivers
{
    public struct BasisJitterFloor
    {
        public const float ServerFloor = 2f, DirectFloor = 1f;
        public const double UnderrunWindowSeconds = 10.0, HoldSeconds = 30.0, MaxHoldSeconds = 600.0, CleanRetrySeconds = 120.0;
        public const int UnderrunsToRaise = 2;
        private double _firstUnderrunAt, _raisedUntil, _hold;
        private int _underruns;

        public float Floor(bool direct, double now) => Floor(direct, now, ServerFloor);

        public float Floor(bool direct, double now, float serverFloor) => direct && now >= _raisedUntil ? System.Math.Min(DirectFloor, serverFloor) : serverFloor;

        public void OnUnderrun(bool direct, double now)
        {
            if (!direct || now < _raisedUntil) return;
            if (_underruns == 0 || now - _firstUnderrunAt > UnderrunWindowSeconds)
            {
                _firstUnderrunAt = now;
                _underruns = 0;
            }
            if (++_underruns < UnderrunsToRaise) return;
            _underruns = 0;
            bool soonAfterRetry = _raisedUntil > 0 && now - _raisedUntil < CleanRetrySeconds;
            _hold = soonAfterRetry ? System.Math.Min(System.Math.Max(_hold, HoldSeconds) * 2.0, MaxHoldSeconds) : HoldSeconds;
            _raisedUntil = now + _hold;
        }

        public void Reset()
        {
            _firstUnderrunAt = 0;
            _raisedUntil = 0;
            _hold = 0;
            _underruns = 0;
        }
    }
}
