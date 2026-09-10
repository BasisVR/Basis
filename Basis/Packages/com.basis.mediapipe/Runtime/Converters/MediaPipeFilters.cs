using Basis.Scripts.Drivers;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.MediaPipe
{
    public struct MediaPipeEuroFloatState
    {
        public bool hasPrev, dHasPrev;
        public float hatX, hatDx;
    }
    public static class MediaPipeFilterMath
    {
        public const float DerivativeCutoff = 1f, PositionSlack = 0.02f, TurnSlackDeg = 15f, MaxHandSpeed = 2f, MaxHeadSpeed = 1.5f, MaxTurnDegPerSec = 1000f;
        public static float EuroFloat(ref MediaPipeEuroFloatState st, float x, float dt, float minCutoff, float beta, float dCutoff)
        {
            dt = Mathf.Max(dt, 1e-6f);
            float dx = st.hasPrev ? (x - st.hatX) / dt : 0f, ad = BasisFilterMath.Alpha(dCutoff, dt);
            if (st.dHasPrev) st.hatDx = Mathf.Lerp(st.hatDx, dx, ad);
            else { st.hatDx = dx; st.dHasPrev = true; }
            float a = BasisFilterMath.Alpha(minCutoff + beta * Mathf.Abs(st.hatDx), dt);
            if (st.hasPrev) st.hatX = Mathf.Lerp(st.hatX, x, a);
            else { st.hatX = x; st.hasPrev = true; }
            return st.hatX;
        }
        public static float PositionLimit(float maxSpeed, float dt) => maxSpeed > 0f ? maxSpeed * dt + PositionSlack : 0f;
        public static float TurnLimit(float maxDegPerSec, float dt) => maxDegPerSec > 0f ? maxDegPerSec * dt + TurnSlackDeg : 0f;
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
        public BasisEuroVec3State Lateral, Depth;
        public Vector3 Sampled, Carried;
        public bool HasSample;
        public int Rejected => Gate.Rejected;
        public Vector3 Apply(Vector3 x, in MediaPipeTiming timing, float cutoff, float beta, float depthScale, float maxSpeed)
        {
            if (timing.IsNewSample || !HasSample)
            {
                float dt = timing.SampleDelta;
                x = Gate.Apply(x, MediaPipeFilterMath.PositionLimit(maxSpeed, dt), dt);
                float3 lateral = BasisFilterMath.EuroVec3(ref Lateral, new float3(x.x, x.y, 0f), dt, cutoff, beta, MediaPipeFilterMath.DerivativeCutoff);
                float3 depth = BasisFilterMath.EuroVec3(ref Depth, new float3(0f, 0f, x.z), dt, cutoff * depthScale, beta, MediaPipeFilterMath.DerivativeCutoff);
                Sampled = new Vector3(lateral.x, lateral.y, depth.z);
                if (!HasSample)
                {
                    Carried = Sampled;
                    HasSample = true;
                    return Carried;
                }
            }
            return Carry(in timing);
        }
        public Vector3 Carry(in MediaPipeTiming timing)
        {
            if (!HasSample) return Vector3.zero;
            Carried = Vector3.Lerp(Carried, Sampled, BasisFilterMath.Alpha(timing.CarryCutoff, timing.RenderDelta));
            return Carried;
        }
        public void Reset()
        {
            Gate.Reset();
            Lateral = default;
            Depth = default;
            HasSample = false;
        }
    }
    public struct MediaPipeRotationFilter
    {
        public MediaPipeSpikeGateQuat Gate;
        public BasisEuroQuatState Euro;
        public Quaternion Sampled, Carried;
        public bool HasSample;
        public int Rejected => Gate.Rejected;
        public Quaternion Apply(Quaternion q, in MediaPipeTiming timing, float cutoff, float beta, float maxDegPerSec)
        {
            if (timing.IsNewSample || !HasSample)
            {
                float dt = timing.SampleDelta;
                q = Gate.Apply(q, MediaPipeFilterMath.TurnLimit(maxDegPerSec, dt));
                Sampled = BasisFilterMath.EuroQuat(ref Euro, q, dt, cutoff, beta, MediaPipeFilterMath.DerivativeCutoff);
                if (!HasSample)
                {
                    Carried = Sampled;
                    HasSample = true;
                    return Carried;
                }
            }
            return Carry(in timing);
        }
        public Quaternion Carry(in MediaPipeTiming timing)
        {
            if (!HasSample) return Quaternion.identity;
            Carried = Quaternion.Slerp(Carried, Sampled, BasisFilterMath.Alpha(timing.CarryCutoff, timing.RenderDelta));
            return Carried;
        }
        public void Reset()
        {
            Gate.Reset();
            Euro = default;
            HasSample = false;
        }
    }
    public struct MediaPipeScalarFilter
    {
        public MediaPipeEuroFloatState Euro;
        public float Sampled, Carried;
        public bool HasSample;
        public float Apply(float x, in MediaPipeTiming timing, float cutoff, float beta)
        {
            if (timing.IsNewSample || !HasSample)
            {
                Sampled = MediaPipeFilterMath.EuroFloat(ref Euro, x, timing.SampleDelta, cutoff, beta, MediaPipeFilterMath.DerivativeCutoff);
                if (!HasSample)
                {
                    Carried = Sampled;
                    HasSample = true;
                    return Carried;
                }
            }
            Carried = Mathf.Lerp(Carried, Sampled, BasisFilterMath.Alpha(timing.CarryCutoff, timing.RenderDelta));
            return Carried;
        }
        public void Reset()
        {
            Euro = default;
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
