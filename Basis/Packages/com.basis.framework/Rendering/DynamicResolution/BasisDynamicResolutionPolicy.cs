namespace Basis.Scripts.Rendering
{
    public struct BasisDynamicResolutionSettings
    {
        public float MinimumScale;
        public float MaximumScale;
        public float RaiseHeadroom;
        public float LowerHeadroom;
        public float MaximumUpStep;
        public float MaximumDownStep;
        public int SettleFrames;
        public float Smoothing;

        public static BasisDynamicResolutionSettings Default()
        {
            return new BasisDynamicResolutionSettings
            {
                MinimumScale = 0.5f,
                MaximumScale = 1f,
                RaiseHeadroom = 1.15f,
                LowerHeadroom = 0.98f,
                MaximumUpStep = 0.02f,
                MaximumDownStep = 0.1f,
                SettleFrames = 6,
                Smoothing = 0.1f
            };
        }
    }

    public struct BasisDynamicResolutionState
    {
        public float Scale;
        public float SmoothedGpuMilliseconds;
        public int FramesSinceChange;
    }

    public static class BasisDynamicResolutionPolicy
    {
        public const float MinimumMeaningfulChange = 0.001f;

        public static float SmoothSample(float previous, float sample, float smoothing)
        {
            if (sample <= 0f)
            {
                return previous;
            }
            if (previous <= 0f)
            {
                return sample;
            }
            if (smoothing <= 0f)
            {
                return previous;
            }
            if (smoothing >= 1f)
            {
                return sample;
            }
            return previous + (sample - previous) * smoothing;
        }

        public static bool Evaluate(in BasisDynamicResolutionSettings settings, ref BasisDynamicResolutionState state, float gpuMilliseconds, float targetMilliseconds)
        {
            if (state.Scale <= 0f)
            {
                state.Scale = settings.MaximumScale;
            }

            if (gpuMilliseconds <= 0f || targetMilliseconds <= 0f)
            {
                state.FramesSinceChange++;
                return false;
            }

            state.SmoothedGpuMilliseconds = SmoothSample(state.SmoothedGpuMilliseconds, gpuMilliseconds, settings.Smoothing);

            if (state.FramesSinceChange < settings.SettleFrames)
            {
                state.FramesSinceChange++;
                return false;
            }

            float smoothed = state.SmoothedGpuMilliseconds;
            if (smoothed <= 0f)
            {
                state.FramesSinceChange++;
                return false;
            }

            float headroom = targetMilliseconds / smoothed;
            if (headroom < settings.RaiseHeadroom && headroom > settings.LowerHeadroom)
            {
                state.FramesSinceChange++;
                return false;
            }

            float desired = state.Scale * (float)System.Math.Sqrt(headroom);

            if (desired > state.Scale)
            {
                float ceiling = state.Scale + settings.MaximumUpStep;
                if (desired > ceiling)
                {
                    desired = ceiling;
                }
            }
            else
            {
                float floor = state.Scale - settings.MaximumDownStep;
                if (desired < floor)
                {
                    desired = floor;
                }
            }

            if (desired < settings.MinimumScale)
            {
                desired = settings.MinimumScale;
            }
            if (desired > settings.MaximumScale)
            {
                desired = settings.MaximumScale;
            }

            float change = desired - state.Scale;
            if (change < 0f)
            {
                change = -change;
            }

            if (change <= MinimumMeaningfulChange)
            {
                state.FramesSinceChange++;
                return false;
            }

            state.Scale = desired;
            state.FramesSinceChange = 0;
            return true;
        }
    }
}
