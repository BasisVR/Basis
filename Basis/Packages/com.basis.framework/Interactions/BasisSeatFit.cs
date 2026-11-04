using System.Collections.Generic;
using Basis.Scripts.Common;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Interactions
{
    public enum BasisSeatLockMode
    {
        Locked,
        UnlessTracked,
        Free,
    }

    public enum BasisSeatPose
    {
        Seated,
        KneesApart,
        CrossLegged,
        LegsOut,
        Dangling,
        Kneeling,
    }

    public struct BasisSeatLegPose
    {
        public Vector3 UpperDir;
        public Vector3 UpperPole;
        public Vector3 LowerDir;
        public Vector3 LowerPole;
        public float FootPitchDegrees;
    }

    public struct BasisSeatFitFrame
    {
        public Vector3 Back;
        public Vector3 Knee;
        public Vector3 Foot;
        public Vector3 UpperLegDir;
        public Vector3 UpperLegPerp;
        public Vector3 LowerLegDir;
        public Vector3 LowerLegPerp;
        public Quaternion SpineRotation;
        public float SpineAngleDegrees;
        public float LegAngleDegrees;
    }

    public struct BasisSeatFitLegs
    {
        public float UpperLegLength;
        public float LowerLegLength;
        public float FootThickness;

        public static BasisSeatFitLegs FromBones(Vector3 upperLeg, Vector3 lowerLeg, Vector3 foot, Vector3 toe)
        {
            return new BasisSeatFitLegs
            {
                UpperLegLength = (lowerLeg - upperLeg).magnitude,
                LowerLegLength = (foot - lowerLeg).magnitude,
                FootThickness = Mathf.Max(foot.y, toe.y),
            };
        }

        public static bool TryFromMapping(Dictionary<HumanBodyBones, BasisCalibratedCoords> tposeFromRoot, Vector3 rootScale, float soleHeight, out BasisSeatFitLegs legs)
        {
            legs = default;
            if (tposeFromRoot == null
                || !tposeFromRoot.TryGetValue(HumanBodyBones.LeftUpperLeg, out BasisCalibratedCoords upperLeg)
                || !tposeFromRoot.TryGetValue(HumanBodyBones.LeftLowerLeg, out BasisCalibratedCoords lowerLeg)
                || !tposeFromRoot.TryGetValue(HumanBodyBones.LeftFoot, out BasisCalibratedCoords foot)
                || !tposeFromRoot.TryGetValue(HumanBodyBones.LeftToes, out BasisCalibratedCoords toe))
            {
                return false;
            }
            legs = FromBones(Vector3.Scale(rootScale, upperLeg.position), Vector3.Scale(rootScale, lowerLeg.position), Vector3.Scale(rootScale, foot.position), Vector3.Scale(rootScale, toe.position));
            legs.FootThickness = Mathf.Max(0f, legs.FootThickness - soleHeight * rootScale.y);
            return true;
        }

        public BasisSeatFitLegs InSeatUnits(float seatScale)
        {
            return new BasisSeatFitLegs
            {
                UpperLegLength = UpperLegLength / seatScale,
                LowerLegLength = LowerLegLength / seatScale,
                FootThickness = FootThickness / seatScale,
            };
        }
    }

    public struct BasisSeatFitResult
    {
        public Vector3 Back;
        public Vector3 Knee;
        public Vector3 Foot;
    }

    public struct BasisSeatRotationLimits
    {
        public float RangeDegrees;

        public float SnapDegrees;

        public static BasisSeatRotationLimits Held => new BasisSeatRotationLimits();

        public bool AllowsRotation => RangeDegrees > 0f;
        public bool IsFullCircle => RangeDegrees >= 360f;
        public float HalfRangeDegrees => IsFullCircle ? 180f : RangeDegrees * 0.5f;

        public BasisSeatRotationLimits(float rangeDegrees, float snapDegrees)
        {
            RangeDegrees = rangeDegrees;
            SnapDegrees = snapDegrees;
        }
    }

    public static class BasisSeatFit
    {
        public const float MinTravelDot = 0.05f;
        public const float MaxBackShift = 0.25f;
        public const float SphereSnapEpsilon = 0.005f;

        public const float SpineBackThicknessRatio = 0.14f;
        public const float UpperLegBackRadiusRatio = 0.14f;
        public const float UpperLegKneeRadiusRatio = 0.08f;
        public const float LowerLegKneeRadiusRatio = 0.10f;
        public const float LowerLegFootRadiusRatio = 0.06f;

        public static bool BuildFrame(Vector3 back, Vector3 foot, Vector3 knee, float spineAngleDegrees, out BasisSeatFitFrame frame)
        {
            frame = new BasisSeatFitFrame
            {
                Back = back,
                Knee = knee,
                Foot = foot,
                SpineAngleDegrees = spineAngleDegrees,
                UpperLegDir = (knee - back).normalized,
                LowerLegDir = (foot - knee).normalized,
            };

            Vector3 left = Vector3.Cross(frame.LowerLegDir, frame.UpperLegDir).normalized;
            if (left == Vector3.zero)
            {
                return false;
            }

            Vector3 spineDir = Quaternion.AngleAxis(spineAngleDegrees, left) * frame.UpperLegDir;
            frame.SpineRotation = Quaternion.LookRotation(Vector3.Cross(spineDir, left), spineDir);
            frame.UpperLegPerp = Vector3.Cross(left, frame.UpperLegDir);
            frame.LowerLegPerp = Vector3.Cross(left, frame.LowerLegDir);
            frame.LegAngleDegrees = Vector3.Angle(frame.UpperLegDir, frame.LowerLegDir);
            return true;
        }

        public static BasisSeatFitResult Solve(in BasisSeatFitFrame seat, in BasisSeatFitLegs legs)
        {
            float upperLegLength = Mathf.Max(legs.UpperLegLength, 1e-6f);
            float lowerLegLength = Mathf.Max(legs.LowerLegLength, 1e-6f);
            float totalLegLength = upperLegLength + lowerLegLength;

            float spineBackThickness = totalLegLength * SpineBackThicknessRatio;
            float upperLegBackRadius = totalLegLength * UpperLegBackRadiusRatio;
            float upperLegKneeRadius = totalLegLength * UpperLegKneeRadiusRatio;
            float lowerLegKneeRadius = totalLegLength * LowerLegKneeRadiusRatio;
            float lowerLegFootRadius = totalLegLength * LowerLegFootRadiusRatio;

            float upperArg = (upperLegBackRadius - upperLegKneeRadius) / upperLegLength;
            float lowerArg = (lowerLegKneeRadius - lowerLegFootRadius) / lowerLegLength;
            float upperLegAngleVsSeatRadians = Mathf.Asin(Mathf.Clamp(upperArg, -0.9999f, 0.9999f));
            float lowerLegAngleVsSeatRadians = Mathf.Asin(Mathf.Clamp(lowerArg, -0.9999f, 0.9999f));

            Vector3 targetFoot = seat.Foot
                                 + (seat.LowerLegPerp * lowerLegFootRadius)
                                 - (seat.LowerLegDir * legs.FootThickness);

            Vector3 targetKnee = seat.Knee
                                 + (seat.UpperLegPerp * upperLegKneeRadius)
                                 + (seat.UpperLegDir * BasisSeat.GetAdjustmentScalar(
                                     Mathf.Clamp(seat.LegAngleDegrees, 10f, 170f),
                                     lowerLegKneeRadius,
                                     upperLegKneeRadius,
                                     upperLegLength));

            Vector3 targetBack = seat.Back
                                 + (seat.UpperLegPerp * upperLegBackRadius)
                                 + (seat.UpperLegDir * BasisSeat.GetAdjustmentScalar(
                                     Mathf.Clamp(180f - seat.SpineAngleDegrees, 10f, 170f),
                                     spineBackThickness,
                                     upperLegBackRadius,
                                     upperLegLength));

            Vector3 preferredBack = targetBack;

            float upperLegAngleVsSpineRadians = upperLegAngleVsSeatRadians + Mathf.Deg2Rad * seat.SpineAngleDegrees;
            Vector3 thighDirSeatLocal = seat.SpineRotation * new Vector3(
                0f,
                Mathf.Cos(upperLegAngleVsSpineRadians),
                Mathf.Sin(upperLegAngleVsSpineRadians));

            float upperLegHorizontalTravelRatio = Mathf.Max(
                MinTravelDot,
                Mathf.Abs(Vector3.Dot(seat.UpperLegDir, thighDirSeatLocal)));

            float availableUpperLegHorizontalTravel = Vector3.Distance(
                targetKnee - seat.UpperLegPerp * upperLegKneeRadius,
                targetBack - seat.UpperLegPerp * upperLegBackRadius);

            float characterUpperLegHorizontalTravel = upperLegLength * upperLegHorizontalTravelRatio;

            if (characterUpperLegHorizontalTravel < availableUpperLegHorizontalTravel)
            {
                float delta = availableUpperLegHorizontalTravel - characterUpperLegHorizontalTravel;
                delta = Mathf.Min(delta, MaxBackShift);
                targetBack += seat.UpperLegDir * delta;
            }
            else
            {
                targetKnee += seat.UpperLegDir * (characterUpperLegHorizontalTravel - availableUpperLegHorizontalTravel);
            }

            targetBack = preferredBack + Vector3.ClampMagnitude(targetBack - preferredBack, MaxBackShift);

            float lowerLegAngleVsSpineRadians = lowerLegAngleVsSeatRadians
                                                - Mathf.Deg2Rad * (seat.SpineAngleDegrees + seat.LegAngleDegrees);
            Vector3 shinDirSeatLocal = seat.SpineRotation * new Vector3(
                0f,
                Mathf.Cos(lowerLegAngleVsSpineRadians),
                -Mathf.Sin(lowerLegAngleVsSpineRadians));

            float lowerLegVerticalTravelRatio = Mathf.Max(
                MinTravelDot,
                Mathf.Abs(Vector3.Dot(seat.LowerLegDir, shinDirSeatLocal)));

            float availableLowerLegVerticalTravel = Vector3.Distance(
                targetFoot + seat.LowerLegDir * lowerLegFootRadius,
                targetKnee + seat.LowerLegDir * lowerLegKneeRadius);

            float characterLowerLegVerticalTravel = lowerLegLength * lowerLegVerticalTravelRatio;

            if (characterLowerLegVerticalTravel < availableLowerLegVerticalTravel)
            {
                targetFoot += seat.LowerLegDir * (characterLowerLegVerticalTravel - availableLowerLegVerticalTravel);
            }
            else
            {
                targetKnee += seat.LowerLegDir * (availableLowerLegVerticalTravel - characterLowerLegVerticalTravel);

                if (characterUpperLegHorizontalTravel > availableUpperLegHorizontalTravel)
                {
                    float calfErr = Mathf.Abs(Vector3.Distance(targetKnee, targetFoot) - lowerLegLength);
                    if (calfErr > SphereSnapEpsilon)
                    {
                        targetKnee = BasisSeat.ClosestPointOnSphere(targetKnee, targetFoot, lowerLegLength);
                    }
                }

                float thighErr = Mathf.Abs(Vector3.Distance(targetBack, targetKnee) - upperLegLength);
                if (thighErr > SphereSnapEpsilon)
                {
                    Vector3 snappedBack = BasisSeat.ClosestPointOnSphere(targetBack, targetKnee, upperLegLength);
                    targetBack = preferredBack + Vector3.ClampMagnitude(snappedBack - preferredBack, MaxBackShift);

                    float thighErrAfterClamp = Mathf.Abs(Vector3.Distance(targetBack, targetKnee) - upperLegLength);
                    if (thighErrAfterClamp > (SphereSnapEpsilon * 4f))
                    {
                        targetBack = snappedBack;
                    }
                }
            }

            return new BasisSeatFitResult
            {
                Back = targetBack,
                Knee = targetKnee,
                Foot = targetFoot,
            };
        }

        public static float ResolveOccupantYaw(float requestedDegrees, in BasisSeatRotationLimits limits)
        {
            if (limits.AllowsRotation == false || float.IsNaN(requestedDegrees) || float.IsInfinity(requestedDegrees))
            {
                return 0f;
            }

            float limit = limits.HalfRangeDegrees;
            float yaw = WrapDegrees(requestedDegrees);
            if (limits.IsFullCircle == false)
            {
                yaw = Mathf.Clamp(yaw, -limit, limit);
            }

            float snap = limits.SnapDegrees;
            if (snap <= 0f)
            {
                return yaw;
            }

            float snapped = Mathf.Round(yaw / snap) * snap;
            if (limits.IsFullCircle)
            {
                return WrapDegrees(snapped);
            }

            if (Mathf.Abs(snapped) > limit)
            {
                float steps = Mathf.Floor((limit + 1e-4f) / snap);
                snapped = Mathf.Sign(snapped) * steps * snap;
            }
            return snapped;
        }

        public static float AddOccupantYaw(float rawDegrees, float deltaDegrees, in BasisSeatRotationLimits limits, out float newRawDegrees)
        {
            if (limits.AllowsRotation == false)
            {
                newRawDegrees = 0f;
                return 0f;
            }

            if (float.IsNaN(deltaDegrees) || float.IsInfinity(deltaDegrees))
            {
                deltaDegrees = 0f;
            }

            float raw = rawDegrees + deltaDegrees;
            newRawDegrees = limits.IsFullCircle
                ? WrapDegrees(raw)
                : Mathf.Clamp(raw, -limits.HalfRangeDegrees, limits.HalfRangeDegrees);

            float snap = limits.SnapDegrees;
            if (snap <= 0f)
            {
                return ResolveOccupantYaw(newRawDegrees, limits);
            }

            // Smooth input accumulates in the raw yaw until it CROSSES a snap step (truncate
            // toward zero). Direct sets round to the nearest step instead — see
            // ResolveOccupantYaw; rounding here would advance half a step early every turn.
            float snapped = (int)(newRawDegrees / snap) * snap;
            if (limits.IsFullCircle)
            {
                return WrapDegrees(snapped);
            }
            float limit = limits.HalfRangeDegrees;
            if (Mathf.Abs(snapped) > limit)
            {
                float steps = Mathf.Floor((limit + 1e-4f) / snap);
                snapped = Mathf.Sign(snapped) * steps * snap;
            }
            return snapped;
        }

        public static float WrapDegrees(float degrees) => Mathf.DeltaAngle(0f, degrees);

        public static bool IsLocked(BasisSeatLockMode mode, bool hasTracker)
        {
            return mode == BasisSeatLockMode.Locked || (mode == BasisSeatLockMode.UnlessTracked && !hasTracker);
        }

        public const float KneesApartDegrees = 25f;
        public const float CrossLeggedThighDegrees = 55f;
        public const float KneelingThighDegrees = 60f;
        public const float KneelingFootPitchDegrees = 60f;
        public const float DanglingFootPitchDegrees = 25f;

        public static BasisSeatLegPose SolveLegPose(BasisSeatPose pose, Vector3 upperDirection, Vector3 lowerDirection, Quaternion spineRotation, bool isLeft)
        {
            Vector3 up = spineRotation * Vector3.up;
            Vector3 forward = spineRotation * Vector3.forward;
            Vector3 right = spineRotation * Vector3.right;
            Vector3 outward = isLeft ? -right : right;
            Vector3 upper = upperDirection.normalized;
            Vector3 lower = lowerDirection.normalized;
            float footPitch = 0f;

            switch (pose)
            {
                case BasisSeatPose.KneesApart:
                    upper = Quaternion.AngleAxis(isLeft ? -KneesApartDegrees : KneesApartDegrees, up) * upper;
                    break;
                case BasisSeatPose.CrossLegged:
                    float thigh = CrossLeggedThighDegrees * Mathf.Deg2Rad;
                    return new BasisSeatLegPose
                    {
                        UpperDir = forward * Mathf.Cos(thigh) + outward * Mathf.Sin(thigh),
                        UpperPole = (up + outward * 0.5f).normalized,
                        LowerDir = (forward * 0.3f - outward * 0.95f - up * (isLeft ? 0.02f : 0.14f)).normalized,
                        LowerPole = (up + forward * 0.4f).normalized,
                    };
                case BasisSeatPose.LegsOut:
                    lower = (upper - up * 0.1f).normalized;
                    break;
                case BasisSeatPose.Dangling:
                    lower = -up;
                    footPitch = DanglingFootPitchDegrees;
                    break;
                case BasisSeatPose.Kneeling:
                    float kneel = KneelingThighDegrees * Mathf.Deg2Rad;
                    upper = forward * Mathf.Cos(kneel) - up * Mathf.Sin(kneel);
                    lower = -forward;
                    footPitch = KneelingFootPitchDegrees;
                    break;
            }

            return new BasisSeatLegPose
            {
                UpperDir = upper,
                UpperPole = Vector3.Cross(upper, right),
                LowerDir = lower,
                LowerPole = Vector3.Cross(lower, right),
                FootPitchDegrees = footPitch,
            };
        }

        public static float PosePelvisLift(BasisSeatPose pose, float upperLegLength)
        {
            return pose == BasisSeatPose.Kneeling ? upperLegLength * Mathf.Sin(KneelingThighDegrees * Mathf.Deg2Rad) : 0f;
        }

        public static Quaternion LimbBoneRotation(Quaternion targetWorldRotation, Quaternion avatarHipsBasis, Quaternion restFromRoot)
        {
            return targetWorldRotation * Quaternion.Inverse(avatarHipsBasis) * restFromRoot;
        }

        public static Vector3 FootTargetPosition(Vector3 targetWorldPosition, Quaternion targetWorldRotation, float footThickness)
        {
            return targetWorldPosition + targetWorldRotation * Vector3.up * footThickness;
        }

        public static void ControlIncoming(in Matrix4x4 playerToWorld, Vector3 worldPosition, Quaternion worldRotation, Vector3 inverseOffsetPosition, Quaternion inverseOffsetRotation, out Vector3 position, out Quaternion rotation)
        {
            rotation = Quaternion.Inverse(playerToWorld.rotation) * worldRotation * Quaternion.Inverse(inverseOffsetRotation);
            position = playerToWorld.inverse.MultiplyPoint3x4(worldPosition) - rotation * inverseOffsetPosition;
        }

        public static bool IsFinitePose(Vector3 position, Quaternion rotation)
        {
            return !float.IsNaN(position.x + position.y + position.z) && !float.IsInfinity(position.x + position.y + position.z)
                && !float.IsNaN(rotation.x + rotation.y + rotation.z + rotation.w) && !float.IsInfinity(rotation.x + rotation.y + rotation.z + rotation.w);
        }

        public static void ComposeHipsWorld(
            Matrix4x4 seatLocalToWorld,
            Quaternion seatWorldRotation,
            Quaternion spineRotation,
            Vector3 seatLocalBack,
            out Vector3 hipsWorldPosition,
            out Quaternion hipsWorldRotation)
        {
            ComposeHipsWorld(seatLocalToWorld, seatWorldRotation, spineRotation, seatLocalBack, 0f,
                out hipsWorldPosition, out hipsWorldRotation, out _);
        }

        public static void ComposeHipsWorld(
            Matrix4x4 seatLocalToWorld,
            Quaternion seatWorldRotation,
            Quaternion spineRotation,
            Vector3 seatLocalBack,
            float occupantYawDegrees,
            out Vector3 hipsWorldPosition,
            out Quaternion hipsWorldRotation,
            out Quaternion occupantPivot)
        {
            hipsWorldPosition = seatLocalToWorld.MultiplyPoint3x4(seatLocalBack);
            Quaternion seatedRotation = seatWorldRotation * spineRotation;

            if (occupantYawDegrees == 0f)
            {
                occupantPivot = Quaternion.identity;
                hipsWorldRotation = seatedRotation;
                return;
            }

            occupantPivot = Quaternion.AngleAxis(occupantYawDegrees, seatedRotation * Vector3.up);
            hipsWorldRotation = occupantPivot * seatedRotation;
        }

        public static Vector3 RotateAboutPivot(Vector3 worldPoint, Vector3 pivot, Quaternion pivotRotation)
        {
            return pivot + pivotRotation * (worldPoint - pivot);
        }

        public static void ComposePlayspaceOffset(
            Vector3 unscaledEyePosition,
            Quaternion unscaledEyeRotation,
            float deviceScale,
            float eyeTposeHeight,
            bool recentreTrackingSpace,
            out Vector3 offsetPosition,
            out Quaternion offsetRotation)
        {
            offsetRotation = Quaternion.Inverse(YawOnly(unscaledEyeRotation));

            if (recentreTrackingSpace == false)
            {
                offsetPosition = Vector3.zero;
                return;
            }

            Vector3 eyePlayspace = offsetRotation * (unscaledEyePosition * deviceScale);
            offsetPosition = -eyePlayspace;
            offsetPosition.y = eyeTposeHeight - eyePlayspace.y;
        }

        public static float ComposePlayspaceHeightOffset(
            Vector3 unscaledEyePosition,
            Quaternion offsetRotation,
            float deviceScale,
            float eyeTposeHeight)
        {
            return eyeTposeHeight - (offsetRotation * (unscaledEyePosition * deviceScale)).y;
        }

        public static Quaternion YawOnly(Quaternion rotation)
        {
            Vector3 euler = rotation.eulerAngles;
            return Quaternion.Euler(0f, euler.y, 0f);
        }

        public static void ComposeSeatedRoot(
            Vector3 hipsWorldPosition,
            Quaternion hipsWorldRotation,
            Quaternion avatarHipsBasis,
            Vector3 hipsTposeLocal,
            out Vector3 rootPosition,
            out Quaternion rootRotation)
        {
            rootRotation = hipsWorldRotation * Quaternion.Inverse(avatarHipsBasis);
            rootPosition = hipsWorldPosition - (rootRotation * hipsTposeLocal);
        }

        public static Quaternion LegDelta(Vector3 restDirection, Vector3 restForward, Vector3 desiredDirection, Vector3 right)
        {
            return LegDeltaWithPole(restDirection, restForward, desiredDirection, Vector3.Cross(desiredDirection.normalized, right));
        }

        public static Quaternion LegDeltaWithPole(Vector3 restDirection, Vector3 restForward, Vector3 desiredDirection, Vector3 desiredPole)
        {
            if (restDirection.sqrMagnitude < 1e-8f || desiredDirection.sqrMagnitude < 1e-8f)
            {
                return Quaternion.identity;
            }
            Vector3 restPole = Vector3.ProjectOnPlane(restForward, restDirection.normalized);
            if (restPole.sqrMagnitude < 1e-8f || desiredPole.sqrMagnitude < 1e-8f)
            {
                return Quaternion.FromToRotation(restDirection, desiredDirection);
            }
            return Quaternion.LookRotation(desiredDirection, desiredPole) * Quaternion.Inverse(Quaternion.LookRotation(restDirection, restPole));
        }
    }
}
