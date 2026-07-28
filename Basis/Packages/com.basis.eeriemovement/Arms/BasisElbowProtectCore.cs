using UnityEngine;
namespace Basis.IK
{
    public struct BasisElbowProtectInput
    {
        // Post arm-solve pose (shoulder/elbow/hand), in the same space as the torso points.
        public Vector3 Shoulder;
        public Vector3 Elbow;
        public Vector3 Hand;

        // Torso capsule segments. Hips->Spine and Spine->Chest are only checked when the
        // matching flag is set (mirrors the live job's handleHips/handleSpine validity gating).
        public Vector3 HipsPos;
        public Vector3 SpinePos;
        public Vector3 ChestPos;   // chest segment start; also seeds the shoulder-out direction
        public Vector3 NeckPos;    // chest segment end
        public bool HasHips;
        public bool HasSpine;

        // Raw radii inputs; the core derives the segment radii so the live rig and the sweep
        // share the exact same derivation.
        public float ChestRadiusBase;
        public float CollisionSkin;
        public float HandRadius;
        public float HandSkin;

        public Vector3 PlayerUp;

        // Body-lateral (left<->right) unit direction, from shoulder-to-shoulder (RightUpperArm - LeftUpperArm).
        // Orients the torso's ELLIPTICAL cross-section: the segment radius is the WIDE (lateral) half-width and
        // the front-back half-depth is a fraction of it (see k_ChestDepthRatio). Zero (the struct default) falls
        // back to deriving the axis from the shoulder's own offset, so old callers that never set it still work.
        public Vector3 BodyRight;
    }

    public struct BasisElbowProtectResult
    {
        public bool Engaged;          // true when a swivel was actually produced
        public int CollisionState;    // 0 none, 1 cleared the torso, 2 engaged but could not fully clear
        public Vector3 DesiredElbow;  // elbow position to swing to; == input Elbow when not engaged
        public float WorstPenetration;
        public float SideDot;         // dot(currentDir, outDir)
        public float BlendUsed;       // fraction of the current->out swivel arc taken (0..1)
        public float SwingAngleDeg;   // angle the elbow is swung this solve
        public float ElbowRadius;
        public Vector3 ElbowCenter;
        public float ResidualClearance; // signed torso clearance at DesiredElbow (>=0 cleared, <0 still penetrating)
    }

    // Stream-free torso-collision elbow push shared by BasisFullIKConstraintJob.SolveHand and the
    // offline BasisElbowProtectSweep harness. Change the protect-elbow math HERE so both stay in
    // lock-step. Reuses the job's public capsule helpers so the penetration test never drifts.
    //
    // The elbow is constrained to a circle around the shoulder->hand axis (the hand stays on its
    // target), so the only freedom is the swivel. When the natural (arm-solved / elbow-tracker)
    // pose penetrates the torso, swing the elbow toward "out" by the MINIMUM that leaves the torso;
    // if nothing on the out side clears (hand too close to the body to wrap the arm around), hold
    // the least-penetrating swivel. This keeps the elbow as close to natural as the geometry allows
    // instead of pinning it to a fixed direction.
    public static class BasisElbowProtectCore
    {
        const float k_Epsilon = 1e-5f;
        const float k_ClearMargin = 0.003f;   // land a hair (3 mm) outside the torso surface
        const int k_SwivelSteps = 48;
        // Bisections that turn the coarse sweep's BRACKET into the actual clearing angle. 12 halvings take
        // the residual grid step from 1/48 of the arc to under a thousandth of a degree, which is far below
        // anything the eye or the rest of the chain can resolve.
        const int k_ClearRefineSteps = 12;
        // Golden-section iterations that locate the best-clearing swivel when nothing clears outright.
        const int k_MaxRefineSteps = 12;

