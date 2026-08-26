using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.IK
{
    public static class BasisSwivelFilterCore
    {
        public const float MinCutoffHz = 1.0f, DerivCutoffHz = 1.0f, Beta = 0.05f;
        public static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(cutoff, 1e-3f));
            return 1f / (1f + tau / dt);
        }
        public static BasisSwivelFilterState Seed(float curSwivel)
        {
            return new BasisSwivelFilterState { Raw = curSwivel, Vel = 0f, Smooth = curSwivel };
        }
        public static BasisSwivelFilterState Step(BasisSwivelFilterState s, float curSwivel, float dt)
        {
            return Step(s, curSwivel, dt, MinCutoffHz, Beta, DerivCutoffHz);
        }
        public static BasisSwivelFilterState Step(BasisSwivelFilterState s, float curSwivel, float dt, float minCutoffHz, float beta, float derivCutoffHz)
        {
            float vel = Mathf.DeltaAngle(s.Raw, curSwivel) / dt;
            float velHat = Mathf.Lerp(s.Vel, vel, Alpha(derivCutoffHz, dt));
            float cutoff = minCutoffHz + beta * Mathf.Abs(velHat);
            float smooth = s.Smooth + Mathf.DeltaAngle(s.Smooth, curSwivel) * Alpha(cutoff, dt);
            return new BasisSwivelFilterState { Raw = curSwivel, Vel = velHat, Smooth = smooth };
        }
    }
    [BurstCompile]
    public static class BasisSwivelHintCore
    {
        const float sqrEpsilon = 1e-10f, epsilon = 1e-5f;
        static readonly float3 elbowTuckPole = new float3(-1f, -0.35f, 0f);
        public const float ElbowTuckWeight = 0.12f;
        static readonly float3 elbowDownPole = new float3(0f, -1f, 0f);
        public const float ElbowDownWeight = 0.85f, ElbowDownReachStart = 0.90f, ElbowDownReachFull = 0.99f;
        public const float LegTrustLo = 0.30f, LegTrustHi = 0.70f, LegDomainReachLo = 0.45f, LegDomainReachHi = 0.60f;
        public static float LegDomainTrust(float reach)
        {
            float t = Mathf.Clamp01((reach - LegDomainReachLo) / (LegDomainReachHi - LegDomainReachLo));
            return t * t * (3f - 2f * t);
        }
        public static float LegModelTrust(float confidence)
        {
            float t = Mathf.Clamp01((confidence - LegTrustLo) / (LegTrustHi - LegTrustLo));
            return t * t * (3f - 2f * t);
        }
        public static BasisSwivelFrame BuildFrame(Vector3 leftAnchor, Vector3 rightAnchor, Vector3 upFrom, Vector3 upTo)
        {
            BasisSwivelFrame f = default;
            Vector3 up = upTo - upFrom;
            float upSqr = up.sqrMagnitude;

            if (!(upSqr > sqrEpsilon))
            {
                return f;
            }
            up /= Mathf.Sqrt(upSqr);

            Vector3 right = rightAnchor - leftAnchor;
            right -= up * Vector3.Dot(right, up);
            float rightSqr = right.sqrMagnitude;
            if (!(rightSqr > sqrEpsilon))
            {
                return f;
            }
            right /= Mathf.Sqrt(rightSqr);

            f.Right = right;
            f.Up = up;
            f.Forward = Vector3.Cross(right, up);
            f.Valid = true;
            return f;
        }
        public static void Features(in BasisSwivelFrame frameNow, Vector3 rootPos, Vector3 tipPos, float limbLen, bool isLeft, out float3 tipLocal)
        {
            Vector3 bOut = isLeft ? -frameNow.Right : frameNow.Right, r2t = tipPos - rootPos;
            float inv = 1f / Mathf.Max(limbLen, epsilon);
            tipLocal = new float3(Vector3.Dot(r2t, bOut) * inv, Vector3.Dot(r2t, frameNow.Up) * inv, Vector3.Dot(r2t, frameNow.Forward) * inv);
        }
        public static bool ArmHint(in BasisSwivelFrame frameNow, Vector3 shoulder, Vector3 handPos, float armLen, bool isLeft, out Vector3 hintPos, out float confidence, bool useNeural = false)
        {
            hintPos = default;
            confidence = 0f;

            if (!frameNow.Valid || !(armLen > epsilon))
            {
                return false;
            }

            Features(frameNow, shoulder, handPos, armLen, isLeft, out float3 tipLocal);

            if (!IsFinite(tipLocal))
            {
                return false;
            }

            float3 elbowLocal = useNeural ? BasisArmElbowNeuralFieldModel.Elbow(tipLocal) : BasisElbowFieldModel.Elbow(tipLocal);
            float3 bend = BasisElbowFieldModel.BendDirection(tipLocal, elbowLocal, out confidence);

            if (!IsFinite(bend))
            {
                return false;
            }

            float3 tuckAxis = math.normalizesafe(tipLocal, new float3(0f, -1f, 0f));
            float3 tuckPerp = elbowTuckPole - tuckAxis * math.dot(elbowTuckPole, tuckAxis);
            bend = math.normalizesafe(bend + ElbowTuckWeight * tuckPerp, bend);

            float reachRatio = math.length(tipLocal);
            float downT = math.saturate((reachRatio - ElbowDownReachStart) / (ElbowDownReachFull - ElbowDownReachStart));
            float downW = ElbowDownWeight * (downT * downT * (3f - 2f * downT));
            if (downW > 0f)
            {
                float3 downPerp = elbowDownPole - tuckAxis * math.dot(elbowDownPole, tuckAxis);
                bend = math.normalizesafe(bend + downW * downPerp, bend);
            }

            Vector3 bOut = isLeft ? -frameNow.Right : frameNow.Right;
            Vector3 bendWorld = bend.x * bOut + bend.y * frameNow.Up + bend.z * frameNow.Forward;

            hintPos = shoulder + 0.5f * armLen * bendWorld;
            return true;
        }
        public static bool LegHint(in BasisSwivelFrame frameNow, Vector3 hip, Vector3 footPos, float legLen, bool isLeft, out Vector3 hintPos, out float confidence, bool useNeural = false)
        {
            hintPos = default;
            confidence = 0f;

            if (!frameNow.Valid || !(legLen > epsilon))
            {
                return false;
            }

            Features(frameNow, hip, footPos, legLen, isLeft, out float3 tipLocal);

            if (!IsFinite(tipLocal))
            {
                return false;
            }

            float swivel = useNeural ? BasisLegSwivelNeuralModel.SwivelRad(tipLocal, out confidence) : BasisLegSwivelModel.SwivelRad(tipLocal, out confidence);

            confidence *= LegDomainTrust(math.length(tipLocal));

            if (isLeft)
            {
                swivel = -swivel;
            }

            Vector3 gOut = isLeft ? -frameNow.Right : frameNow.Right, h2f = footPos - hip;
            float3 bend = BasisLegSwivelModel.BendDirection( new float3(h2f.x, h2f.y, h2f.z), new float3(gOut.x, gOut.y, gOut.z), swivel);

            if (!IsFinite(bend))
            {
                return false;
            }

            hintPos = hip + 0.5f * legLen * new Vector3(bend.x, bend.y, bend.z);
            return true;
        }
        static bool IsFinite(in float3 v) => math.all(math.isfinite(v));
    }
    public static class BasisSwivelSmootherCore
    {
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f;
        public const float DefaultHoldCondLo = 0.05f, DefaultHoldCondHi = 0.12f;
        const float holdReseedDeg = 25f;
        /// <summary>Convenience overload for callers with nothing to draw into.</summary>
        public static void Solve(in BasisSwivelSmootherInput i, out BasisSwivelSmootherResult r)
        {
            BasisIKGizmoRecorder gizmos = default;
            Solve(i, out r, ref gizmos);
        }
        public static void Solve(in BasisSwivelSmootherInput i, out BasisSwivelSmootherResult r, ref BasisIKGizmoRecorder gizmos)
        {
            r = default;
            r.DesiredMid = i.Mid;
            r.State = i.State;
            r.Seeded = i.Seeded;

            if (i.Dt <= 1e-6f)
            {
                return;
            }

            Quaternion body = i.BodyRotation;
            if (body.x * body.x + body.y * body.y + body.z * body.z + body.w * body.w < 0.5f)
            {
                return;
            }

            Vector3 ac = i.Tip - i.Root;
            float acSqr = ac.sqrMagnitude;
            if (acSqr < sqrEpsilon)
            {
                return;
            }
            Vector3 axis = ac / Mathf.Sqrt(acSqr), refDir = Vector3.zero;
            bool transported = false;
            if (i.TransportHomeLocal.sqrMagnitude > sqrEpsilon)
            {
                Vector3 home = body * i.TransportHomeLocal;
                float homeSqr = home.sqrMagnitude;
                if (homeSqr > sqrEpsilon)
                {
                    home /= Mathf.Sqrt(homeSqr);

                    Vector3 swingXyz = Vector3.Cross(home, axis);
                    float swingW = 1f + Vector3.Dot(home, axis), swingSqr = swingXyz.sqrMagnitude + swingW * swingW;
                    if (swingW > 1e-4f && swingSqr > sqrEpsilon)
                    {
                        float inv = 1f / Mathf.Sqrt(swingSqr);
                        Quaternion swing = new Quaternion(swingXyz.x * inv, swingXyz.y * inv, swingXyz.z * inv, swingW * inv);
                        refDir = swing * (body * i.ReferenceLocal);
                        refDir -= axis * Vector3.Dot(refDir, axis);
                        transported = refDir.sqrMagnitude > sqrEpsilon;
                    }
                }
            }

            if (!transported)
            {
                refDir = Vector3.ProjectOnPlane(body * i.ReferenceLocal, axis);
                if (refDir.sqrMagnitude < sqrEpsilon && i.FallbackLocal.sqrMagnitude > sqrEpsilon)
                {
                    refDir = Vector3.ProjectOnPlane(body * i.FallbackLocal, axis);
                }
            }
            Vector3 upper = i.Mid - i.Root, pole = Vector3.ProjectOnPlane(upper, axis);
            if (refDir.sqrMagnitude < sqrEpsilon || pole.sqrMagnitude < sqrEpsilon)
            {
                return;
            }
            refDir.Normalize();

            float upperLen = upper.magnitude;
            float conditioning = upperLen > epsilon ? Mathf.Clamp01(pole.magnitude / upperLen) : 0f;
            r.Conditioning = conditioning;

            float curSwivel = Vector3.SignedAngle(refDir, pole, axis);
            r.RawSwivelDeg = curSwivel;

            float guardedSwivel = curSwivel;
            if (i.GuardAnteriorHalfSpace)
            {
                guardedSwivel = BasisLegSolveCore.ClampKneeSwivelDeg(curSwivel, i.AnteriorSoftDeg, i.AnteriorHardDeg);
                r.AnteriorGuardApplied = guardedSwivel != curSwivel;
            }

            if (!i.Seeded)
            {
                bool deferSeedWhileSingular = i.HoldWhenSingular && conditioning < i.HoldCondHi;
                r.State = BasisSwivelFilterCore.Seed(guardedSwivel);
                r.Seeded = !deferSeedWhileSingular;
                r.WriteState = !deferSeedWhileSingular;
                r.SmoothSwivelDeg = guardedSwivel;
                r.HoldGate = 1f;
                return;
            }

            float minCutoffHz = i.MinCutoffHz, beta = i.Beta;
            if (i.ConditionOnPole)
            {
                beta *= conditioning;
                minCutoffHz = Mathf.Lerp(i.SingularMinCutoffHz, i.MinCutoffHz, conditioning);
            }

            BasisSwivelFilterState state = BasisSwivelFilterCore.Step(i.State, guardedSwivel, i.Dt, minCutoffHz, beta, i.DerivCutoffHz);
            float holdGate = 1f;
            if (i.HoldWhenSingular)
            {
                if (Mathf.Abs(Mathf.DeltaAngle(i.State.Smooth, guardedSwivel)) > holdReseedDeg)
                {
                    state = BasisSwivelFilterCore.Seed(guardedSwivel);
                }
                else
                {
                    holdGate = Smoothstep(i.HoldCondLo, i.HoldCondHi, conditioning);
                    float innovation = Mathf.DeltaAngle(i.State.Smooth, state.Smooth);
                    state.Smooth = i.State.Smooth + innovation * holdGate;
                }
            }
            r.HoldGate = holdGate;
            r.State = state;
            r.Seeded = true;
            r.WriteState = true;

            float outSwivel = state.Smooth;
            if (i.GuardAnteriorHalfSpace)
            {
                outSwivel = BasisLegSolveCore.ClampKneeSwivelDeg(outSwivel, i.AnteriorSoftDeg, i.AnteriorHardDeg);
            }
            r.SmoothSwivelDeg = outSwivel;

            Vector3 center = i.Root + axis * Vector3.Dot(i.Mid - i.Root, axis);
            float radius = (i.Mid - center).magnitude;
            if (radius < epsilon)
            {
                return;
            }

            r.DesiredMid = center + (Quaternion.AngleAxis(outSwivel, axis) * refDir) * radius;
            r.Valid = true;
        }
        static float Smoothstep(float a, float b, float v)
        {
            if (b <= a) return v >= b ? 1f : 0f;
            float t = (v - a) / (b - a);
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            return t * t * (3f - 2f * t);
        }
    }
}
