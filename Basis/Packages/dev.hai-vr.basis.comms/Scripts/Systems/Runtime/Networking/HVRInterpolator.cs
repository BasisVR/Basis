using System.Collections.Generic;
using UnityEngine;

namespace HVR.Basis.Comms
{
    public class HVRInterpolator
    {
        private const float MinimumDurationInQueueToCatchUp = 0.2f;
        private const float MaximumDurationInQueueToCatchUp = 4f;
        private const float SlowCatchUpMultiplier = 0.66f;
        private const float FastCatchUpMultiplier = 0.05f;

        private readonly Queue<HVRInterpolationSnapshot> _snapshots = new();
        private readonly Stack<HVRInterpolationSnapshot> _snapshotPool = new();
        private readonly Dictionary<int, float> _memoryOfPreviousSnapshotValue = new();
        private HVRInterpolationSnapshot _currentSnapshot;
        private float _advanced;

        private float _currentAdjustedDeltaTime;
        private bool _doCatchUp = false;
        private float _totalQueueSeconds;

        public HVRInterpolator(bool doCatchUp)
        {
            _doCatchUp = doCatchUp;
        }

        public void Add(HVRInterpolationSnapshot snapshot)
        {
            _totalQueueSeconds += snapshot.deltaTime;
            _snapshots.Enqueue(snapshot);
        }

        // Rented snapshots must be given back through Add; consumed snapshots return to the pool once the interpolator moves past them.
        public HVRInterpolationSnapshot RentSnapshot()
        {
            return _snapshotPool.Count > 0 ? _snapshotPool.Pop() : new HVRInterpolationSnapshot();
        }

        public bool HasPendingWork => _currentSnapshot != null || _snapshots.Count > 0;

        public void SetCatchUp(bool doCatchUp)
        {
            _doCatchUp = doCatchUp;
        }

        private void TryDequeue()
        {
            var previousSnapshot = _currentSnapshot;
            if (_snapshots.Count > 0)
            {
                var currentQueueSeconds = _totalQueueSeconds;

                _currentSnapshot = _snapshots.Dequeue();
                if (_snapshots.Count == 0)
                {
                    _totalQueueSeconds = 0f;
                }
                else
                {
                    _totalQueueSeconds -= _currentSnapshot.deltaTime;
                    if (_totalQueueSeconds < 0f) _totalQueueSeconds = 0f;
                }

                var needToCatchUp = _doCatchUp && currentQueueSeconds >= MinimumDurationInQueueToCatchUp;
                if (needToCatchUp)
                {
                    var howFastToRecover01 = Mathf.InverseLerp(MinimumDurationInQueueToCatchUp, MaximumDurationInQueueToCatchUp, currentQueueSeconds);
                    var multiplierToCatchUp = Mathf.Lerp(SlowCatchUpMultiplier, FastCatchUpMultiplier, howFastToRecover01);
                    _currentAdjustedDeltaTime = _currentSnapshot.deltaTime * multiplierToCatchUp;
                }
                else
                {
                    _currentAdjustedDeltaTime = _currentSnapshot.deltaTime;
                }
            }
            else
            {
                _currentSnapshot = null;
                _advanced = 0f;
            }

            if (previousSnapshot != null)
            {
                previousSnapshot.addressIdsToValues.Clear();
                _snapshotPool.Push(previousSnapshot);
            }
        }

        public Dictionary<int, float> Advance(float deltaTime, Dictionary<int, float> results)
        {
            results.Clear();

            _advanced += deltaTime;

            if (_currentSnapshot == null)
            {
                TryDequeue();
            }

            while (_currentSnapshot != null && _advanced >= _currentAdjustedDeltaTime)
            {
                foreach (var (addressId, value) in _currentSnapshot.addressIdsToValues)
                {
                    results[addressId] = value;
                    _memoryOfPreviousSnapshotValue[addressId] = value;
                }
                _advanced -= _currentAdjustedDeltaTime;

                TryDequeue();
            }

            if (_currentSnapshot != null)
            {
                foreach (var (addressId, currentValue) in _currentSnapshot.addressIdsToValues)
                {
                    if (_memoryOfPreviousSnapshotValue.TryGetValue(addressId, out var previousValue))
                    {
                        results[addressId] = Lerp(previousValue, currentValue, _advanced / _currentAdjustedDeltaTime);
                    }
                    else
                    {
                        results[addressId] = currentValue;
                    }
                }
            }

            if (results.Count == 0)
            {
                _advanced = 0f;
            }

            return results;
        }

        private float Lerp(float from, float to, float amount01)
        {
            return from + (to - from) * amount01;
        }
    }

    public class HVRInterpolationSnapshot
    {
        public float deltaTime;
        public Dictionary<int, float> addressIdsToValues = new();
    }
}