        // =============================================================================================
        // A FLAT-TOPPED MAXIMUM HAS NO WELL-DEFINED ARGMAX, AND THAT IS WHERE THE CLICK LIVES.
        //
        // When no swivel on the arc clears the torso, the fallback is "take the least-penetrating one".
        // But measured along a hand run up the chest, the top of that clearance curve is a PLATEAU: the
        // residual clearance holds at -8.0 mm to four decimal places while the winning swivel wanders
        // across 17% of the arc. The VALUE is well determined; the LOCATION is not. So the elbow gets
        // shoved around by whichever end of the plateau happens to score a few microns higher -- and
        // since that is decided by float noise, it hops. Refining the search only converts the hop into
        // jitter; it cannot invent information the objective does not contain.
        //
        // The fix is to stop asking a question with no unique answer. Score the swivel on clearance MINUS
        // a small price on the swing itself, so that among poses that clear equally well the SMALLEST
        // swing wins outright. That makes the maximum unique, makes it move continuously as the plateau's
        // edge moves, and restores what this file says it does in its own header -- swing by the MINIMUM
        // that clears -- to the branch that had quietly stopped doing it.
        //
        // Expressed as a FRACTION OF THE CHEST RADIUS, not in metres. Every length in here is already
        // avatar-scaled by the driver, so a hardcoded metre value would be a quarter of a small avatar's
        // torso and a sliver of a large one -- the same knob meaning two different things.
        //
        // ⚠️ SIZE IT AGAINST THE PLATEAU, NOT AGAINST "FEELS SMALL". The first cut used 1/16 of the chest
        // radius (~7.5 mm of clearance per full swing), reasoning that it was negligible next to the 51 mm
        // a swing buys on a chest-slide. It was not: BasisElbowProtectRollTests caught it commanding a
        // 0.0017 degree swing where it used to command tens of degrees, i.e. the tie-break had stopped
        // breaking ties and started BUYING OFF the whole correction on any pose whose clearance profile is
        // merely shallow rather than flat. That is the failure this codebase names "the fade has eaten the
        // feature instead of the singularity".
        //
        // The plateau it has to resolve is flat to WELL UNDER A MILLIMETRE across ~17% of the arc, so the
        // penalty only ever needs to be worth about a millimetre. At 1/80 of the chest radius it is ~1 mm
        // on a default body: still an order of magnitude above the plateau's own flatness, and now two
        // orders BELOW anything the protect would be right to do.
        // =============================================================================================
        const float k_SwingPreferenceRatio = 0.0125f;

        // =============================================================================================
        // THE BARRIER HAS TO START BEFORE THE SURFACE, THE WAY A REAL CONTACT DOES.
        //
        // A rigid solver switches on the instant it detects overlap: one frame the elbow is untouched,
        // the next it is carrying a fully-formed correction. The size of that first correction is set by
        // the geometry, not by how far in you are -- flipCommit alone can hand it a FULL swing to outDir
        // on the very first penetrating frame. So contact is a STEP, and a step is a snap you can feel.
        //
        // Every physics engine solves this the same way: a speculative contact margin. Start responding
        // while there is still a gap and scale the response by how far into the margin you have come, so
        // the force is already moving by the time the surfaces actually meet. That is what this is.
        //
        //     clearance >= margin ... untouched, and BIT-IDENTICAL to the old early-out
        //     clearance in (0,margin) ... the swing ramps in smoothly from exactly zero
        //     clearance <= 0 ... full response, BIT-IDENTICAL to what it always was
        //
        // Note the ramp lives entirely OUTSIDE the surface. It never weakens the clearing once you are
        // actually inside, so this buys smoothness without giving back any penetration.
        //
        // It matters most on a SMALL collider. Measured sliding a hand up the chest: shrinking the torso
        // without this took the worst pop from 1.6 mm back up to 10.4 mm, because a smaller torso means
        // the arm crosses the contact boundary far more often, and every crossing was a step.
        //
        // A FRACTION OF THE CHEST RADIUS, for the same reason as k_SwingPreferenceRatio -- a margin fixed
        // in metres would swallow a small avatar's torso whole and barely graze a large one.
        // =============================================================================================
        const float k_ContactMarginRatio = 0.25f;

