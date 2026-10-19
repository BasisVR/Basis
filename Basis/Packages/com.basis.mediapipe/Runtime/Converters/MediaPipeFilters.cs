using UnityEngine;
namespace Basis.MediaPipe
{
    public static class MediaPipeFilterMath
    {
        public const float PositionSlack = 0.02f, TurnSlackDeg = 15f, MaxHandSpeed = 2f, MaxHeadSpeed = 1.5f, MaxTorsoSpeed = 1.5f, MaxTurnDegPerSec = 1000f;
        public const float PositionNoiseMin = 2.5e-7f, PositionNoiseMax = 4e-3f, PositionProcess = 0.1f, RotationNoiseMin = 3e-6f, RotationNoiseMax = 0.03f, RotationProcess = 0.25f, ScalarNoiseMin = 1e-6f, ScalarNoiseMax = 0.01f, ScalarProcess = 1f;
        public const float SurpriseClip = 2.5f, RunSurprise = 1.5f, OnsetSurprise = 3f, OnsetMaxInflation = 25f, CorrelationRate = 0.1f, CorrelationClip = 1.5f, AdaptGain = 0.6f, AdaptDecay = 0.15f, AdaptMin = 0.001f, AdaptMax = 100f;
        public const int ScalarOnsetRun = 0, OnsetRun = 2, DepthOnsetRun = 3, ReturnWindow = 5;
        public const float ReturnSurprise = 3f;
        public const float NoiseFromSecondDifference = 1f / (6f * 0.455f), NoiseRateFloor = 0.03f, NoiseRateCeiling = 0.5f, InitialVelocityVariance = 400f, BlendPeriods = 0.5f, LeadPeriods = 0.5f;
        public static float PositionLimit(float maxSpeed, float dt) => maxSpeed > 0f ? maxSpeed * dt + PositionSlack : 0f;
        public static float TurnLimit(float maxDegPerSec, float dt) => maxDegPerSec > 0f ? maxDegPerSec * dt + TurnSlackDeg : 0f;
        public static float Lead(float age, float period) => LeadPeriods * period * (1f - Mathf.Exp(-age / (LeadPeriods * period)));
        public static float OffsetDecay(float age, float period) => Mathf.Exp(-age / (BlendPeriods * period));
        public static void Step(ref MediaPipeAdaptiveAxis axis, float z, float last, float previous, int samples, float dt, float process, float noiseMin, float noiseMax, int onsetRun)
        {
            if (!axis.Started)
            {
                axis.Start(z, Mathf.Sqrt(noiseMin * noiseMax));
                return;
            }
            if (samples >= 2) axis.TrackNoise(z - 2f * last + previous, noiseMin, noiseMax, samples);
            axis.Predict(dt, process);
            axis.Correct(z - axis.X, onsetRun);
        }
        public static Vector3 Log(Quaternion q)
        {
            if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            Vector3 v = new Vector3(q.x, q.y, q.z);
            float s = v.magnitude;
            return s < 1e-6f ? v * 2f : v * (2f * Mathf.Atan2(s, q.w) / s);
        }
        public static Quaternion Exp(Vector3 v)
        {
            float theta = v.magnitude;
            if (theta < 1e-6f) return Normalize(new Quaternion(v.x * 0.5f, v.y * 0.5f, v.z * 0.5f, 1f));
            float half = theta * 0.5f, k = Mathf.Sin(half) / theta;
            return new Quaternion(v.x * k, v.y * k, v.z * k, Mathf.Cos(half));
        }
        public static Quaternion Normalize(Quaternion q)
        {
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return n > 1e-8f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
        }
        public static Quaternion Power(Quaternion q, float t) => Exp(Log(q) * t);
        public static Quaternion Conjugate(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);
    }
    public struct MediaPipeAdaptiveAxis
    {
        public float X, V, P00, P01, P11, R, Adapt, Surprise, Correlation, Baseline;
        public int Run, SinceOnset;
        public bool Started;
        public void Start(float x, float noise)
        {
            X = x;
            V = 0f;
            R = noise;
            P00 = noise;
            P01 = 0f;
            P11 = noise * MediaPipeFilterMath.InitialVelocityVariance;
            Adapt = 1f;
            Surprise = Correlation = 0f;
            Run = 0;
            SinceOnset = int.MaxValue;
            Started = true;
        }
        public void Predict(float dt, float process)
        {
            float q = process * Adapt, dt2 = dt * dt;
            X += V * dt;
            P00 += dt * (2f * P01 + dt * P11) + q * dt2 * dt / 3f;
            P01 += dt * P11 + q * dt2 * 0.5f;
            P11 += q * dt;
        }
        public void Correct(float innovation, int onsetRun)
        {
            float s = Mathf.Max(P00 + R, 1e-12f), surprise = innovation / Mathf.Sqrt(s);
            if (!float.IsFinite(surprise)) return;
            Run = Mathf.Abs(surprise) > MediaPipeFilterMath.RunSurprise && surprise * Surprise > 0f ? Run + 1 : Mathf.Abs(surprise) > MediaPipeFilterMath.RunSurprise ? 1 : 0;
            float measured = X + innovation;
            if (SinceOnset < int.MaxValue) SinceOnset++;
            if (onsetRun > 1 && SinceOnset <= MediaPipeFilterMath.ReturnWindow && Mathf.Abs(surprise) > MediaPipeFilterMath.OnsetSurprise && Mathf.Abs(measured - Baseline) < MediaPipeFilterMath.ReturnSurprise * Mathf.Sqrt(R))
            {
                X = measured;
                V = 0f;
                P01 = 0f;
                SinceOnset = int.MaxValue;
                Run = 0;
                Surprise = 0f;
                return;
            }
            if (Run >= onsetRun && Mathf.Abs(surprise) > MediaPipeFilterMath.OnsetSurprise)
            {
                if (SinceOnset > MediaPipeFilterMath.ReturnWindow) Baseline = X;
                SinceOnset = 0;
                P00 *= Mathf.Min(surprise * surprise / (MediaPipeFilterMath.OnsetSurprise * MediaPipeFilterMath.OnsetSurprise), MediaPipeFilterMath.OnsetMaxInflation);
                P01 = 0f;
                s = P00 + R;
            }
            else if (onsetRun > 1 && Mathf.Abs(surprise) > MediaPipeFilterMath.SurpriseClip)
            {
                innovation = Mathf.Sign(innovation) * MediaPipeFilterMath.SurpriseClip * Mathf.Sqrt(s);
            }
            float clip = MediaPipeFilterMath.CorrelationClip;
            Correlation = Mathf.Lerp(Correlation, Mathf.Clamp(surprise, -clip, clip) * Mathf.Clamp(Surprise, -clip, clip), MediaPipeFilterMath.CorrelationRate);
            Adapt = Mathf.Clamp(Adapt * Mathf.Exp(MediaPipeFilterMath.AdaptGain * Correlation - MediaPipeFilterMath.AdaptDecay), MediaPipeFilterMath.AdaptMin, MediaPipeFilterMath.AdaptMax);
            Surprise = surprise;
            float k0 = P00 / s, k1 = P01 / s, p00 = P00, p01 = P01;
            X += k0 * innovation;
            V += k1 * innovation;
            P00 = (1f - k0) * p00;
            P01 = (1f - k0) * p01;
            P11 -= k1 * p01;
        }
        public void TrackNoise(float secondDifference, float noiseMin, float noiseMax, int samples)
        {
            float measured = secondDifference * secondDifference * MediaPipeFilterMath.NoiseFromSecondDifference;
            if (!float.IsFinite(measured)) return;
            float rate = Mathf.Clamp(1f / samples, MediaPipeFilterMath.NoiseRateFloor, MediaPipeFilterMath.NoiseRateCeiling);
            R = Mathf.Clamp(measured > R ? R * (1f + rate) : R / (1f + rate), noiseMin, noiseMax);
        }
    }
    public struct MediaPipeSpikeGateVec3
    {
        public Vector3 Last, Velocity, Pending;
        public bool HasLast, HasPending;
        public int Rejected;
        public Vector3 Apply(Vector3 x, float limit, float dt)
        {
            if (limit <= 0f || !HasLast)
            {
                Last = x;
                Velocity = Vector3.zero;
                HasLast = true;
                HasPending = false;
                return x;
            }
            dt = Mathf.Max(dt, 1e-4f);
            Vector3 predicted = Last + Velocity * dt;
            if ((x - predicted).sqrMagnitude <= limit * limit)
            {
                Velocity = (x - Last) / dt;
                Last = x;
                HasPending = false;
                return x;
            }
            if (HasPending)
            {
                Velocity = (x - Pending) / dt;
                Last = x;
                HasPending = false;
                return x;
            }
            Pending = x;
            HasPending = true;
            Rejected++;
            return Last;
        }
        public void Reset()
        {
            HasLast = false;
            HasPending = false;
            Velocity = Vector3.zero;
        }
    }
    public struct MediaPipeSpikeGateQuat
    {
        public Quaternion Last;
        public bool HasLast, HasPending;
        public int Rejected;
        public Quaternion Apply(Quaternion q, float limitDeg)
        {
            if (limitDeg <= 0f || !HasLast || HasPending || Quaternion.Angle(Last, q) <= limitDeg)
            {
                Last = q;
                HasLast = true;
                HasPending = false;
                return q;
            }
            HasPending = true;
            Rejected++;
            return Last;
        }
        public void Reset()
        {
            HasLast = false;
            HasPending = false;
        }
    }
    public struct MediaPipePositionFilter
    {
        public MediaPipeSpikeGateVec3 Gate;
        public MediaPipeAdaptiveAxis AxisX, AxisY, AxisZ;
        public Vector3 Sampled, Velocity, Offset, Carried, Last, Previous;
        public float Age;
        public int Samples;
        public bool HasSample;
        public int Rejected => Gate.Rejected;
        public Vector3 Apply(Vector3 x, in MediaPipeTiming timing, float maxSpeed)
        {
            if (timing.IsNewSample || !HasSample)
            {
                float dt = timing.SampleDelta, process = MediaPipeFilterMath.PositionProcess;
                x = Gate.Apply(x, MediaPipeFilterMath.PositionLimit(maxSpeed, dt), dt);
                MediaPipeFilterMath.Step(ref AxisX, x.x, Last.x, Previous.x, Samples, dt, process, MediaPipeFilterMath.PositionNoiseMin, MediaPipeFilterMath.PositionNoiseMax, MediaPipeFilterMath.OnsetRun);
                MediaPipeFilterMath.Step(ref AxisY, x.y, Last.y, Previous.y, Samples, dt, process, MediaPipeFilterMath.PositionNoiseMin, MediaPipeFilterMath.PositionNoiseMax, MediaPipeFilterMath.OnsetRun);
                MediaPipeFilterMath.Step(ref AxisZ, x.z, Last.z, Previous.z, Samples, dt, process, MediaPipeFilterMath.PositionNoiseMin, MediaPipeFilterMath.PositionNoiseMax, MediaPipeFilterMath.DepthOnsetRun);
                Previous = Samples > 0 ? Last : x;
                Last = x;
                Samples++;
                Vector3 estimate = new Vector3(AxisX.X, AxisY.X, AxisZ.X);
                Velocity = new Vector3(AxisX.V, AxisY.V, AxisZ.V);
                Offset = HasSample ? Carried - estimate : Vector3.zero;
                Sampled = estimate;
                Age = 0f;
                if (!HasSample)
                {
                    Carried = estimate;
                    HasSample = true;
                    return Carried;
                }
            }
            return Carry(in timing);
        }
        public Vector3 Carry(in MediaPipeTiming timing)
        {
            if (!HasSample) return Vector3.zero;
            Age += timing.RenderDelta;
            Carried = Sampled + Velocity * MediaPipeFilterMath.Lead(Age, timing.SampleDelta) + Offset * MediaPipeFilterMath.OffsetDecay(Age, timing.SampleDelta);
            return Carried;
        }
        public void Reset()
        {
            Gate.Reset();
            AxisX = AxisY = AxisZ = default;
            Samples = 0;
            HasSample = false;
        }
    }
    public struct MediaPipeRotationFilter
    {
        public MediaPipeSpikeGateQuat Gate;
        public MediaPipeAdaptiveAxis AxisX, AxisY, AxisZ;
        public Quaternion Sampled, Offset, Carried, Last, Previous;
        public Vector3 Velocity;
        public float Age;
        public int Samples;
        public bool HasSample;
        public int Rejected => Gate.Rejected;
        public Quaternion Apply(Quaternion q, in MediaPipeTiming timing, float maxDegPerSec)
        {
            if (timing.IsNewSample || !HasSample)
            {
                float dt = timing.SampleDelta;
                q = MediaPipeFilterMath.Normalize(Gate.Apply(q, MediaPipeFilterMath.TurnLimit(maxDegPerSec, dt)));
                if (!HasSample)
                {
                    float start = Mathf.Sqrt(MediaPipeFilterMath.RotationNoiseMin * MediaPipeFilterMath.RotationNoiseMax);
                    AxisX.Start(0f, start);
                    AxisY.Start(0f, start);
                    AxisZ.Start(0f, start);
                    Sampled = Carried = Last = Previous = q;
                    Offset = Quaternion.identity;
                    Velocity = Vector3.zero;
                    Samples = 1;
                    Age = 0f;
                    HasSample = true;
                    return Carried;
                }
                if (Samples >= 2)
                {
                    Vector3 secondDifference = MediaPipeFilterMath.Log(MediaPipeFilterMath.Conjugate(Last) * q) - MediaPipeFilterMath.Log(MediaPipeFilterMath.Conjugate(Previous) * Last);
                    AxisX.TrackNoise(secondDifference.x, MediaPipeFilterMath.RotationNoiseMin, MediaPipeFilterMath.RotationNoiseMax, Samples);
                    AxisY.TrackNoise(secondDifference.y, MediaPipeFilterMath.RotationNoiseMin, MediaPipeFilterMath.RotationNoiseMax, Samples);
                    AxisZ.TrackNoise(secondDifference.z, MediaPipeFilterMath.RotationNoiseMin, MediaPipeFilterMath.RotationNoiseMax, Samples);
                }
                AxisX.X = AxisY.X = AxisZ.X = 0f;
                AxisX.Predict(dt, MediaPipeFilterMath.RotationProcess);
                AxisY.Predict(dt, MediaPipeFilterMath.RotationProcess);
                AxisZ.Predict(dt, MediaPipeFilterMath.RotationProcess);
                Vector3 innovation = MediaPipeFilterMath.Log(MediaPipeFilterMath.Conjugate(Sampled * MediaPipeFilterMath.Exp(new Vector3(AxisX.X, AxisY.X, AxisZ.X))) * q);
                AxisX.Correct(innovation.x, MediaPipeFilterMath.OnsetRun);
                AxisY.Correct(innovation.y, MediaPipeFilterMath.OnsetRun);
                AxisZ.Correct(innovation.z, MediaPipeFilterMath.OnsetRun);
                Quaternion estimate = MediaPipeFilterMath.Normalize(Sampled * MediaPipeFilterMath.Exp(new Vector3(AxisX.X, AxisY.X, AxisZ.X)));
                Previous = Last;
                Last = q;
                Samples++;
                Velocity = new Vector3(AxisX.V, AxisY.V, AxisZ.V);
                Offset = MediaPipeFilterMath.Normalize(MediaPipeFilterMath.Conjugate(estimate) * Carried);
                Sampled = estimate;
                Age = 0f;
            }
            return Carry(in timing);
        }
        public Quaternion Carry(in MediaPipeTiming timing)
        {
            if (!HasSample) return Quaternion.identity;
            Age += timing.RenderDelta;
            Carried = MediaPipeFilterMath.Normalize(Sampled * MediaPipeFilterMath.Exp(Velocity * MediaPipeFilterMath.Lead(Age, timing.SampleDelta)) * MediaPipeFilterMath.Power(Offset, MediaPipeFilterMath.OffsetDecay(Age, timing.SampleDelta)));
            return Carried;
        }
        public void Reset()
        {
            Gate.Reset();
            AxisX = AxisY = AxisZ = default;
            Samples = 0;
            HasSample = false;
        }
    }
    public struct MediaPipeScalarFilter
    {
        public MediaPipeAdaptiveAxis Axis;
        public float Sampled, Offset, Carried, Last, Previous, Age;
        public int Samples;
        public bool HasSample;
        public float Apply(float x, in MediaPipeTiming timing)
        {
            if (timing.IsNewSample || !HasSample)
            {
                MediaPipeFilterMath.Step(ref Axis, x, Last, Previous, Samples, timing.SampleDelta, MediaPipeFilterMath.ScalarProcess, MediaPipeFilterMath.ScalarNoiseMin, MediaPipeFilterMath.ScalarNoiseMax, MediaPipeFilterMath.ScalarOnsetRun);
                Previous = Samples > 0 ? Last : x;
                Last = x;
                Samples++;
                Offset = HasSample ? Carried - Axis.X : 0f;
                Sampled = Axis.X;
                Age = 0f;
                if (!HasSample)
                {
                    Carried = Sampled;
                    HasSample = true;
                    return Carried;
                }
            }
            return Carry(in timing);
        }
        public float Carry(in MediaPipeTiming timing)
        {
            if (!HasSample) return 0f;
            Age += timing.RenderDelta;
            Carried = Sampled + Offset * MediaPipeFilterMath.OffsetDecay(Age, timing.SampleDelta);
            return Carried;
        }
        public float Relax(float target, float alpha)
        {
            Carried = HasSample ? Mathf.Lerp(Carried, target, alpha) : target;
            if (!Axis.Started) Axis.Start(Carried, Mathf.Sqrt(MediaPipeFilterMath.ScalarNoiseMin * MediaPipeFilterMath.ScalarNoiseMax));
            Axis.X = Sampled = Last = Previous = Carried;
            Axis.V = Offset = 0f;
            Samples = Mathf.Max(Samples, 1);
            HasSample = true;
            return Carried;
        }
        public void Reset()
        {
            Axis = default;
            Samples = 0;
            HasSample = false;
        }
    }
    public struct MediaPipePresenceFade
    {
        public float Weight, Lost;
        public bool Started;
        public float Step(bool present, float dt, float holdSeconds, float inHz, float outHz)
        {
            dt = Mathf.Max(dt, 1e-4f);
            if (present)
            {
                Lost = 0f;
                Weight = Started ? Mathf.Lerp(Weight, 1f, 1f - Mathf.Exp(-inHz * dt)) : 1f;
                Started = true;
                if (Weight > 0.999f) Weight = 1f;
                return Weight;
            }
            Lost += dt;
            if (Lost > holdSeconds)
            {
                Weight = Mathf.Lerp(Weight, 0f, 1f - Mathf.Exp(-outHz * dt));
                if (Weight < 0.001f) Weight = 0f;
            }
            return Weight;
        }
        public void Reset()
        {
            Weight = 0f;
            Lost = 0f;
            Started = false;
        }
    }
}
