using Unity.Burst;
using UnityEngine;
namespace Basis.IK
{
    [BurstCompile]
    public static class BasisArmSolveCore
    {
        public const float MinElbowInteriorDeg = 35f;
        public const float HeadFadeStartSin = 0.15f, HeadFadeFullSin = 0.45f, RestOutward = 0.35f, RestBack = 0.25f;
        public const float MinReachFraction = 0.05f, ModelWeight = 0.85f, TeleportFraction = 0.6f;
        public const float WristKeepFrac = 0.15f, WristKeepMaxDeg = 15f, ForearmRollMaxDeg = 120f, WrapFadeStartDeg = 155f, WrapFadeEndDeg = 178f;
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
        static float Smoothstep(float a, float b, float v)
        {
            float t = b > a ? Mathf.Clamp01((v - a) / (b - a)) : (v >= b ? 1f : 0f);
            return t * t * (3f - 2f * t);
        }
        static Vector3 Swing(Vector3 from, Vector3 to, Vector3 v) => to.sqrMagnitude < sqrEpsilon ? v : BasisQuaternionExt.FromToRotation(from, to) * v;
        public static void Solve(in BasisArmSolveInput i, ref BasisArmState state, out BasisArmSolveResult r)
        {
            r = default;
            float upper = (i.RestElbow - i.Shoulder).magnitude, lower = (i.RestHand - i.RestElbow).magnitude;
            if (upper <= epsilon || lower <= epsilon)
            {
                return;
            }
            Vector3 toTarget = i.TargetPosition - i.Shoulder;
            float reachDistance = toTarget.magnitude, minReach = MinReach(upper, lower);
            Vector3 axis;
            if (reachDistance > minReach)
            {
                axis = toTarget / reachDistance;
            }
            else
            {
                axis = state.Seeded && state.LastAxis.sqrMagnitude > sqrEpsilon ? state.LastAxis : (reachDistance > epsilon ? toTarget / reachDistance : (i.RestHand - i.Shoulder).normalized);
            }
            // The tracked hand is a hard endpoint. Only move it off the controller
            // when the target is outside the arm's anatomical reach interval.
            float dEff = Mathf.Clamp(reachDistance, minReach, upper + lower);
            r.ReachRatio = reachDistance / (upper + lower);
            float cosAlpha = Mathf.Clamp((upper * upper + dEff * dEff - lower * lower) / (2f * upper * dEff), -1f, 1f), sinAlpha = Mathf.Sqrt(Mathf.Max(0f, 1f - cosAlpha * cosAlpha));
            Vector3 center = i.Shoulder + axis * (upper * cosAlpha);
            float radius = upper * sinAlpha;
            r.ElbowDeg = Mathf.Acos(Mathf.Clamp((upper * upper + lower * lower - dEff * dEff) / (2f * upper * lower), -1f, 1f)) * Mathf.Rad2Deg;
            Frame(axis, i.TorsoUp, i.TorsoForward, i.TorsoOut, out Vector3 ex, out Vector3 ey);

            float chain = upper + lower;
            Vector3 handPos = i.Shoulder + axis * dEff;

            // If we don't have an elbow hint (active elbow tracker) then we make our own and position it accordingly.
            Vector3 worldHint = i.HintPosition;
            if (!i.HasTrackerHint)
            {
                // Start behind and below the hand.
                worldHint = handPos - i.TorsoForward * (chain * 2f) - i.TorsoUp * (chain * 0.5f);

                // Keep the elbow hint behind the shoulder/hand plane since realistically it should never swing out forwards
                Vector3 planeFront = i.TorsoForward - axis * Vector3.Dot(i.TorsoForward, axis);
                float planeFrontLen = planeFront.magnitude, behindFade = Smoothstep(0.1f, 0.3f, planeFrontLen);
                if (behindFade > 0f)
                {
                    planeFront /= planeFrontLen;
                    float front = Vector3.Dot(worldHint - i.Shoulder, planeFront);
                    if (front > 0f) worldHint -= planeFront * (2f * front * behindFade);
                }

                // Stick the elbow outward if the hand is moved inward toward the torso so it doesn't clip inside.
                //   How far the hand is in front of the chest (negative when behind)
                float chestToHandForward = Vector3.Dot(handPos - i.Chest, i.TorsoForward) / chain;
                float shoulderToHand = Vector3.Dot(handPos - i.Shoulder, i.TorsoOut) / chain;  // How far the hand is away from the shoulder
                float chestToShoulder = Vector3.Dot(i.Shoulder - i.Chest, i.TorsoOut) / chain; // How far the shoulder is from the chest
                float inward = Smoothstep(-chestToShoulder / 2.0f, chestToShoulder * 2f, -shoulderToHand); // How far the hand has moved inward past the shoulder
                float outPush = inward * 4.0f; //   How far to push the elbow hint
                float forwardPush = Smoothstep(0f, chestToShoulder, chestToHandForward) * inward * 2.0f; //   How far to push the elbow hint forward
                worldHint += i.TorsoOut * (outPush * chain) + i.TorsoForward * (forwardPush * chain);
            }

            // Project the hint onto the elbow circle to get the swivel angle.
            Vector3 hintDir = worldHint - center;
            hintDir -= axis * Vector3.Dot(hintDir, axis);
            float targetDeg = hintDir.sqrMagnitude > sqrEpsilon ? DirToDeg(hintDir.normalized, ex, ey) : BodyPrior(i, axis, ex, ey);

            // Smooth the swivel toward the target or teleport if needed
            float swivelDeg;
            bool teleport = state.Seeded && (i.TargetPosition - state.LastTarget).sqrMagnitude > TeleportFraction * TeleportFraction * chain * chain;
            if (!state.Seeded || teleport || i.Dt <= 0f)
            {
                swivelDeg = targetDeg;
            }
            else
            {
                // If we have an actual tracker for the elbow, don't smooth since the trackers have their own smoothing.
                // Otherwise, use internal tracker
                float smoothing = !i.HasTrackerHint ? i.SmoothTime : 0.0f;
                float delta = Wrap(targetDeg - state.SwivelDeg), alpha = smoothing > 1e-4f ? 1f - Mathf.Exp(-i.Dt / smoothing) : 1f, step = delta * alpha;
                float maxStep = i.MaxRateDeg > 0f ? i.MaxRateDeg * i.Dt : float.MaxValue;
                if (step > maxStep) step = maxStep; else if (step < -maxStep) step = -maxStep;
                swivelDeg = Wrap(state.SwivelDeg + step);
            }

            float side = i.IsLeft ? 1f : -1f;
            Quaternion restHandInv = Quaternion.Inverse(i.RestHandRotation);
            Vector3 palmLocal = restHandInv * Swing(i.TorsoOut, (i.RestElbow - i.Shoulder).normalized, -i.TorsoUp), fwdLocal = restHandInv * (i.RestHand - i.RestElbow).normalized;
            Vector3 palm = i.TargetRotation * palmLocal, handFwd = i.TargetRotation * fwdLocal;
            Vector3 finalDir = DegToDir(swivelDeg, ex, ey), finalElbow = center + finalDir * radius;
            Joints(i, finalElbow, finalDir, axis, side, palm, handFwd, out r.HumeralDeg, out r.PronationDeg, out r.WristFlexDeg, out r.WristDevDeg);

            r.Elbow = finalElbow;
            r.Hand = handPos;
            r.Hinge = Hinge(finalDir, axis, side);
            r.Valid = true;
            state.SwivelDeg = swivelDeg;
            state.Seeded = true;
            state.LastTarget = i.TargetPosition;
            state.LastAxis = axis;
            state.Switched = false;
            state.HintPosition = i.HintPosition;
            state.ConstrainedHintPosition = worldHint;
            state.PriorDeg = targetDeg;
            state.RawDeg = targetDeg;
            state.PriorDir = DegToDir(targetDeg, ex, ey);
            state.ElbowDir = finalDir;
            state.ReachRatio = r.ReachRatio;
            state.ElbowDeg = r.ElbowDeg;
            state.HumeralDeg = r.HumeralDeg;
            state.PronationDeg = r.PronationDeg;
            state.WristFlexDeg = r.WristFlexDeg;
            state.WristDevDeg = r.WristDevDeg;
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
        public static void Pose(in BasisArmSolveInput i, in BasisArmSolveResult r, Quaternion restUpperRot, Quaternion restLowerRot, out Quaternion upperRot, out Quaternion lowerRot) => Pose(i, r, restUpperRot, restLowerRot, out upperRot, out lowerRot, out _);
        public static void Pose(in BasisArmSolveInput i, in BasisArmSolveResult r, Quaternion restUpperRot, Quaternion restLowerRot, out Quaternion upperRot, out Quaternion lowerRot, out float forearmRollDeg)
        {
            Vector3 u0 = (i.RestElbow - i.Shoulder).normalized, w0 = (i.RestHand - i.RestElbow).normalized, u = (r.Elbow - i.Shoulder).normalized, w = (r.Hand - r.Elbow).normalized;
            Vector3 h0 = Swing(i.TorsoOut, u0, i.TorsoUp);
            h0 -= u0 * Vector3.Dot(h0, u0);
            if (h0.sqrMagnitude < sqrEpsilon) h0 = i.TorsoUp;
            h0.Normalize();
            upperRot = Align(restUpperRot, u0, u, h0, r.Hinge);
            lowerRot = Align(restLowerRot, w0, w, h0, r.Hinge);
            forearmRollDeg = ForearmRoll(i, r, lowerRot, restLowerRot);
        }
        public static float ForearmRoll(in BasisArmSolveInput i, in BasisArmSolveResult r, Quaternion lowerRot, Quaternion restLowerRot)
        {
            Vector3 forearm = r.Hand - r.Elbow;
            float forearmSqr = forearm.sqrMagnitude;
            if (forearmSqr < sqrEpsilon)
            {
                return 0f;
            }
            Quaternion lowerInv = Quaternion.Inverse(lowerRot);
            Quaternion delta = lowerInv * i.TargetRotation * Quaternion.Inverse(Quaternion.Inverse(restLowerRot) * i.RestHandRotation);
            float demand = BasisTwistSolveCore.SignedTwistAngleDeg(delta, lowerInv * (forearm / Mathf.Sqrt(forearmSqr))), magnitude = Mathf.Abs(demand);
            float roll = (magnitude - Mathf.Min(WristKeepFrac * magnitude, WristKeepMaxDeg)) * (1f - Smoothstep(WrapFadeStartDeg, WrapFadeEndDeg, magnitude));
            if (roll > ForearmRollMaxDeg) roll = ForearmRollMaxDeg;
            return demand < 0f ? -roll : roll;
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