        // =============================================================================================
        // THE TORSO IS AN ELLIPSE, NOT A CIRCLE. A real chest is roughly 1.4x wider left-to-right than it is
        // deep front-to-back. The old model was a round capsule with one radius, so it had to be a compromise:
        // fat enough to cover the SIDES, it stood far too proud of the FRONT, and a cross-body reach (the arm
        // coming across the chest, front-on) got shoved off it -- "the collider is a little big and not well
        // formed". So the per-segment radius is now the WIDE (lateral) half-width, and the front-back half-depth
        // is this fraction of it: the arm can hug the chest FRONT while the SIDES stay covered exactly as before.
        // 0.68 is the anatomical chest depth:width ratio; the sides are unchanged, only the front is drawn in.
        // =============================================================================================
        const float k_ChestDepthRatio = 0.68f;

        // =============================================================================================
        // THE PROTECT'S ONLY AUTHORITY IS THE ELBOW'S CIRCLE RADIUS, AND IT MUST FADE OUT WITH IT.
        //
        // This whole file clears the torso by SWIVELLING the elbow about the shoulder->hand axis. That
        // rotation does two things:
        //
        //     it MOVES THE ELBOW ...... by rho * theta      <- the point of the exercise
        //     it ROLLS THE UPPER ARM .. by theta            <- an unavoidable side effect
        //
        // rho is the elbow's stand-off from the shoulder->hand axis, and IT COLLAPSES AS THE ARM
        // STRAIGHTENS. So at full extension the benefit goes to zero and THE COST DOES NOT: the swivel
        // axis IS the arm's own long axis there, and the entire correction lands as ROLL.
        //
        // Measured, on the user's report ("fully extend the arm, then cross it over the body as far as
        // humanly possible -- the elbow does a nasty rotation"): the protect engages (crossing your body
        // is exactly what presses the upper arm into the chest), commands a steady ~47 degree swing, and
        // at extension >= 0.999 that swing is PURE ROLL -- 47 to 51 degrees of upper-arm spin per
        // MILLIMETRE of hand travel. With the protect disabled the same sweep measures 0.0.
        //
        // FADE ON THE REACH RATIO, NOT ON rho. rho = sqrt(upper^2 - (d/2)^2) is SQUARE-ROOT SINGULAR in
        // the hand position -- its own gradient blows up exactly where we need the fade to be gentle, so
        // fading on it just converts a cliff into a steep ramp (measured: 9-10 deg/mm, versus 2-3 for
        // this). The reach ratio is smooth and its derivative is bounded by 1/armLen.
        //
        // Below Start the protect is BIT-IDENTICAL to what it always was. It reaches zero authority at
        // End, where rho is 3 cm and falling and there is nothing left for a swivel to achieve anyway.
        // =============================================================================================
        const float k_AuthorityFadeStart = 0.95f;   // rho = 9.4 cm: the elbow can still travel 18.7 cm
        const float k_AuthorityFadeEnd = 0.995f;    // rho = 3.0 cm: a swivel is mostly roll by here

