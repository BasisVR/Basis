using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Basis.Scripts.Drivers
{

    public struct BasisFrameBottleneckReading
    {
        public double FrameMs;
        public double MainThreadMs;
        public double RenderThreadMs;
        public double PresentWaitMs;
        public double CpuBusyMs;
        public double GpuMs;
        public double TargetMs;
        public bool HasGpuTiming;
        public BasisFrameBottleneckKind Kind;
    }

    [AutoStaticsCleanup]
    public static partial class BasisFrameBottleneck
    {
        public const double DominanceRatio = 1.15;
        public const double DominanceReleaseRatio = 1.03;
        public const double CapReachedRatio = 1.05;
        public const double CapReachedReleaseRatio = 1.15;
        public const double CapHeadroomRatio = 0.85;
        public const double CapHeadroomReleaseRatio = 0.95;

        private const double Smoothing = 0.1;
        private const int WarmupSamples = 8;
        private const int TargetRefreshFrames = 60;

        private static readonly FrameTiming[] _timings = new FrameTiming[8];
        private static readonly List<XRDisplaySubsystem> _displays = new List<XRDisplaySubsystem>();

        private static double _frameMs;
        private static double _mainMs;
        private static double _renderMs;
        private static double _waitMs;
        private static double _gpuMs;
        private static double _targetMs;

        private static int _samples;
        private static int _gpuSamples;
        private static int _targetCountdown;
        private static int _lastSampledFrame = -1;
        private static bool _captureFailed;

        public static void Reset()
        {
            _frameMs = 0;
            _mainMs = 0;
            _renderMs = 0;
            _waitMs = 0;
            _gpuMs = 0;
            _targetMs = 0;
            _samples = 0;
            _gpuSamples = 0;
            _targetCountdown = 0;
            _lastSampledFrame = -1;
        }

        public static void Sample()
        {
            int frame = Time.frameCount;
            if (_lastSampledFrame == frame)
            {
                return;
            }
            _lastSampledFrame = frame;

            if (_targetCountdown <= 0)
            {
                _targetMs = ResolveTargetMs();
                _targetCountdown = TargetRefreshFrames;
            }
            _targetCountdown--;

            double fallbackFrameMs = Time.unscaledDeltaTime * 1000.0;

            if (_captureFailed)
            {
                Accumulate(ref _frameMs, fallbackFrameMs);
                _samples++;
                return;
            }

            uint count;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                count = FrameTimingManager.GetLatestTimings((uint)_timings.Length, _timings);
            }
            catch
            {
                _captureFailed = true;
                Accumulate(ref _frameMs, fallbackFrameMs);
                _samples++;
                return;
            }

            if (count == 0)
            {
                Accumulate(ref _frameMs, fallbackFrameMs);
                return;
            }

            FrameTiming latest = _timings[0];
            Accumulate(ref _frameMs, latest.cpuFrameTime > 0.0 ? latest.cpuFrameTime : fallbackFrameMs);
            Accumulate(ref _mainMs, latest.cpuMainThreadFrameTime);
            Accumulate(ref _renderMs, latest.cpuRenderThreadFrameTime);
            Accumulate(ref _waitMs, latest.cpuMainThreadPresentWaitTime);
            _samples++;

            int available = (int)count;
            if (available > _timings.Length)
            {
                available = _timings.Length;
            }

            for (int index = 0; index < available; index++)
            {
                double gpu = _timings[index].gpuFrameTime;
                if (gpu > 0.0)
                {
                    Accumulate(ref _gpuMs, gpu);
                    _gpuSamples++;
                    break;
                }
            }
        }

        public static BasisFrameBottleneckReading Read()
        {
            return Read(BasisFrameBottleneckKind.Measuring);
        }

        public static BasisFrameBottleneckReading Read(BasisFrameBottleneckKind previous)
        {
            BasisFrameBottleneckReading reading = new BasisFrameBottleneckReading
            {
                FrameMs = _frameMs,
                MainThreadMs = _mainMs,
                RenderThreadMs = _renderMs,
                PresentWaitMs = _waitMs,
                GpuMs = _gpuMs,
                TargetMs = _targetMs,
                HasGpuTiming = _gpuSamples > 0
            };

            reading.CpuBusyMs = ResolveCpuBusyMs(reading);
            reading.Kind = Classify(reading, _samples >= WarmupSamples, previous);
            return reading;
        }

        public static double ResolveCpuBusyMs(BasisFrameBottleneckReading reading)
        {
            double busy = reading.MainThreadMs > 0.0 ? reading.MainThreadMs : reading.FrameMs;
            busy -= reading.PresentWaitMs;
            return busy > 0.0 ? busy : 0.0;
        }

        public static BasisFrameBottleneckKind Classify(BasisFrameBottleneckReading reading, bool warmedUp)
        {
            return Classify(reading, warmedUp, BasisFrameBottleneckKind.Measuring);
        }

        public static BasisFrameBottleneckKind Classify(
            BasisFrameBottleneckReading reading,
            bool warmedUp,
            BasisFrameBottleneckKind previous)
        {
            if (!warmedUp || reading.FrameMs <= 0.0)
            {
                return BasisFrameBottleneckKind.Measuring;
            }

            if (!reading.HasGpuTiming)
            {
                return BasisFrameBottleneckKind.NoGpuTimer;
            }

            bool holdingCap = previous == BasisFrameBottleneckKind.FrameCap;
            double capReached = holdingCap ? CapReachedReleaseRatio : CapReachedRatio;
            double capHeadroom = holdingCap ? CapHeadroomReleaseRatio : CapHeadroomRatio;

            if (reading.TargetMs > 0.0
                && reading.FrameMs <= reading.TargetMs * capReached
                && reading.CpuBusyMs <= reading.TargetMs * capHeadroom
                && reading.GpuMs <= reading.TargetMs * capHeadroom)
            {
                return BasisFrameBottleneckKind.FrameCap;
            }

            double gpuGate = previous == BasisFrameBottleneckKind.Gpu ? DominanceReleaseRatio : DominanceRatio;
            if (reading.GpuMs > reading.CpuBusyMs * gpuGate)
            {
                return BasisFrameBottleneckKind.Gpu;
            }

            double cpuGate = previous == BasisFrameBottleneckKind.Cpu ? DominanceReleaseRatio : DominanceRatio;
            if (reading.CpuBusyMs > reading.GpuMs * cpuGate)
            {
                return BasisFrameBottleneckKind.Cpu;
            }

            return BasisFrameBottleneckKind.Balanced;
        }

        private static void Accumulate(ref double smoothed, double value)
        {
            if (value <= 0.0)
            {
                return;
            }

            if (smoothed <= 0.0)
            {
                smoothed = value;
                return;
            }

            smoothed += (value - smoothed) * Smoothing;
        }

        private static double ResolveTargetMs()
        {
            int targetFrameRate = Application.targetFrameRate;
            if (targetFrameRate > 0)
            {
                return 1000.0 / targetFrameRate;
            }

            if (XRSettings.isDeviceActive)
            {
                SubsystemManager.GetSubsystems(_displays);
                for (int index = 0; index < _displays.Count; index++)
                {
                    XRDisplaySubsystem display = _displays[index];
                    if (display == null || !display.running)
                    {
                        continue;
                    }

                    if (display.TryGetDisplayRefreshRate(out float refreshRate) && refreshRate > 0f)
                    {
                        return 1000.0 / refreshRate;
                    }
                }

                return 0.0;
            }

            int vSyncCount = QualitySettings.vSyncCount;
            if (vSyncCount > 0)
            {
                double refreshHz = Screen.currentResolution.refreshRateRatio.value;
                if (refreshHz > 0.0)
                {
                    return 1000.0 * vSyncCount / refreshHz;
                }
            }

            return 0.0;
        }
    }
}
