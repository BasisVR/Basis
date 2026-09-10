using Unity.Burst;
using UnityEngine;
namespace Basis.IK
{
    [BurstCompile]
    public static class BasisArmSolveCore
    {
        public const int Samples = 36, RefineIterations = 8;
        public const float SampleStepDeg = 360f / Samples, MinElbowInteriorDeg = 22f, LimitMarginDeg = 12f, HardLimitWeight = 0.006f;
        public const float HumeralWeight = 0.6f, PronationWeight = 0.6f, WristFlexWeight = 0.35f, WristDevWeight = 0.35f, WristStrainWeight = 0.12f;
        public const float TorsoWeight = 1.5f, TrackerPriorWeight = 4f, SwitchMarginCost = 0.12f, LocalBasinDeg = 60f, BasinJumpDeg = 100f;
        public const float HeadFadeStartSin = 0.15f, HeadFadeFullSin = 0.45f, ElevationFadeStart = 0.85f, ElevationFadeFull = 0.97f, RestOutward = 0.35f, RestBack = 0.25f;
        public const float TeleportFraction = 0.6f, TrackerSmoothTime = 0.015f, MinReachFraction = 0.05f, TrackerLimitScale = 0.15f, ModelWeight = 0.85f;
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f;
        public static void Frame(Vector3 axis, Vector3 torsoUp, Vector3 torsoForward, Vector3 torsoOut, out Vector3 ex, out Vector3 ey)
        {
            Vector3 et = Vector3.Normalize(-torsoOut * 0.45f - torsoUp * 0.6f - torsoForward * 0.65f);
            Vector3 er = -torsoUp - et * Vector3.Dot(-torsoUp, et);
            float erSqr = er.sqrMagnitude;
            er = erSqr > sqrEpsilon ? er / Mathf.Sqrt(erSqr) : Vector3.Cross(et, torsoForward).normalized;
            Vector3 krt = Vector3.Cross(axis - et, er), kx = Vector3.Cross(krt, axis);
            float kxSqr = kx.sqrMagnitude;
            if (kxSqr > sqrEpsilon)
            {
                ex = kx / Mathf.Sqrt(kxSqr);
            }
            else
            {
                ex = er - axis * Vector3.Dot(er, axis);
                if (ex.sqrMagnitude < sqrEpsilon) ex = torsoForward - axis * Vector3.Dot(torsoForward, axis);
                ex = ex.normalized;
            }
            ey = Vector3.Cross(axis, ex);
        }
        public static float DirToDeg(Vector3 dir, Vector3 ex, Vector3 ey) => Mathf.Atan2(Vector3.Dot(dir, ey), Vector3.Dot(dir, ex)) * Mathf.Rad2Deg;
        public static Vector3 DegToDir(float deg, Vector3 ex, Vector3 ey)
        {
            float r = deg * Mathf.Deg2Rad;
            return ex * Mathf.Cos(r) + ey * Mathf.Sin(r);
        }
        public static float Wrap(float deg) => deg - 360f * Mathf.Floor((deg + 180f) / 360f);
        public static float SoftReach(float d, float upper, float lower, float softness)
        {
            float full = upper + lower, s = Mathf.Clamp(softness, 0.005f, 0.3f), start = full * (1f - s);
            if (d <= start) return d;
            return full * (1f - s * Mathf.Exp(-(d - start) / (s * full)));
        }
        public static float MinReach(float upper, float lower)
        {
            float c = Mathf.Cos(MinElbowInteriorDeg * Mathf.Deg2Rad), d2 = upper * upper + lower * lower - 2f * upper * lower * c;
            return Mathf.Max(Mathf.Max(d2 > 0f ? Mathf.Sqrt(d2) : 0f, Mathf.Abs(upper - lower) + epsilon), MinReachFraction * (upper + lower));
        }
        public static Vector3 Hinge(Vector3 elbowDir, Vector3 axis, float side) => Vector3.Cross(elbowDir, axis) * side;
        static float LimitCost(float x, float lo, float hi, float weight)
        {
            float soft = Mathf.Max(lo + LimitMarginDeg - x, x - (hi - LimitMarginDeg));
            float cost = soft > 0f ? weight * (soft / LimitMarginDeg) * (soft / LimitMarginDeg) : 0f, excess = Mathf.Max(lo - x, x - hi);
            return excess > 0f ? cost + HardLimitWeight * excess * excess : cost;
        }
        static float Smoothstep(float a, float b, float v)
        {
            float t = b > a ? Mathf.Clamp01((v - a) / (b - a)) : (v >= b ? 1f : 0f);
            return t * t * (3f - 2f * t);
        }
        static Vector3 Swing(Vector3 from, Vector3 to, Vector3 v) => to.sqrMagnitude < sqrEpsilon ? v : BasisQuaternionExt.FromToRotation(from, to) * v;
        public static unsafe void Solve(in BasisArmSolveInput i, ref BasisArmState state, out BasisArmSolveResult r)
        {
            r = default;
            float upper = (i.RestElbow - i.Shoulder).magnitude, lower = (i.RestHand - i.RestElbow).magnitude;
            r.UpperLength = upper;
            r.LowerLength = lower;
            if (upper <= epsilon || lower <= epsilon)
            {
                return;
            }
            Vector3 toTarget = i.TargetPosition - i.Shoulder;
            float d = toTarget.magnitude, minReach = MinReach(upper, lower);
            Vector3 axis;
            if (d > minReach)
            {
                axis = toTarget / d;
            }
            else
            {
                axis = state.Seeded && state.LastAxis.sqrMagnitude > sqrEpsilon ? state.LastAxis : (d > epsilon ? toTarget / d : (i.RestHand - i.Shoulder).normalized);
            }
            float dEff = Mathf.Max(SoftReach(d, upper, lower, i.ReachSoftness), minReach);
            r.TargetDistance = d;
            r.EffectiveDistance = dEff;
            r.ReachRatio = d / (upper + lower);
            float cosAlpha = Mathf.Clamp((upper * upper + dEff * dEff - lower * lower) / (2f * upper * dEff), -1f, 1f), sinAlpha = Mathf.Sqrt(Mathf.Max(0f, 1f - cosAlpha * cosAlpha));
            Vector3 center = i.Shoulder + axis * (upper * cosAlpha);
            float radius = upper * sinAlpha;
            r.ElbowDeg = Mathf.Acos(Mathf.Clamp((upper * upper + lower * lower - dEff * dEff) / (2f * upper * lower), -1f, 1f)) * Mathf.Rad2Deg;
            Frame(axis, i.TorsoUp, i.TorsoForward, i.TorsoOut, out Vector3 ex, out Vector3 ey);
            float side = i.IsLeft ? 1f : -1f;
            bool tracker = i.HasHint;
            float priorDeg, priorWeight = i.PriorWeight;
            if (tracker)
            {
                Vector3 hintDir = i.HintPosition - center;
                hintDir -= axis * Vector3.Dot(hintDir, axis);
                if (hintDir.sqrMagnitude > sqrEpsilon)
                {
                    priorDeg = DirToDeg(hintDir.normalized, ex, ey);
                    priorWeight = TrackerPriorWeight;
                }
                else
                {
                    tracker = false;
                    priorDeg = BodyPrior(i, axis, ex, ey);
                }
            }
            else
            {
                priorDeg = BodyPrior(i, axis, ex, ey);
            }
            r.PriorDeg = priorDeg;
            Quaternion restHandInv = Quaternion.Inverse(i.RestHandRotation);
            Vector3 palmLocal = restHandInv * Swing(i.TorsoOut, (i.RestElbow - i.Shoulder).normalized, -i.TorsoUp), fwdLocal = restHandInv * (i.RestHand - i.RestElbow).normalized;
            Vector3 palm = i.TargetRotation * palmLocal, handFwd = i.TargetRotation * fwdLocal;
            float humeralFade = 1f - Smoothstep(ElevationFadeStart, ElevationFadeFull, Vector3.Dot(axis, i.TorsoUp));
            bool limits = i.JointLimits;
            float prevDeg = state.Seeded ? state.SwivelDeg : priorDeg, prevWeight = state.Seeded ? i.PreviousWeight : 0f, limitScale = tracker ? TrackerLimitScale : 1f;
            float bestCost = float.MaxValue, localCost = float.MaxValue;
            int best = 0, local = -1;
            float* costs = stackalloc float[Samples];
            for (int k = 0; k < Samples; k++)
            {
                float psi = k * SampleStepDeg - 180f;
                Vector3 dir = DegToDir(psi, ex, ey), elbow = center + dir * radius;
                float cost = priorWeight * (1f - Mathf.Cos((psi - priorDeg) * Mathf.Deg2Rad)) + prevWeight * (1f - Mathf.Cos((psi - prevDeg) * Mathf.Deg2Rad));
                cost += limitScale * PoseCost(i, elbow, dir, axis, side, palm, handFwd, humeralFade, limits);
                costs[k] = cost;
                if (cost < bestCost) { bestCost = cost; best = k; }
                if (state.Seeded && Mathf.Abs(Wrap(psi - state.SwivelDeg)) <= LocalBasinDeg && cost < localCost) { localCost = cost; local = k; }
            }
            int chosen = best;
            bool switched = false;
            if (state.Seeded && local >= 0 && !tracker && Mathf.Abs(Wrap(best * SampleStepDeg - 180f - state.SwivelDeg)) > BasinJumpDeg)
            {
                if (localCost - bestCost > SwitchMarginCost)
                {
                    state.SwitchTimer += i.Dt;
                    if (state.SwitchTimer >= i.SwitchDwell) { state.SwitchTimer = 0f; switched = true; }
                    else chosen = local;
                }
                else
                {
                    state.SwitchTimer = 0f;
                    chosen = local;
                }
            }
            else
            {
                state.SwitchTimer = 0f;
            }
            float lo = chosen * SampleStepDeg - 180f - SampleStepDeg, hi = lo + 2f * SampleStepDeg, invPhi = 0.6180339887f;
            float x1 = hi - invPhi * (hi - lo), x2 = lo + invPhi * (hi - lo);
            float f1 = SwivelCost(i, center, radius, ex, ey, axis, side, palm, handFwd, humeralFade, limits, limitScale, priorDeg, priorWeight, prevDeg, prevWeight, x1);
            float f2 = SwivelCost(i, center, radius, ex, ey, axis, side, palm, handFwd, humeralFade, limits, limitScale, priorDeg, priorWeight, prevDeg, prevWeight, x2);
            for (int it = 0; it < RefineIterations; it++)
            {
                if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - invPhi * (hi - lo); f1 = SwivelCost(i, center, radius, ex, ey, axis, side, palm, handFwd, humeralFade, limits, limitScale, priorDeg, priorWeight, prevDeg, prevWeight, x1); }
                else { lo = x1; x1 = x2; f1 = f2; x2 = lo + invPhi * (hi - lo); f2 = SwivelCost(i, center, radius, ex, ey, axis, side, palm, handFwd, humeralFade, limits, limitScale, priorDeg, priorWeight, prevDeg, prevWeight, x2); }
            }
            float target = Wrap(f1 < f2 ? x1 : x2);
            r.RawDeg = target;
            float chainLen = upper + lower;
            bool teleport = state.Seeded && (i.TargetPosition - state.LastTarget).sqrMagnitude > TeleportFraction * TeleportFraction * chainLen * chainLen;
            float smoothTime = tracker ? TrackerSmoothTime : i.SmoothTime;
            if (!state.Seeded || teleport || i.Dt <= 0f)
            {
                state.SwivelDeg = target;
            }
            else
            {
                float delta = Wrap(target - state.SwivelDeg), alpha = smoothTime > 1e-4f ? 1f - Mathf.Exp(-i.Dt / smoothTime) : 1f, step = delta * alpha;
                float maxStep = i.MaxRateDeg > 0f ? i.MaxRateDeg * i.Dt : float.MaxValue;
                if (step > maxStep) step = maxStep; else if (step < -maxStep) step = -maxStep;
                state.SwivelDeg = Wrap(state.SwivelDeg + step);
            }
            state.Seeded = true;
            state.LastTarget = i.TargetPosition;
            state.LastAxis = axis;
            state.Switched = switched;
            Vector3 finalDir = DegToDir(state.SwivelDeg, ex, ey), finalElbow = center + finalDir * radius;
            Joints(i, finalElbow, finalDir, axis, side, palm, handFwd, out r.HumeralDeg, out r.PronationDeg, out r.WristFlexDeg, out r.WristDevDeg);
            r.SwivelDeg = state.SwivelDeg;
            r.Cost = costs[chosen];
            r.Elbow = finalElbow;
            r.Hand = i.Shoulder + axis * dEff;
            r.Axis = axis;
            r.ElbowDir = finalDir;
            r.Hinge = Hinge(finalDir, axis, side);
            r.Switched = switched;
            r.Valid = true;
            state.PriorDeg = priorDeg;
            state.PriorDir = DegToDir(priorDeg, ex, ey);
            state.ElbowDir = finalDir;
            state.RawDeg = target;
            state.ReachRatio = r.ReachRatio;
            state.ElbowDeg = r.ElbowDeg;
            state.HumeralDeg = r.HumeralDeg;
            state.PronationDeg = r.PronationDeg;
            state.WristFlexDeg = r.WristFlexDeg;
            state.WristDevDeg = r.WristDevDeg;
            state.Cost = r.Cost;
        }
        static float SwivelCost(in BasisArmSolveInput i, Vector3 center, float radius, Vector3 ex, Vector3 ey, Vector3 axis, float side, Vector3 palm, Vector3 handFwd, float humeralFade, bool limits, float limitScale, float priorDeg, float priorWeight, float prevDeg, float prevWeight, float psi)
        {
            Vector3 dir = DegToDir(psi, ex, ey);
            float cost = priorWeight * (1f - Mathf.Cos((psi - priorDeg) * Mathf.Deg2Rad)) + prevWeight * (1f - Mathf.Cos((psi - prevDeg) * Mathf.Deg2Rad));
            return cost + limitScale * PoseCost(i, center + dir * radius, dir, axis, side, palm, handFwd, humeralFade, limits);
        }
        static float PoseCost(in BasisArmSolveInput i, Vector3 elbow, Vector3 dir, Vector3 axis, float side, Vector3 palm, Vector3 handFwd, float humeralFade, bool limits)
        {
            float cost = 0f;
            if (limits)
            {
                Joints(i, elbow, dir, axis, side, palm, handFwd, out float humeral, out float pronation, out float flex, out float dev);
                cost += humeralFade * LimitCost(humeral, -i.Limits.HumeralInternalMaxDeg, i.Limits.HumeralExternalMaxDeg, HumeralWeight);
                cost += LimitCost(pronation, -i.Limits.SupinationMaxDeg, i.Limits.PronationMaxDeg, PronationWeight);
                cost += LimitCost(flex, -i.Limits.WristExtensionMaxDeg, i.Limits.WristFlexionMaxDeg, WristFlexWeight);
                cost += LimitCost(dev, -i.Limits.WristUlnarMaxDeg, i.Limits.WristRadialMaxDeg, WristDevWeight);
                float nf = flex / Mathf.Max(i.Limits.WristFlexionMaxDeg, 1f), nd = dev / Mathf.Max(i.Limits.WristRadialMaxDeg, 1f), np = pronation / 90f;
                cost += WristStrainWeight * (nf * nf + nd * nd + np * np);
            }
            if (i.TorsoCapsule)
            {
                cost += TorsoCost(elbow, i.TorsoA, i.TorsoB, i.TorsoRadius);
            }
            return cost;
        }
        static void Joints(in BasisArmSolveInput i, Vector3 elbow, Vector3 dir, Vector3 axis, float side, Vector3 palm, Vector3 handFwd, out float humeralDeg, out float pronationDeg, out float flexDeg, out float devDeg)
        {
            Vector3 u = (elbow - i.Shoulder).normalized, w = i.TargetPosition - elbow;
            float wSqr = w.sqrMagnitude;
            w = wSqr > sqrEpsilon ? w / Mathf.Sqrt(wSqr) : axis;
            Vector3 h = Hinge(dir, axis, side), hSwing = Swing(-i.TorsoUp, u, i.TorsoOut);
            hSwing -= u * Vector3.Dot(hSwing, u);
            humeralDeg = hSwing.sqrMagnitude > sqrEpsilon ? Vector3.SignedAngle(hSwing.normalized, h, u) * side : 0f;
            Vector3 p = palm - w * Vector3.Dot(palm, w);
            Vector3 pN = p.sqrMagnitude > sqrEpsilon ? p.normalized : -h, thumb = Vector3.Cross(w, pN) * side;
            pronationDeg = Vector3.SignedAngle(-h, pN, w) * -side;
            float along = Vector3.Dot(handFwd, w);
            flexDeg = Mathf.Atan2(Vector3.Dot(handFwd, pN), along) * Mathf.Rad2Deg;
            devDeg = Mathf.Atan2(Vector3.Dot(handFwd, thumb), along) * Mathf.Rad2Deg;
        }
        static float RestPrior(in BasisArmSolveInput i, Vector3 axis, Vector3 ex, Vector3 ey)
        {
            Vector3 rest = i.TorsoOut * RestOutward - i.TorsoForward * RestBack - i.TorsoUp;
            rest -= axis * Vector3.Dot(rest, axis);
            if (rest.sqrMagnitude < sqrEpsilon)
            {
                rest = i.TorsoOut - axis * Vector3.Dot(i.TorsoOut, axis);
            }
            return DirToDeg(rest.normalized, ex, ey);
        }
        static float BodyPrior(in BasisArmSolveInput i, Vector3 axis, Vector3 ex, Vector3 ey)
        {
            float restDeg = RestPrior(i, axis, ex, ey);
            Vector3 blend = DegToDir(restDeg, ex, ey);
            if (i.HasHead)
            {
                Vector3 f = i.TargetPosition - i.HeadPosition;
                float fLen = f.magnitude;
                if (fLen > epsilon)
                {
                    Vector3 fp = f - axis * Vector3.Dot(f, axis);
                    float sin = fp.magnitude / fLen, w = Smoothstep(HeadFadeStartSin, HeadFadeFullSin, sin);
                    if (w > 0f)
                    {
                        Vector3 mix = fp / (sin * fLen) * w + blend * (1f - w);
                        if (mix.sqrMagnitude > sqrEpsilon) blend = mix.normalized;
                    }
                }
            }
            float chain = (i.RestElbow - i.Shoulder).magnitude + (i.RestHand - i.RestElbow).magnitude;
            Vector3 toHand = (i.TargetPosition - i.Shoulder) / Mathf.Max(chain, epsilon);
            Vector3 m = BasisArmPriorModel.ElbowDir(new Vector3(Vector3.Dot(toHand, i.TorsoOut), Vector3.Dot(toHand, i.TorsoUp), Vector3.Dot(toHand, i.TorsoForward)));
            Vector3 model = i.TorsoOut * m.x + i.TorsoUp * m.y + i.TorsoForward * m.z;
            model -= axis * Vector3.Dot(model, axis);
            if (model.sqrMagnitude > sqrEpsilon)
            {
                Vector3 mix = model.normalized * ModelWeight + blend * (1f - ModelWeight);
                if (mix.sqrMagnitude > sqrEpsilon) blend = mix.normalized;
            }
            return DirToDeg(blend, ex, ey);
        }
        static float TorsoCost(Vector3 elbow, Vector3 a, Vector3 b, float radius)
        {
            Vector3 ab = b - a;
            float abSqr = ab.sqrMagnitude, t = abSqr > sqrEpsilon ? Mathf.Clamp01(Vector3.Dot(elbow - a, ab) / abSqr) : 0f;
            Vector3 q = a + ab * t;
            float dist = (elbow - q).magnitude, margin = radius * 1.5f;
            if (dist >= margin)
            {
                return 0f;
            }
            float soft = (margin - dist) / Mathf.Max(margin - radius, epsilon), cost = TorsoWeight * soft * soft;
            if (dist < radius)
            {
                float pen = (radius - dist) / Mathf.Max(radius, epsilon);
                cost += TorsoWeight * 8f * pen * pen;
            }
            return cost;
        }
        public static void Pose(in BasisArmSolveInput i, in BasisArmSolveResult r, Quaternion restUpperRot, Quaternion restLowerRot, out Quaternion upperRot, out Quaternion lowerRot)
        {
            Vector3 u0 = (i.RestElbow - i.Shoulder).normalized, w0 = (i.RestHand - i.RestElbow).normalized, u = (r.Elbow - i.Shoulder).normalized, w = (r.Hand - r.Elbow).normalized;
            Vector3 h0 = Swing(i.TorsoOut, u0, i.TorsoUp);
            h0 -= u0 * Vector3.Dot(h0, u0);
            if (h0.sqrMagnitude < sqrEpsilon) h0 = i.TorsoUp;
            h0.Normalize();
            upperRot = Align(restUpperRot, u0, u, h0, r.Hinge);
            lowerRot = Align(restLowerRot, w0, w, h0, r.Hinge);
        }
        static Quaternion Align(Quaternion rest, Vector3 from, Vector3 to, Vector3 hingeRest, Vector3 hinge)
        {
            Quaternion swing = BasisQuaternionExt.FromToRotation(from, to), rot = swing * rest;
            Vector3 hNow = swing * hingeRest;
            hNow -= to * Vector3.Dot(hNow, to);
            Vector3 hWant = hinge - to * Vector3.Dot(hinge, to);
            if (hNow.sqrMagnitude > sqrEpsilon && hWant.sqrMagnitude > sqrEpsilon)
            {
                rot = Quaternion.AngleAxis(Vector3.SignedAngle(hNow.normalized, hWant.normalized, to), to) * rot;
            }
            return rot;
        }
    }
    [BurstCompile]
    public static class BasisShoulderSolveCore
    {
        public const float ElevationStartDeg = 30f, ElevationSlope = 0.36f, ElevationIntercept = -10.8f, RetractionStartDeg = 70f, RetractionSlope = -0.22f, RetractionIntercept = 15.4f;
        public const float RetractionShare = 0.5f, ForwardReachStart = 0.6f, ForwardReachFullDeg = 15f, CrossBodyDeg = 20f, BehindStart = 0.3f, BehindFullDeg = 12f, ShrugStartDeg = 150f, ShrugFullDeg = 10f;
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f;
        public static void Solve(in BasisShoulderSolveInput i, out BasisShoulderSolveResult r)
        {
            r = default;
            Vector3 clavicle = i.UpperArmPos - i.ShoulderPos, toHand = i.HandTargetPos - i.UpperArmPos;
            float clavLen = clavicle.magnitude, armLen = Mathf.Max(i.ArmLength, epsilon), reach = toHand.magnitude;
            if (clavLen < epsilon || reach < epsilon)
            {
                return;
            }
            Vector3 dir = toHand / reach;
            float elevation = Vector3.Angle(-i.TorsoUp, dir), elev = elevation > ElevationStartDeg ? ElevationSlope * elevation + ElevationIntercept : 0f;
            if (i.ShrugEnabled && elevation > ShrugStartDeg) elev += ShrugFullDeg * Mathf.Clamp01((elevation - ShrugStartDeg) / (180f - ShrugStartDeg));
            float forward = Vector3.Dot(toHand, i.TorsoForward) / armLen, medial = -Vector3.Dot(toHand, i.TorsoOut) / armLen;
            float prot = Mathf.Clamp01((forward - ForwardReachStart) / (1f - ForwardReachStart)) * ForwardReachFullDeg + Mathf.Clamp01(medial) * CrossBodyDeg;
            prot -= Mathf.Clamp01((-forward - BehindStart) / 0.5f) * BehindFullDeg;
            if (elevation > RetractionStartDeg) prot += (RetractionSlope * elevation + RetractionIntercept) * RetractionShare;
            elev = Mathf.Clamp(elev * i.ElevationFactor, -i.MaxDeg, i.MaxDeg);
            prot = Mathf.Clamp(prot * i.ProtractionFactor, -i.MaxDeg, i.MaxDeg);
            Vector3 c0 = clavicle / clavLen, up = i.TorsoUp - c0 * Vector3.Dot(i.TorsoUp, c0), fwd = i.TorsoForward - c0 * Vector3.Dot(i.TorsoForward, c0);
            if (up.sqrMagnitude < sqrEpsilon || fwd.sqrMagnitude < sqrEpsilon)
            {
                return;
            }
            up.Normalize();
            fwd.Normalize();
            float e = elev * Mathf.Deg2Rad, p = prot * Mathf.Deg2Rad;
            Vector3 c1 = (c0 * Mathf.Cos(e) + up * Mathf.Sin(e)).normalized, c2 = (c1 * Mathf.Cos(p) + fwd * Mathf.Sin(p)).normalized;
            r.Delta = BasisQuaternionExt.FromToRotation(c0, c2);
            r.ElevationDeg = elev;
            r.ProtractionDeg = prot;
            r.HumeralElevationDeg = elevation;
            r.ReachRatio = reach / armLen;
            r.Apply = true;
        }
    }
    public static class BasisShoulderBlendCore
    {
        public const float DefaultBlendTime = 0.25f;
        public static float Step(float blend, float target, float dt, float blendTime) => blendTime <= 0f ? target : Mathf.MoveTowards(blend, target, Mathf.Max(dt, 0f) / blendTime);
        public static Quaternion Blend(Quaternion fallback, Quaternion tracked, float blend) => blend <= 0f ? fallback : blend >= 1f ? tracked : Quaternion.Slerp(fallback, tracked, blend);
    }
}
