using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// Pure, side-effect-free constellation classifier: the tracker→role matching math
    /// extracted out of <see cref="BasisAvatarIKStageCalibration"/> so it can be driven by
    /// synthetic data (the editor Tracker Placement Sweep) as well as by live devices. It
    /// takes a set of trackers described only by their body-relative height/lateral ratios and
    /// returns a role per tracker — no avatar lookup, no device mutation, no settings reads.
    /// The live calibration builds these samples from real devices, calls Classify, then applies
    /// the returned roles; the sweep generates them from body archetypes and checks the result.
    /// </summary>
    public readonly struct BasisConstellationPrior
    {
        public readonly BasisBoneTrackedRole Role;
        public readonly float ExpectedHeightRatio;
        public readonly float ExpectedLateralRatio;
        public readonly float HeightSigma;
        public readonly float LateralSigma;

        public BasisConstellationPrior(BasisBoneTrackedRole role, float h, float lat, float hSigma, float latSigma)
        {
            Role = role;
            ExpectedHeightRatio = h;
            ExpectedLateralRatio = lat;
            HeightSigma = hSigma;
            LateralSigma = latSigma;
        }
    }

    /// <summary>One tracker described purely by its position in the player's T-pose body frame.</summary>
    public struct BasisConstellationSample
    {
        public float HeightRatio;   // y / eyeHeight: 0 ≈ floor, 1 ≈ HMD
        public float LateralRatio;  // signed x / eyeHeight: +x = body's right
        public float DepthLocal;    // body-local z in meters (forward+), used only by the toe-forward swap
        public bool NearOrigin;     // device polled at ≈ origin — excluded from scoring
        public int ForcedRole;      // -1 = none; otherwise (int)BasisBoneTrackedRole, a user override that bypasses scoring
    }

    /// <summary>A known hand-controller pose (body-relative ratios). Re-centers the elbow prior.</summary>
    public struct BasisConstellationHand
    {
        public bool Present;
        public float HeightRatio;
        public float LateralRatio;

        public static readonly BasisConstellationHand None = new BasisConstellationHand { Present = false };
    }

    public enum BasisConstellationAssignKind : byte
    {
        None = 0,
        Forced = 1,
        HipsFirst = 2,
        Greedy = 3,
        Leftover = 4,
        ToeSwap = 5,
    }

    public class BasisConstellationResult
    {
        public int[] AssignedRole;                       // per sample: -1 or (int)BasisBoneTrackedRole
        public float[] AssignedScore;                    // per sample: assigned score (+inf for forced), NaN if unassigned
        public BasisConstellationAssignKind[] AssignedKind;
        public BasisConstellationPrior[] Priors;         // full recentered prior set (pre enabled-filter), for visualization / score tables
        public float ArmReach;
        public float StanceReach;
        public int AssignedCount;

        public bool TryGetRole(int sampleIndex, out BasisBoneTrackedRole role)
        {
            int r = AssignedRole[sampleIndex];
            if (r < 0) { role = BasisBoneTrackedRole.CenterEye; return false; }
            role = (BasisBoneTrackedRole)r;
            return true;
        }
    }

    public static class BasisConstellationClassifier
    {
        // -9 ≈ a combined 3-sigma fit. A tracker that can't beat this against any role is
        // left unassigned rather than forced into a bad slot.
        public const float AcceptThreshold = -9f;
        public const float ArmHeightFloor = 0.65f;
        public const float ArmLateralFloor = 0.20f;
        // Half arm-span as a fraction of eye height for a typical adult — used as a fallback
        // when no arm-height tracker is present to measure the player's own reach.
        public const float DefaultArmReachRatio = 0.55f;
        public const float ToeForwardEpsilon = 0.02f;
        // Where the elbow prior sits along the hand→shoulder line. 0.5 = true midpoint.
        public const float ElbowShoulderBlend = 0.5f;
        // Floor on the re-centered elbow's lateral magnitude, in units of its own lateral
        // sigma, so the region's inner edge can't cross the body midline onto the other arm.
        public const float ElbowMidlineSigmaGuard = 3.0f;
        // Leg lateral priors scale with measured stance the way arm priors scale with reach.
        public const float DefaultStanceRatio = 0.10f;
        // Trackers at/under this height ratio are treated as feet/toes when measuring stance.
        public const float FootHeightCeiling = 0.18f;
        public const float StanceLateralFloor = 0.03f;
        public const float FootHeightMin = 0.0f;
        public const float FootHeightMax = 0.16f;
        public const float ToeBelowFoot = 0.03f;
        // Leftover pass accepts fits up to this multiple of the main threshold (−9 → −15.3, ≈3.9σ).
        public const float LeftoverThresholdScale = 1.7f;
        // A tracker more than this far (eye-height ratio) onto the wrong side of the midline can't
        // take a side-specific role — kills cross-side limb binds when noise pulls a tracker inward.
        public const float MidlineGuardRatio = 0.02f;
        // Foot-mount height re-center uses only samples at/above this ratio, so floor-hugging toe
        // trackers don't drag the foot prior down into the toe band (keeps foot/toe discriminable).
        public const float FootBandFloor = 0.035f;

        /// <summary>
        /// Classifies the given samples into roles. <paramref name="roleEnabled"/> is consulted
        /// once to drop disabled roles from the candidate set (a forced override may still bind a
        /// disabled / off-list role). Returns one role (or -1) per input sample, plus the full
        /// recentered prior set for diagnostics.
        /// </summary>
        public static BasisConstellationResult Classify(
            BasisConstellationSample[] samples, int count,
            BasisConstellationHand leftHand, BasisConstellationHand rightHand,
            float tolerance,
            System.Func<BasisBoneTrackedRole, bool> roleEnabled)
        {
            var result = new BasisConstellationResult
            {
                AssignedRole = new int[count],
                AssignedScore = new float[count],
                AssignedKind = new BasisConstellationAssignKind[count],
            };
            for (int i = 0; i < count; i++)
            {
                result.AssignedRole[i] = -1;
                result.AssignedScore[i] = float.NaN;
            }

            float armReach = EstimateArmReach(samples, count, leftHand, rightHand);
            float stanceReach = EstimateStanceWidth(samples, count);
            result.ArmReach = armReach;
            result.StanceReach = stanceReach;

            // Full recentered prior set (matches the snapshot the live visualizer captures
            // before the enabled-toggle filter).
            BasisConstellationPrior[] priorsFull = BuildPriors(armReach, stanceReach, tolerance);
            ApplyMeasuredFootHeightPriors(priorsFull, samples, count);
            ApplyElbowMidpointPriors(priorsFull, samples, count, leftHand, rightHand);
            result.Priors = priorsFull;

            // Honor per-role calibration toggles: a disabled role is removed from the candidate
            // set entirely (so a dependent role's precondition treats it as "nothing to wait on").
            BasisConstellationPrior[] priors = FilterEnabled(priorsFull, roleEnabled, out int keptCount);
            if (keptCount == 0)
            {
                result.AssignedCount = 0;
                return result;
            }

            bool[] sampleUsed = new bool[count];
            bool[] roleUsed = new bool[priors.Length];
            int assignedCount = 0;

            assignedCount += ApplyForcedRoleOverrides(samples, count, priors, sampleUsed, roleUsed, result);
            assignedCount += AssignHipsFirst(samples, count, priors, sampleUsed, roleUsed, result);

            // Greedy global-best: each iteration takes the (sample, role) pair with the highest
            // score still above threshold. One tracker per role, one role per tracker.
            while (true)
            {
                float bestScore = AcceptThreshold;
                int bestSampleIdx = -1;
                int bestRoleIdx = -1;
                for (int s = 0; s < count; s++)
                {
                    if (sampleUsed[s]) continue;
                    if (samples[s].NearOrigin) continue;
                    for (int r = 0; r < priors.Length; r++)
                    {
                        if (roleUsed[r]) continue;
                        if (!IsAssignmentAllowed(priors[r].Role, priors, roleUsed, leftoverPass: false)) continue;
                        if (!SideAllowed(samples[s], priors[r].Role)) continue;
                        float score = Score(samples[s], priors[r]);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestSampleIdx = s;
                            bestRoleIdx = r;
                        }
                    }
                }
                if (bestSampleIdx < 0) break;

                Assign(result, bestSampleIdx, priors[bestRoleIdx].Role, bestScore, BasisConstellationAssignKind.Greedy);
                sampleUsed[bestSampleIdx] = true;
                roleUsed[bestRoleIdx] = true;
                assignedCount++;
            }

            assignedCount += AssignLeftoverTrackers(samples, count, priors, sampleUsed, roleUsed, result);

            ApplyToeForwardConstraint(samples, count, BasisBoneTrackedRole.LeftFoot, BasisBoneTrackedRole.LeftToes, result);
            ApplyToeForwardConstraint(samples, count, BasisBoneTrackedRole.RightFoot, BasisBoneTrackedRole.RightToes, result);

            result.AssignedCount = assignedCount;
            return result;
        }

        public static float Score(in BasisConstellationSample sample, in BasisConstellationPrior prior)
        {
            float dh = (sample.HeightRatio - prior.ExpectedHeightRatio) / prior.HeightSigma;
            float dl = (sample.LateralRatio - prior.ExpectedLateralRatio) / prior.LateralSigma;
            return -(dh * dh + dl * dl);
        }

        // A tracker measured clearly on one side of the midline can't take the opposite side's
        // role — prevents cross-side limb binds when noise pulls a tracker toward the center.
        // Centerline roles (Hips/Chest) are unconstrained; forced overrides bypass this entirely.
        public static bool SideAllowed(in BasisConstellationSample sample, BasisBoneTrackedRole role)
        {
            int side = role.SideSign();
            if (side == 0) return true;
            if (side < 0 && sample.LateralRatio > MidlineGuardRatio) return false;
            if (side > 0 && sample.LateralRatio < -MidlineGuardRatio) return false;
            return true;
        }

        /// <summary>
        /// Heights are fractions of player eye height. Lateral is signed — negative is the body's
        /// left. Every sigma is multiplied by toleranceScale (the user-facing "calibration
        /// tolerance" knob, 1 = stock). Leg lateral tracks measured stance; arm lateral tracks
        /// measured reach, so the same priors work for kids and tall adults.
        /// </summary>
        public static BasisConstellationPrior[] BuildPriors(float armReach, float stanceReach, float toleranceScale)
        {
            float legLat = stanceReach;
            float legLatSigma = Mathf.Max(stanceReach * 0.7f, 0.07f);
            return new BasisConstellationPrior[]
            {
                // Centered torso bones — height is the discriminator.
                new BasisConstellationPrior(BasisBoneTrackedRole.Hips,          h: 0.55f, lat: 0f,                hSigma: 0.10f * toleranceScale, latSigma: 0.10f * toleranceScale),
                new BasisConstellationPrior(BasisBoneTrackedRole.Chest,         h: 0.78f, lat: 0f,                hSigma: 0.08f * toleranceScale, latSigma: 0.10f * toleranceScale),

                // Legs — toes near floor, feet just above, knees mid-shin. Lateral scales with stance.
                new BasisConstellationPrior(BasisBoneTrackedRole.LeftToes,      h: 0.02f, lat: -legLat,           hSigma: 0.04f * toleranceScale, latSigma: legLatSigma * toleranceScale),
                new BasisConstellationPrior(BasisBoneTrackedRole.RightToes,     h: 0.02f, lat: +legLat,           hSigma: 0.04f * toleranceScale, latSigma: legLatSigma * toleranceScale),
                new BasisConstellationPrior(BasisBoneTrackedRole.LeftFoot,      h: 0.05f, lat: -legLat,           hSigma: 0.08f * toleranceScale, latSigma: legLatSigma * toleranceScale),
                new BasisConstellationPrior(BasisBoneTrackedRole.RightFoot,     h: 0.05f, lat: +legLat,           hSigma: 0.08f * toleranceScale, latSigma: legLatSigma * toleranceScale),
                new BasisConstellationPrior(BasisBoneTrackedRole.LeftLowerLeg,  h: 0.27f, lat: -legLat,           hSigma: 0.10f * toleranceScale, latSigma: legLatSigma * toleranceScale),
                new BasisConstellationPrior(BasisBoneTrackedRole.RightLowerLeg, h: 0.27f, lat: +legLat,           hSigma: 0.10f * toleranceScale, latSigma: legLatSigma * toleranceScale),

                // Arms in T-pose share approximate height; lateral position discriminates
                // shoulder vs elbow. Lateral priors scale with measured reach so this works for
                // any arm length; lateral sigma (×0.08 of reach) keeps the 3σ band off the midline.
                new BasisConstellationPrior(BasisBoneTrackedRole.LeftShoulder,  h: 0.88f, lat: -armReach * 0.30f, hSigma: 0.08f * toleranceScale, latSigma: armReach * 0.08f * toleranceScale),
                new BasisConstellationPrior(BasisBoneTrackedRole.RightShoulder, h: 0.88f, lat: +armReach * 0.30f, hSigma: 0.08f * toleranceScale, latSigma: armReach * 0.08f * toleranceScale),
                new BasisConstellationPrior(BasisBoneTrackedRole.LeftLowerArm,  h: 0.88f, lat: -armReach * 0.65f, hSigma: 0.10f * toleranceScale, latSigma: armReach * 0.12f * toleranceScale),
                new BasisConstellationPrior(BasisBoneTrackedRole.RightLowerArm, h: 0.88f, lat: +armReach * 0.65f, hSigma: 0.10f * toleranceScale, latSigma: armReach * 0.12f * toleranceScale),
            };
        }

        /// <summary>
        /// Largest absolute lateral ratio among arm-height trackers, or a typical-adult fallback.
        /// Adapts shoulder/elbow priors to the player's actual arm length.
        /// </summary>
        public static float EstimateArmReach(BasisConstellationSample[] samples, int count, BasisConstellationHand leftHand, BasisConstellationHand rightHand)
        {
            float maxAbs = 0f;
            for (int i = 0; i < count; i++)
            {
                BasisConstellationSample s = samples[i];
                if (s.NearOrigin) continue;
                if (s.HeightRatio < ArmHeightFloor) continue;
                float lAbs = Mathf.Abs(s.LateralRatio);
                if (lAbs < ArmLateralFloor) continue;
                if (lAbs > maxAbs) maxAbs = lAbs;
            }
            // The hand controllers are pinned and are the true outermost arm point. Including them
            // sizes the shoulder/elbow priors to real arm length instead of the (inboard) elbow
            // tracker — otherwise the shoulder prior stays too tight and shoulders drop out.
            if (leftHand.Present) maxAbs = Mathf.Max(maxAbs, Mathf.Abs(leftHand.LateralRatio));
            if (rightHand.Present) maxAbs = Mathf.Max(maxAbs, Mathf.Abs(rightHand.LateralRatio));
            return maxAbs > ArmLateralFloor ? maxAbs : DefaultArmReachRatio;
        }

        /// <summary>
        /// Largest absolute lateral ratio among foot-height trackers (the stance-defining
        /// trackers), or the typical-adult fallback. Lets leg priors track the player's stance.
        /// </summary>
        public static float EstimateStanceWidth(BasisConstellationSample[] samples, int count)
        {
            float maxAbs = 0f;
            for (int i = 0; i < count; i++)
            {
                BasisConstellationSample s = samples[i];
                if (s.NearOrigin) continue;
                if (s.HeightRatio > FootHeightCeiling) continue;
                float lAbs = Mathf.Abs(s.LateralRatio);
                if (lAbs < StanceLateralFloor) continue;
                if (lAbs > maxAbs) maxAbs = lAbs;
            }
            return maxAbs > StanceLateralFloor ? maxAbs : DefaultStanceRatio;
        }

        /// <summary>
        /// Re-centers the Foot/Toes height priors on the player's measured foot-tracker height
        /// (ankle-strap / boot mount variance), keeping the toe band a fixed step below the foot
        /// band. No-op when no foot-height tracker is present.
        /// </summary>
        private static void ApplyMeasuredFootHeightPriors(BasisConstellationPrior[] priors, BasisConstellationSample[] samples, int count)
        {
            // Foot-band samples (>= FootBandFloor) anchor the foot height so a toe in the set
            // doesn't drag the foot prior down toward the toes and collapse the foot/toe height
            // discrimination. Fall back to all low samples when no foot-band one is present.
            float sum = 0f;
            int n = 0;
            float sumAll = 0f;
            int nAll = 0;
            for (int i = 0; i < count; i++)
            {
                BasisConstellationSample s = samples[i];
                if (s.NearOrigin) continue;
                if (s.HeightRatio > FootHeightCeiling) continue;
                if (s.HeightRatio < -FootHeightCeiling) continue; // discard far-sub-floor (bad) reads
                sumAll += s.HeightRatio;
                nAll++;
                if (s.HeightRatio >= FootBandFloor)
                {
                    sum += s.HeightRatio;
                    n++;
                }
            }
            if (nAll == 0) return;

            float footH = Mathf.Clamp(n > 0 ? sum / n : sumAll / nAll, FootHeightMin, FootHeightMax);
            float toeH = Mathf.Max(footH - ToeBelowFoot, 0f);

            SetPriorHeight(priors, BasisBoneTrackedRole.LeftFoot, footH);
            SetPriorHeight(priors, BasisBoneTrackedRole.RightFoot, footH);
            SetPriorHeight(priors, BasisBoneTrackedRole.LeftToes, toeH);
            SetPriorHeight(priors, BasisBoneTrackedRole.RightToes, toeH);
        }

        private static void SetPriorHeight(BasisConstellationPrior[] priors, BasisBoneTrackedRole role, float newHeight)
        {
            int idx = FindPriorIndex(priors, role);
            if (idx < 0) return;
            priors[idx] = new BasisConstellationPrior(role, newHeight, priors[idx].ExpectedLateralRatio, priors[idx].HeightSigma, priors[idx].LateralSigma);
        }

        /// <summary>
        /// Overrides the LeftLowerArm / RightLowerArm prior centers with the midpoint between
        /// that side's hand controller and shoulder. No-ops per side when that hand isn't present.
        /// </summary>
        private static void ApplyElbowMidpointPriors(BasisConstellationPrior[] priors, BasisConstellationSample[] samples, int count, BasisConstellationHand leftHand, BasisConstellationHand rightHand)
        {
            ApplyElbowMidpointForSide(priors, samples, count, leftHand, BasisBoneTrackedRole.LeftShoulder, BasisBoneTrackedRole.LeftLowerArm, sideSign: -1);
            ApplyElbowMidpointForSide(priors, samples, count, rightHand, BasisBoneTrackedRole.RightShoulder, BasisBoneTrackedRole.RightLowerArm, sideSign: +1);
        }

        private static void ApplyElbowMidpointForSide(
            BasisConstellationPrior[] priors,
            BasisConstellationSample[] samples, int count,
            BasisConstellationHand hand,
            BasisBoneTrackedRole shoulderRole,
            BasisBoneTrackedRole lowerArmRole,
            int sideSign)
        {
            int elbowIdx = FindPriorIndex(priors, lowerArmRole);
            if (elbowIdx < 0) return;

            if (!hand.Present)
            {
                // No usable hand pose this side. The static center sits at armReach×0.65, but
                // armReach is by construction the outermost arm-height tracker — i.e. the elbow
                // itself — so re-center on the measured outermost same-side arm tracker instead.
                if (TryGetOutermostArmSampleLateral(samples, count, sideSign, out float measuredLat))
                {
                    float latSigmaFallback = priors[elbowIdx].LateralSigma;
                    float minMagFallback = ElbowMidlineSigmaGuard * latSigmaFallback;
                    float clampedFallback = sideSign < 0 ? Mathf.Min(measuredLat, -minMagFallback) : Mathf.Max(measuredLat, minMagFallback);
                    priors[elbowIdx] = new BasisConstellationPrior(
                        lowerArmRole,
                        priors[elbowIdx].ExpectedHeightRatio,
                        clampedFallback,
                        priors[elbowIdx].HeightSigma,
                        latSigmaFallback);
                }
                return;
            }

            float shoulderHeight, shoulderLateral;
            int shoulderIdx = FindPriorIndex(priors, shoulderRole);
            if (shoulderIdx >= 0)
            {
                shoulderHeight = priors[shoulderIdx].ExpectedHeightRatio;
                shoulderLateral = priors[shoulderIdx].ExpectedLateralRatio;
            }
            else
            {
                shoulderHeight = priors[elbowIdx].ExpectedHeightRatio;
                shoulderLateral = priors[elbowIdx].ExpectedLateralRatio;
            }

            float t = ElbowShoulderBlend;
            float elbowHeight = Mathf.Lerp(shoulderHeight, hand.HeightRatio, t);
            float elbowLateral = Mathf.Lerp(shoulderLateral, hand.LateralRatio, t);

            float latSigma = priors[elbowIdx].LateralSigma;
            float minMag = ElbowMidlineSigmaGuard * latSigma;
            float clampedLateral = sideSign < 0 ? Mathf.Min(elbowLateral, -minMag) : Mathf.Max(elbowLateral, minMag);

            priors[elbowIdx] = new BasisConstellationPrior(
                lowerArmRole,
                elbowHeight,
                clampedLateral,
                priors[elbowIdx].HeightSigma,
                latSigma);
        }

        /// <summary>Largest-|lateral| arm-height sample on the requested side. Returns false when this side has no arm-height tracker.</summary>
        private static bool TryGetOutermostArmSampleLateral(BasisConstellationSample[] samples, int count, int sideSign, out float lateral)
        {
            lateral = 0f;
            float bestAbs = 0f;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                BasisConstellationSample s = samples[i];
                if (s.NearOrigin) continue;
                if (s.HeightRatio < ArmHeightFloor) continue;
                if (sideSign < 0 && s.LateralRatio >= 0f) continue;
                if (sideSign > 0 && s.LateralRatio <= 0f) continue;
                float a = Mathf.Abs(s.LateralRatio);
                if (a > bestAbs)
                {
                    bestAbs = a;
                    lateral = s.LateralRatio;
                    found = true;
                }
            }
            return found;
        }

        public static int FindPriorIndex(BasisConstellationPrior[] priors, BasisBoneTrackedRole role)
        {
            for (int i = 0; i < priors.Length; i++)
            {
                if (priors[i].Role == role) return i;
            }
            return -1;
        }

        private static BasisConstellationPrior[] FilterEnabled(BasisConstellationPrior[] priorsFull, System.Func<BasisBoneTrackedRole, bool> roleEnabled, out int keptCount)
        {
            if (roleEnabled == null)
            {
                keptCount = priorsFull.Length;
                return priorsFull;
            }
            var kept = new BasisConstellationPrior[priorsFull.Length];
            keptCount = 0;
            for (int i = 0; i < priorsFull.Length; i++)
            {
                if (roleEnabled(priorsFull[i].Role)) kept[keptCount++] = priorsFull[i];
            }
            if (keptCount != kept.Length) System.Array.Resize(ref kept, keptCount);
            return kept;
        }

        // Returns true when dependsOn is already taken, or isn't in the (enabled) prior list at all.
        private static bool IsRolePreconditionMet(BasisConstellationPrior[] priors, bool[] roleUsed, BasisBoneTrackedRole dependsOn)
        {
            for (int i = 0; i < priors.Length; i++)
            {
                if (priors[i].Role == dependsOn) return roleUsed[i];
            }
            return true;
        }

        private static bool IsAssignmentAllowed(BasisBoneTrackedRole role, BasisConstellationPrior[] priors, bool[] roleUsed, bool leftoverPass)
        {
            if (role == BasisBoneTrackedRole.LeftToes && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.LeftFoot)) return false;
            if (role == BasisBoneTrackedRole.RightToes && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.RightFoot)) return false;

            if (leftoverPass) return true;

            if (role == BasisBoneTrackedRole.Chest && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.Hips)) return false;
            if (role == BasisBoneTrackedRole.LeftLowerLeg && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.LeftFoot)) return false;
            if (role == BasisBoneTrackedRole.RightLowerLeg && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.RightFoot)) return false;
            if (role == BasisBoneTrackedRole.LeftShoulder && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.LeftLowerArm)) return false;
            if (role == BasisBoneTrackedRole.RightShoulder && !IsRolePreconditionMet(priors, roleUsed, BasisBoneTrackedRole.RightLowerArm)) return false;
            return true;
        }

        private static int ApplyForcedRoleOverrides(BasisConstellationSample[] samples, int count, BasisConstellationPrior[] priors, bool[] sampleUsed, bool[] roleUsed, BasisConstellationResult result)
        {
            int forced = 0;
            for (int s = 0; s < count; s++)
            {
                if (sampleUsed[s]) continue;
                if (samples[s].NearOrigin) continue;
                if (samples[s].ForcedRole < 0) continue;

                var forcedRole = (BasisBoneTrackedRole)samples[s].ForcedRole;
                int roleIdx = FindPriorIndex(priors, forcedRole);

                // Role already taken by an earlier override (checked against the result itself so
                // off-list roles are covered too — roleUsed only tracks the filtered prior list).
                // The losing pin is consumed rather than released: falling through to the scored
                // passes would bind an explicitly-pinned tracker to an arbitrary other role.
                bool roleTaken = false;
                for (int p = 0; p < count; p++)
                {
                    if (result.AssignedRole[p] == (int)forcedRole) { roleTaken = true; break; }
                }
                if (roleTaken)
                {
                    sampleUsed[s] = true;
                    continue;
                }

                // roleIdx < 0 means the role is toggled off / off-list; honor the override anyway
                // and just skip the roleUsed bookkeeping (the classifier wasn't going to pick it).
                Assign(result, s, forcedRole, float.PositiveInfinity, BasisConstellationAssignKind.Forced);
                sampleUsed[s] = true;
                if (roleIdx >= 0) roleUsed[roleIdx] = true;
                forced++;
            }
            return forced;
        }

        private static int AssignHipsFirst(BasisConstellationSample[] samples, int count, BasisConstellationPrior[] priors, bool[] sampleUsed, bool[] roleUsed, BasisConstellationResult result)
        {
            int hipsIdx = FindPriorIndex(priors, BasisBoneTrackedRole.Hips);
            if (hipsIdx < 0 || roleUsed[hipsIdx]) return 0;

            float bestScore = AcceptThreshold;
            int bestSampleIdx = -1;
            for (int s = 0; s < count; s++)
            {
                if (sampleUsed[s]) continue;
                if (samples[s].NearOrigin) continue;
                float score = Score(samples[s], priors[hipsIdx]);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSampleIdx = s;
                }
            }
            if (bestSampleIdx < 0) return 0;

            Assign(result, bestSampleIdx, BasisBoneTrackedRole.Hips, bestScore, BasisConstellationAssignKind.HipsFirst);
            sampleUsed[bestSampleIdx] = true;
            roleUsed[hipsIdx] = true;
            return 1;
        }

        private static int AssignLeftoverTrackers(BasisConstellationSample[] samples, int count, BasisConstellationPrior[] priors, bool[] sampleUsed, bool[] roleUsed, BasisConstellationResult result)
        {
            float leftoverThreshold = AcceptThreshold * LeftoverThresholdScale;
            int assigned = 0;
            while (true)
            {
                float bestScore = leftoverThreshold;
                int bestSampleIdx = -1;
                int bestRoleIdx = -1;
                for (int s = 0; s < count; s++)
                {
                    if (sampleUsed[s]) continue;
                    if (samples[s].NearOrigin) continue;
                    for (int r = 0; r < priors.Length; r++)
                    {
                        if (roleUsed[r]) continue;
                        if (!IsAssignmentAllowed(priors[r].Role, priors, roleUsed, leftoverPass: true)) continue;
                        if (!SideAllowed(samples[s], priors[r].Role)) continue;
                        float score = Score(samples[s], priors[r]);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestSampleIdx = s;
                            bestRoleIdx = r;
                        }
                    }
                }
                if (bestSampleIdx < 0) break;

                Assign(result, bestSampleIdx, priors[bestRoleIdx].Role, bestScore, BasisConstellationAssignKind.Leftover);
                sampleUsed[bestSampleIdx] = true;
                roleUsed[bestRoleIdx] = true;
                assigned++;
            }
            return assigned;
        }

        private static void ApplyToeForwardConstraint(BasisConstellationSample[] samples, int count, BasisBoneTrackedRole footRole, BasisBoneTrackedRole toeRole, BasisConstellationResult result)
        {
            int footIdx = -1, toesIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (result.AssignedRole[i] == (int)footRole) footIdx = i;
                else if (result.AssignedRole[i] == (int)toeRole) toesIdx = i;
            }
            if (footIdx < 0 || toesIdx < 0) return;

            // Forced overrides bypass every heuristic — never un-do a user's explicit pin.
            if (result.AssignedKind[footIdx] == BasisConstellationAssignKind.Forced ||
                result.AssignedKind[toesIdx] == BasisConstellationAssignKind.Forced) return;

            float footZ = samples[footIdx].DepthLocal;
            float toesZ = samples[toesIdx].DepthLocal;
            if (footZ <= toesZ + ToeForwardEpsilon) return;

            result.AssignedRole[footIdx] = (int)toeRole;
            result.AssignedKind[footIdx] = BasisConstellationAssignKind.ToeSwap;
            result.AssignedRole[toesIdx] = (int)footRole;
            result.AssignedKind[toesIdx] = BasisConstellationAssignKind.ToeSwap;
        }

        private static void Assign(BasisConstellationResult result, int sampleIdx, BasisBoneTrackedRole role, float score, BasisConstellationAssignKind kind)
        {
            result.AssignedRole[sampleIdx] = (int)role;
            result.AssignedScore[sampleIdx] = score;
            result.AssignedKind[sampleIdx] = kind;
        }
    }
}
