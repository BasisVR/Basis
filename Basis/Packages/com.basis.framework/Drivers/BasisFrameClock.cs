using Unity.Scripting.LifecycleManagement;
namespace Basis.Scripts.Drivers
{
    [AutoStaticsCleanup]
    public static partial class BasisFrameClock
    {
        public static float SmoothedFramesPerSecond { get; private set; }

        public static event System.Action OnTick;

        private static float _smoothedUnscaledDelta;
        private static int _requestCount;
        private static bool _shouldTick;

        public static void AddRequest()
        {
            _requestCount++;
            _shouldTick = _requestCount > 0;
        }

        public static void RemoveRequest()
        {
            if (_requestCount > 0)
            {
                _requestCount--;
            }
            _shouldTick = _requestCount > 0;
            if (!_shouldTick)
            {
                _smoothedUnscaledDelta = 0f;
            }
        }

        public static void Tick(float unscaledDeltaTime)
        {
            if (!_shouldTick)
            {
                return;
            }

            if (unscaledDeltaTime > 0f)
            {
                if (_smoothedUnscaledDelta <= 0f)
                {
                    _smoothedUnscaledDelta = unscaledDeltaTime;
                }
                else
                {
                    _smoothedUnscaledDelta += (unscaledDeltaTime - _smoothedUnscaledDelta) * 0.1f;
                }
                SmoothedFramesPerSecond = _smoothedUnscaledDelta > 0f ? 1f / _smoothedUnscaledDelta : 0f;
            }

            OnTick?.Invoke();
        }
    }
}
