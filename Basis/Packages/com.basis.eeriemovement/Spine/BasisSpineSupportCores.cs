using UnityEngine;
namespace Basis.IK
{
    public static class BasisChestSpringCore
    {
        public static void Step(Vector3 pos, Vector3 vel, Vector3 target, float dt, float hz, float damping, out Vector3 newPos, out Vector3 newVel)
        {
            float omega = 2f * Mathf.PI * hz, omegaSq = omega * omega;
            float twoOmegaDamping = 2f * omega * Mathf.Max(0f, damping);
            float denom = 1f + dt * twoOmegaDamping + dt * dt * omegaSq;
            newVel = (vel + dt * omegaSq * (target - pos)) / denom;
            newPos = pos + dt * newVel;
        }
    }
    public static class BasisHipFrameSpringCore
    {
        public static void Step(Quaternion rot, Vector3 angVel, Quaternion target, float dt, float hz, float damping, out Quaternion newRot, out Vector3 newAngVel)
        {
            Quaternion errQ = target * Quaternion.Inverse(rot);
            if (errQ.w < 0f) errQ = new Quaternion(-errQ.x, -errQ.y, -errQ.z, -errQ.w);
            Vector3 ev = new Vector3(errQ.x, errQ.y, errQ.z);
            float evLen = Mathf.Sqrt(ev.x * ev.x + ev.y * ev.y + ev.z * ev.z);
            Vector3 e;
            if (evLen > 1e-6f)
            {
                float angle = 2f * Mathf.Atan2(evLen, errQ.w);
                e = ev * (angle / evLen);
            }
            else
            {
                e = 2f * ev;
            }

            float omega = 2f * Mathf.PI * hz, omegaSq = omega * omega;
            float twoOmegaDamping = 2f * omega * Mathf.Max(0f, damping);
            float denom = 1f + dt * twoOmegaDamping + dt * dt * omegaSq;
            newAngVel = (angVel + dt * omegaSq * e) / denom;

            Vector3 dTheta = dt * newAngVel;
            float ang = Mathf.Sqrt(dTheta.x * dTheta.x + dTheta.y * dTheta.y + dTheta.z * dTheta.z);
            Quaternion step;
            if (ang > 1e-8f)
            {
                float half = 0.5f * ang, s = Mathf.Sin(half) / ang;
                step = new Quaternion(dTheta.x * s, dTheta.y * s, dTheta.z * s, Mathf.Cos(half));
            }
            else
            {
                step = Quaternion.identity;
            }

            Quaternion outRot = step * rot;
            float n = Mathf.Sqrt(outRot.x * outRot.x + outRot.y * outRot.y + outRot.z * outRot.z + outRot.w * outRot.w);
            newRot = n > 1e-8f ? new Quaternion(outRot.x / n, outRot.y / n, outRot.z / n, outRot.w / n) : Quaternion.identity;
        }
    }
    public static class BasisHeadPitchSwingCore
    {
        const float sqrEpsilon = 1e-10f;
        public static void Solve(float pitchDeg, float yawDeg, Vector3 eyeFromNeck, float strength, out Vector3 offset, out float forwardMeters)
        {
            offset = Vector3.zero;
            forwardMeters = 0f;

            float lever = eyeFromNeck.y, forwardRest = eyeFromNeck.z;

            if (!(lever * lever + forwardRest * forwardRest > sqrEpsilon)) return;
            if (!(pitchDeg > -180f && pitchDeg < 180f)) return;
            if (!(yawDeg > -720f && yawDeg < 720f)) return;
            if (!(strength > 0f)) return;

            float p = pitchDeg * Mathf.Deg2Rad, swung = lever * Mathf.Sin(p) + forwardRest * Mathf.Cos(p);
            float forward = (swung - forwardRest) * strength;

            if (!(forward > -4f && forward < 4f)) return;
            forward = Mathf.Clamp(forward, -1f, 1f);

            float yawRad = yawDeg * Mathf.Deg2Rad;
            Vector3 heading = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad));

            forwardMeters = forward;
            offset = new Vector3(heading.x * forward, 0f, heading.z * forward);
        }
    }
    public static class BasisHipHingeCore
    {
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f;
        public const float PelvisFollowSlope = 1.0f;
        public static bool Solve(Vector3 headPos, Vector3 hipsPos, Quaternion hipsRot, Vector3 playerUp, float startDeg, float maxAddDeg, out Quaternion newHipsRot, out float leanDeg, out float addDeg)
        {
            newHipsRot = hipsRot;
            leanDeg = float.NaN;
            addDeg = 0f;

            if (maxAddDeg <= 0f)
            {
                return false;
            }

            Vector3 hipsToHead = headPos - hipsPos;
            float upDot = Vector3.Dot(hipsToHead, playerUp);
            Vector3 horizontal = hipsToHead - playerUp * upDot;
            float horizMag = horizontal.magnitude;

            if (horizMag < epsilon)
            {
                return false;
            }

            leanDeg = Mathf.Atan2(horizMag, upDot) * Mathf.Rad2Deg;
            if (leanDeg <= startDeg)
            {
                return false;
            }

            float excess = (leanDeg - startDeg) * PelvisFollowSlope, capped = Saturate(excess, maxAddDeg);
            Vector3 hingeAxis = Vector3.Cross(playerUp, horizontal / horizMag);
            if (hingeAxis.sqrMagnitude < sqrEpsilon)
            {
                return false;
            }

            hingeAxis.Normalize();
            addDeg = capped;
            newHipsRot = Quaternion.AngleAxis(capped, hingeAxis) * hipsRot;
            return true;
        }
        public static float Saturate(float x, float cap) => BasisTrunkCounterbalanceCore.Saturate(x, cap);
    }
}