        public static void Solve(in BasisElbowProtectInput i, out BasisElbowProtectResult r)
        {
            r = default;
            r.DesiredElbow = i.Elbow;
            r.SideDot = float.NaN;

            Vector3 shoulderPos = i.Shoulder;
            Vector3 elbowPos = i.Elbow;
            Vector3 handPos = i.Hand;

            Vector3 acAxis = handPos - shoulderPos;
            float acSqr = Vector3.Dot(acAxis, acAxis);
            if (acSqr <= k_Epsilon * k_Epsilon)
            {
                return;
            }

            Vector3 acDir = acAxis / Mathf.Sqrt(acSqr);
            Vector3 toElbow = elbowPos - shoulderPos;
            Vector3 elbowCenter = shoulderPos + acDir * Vector3.Dot(toElbow, acDir);
            float elbowRadius = (elbowPos - elbowCenter).magnitude;
            r.ElbowCenter = elbowCenter;
            r.ElbowRadius = elbowRadius;
            if (elbowRadius <= k_Epsilon)
            {
                return;
            }

            float upperArmR = Mathf.Max(0f, (i.HandRadius + i.HandSkin) * 1.2f);
            float chestRBase = i.ChestRadiusBase;
            float skin = i.CollisionSkin;
            // These are the LATERAL (wide, side-to-side) half-widths now; the front-back depth is derived from
            // them in SegmentClearance. The values are unchanged, so the SIDES collide exactly as before.
            float chestR = Mathf.Max(0f, chestRBase + skin);
            float spineR = Mathf.Max(0f, chestRBase * 0.8f + skin);
            float hipsR = Mathf.Max(0f, chestRBase * 1.4f + skin);

            // Body frame for the elliptical cross-section: bodyLat (wide) and bodyFwd (thin), both horizontal.
            Vector3 upN = acSqr > 0f && i.PlayerUp.sqrMagnitude > k_Epsilon ? i.PlayerUp.normalized : Vector3.up;
            Vector3 bodyLat = i.BodyRight - upN * Vector3.Dot(i.BodyRight, upN);
            if (bodyLat.sqrMagnitude <= k_Epsilon * k_Epsilon)
            {
                // Old caller (no shoulder-to-shoulder axis): derive lateral from the shoulder's own offset off
                // the chest axis. Slightly forward-tilted vs the true lateral, but keeps every caller working.
                Vector3 chestClosest = BasisEerieMovement.ClosestPointOnSegment(shoulderPos, i.ChestPos, i.NeckPos);
                Vector3 off = shoulderPos - chestClosest;
                bodyLat = off - upN * Vector3.Dot(off, upN);
            }
            Vector3 bodyFwd = Vector3.zero;
            float bodyLatLen = bodyLat.magnitude;
            if (bodyLatLen > k_Epsilon)
            {
                bodyLat /= bodyLatLen;
                Vector3 fwd = Vector3.Cross(bodyLat, upN);
                float fLen = fwd.magnitude;
                bodyFwd = fLen > k_Epsilon ? fwd / fLen : Vector3.zero;
            }
            else
            {
                bodyLat = Vector3.zero;   // fully degenerate -> SegmentClearance falls back to a round radius
            }

            // Both derived from the chest radius so they carry avatar scale with the collider they guard.
            float contactMargin = chestR * k_ContactMarginRatio;
            float swingPreference = chestR * k_SwingPreferenceRatio;

            float natClear = MinTorsoClearance(i, shoulderPos, elbowPos, upperArmR, chestR, spineR, hipsR, bodyLat, bodyFwd);
            float worstPen = natClear < 0f ? -natClear : 0f;
            r.WorstPenetration = worstPen;
            if (natClear >= contactMargin)
            {
                return;
            }

            // Anatomical "out" direction (shoulder's own side of the body), in the swing plane.
            Vector3 shoulderClosest = BasisEerieMovement.ClosestPointOnSegment(shoulderPos, i.ChestPos, i.NeckPos);
            Vector3 shoulderOut = shoulderPos - shoulderClosest;
            Vector3 shoulderPerp = shoulderOut - acDir * Vector3.Dot(shoulderOut, acDir);
            float shoulderPerpSqr = shoulderPerp.sqrMagnitude;
            if (shoulderPerpSqr <= k_Epsilon * k_Epsilon)
            {
                return;
            }
            Vector3 outDir = shoulderPerp / Mathf.Sqrt(shoulderPerpSqr);

            Vector3 currentDir = (elbowPos - elbowCenter) / elbowRadius;
            r.SideDot = Vector3.Dot(currentDir, outDir);

            // Sweep the swivel from the natural pole (t=0) to fully-out (t=1) around the shoulder->hand
            // axis. Take the first sample that clears (smallest swing); else the least-penetrating one.
            float thetaOut = Mathf.Atan2(Vector3.Dot(Vector3.Cross(currentDir, outDir), acDir),
                Vector3.Dot(currentDir, outDir)) * Mathf.Rad2Deg;
            float firstClearT = -1f;
            float lastBlockedT = 0f;   // the sample just below firstClearT, known NOT to clear
            float bestClear = float.NegativeInfinity;
            int bestClearK = 0;
            for (int k = 0; k <= k_SwivelSteps; k++)
            {
                float t = (float)k / k_SwivelSteps;
                float c = SwivelClearance(i, t, thetaOut, acDir, currentDir, elbowCenter, elbowRadius,
                    shoulderPos, upperArmR, chestR, spineR, hipsR, bodyLat, bodyFwd);
                if (firstClearT < 0f)
                {
                    if (c >= k_ClearMargin)
                    {
                        firstClearT = t;
                    }
                    else
                    {
                        lastBlockedT = t;
                    }
                }
                float s = c - swingPreference * t;
                if (s > bestClear)
                {
                    bestClear = s;
                    bestClearK = k;
                }
            }
            float bestClearT = (float)bestClearK / k_SwivelSteps;

            // ---------------------------------------------------------------------------------------------
            // THE SWEEP ABOVE IS A GRID, AND A GRID ANSWER IS A STAIRCASE.
            //
            // The true clearing angle moves CONTINUOUSLY as the hand slides along the torso, but the loop can
            // only ever report a multiple of 1/k_SwivelSteps. So the swing sits still, sits still, then jumps a
            // whole grid cell at once -- which is exactly what a hand run up the chest feels like: a series of
            // discrete CLICKS. Measured on a 0.5 mm hand step: up to 18 mm of elbow travel in one frame, a 37x
            // amplification of the motion that caused it, on ~4% of samples along the sweep.
            //
            // The grid is only a BRACKET. We know the crossing lies between lastBlockedT and firstClearT, so
            // bisect it and hand back the real angle. The grid keeps doing the job it is good at -- finding
            // WHICH lobe clears -- and stops being the answer's resolution.
            // ---------------------------------------------------------------------------------------------
            bool cleared = firstClearT >= 0f;
            if (cleared)
            {
                if (firstClearT > 0f)
                {
                    float lo = lastBlockedT, hi = firstClearT;
                    for (int b = 0; b < k_ClearRefineSteps; b++)
                    {
                        float mid = 0.5f * (lo + hi);
                        float c = SwivelClearance(i, mid, thetaOut, acDir, currentDir, elbowCenter, elbowRadius,
                            shoulderPos, upperArmR, chestR, spineR, hipsR, bodyLat, bodyFwd);
                        if (c >= k_ClearMargin)
                        {
                            hi = mid;
                        }
                        else
                        {
                            lo = mid;
                        }
                    }
                    firstClearT = hi;
                }
            }
            else
            {
                // ...AND THE SAME STAIRCASE, ONE BRANCH OVER. When nothing on the arc clears -- the arm is
                // pressed too close to wrap around the torso, which is most of a hand run up the chest -- the
                // answer is the ARGMAX of the same grid, and an argmax over samples is every bit as quantised
                // as a threshold crossing. It is in fact the WORSE of the two: measured, every single one of
                // the worst clicks along the sweep came through here, none through the clear branch.
                //
                // The peak drifts smoothly as the hand moves, so bracket it with the grid neighbours and let
                // golden-section find where it actually is. A boundary peak (k = 0 or k = steps) brackets
                // one-sided and converges onto the boundary, so that case stays put instead of special-casing.
                float lo = (float)Mathf.Max(0, bestClearK - 1) / k_SwivelSteps;
                float hi = (float)Mathf.Min(k_SwivelSteps, bestClearK + 1) / k_SwivelSteps;
                bestClearT = RefineClearanceMax(i, lo, hi, thetaOut, acDir, currentDir, elbowCenter,
                    elbowRadius, shoulderPos, upperArmR, chestR, spineR, hipsR, bodyLat, bodyFwd,
                    swingPreference);
            }

            float chosenT = cleared ? firstClearT : bestClearT;
            // Near anti-parallel (|thetaOut|~180) the partial clear-swing direction is ambiguous and
            // snaps sides on smooth motion (the pole flip). Commit toward fully-out (chosenT->1, the
            // stable max-clear pose == outDir exactly) as the natural pole nears anti-parallel to out, so
            // the elbow lands on outDir instead of a fast-moving halfway swing. The ramp starts EARLY
            // (100 deg, was 120) and runs all the way to 180: that shrinks the partial-swing transition
            // band where the elbow swings through large angles, which is what popped on a tight torso-
            // hugging circle (worst trajectory pop 55 -> ~40 deg, under the gate). It trades a little
            // naturalness (the elbow flares out sooner on cross-body reaches) for smoothness.
            float flipCommit = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Mathf.Abs(thetaOut) - 100f) / 80f));
            chosenT += (1f - chosenT) * flipCommit;

            // Bones do not stretch, so the FK chain gives the arm's true length whatever pose it is in.
            float totalLen = (elbowPos - shoulderPos).magnitude + (handPos - elbowPos).magnitude;
            float reach = totalLen > k_Epsilon ? Mathf.Sqrt(acSqr) / totalLen : 1f;
            float authority = 1f - Mathf.SmoothStep(0f, 1f,
                Mathf.Clamp01((reach - k_AuthorityFadeStart) / (k_AuthorityFadeEnd - k_AuthorityFadeStart)));
            chosenT *= authority;

            // The speculative contact ramp (see k_ContactMargin). Applied LAST, after flipCommit, because
            // flipCommit is the term that can hand a full swing to outDir the moment contact is detected --
            // it is precisely what has to be eased in rather than switched on.
            float approach = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(natClear / contactMargin));
            chosenT *= approach;

            // At zero authority OR at the outer edge of the contact margin chosenT is exactly 0, so `dir` is
            // exactly `currentDir` and DesiredElbow is exactly the elbow we were handed. SwingElbowAroundAC
            // then sees v1 == v2 and applies the identity. The no-op is structural, not a tolerance -- there
            // is no residual roll to leak.
            Vector3 dir = Quaternion.AngleAxis(thetaOut * chosenT, acDir) * currentDir;

            r.DesiredElbow = elbowCenter + dir * elbowRadius;
            r.SwingAngleDeg = Mathf.Abs(thetaOut * chosenT);
            r.BlendUsed = chosenT;
            // Reported state stays keyed to ACTUAL penetration, not to the margin: SolveHand feeds this to
            // BasisSwingContinuityCore, which rate-limits on a state CHANGE, and that tag should still flip
            // where the surfaces really meet rather than where the soft ramp begins.
            r.CollisionState = worstPen <= k_Epsilon ? 0 : (cleared ? 1 : 2);
            r.ResidualClearance = MinTorsoClearance(i, shoulderPos, r.DesiredElbow, upperArmR, chestR, spineR, hipsR, bodyLat, bodyFwd);
            r.Engaged = true;
        }

        // Golden-section search for the best-SCORING swivel on [lo, hi] (clearance less the swing price --
        // see k_SwingPreference). Used when nothing on the arc clears outright, so the grid's argmax stops
        // being the answer's resolution. Continuous in the bracket, which is the whole point: the optimum
        // drifts as the hand moves, and the reported angle must drift with it rather than hop between cells.
        static float RefineClearanceMax(in BasisElbowProtectInput i, float lo, float hi, float thetaOut,
            Vector3 acDir, Vector3 currentDir, Vector3 elbowCenter, float elbowRadius, Vector3 shoulderPos,
            float upperArmR, float chestR, float spineR, float hipsR, Vector3 bodyLat, Vector3 bodyFwd,
            float swingPreference)
        {
            const float invPhi = 0.6180339887f;
            float a = lo, b = hi;
            float t1 = b - invPhi * (b - a);
            float t2 = a + invPhi * (b - a);
            float f1 = SwivelScore(i, t1, thetaOut, acDir, currentDir, elbowCenter, elbowRadius, shoulderPos, upperArmR, chestR, spineR, hipsR, bodyLat, bodyFwd, swingPreference);
            float f2 = SwivelScore(i, t2, thetaOut, acDir, currentDir, elbowCenter, elbowRadius, shoulderPos, upperArmR, chestR, spineR, hipsR, bodyLat, bodyFwd, swingPreference);
            for (int n = 0; n < k_MaxRefineSteps; n++)
            {
                if (f1 < f2)
                {
                    a = t1; t1 = t2; f1 = f2;
                    t2 = a + invPhi * (b - a);
                    f2 = SwivelScore(i, t2, thetaOut, acDir, currentDir, elbowCenter, elbowRadius, shoulderPos, upperArmR, chestR, spineR, hipsR, bodyLat, bodyFwd, swingPreference);
                }
                else
                {
                    b = t2; t2 = t1; f2 = f1;
                    t1 = b - invPhi * (b - a);
                    f1 = SwivelScore(i, t1, thetaOut, acDir, currentDir, elbowCenter, elbowRadius, shoulderPos, upperArmR, chestR, spineR, hipsR, bodyLat, bodyFwd, swingPreference);
                }
            }
            return 0.5f * (a + b);
        }

        // What the fallback branch actually maximises: clearance, less a small price on the swing, so a
        // plateau resolves to its SMALLEST swing instead of to float noise. See k_SwingPreference.
        static float SwivelScore(in BasisElbowProtectInput i, float t, float thetaOut, Vector3 acDir,
            Vector3 currentDir, Vector3 elbowCenter, float elbowRadius, Vector3 shoulderPos,
            float upperArmR, float chestR, float spineR, float hipsR, Vector3 bodyLat, Vector3 bodyFwd,
            float swingPreference)
        {
            return SwivelClearance(i, t, thetaOut, acDir, currentDir, elbowCenter, elbowRadius,
                shoulderPos, upperArmR, chestR, spineR, hipsR, bodyLat, bodyFwd) - swingPreference * t;
        }

        // Torso clearance with the elbow swivelled to `t` along the natural->out arc. Shared by the coarse
        // sweep and the bisection that refines it, so both score the exact same candidate pose.
        static float SwivelClearance(in BasisElbowProtectInput i, float t, float thetaOut, Vector3 acDir,
            Vector3 currentDir, Vector3 elbowCenter, float elbowRadius, Vector3 shoulderPos,
            float upperArmR, float chestR, float spineR, float hipsR, Vector3 bodyLat, Vector3 bodyFwd)
        {
            Vector3 d = Quaternion.AngleAxis(thetaOut * t, acDir) * currentDir;
            return MinTorsoClearance(i, shoulderPos, elbowCenter + d * elbowRadius, upperArmR, chestR, spineR, hipsR, bodyLat, bodyFwd);
        }

        // Signed worst-case clearance (gap > 0, penetration < 0) of the upper-arm capsule against the
        // torso segments. Same segment set + validity gating as the live penetration test, so the
        // engage decision is unchanged; sampled once per swivel candidate above. The torso radii passed
        // here are the LATERAL half-widths; SegmentClearance draws the front-back depth in from them.
        static float MinTorsoClearance(in BasisElbowProtectInput i, Vector3 shoulderPos, Vector3 elbowPos,
            float upperArmR, float chestLatR, float spineLatR, float hipsLatR, Vector3 bodyLat, Vector3 bodyFwd)
        {
            // Each segment TAPERS from its own radius to its neighbour's, so the radius the arm feels is
            // continuous all the way up the torso. See the k_ChestDepthRatio block for why a step here is
            // felt as a click.
            float worst = float.PositiveInfinity;
            if (i.HasHips && i.HasSpine)
            {
                worst = Mathf.Min(worst, SegmentClearance(shoulderPos, elbowPos, upperArmR, i.HipsPos, i.SpinePos, hipsLatR, spineLatR, bodyLat, bodyFwd));
            }
            if (i.HasSpine)
            {
                worst = Mathf.Min(worst, SegmentClearance(shoulderPos, elbowPos, upperArmR, i.SpinePos, i.ChestPos, spineLatR, chestLatR, bodyLat, bodyFwd));
            }
            worst = Mathf.Min(worst, SegmentClearance(shoulderPos, elbowPos, upperArmR, i.ChestPos, i.NeckPos, chestLatR, chestLatR, bodyLat, bodyFwd));
            return worst;
        }

        // Signed clearance of the arm capsule (p1,q1,r1) against ONE torso segment (p2,q2) modeled as an
        // ELLIPTICAL capsule: `latR` is the wide (lateral) half-width, the front-back half-depth is
        // latR * k_ChestDepthRatio, and the ellipse is oriented by (bodyLat, bodyFwd). The effective torso
        // radius toward the closest-approach direction is the ellipse's radius in that direction, so the
        // arm clears the chest FRONT far sooner than its SIDES -- the whole point. Falls back to a round
        // radius (latR) when the frame is unavailable or the separation is along the segment axis.
        static float SegmentClearance(Vector3 p1, Vector3 q1, float r1, Vector3 p2, Vector3 q2,
            float latR0, float latR1, Vector3 bodyLat, Vector3 bodyFwd)
        {
            BasisEerieMovement.SegmentSegmentClosestPoints(p1, q1, p2, q2, out _, out float segT, out Vector3 c1, out Vector3 c2);
            Vector3 sep = c1 - c2;
            float sepLen = sep.magnitude;

            // TAPER, so the torso has no CLIFF at a joint. The three segments carry different radii
            // (hips 1.4x, spine 0.8x, chest 1.0x), and holding each one CONSTANT along its length puts a
            // 4 cm step in the surface exactly where two segments meet. Slide a hand up the chest and the
            // clearance the solver is chasing jumps at that seam -- a click you can feel, at the same height
            // every time. Interpolating to the neighbour's radius at the shared joint removes the seam
            // outright: same radius at each joint, no step left to fall off.
            float latR = latR0 + (latR1 - latR0) * segT;
            float rEff = latR;
            float apR = latR * k_ChestDepthRatio;
            Vector3 axis = q2 - p2;
            float axisSqr = axis.sqrMagnitude;
            if (apR > k_Epsilon && sepLen > k_Epsilon && axisSqr > k_Epsilon
                && bodyLat.sqrMagnitude > k_Epsilon && bodyFwd.sqrMagnitude > k_Epsilon)
            {
                // Separation direction projected into the segment's cross-section (perpendicular to its axis).
                Vector3 axisN = axis / Mathf.Sqrt(axisSqr);
                Vector3 sepPerp = sep - axisN * Vector3.Dot(sep, axisN);
                float sepPerpLen = sepPerp.magnitude;
                if (sepPerpLen > k_Epsilon)
                {
                    Vector3 sepDir = sepPerp / sepPerpLen;
                    float cu = Vector3.Dot(sepDir, bodyLat);   // component toward the wide (lateral) axis
                    float cw = Vector3.Dot(sepDir, bodyFwd);   // component toward the thin (front-back) axis
                    float denom = (cu * cu) / (latR * latR) + (cw * cw) / (apR * apR);
                    if (denom > k_Epsilon)
                    {
                        rEff = 1f / Mathf.Sqrt(denom);   // ellipse radius toward sepDir
                    }
                }
            }
            return sepLen - (r1 + rEff);
        }
    }
}
