using Basis.Scripts.Drivers;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>
    /// Retargets the arms from PoseLandmarker WORLD landmarks (metric metres, real depth) instead of
    /// image positions.
    ///
    /// Wrist and elbow are measured relative to the user's own shoulder, expressed in a body frame built
    /// from their shoulder line and torso, then divided by the user's own arm length. What is left is a
    /// unitless "fraction of my reach, in my body's frame" vector that does not care how far from the
    /// camera the user sits or how long their arms are. Multiplying by the avatar's arm length and
    /// applying the avatar's body frame — built by the exact same rule — puts the hand where it is on you.
    ///
    /// Reach matching alone still misses when the avatar's head sits at a different height above the
    /// shoulders than yours, which is precisely the "hand next to my face" case, so the vertical component
    /// is blended toward a head-relative scale as the hand rises to head height.
    /// </summary>
    public sealed class MediaPipeArmConverter
    {
        public float Smoothing = 0.5f;
        public float HeadAnchor = 1f;
        public float MaxReach = 0.98f;
        public float HandReachGain = 1.1f;

        /// <summary>How far to pull the elbow swivel toward the resting pole. 0 = trust the camera's elbow completely.</summary>
        public float ElbowRestBias = 0.5f;

        /// <summary>How far the resting elbow sits away from the ribs, relative to straight down.</summary>
        private const float ElbowOutward = 0.3f;

        // One-euro tuning, applied on the CAMERA clock. It only has to take the sensor noise off; the carry pass
        // below is what bridges the gap between samples, so this stays light rather than stacking two heavy
        // low-passes and burying the hands in latency. Beta matches the FBIK tracker default.
        private const float CutoffResponsive = 10f;
        private const float CutoffSmooth = 1.5f;
        private const float DepthCutoffScale = 0.4f;
        private const float Beta = 3.25f;
        private const float DerivativeCutoff = 1f;
        // Asymmetric, because the measurement error is asymmetric — see TrackUserArm.
        private const float ArmLengthRiseHz = 3f;        // a straight side-on arm locks in within ~a third of a second
        private const float ArmLengthFallHz = 0.1f;      // ~10s to let go: only for a bad initial lock or a new user
        private const float ArmLengthMaxJump = 1.25f;    // an arm cannot grow 25% between samples; that is a blown landmark

        private ArmFilter _leftWrist, _leftElbow, _rightWrist, _rightElbow;
        private float _leftUserArm, _rightUserArm;

        /// <summary>
        /// Two-stage filter over a body-space offset.
        ///
        /// Stage one runs ONLY when a fresh camera sample lands, on the camera's own delta — a one-euro filter
        /// fed a held sample at render rate reads every new sample as a burst of speed, opens its cutoff and
        /// snaps to it, which is precisely the stutter. Depth gets its own filter on a lower cutoff, because
        /// monocular z is by far the noisiest axis.
        ///
        /// Stage two runs every rendered frame and carries the pose toward that sample, turning the camera's
        /// staircase back into continuous motion.
        ///
        /// Both stages work in BODY space, so the avatar turning underneath is applied instantly and never
        /// looks like hand movement to the filter.
        /// </summary>
        private struct ArmFilter
        {
            public BasisEuroVec3State Lateral;
            public BasisEuroVec3State Depth;
            public Vector3 Sampled;
            public Vector3 Carried;
            public bool HasSample;

            public Vector3 Apply(Vector3 body, in MediaPipeTiming timing, float cutoff)
            {
                if (timing.IsNewSample || !HasSample)
                {
                    float dt = timing.SampleDelta;
                    float3 lateral = BasisFilterMath.EuroVec3(ref Lateral, new float3(body.x, body.y, 0f),
                        dt, cutoff, Beta, DerivativeCutoff);
                    float3 depth = BasisFilterMath.EuroVec3(ref Depth, new float3(0f, 0f, body.z),
                        dt, cutoff * DepthCutoffScale, Beta, DerivativeCutoff);
                    Sampled = new Vector3(lateral.x, lateral.y, depth.z);

                    if (!HasSample)
                    {
                        Carried = Sampled;
                        HasSample = true;
                        return Carried;
                    }
                }

                Carried = Vector3.Lerp(Carried, Sampled,
                    BasisFilterMath.Alpha(timing.CarryCutoff, timing.RenderDelta));
                return Carried;
            }

            public void Reset()
            {
                Lateral = default;
                Depth = default;
                HasSample = false;
            }
        }

        /// <summary>Avatar arm geometry in player-root-local space, rebuilt per frame by the manager.</summary>
        public struct AvatarArmRig
        {
            public Vector3 LeftAnchor;
            public Vector3 RightAnchor;
            public float LeftUpperLen, LeftForeLen;
            public float RightUpperLen, RightForeLen;
            public Vector3 Right, Up, Forward;
            public Vector3 HeadLocal;
            public float HeadMetric;
            public bool Valid;
        }

        private float Cutoff => Mathf.Lerp(CutoffResponsive, CutoffSmooth, Mathf.Clamp01(Smoothing));

        /// <summary>Full arm reconstruction from the metric body pose, with a kinematically valid elbow.</summary>
        public bool TryGetArm(Vector3[] pose, in AvatarArmRig rig, bool avatarLeft, in MediaPipeTiming timing,
            out Vector3 wristLocal, out Vector3 elbowLocal, out Quaternion wristRotation)
        {
            wristLocal = Vector3.zero;
            elbowLocal = Vector3.zero;
            wristRotation = Quaternion.identity;

            if (!rig.Valid || pose == null || pose.Length < MediaPipeSpace.PoseCount) return false;
            if (!MediaPipeSpace.TryBodyFrame(pose, out _, out Quaternion bodyFrame)) return false;

            Vector3 shoulder = pose[avatarLeft ? MediaPipeSpace.LeftShoulder : MediaPipeSpace.RightShoulder];
            Vector3 elbow = pose[avatarLeft ? MediaPipeSpace.LeftElbow : MediaPipeSpace.RightElbow];
            Vector3 wrist = pose[avatarLeft ? MediaPipeSpace.LeftWrist : MediaPipeSpace.RightWrist];
            if (!MediaPipeSpace.IsFinite(shoulder) || !MediaPipeSpace.IsFinite(elbow) || !MediaPipeSpace.IsFinite(wrist))
            {
                return false;
            }

            float userArm = TrackUserArm(avatarLeft, shoulder, elbow, wrist, timing);
            float upperLen = avatarLeft ? rig.LeftUpperLen : rig.RightUpperLen;
            float foreLen = avatarLeft ? rig.LeftForeLen : rig.RightForeLen;
            float avatarArm = upperLen + foreLen;
            if (!(userArm > 1e-3f) || !(avatarArm > 1e-4f)) return false;

            Quaternion toBody = Quaternion.Inverse(bodyFrame);
            Vector3 wristBody = toBody * (wrist - shoulder);
            Vector3 elbowBody = toBody * (elbow - shoulder);
            Vector3 headBody = toBody * (pose[MediaPipeSpace.Nose] - shoulder);

            float cutoff = Cutoff;
            wristBody = avatarLeft
                ? _leftWrist.Apply(wristBody, in timing, cutoff)
                : _rightWrist.Apply(wristBody, in timing, cutoff);
            elbowBody = avatarLeft
                ? _leftElbow.Apply(elbowBody, in timing, cutoff)
                : _rightElbow.Apply(elbowBody, in timing, cutoff);

            Vector3 anchor = avatarLeft ? rig.LeftAnchor : rig.RightAnchor;
            float reach = avatarArm / userArm;
            float lift = VerticalScale(headBody, Vector3.Dot(rig.HeadLocal - anchor, rig.Up), wristBody.y, reach);

            Vector3 wristTarget = ClampReach(anchor, Place(anchor, wristBody, reach, lift, in rig), avatarArm);

            // The elbow scales UNIFORMLY by reach — never by lift. lift is the head-anchor scale derived from the
            // WRIST's height; it exists to pull the HAND up to the avatar's face and means nothing for the elbow.
            // Applying it here stretches the elbow's vertical component against its lateral/forward ones, which
            // does not merely move the elbow — it ROTATES the swivel, and the swivel is the whole ballgame (the
            // solver reads only the hint's DIRECTION in the swing plane).
            Vector3 measuredElbow = Place(anchor, elbowBody, reach, reach, in rig);

            wristLocal = wristTarget;
            elbowLocal = SolveElbow(anchor, wristTarget, measuredElbow, upperLen, foreLen, in rig, avatarLeft);
            wristRotation = LookFrom(wristLocal - elbowLocal, rig.Up);
            return true;
        }

        /// <summary>
        /// Wrist-only fallback for when the body pose is lost but a hand is still tracked. Placed relative
        /// to the avatar's head and scaled by apparent face size, so it stays put as the user moves toward
        /// or away from the camera. Image x runs camera-right, which is the user's left, hence the negation.
        /// </summary>
        public bool TryGetArmFromHand(Vector3 handWrist, Vector2 headImage, float faceSize, float aspect,
            in AvatarArmRig rig, bool avatarLeft, in MediaPipeTiming timing, out Vector3 wristLocal, out Quaternion wristRotation)
        {
            wristLocal = Vector3.zero;
            wristRotation = Quaternion.identity;
            if (!rig.Valid || faceSize <= 1e-4f || rig.HeadMetric <= 1e-4f) return false;

            float metric = rig.HeadMetric * HandReachGain / faceSize;
            float h = -(handWrist.x - headImage.x) * aspect * metric;
            float v = (handWrist.y - headImage.y) * metric;

            Vector3 anchor = avatarLeft ? rig.LeftAnchor : rig.RightAnchor;
            float avatarArm = avatarLeft
                ? rig.LeftUpperLen + rig.LeftForeLen
                : rig.RightUpperLen + rig.RightForeLen;

            Vector3 offset = new Vector3(h, v, 0f);
            offset = avatarLeft
                ? _leftWrist.Apply(offset, in timing, Cutoff)
                : _rightWrist.Apply(offset, in timing, Cutoff);

            wristLocal = ClampReach(anchor, rig.HeadLocal + offset.x * rig.Right + offset.y * rig.Up, avatarArm);
            wristRotation = LookFrom(wristLocal - anchor, rig.Up);
            return true;
        }

        public void Reset()
        {
            _leftWrist.Reset();
            _leftElbow.Reset();
            _rightWrist.Reset();
            _rightElbow.Reset();
            _leftUserArm = _rightUserArm = 0f;
        }

        /// <summary>
        /// Places the elbow on the circle the avatar's own bone lengths allow, around a swivel direction taken
        /// from the measured elbow and pulled toward the anatomical rest pole.
        ///
        /// What the arm solver actually consumes is only the DIRECTION of this hint in the swing plane
        /// (BasisArmSolveCore: `hintR = FromToRotation(abProj, ahProj)`), so the swivel is the entire ballgame —
        /// the radius only conditions how far the hint fades in. And that swivel is the least trustworthy thing
        /// we compute: it is dominated by the elbow's DEPTH, which is the worst number a monocular pose model
        /// produces. A depth error does not push the elbow forward or back, it ROTATES it around the arm — and
        /// it reads as the elbow winging UP, which a real elbow essentially never does.
        ///
        /// So bias it toward where an elbow actually lives: hanging down, a little away from the ribs. The solver
        /// already reaches for the same prior (`downPole = -PlayerUp`) whenever it has no hint at all.
        /// </summary>
        private Vector3 SolveElbow(Vector3 shoulder, Vector3 wrist, Vector3 measured,
            float upperLen, float foreLen, in AvatarArmRig rig, bool avatarLeft)
        {
            Vector3 toWrist = wrist - shoulder;
            float span = toWrist.magnitude;
            if (span < 1e-4f) return measured;

            Vector3 axis = toWrist / span;
            float along = span >= upperLen + foreLen - 1e-4f
                ? upperLen
                : (upperLen * upperLen - foreLen * foreLen + span * span) / (2f * span);

            Vector3 center = shoulder + axis * along;
            float radiusSq = upperLen * upperLen - along * along;
            if (radiusSq <= 1e-8f) return center;

            Vector3 outward = avatarLeft ? -rig.Right : rig.Right;
            Vector3 rest = Vector3.ProjectOnPlane(-rig.Up + outward * ElbowOutward, axis);
            Vector3 swivel = Vector3.ProjectOnPlane(measured - center, axis);

            bool hasRest = rest.sqrMagnitude > 1e-8f;
            bool hasSwivel = swivel.sqrMagnitude > 1e-8f;
            if (!hasSwivel && !hasRest) return center;

            if (!hasSwivel)
            {
                swivel = rest;
            }
            else if (hasRest)
            {
                swivel = Vector3.Slerp(swivel.normalized, rest.normalized, Mathf.Clamp01(ElbowRestBias));
            }
            if (swivel.sqrMagnitude < 1e-8f) return center;

            return center + swivel.normalized * Mathf.Sqrt(radiusSq);
        }

        /// <summary>
        /// Blends the vertical scale from pure reach matching at shoulder height toward a head-relative
        /// scale at head height, so a hand held beside the face lands beside the avatar's face even when
        /// the avatar's neck is proportioned differently.
        /// </summary>
        private float VerticalScale(Vector3 headBody, float avatarHeadUp, float wristUp, float reach)
        {
            if (HeadAnchor <= 0f || headBody.y < 1e-3f || avatarHeadUp < 1e-4f) return reach;

            float headScale = avatarHeadUp / headBody.y;
            float t = Mathf.Clamp01(wristUp / headBody.y) * Mathf.Clamp01(HeadAnchor);
            return Mathf.Lerp(reach, headScale, t);
        }

        private static Vector3 Place(Vector3 anchor, Vector3 body, float reach, float lift, in AvatarArmRig rig) =>
            anchor
            + (body.x * reach) * rig.Right
            + (body.y * lift) * rig.Up
            + (body.z * reach) * rig.Forward;

        private Vector3 ClampReach(Vector3 anchor, Vector3 target, float limit)
        {
            Vector3 delta = target - anchor;
            float max = limit * Mathf.Max(0.1f, MaxReach);
            float distance = delta.magnitude;
            return distance > max && distance > 1e-6f ? anchor + delta * (max / distance) : target;
        }

        // Arm length is a body CONSTANT, and the error in measuring it is ONE-SIDED. MediaPipe estimates depth
        // from a single camera, and depth is its weakest axis: point your arm at the lens and the
        // shoulder->elbow->wrist chain reads short. It essentially never reads LONG. So an averaging filter does
        // not average the arm — it averages the foreshortening. The estimate sags toward whatever pose you hold
        // most, `reach = avatarArm / userArm` inflates to compensate, and the avatar's hand overshoots exactly
        // when you reach toward the camera, which is most of the time.
        //
        // Track the longest arm we have recently had good reason to believe in instead. Rise quickly — a
        // side-on straight arm is the truth and no average should be allowed to dilute it — and decay slowly,
        // which is all that is needed to let go of a bad initial lock or re-learn after a different person sits
        // down. This is an asymmetric EMA rather than a plain running max so that a single blown landmark moves
        // it a few percent instead of latching it high for the rest of the session.
        //
        // Advances on the CAMERA clock: stepping it every rendered frame over a held sample would converge it
        // many times faster than intended.
        //
        // The range test is written as `!(measured > lo && measured < hi)` on purpose. A NaN fails EVERY ordered
        // comparison, so it takes the reject branch here; phrased the natural way round it would sail through, and
        // the old `Mathf.Max(stored, NaN)` returned NaN — poisoning the arm length PERMANENTLY, which made
        // `reach = avatarArm / NaN` NaN and every target after it, ending in a Burst abort inside the IK.
        private float TrackUserArm(bool left, Vector3 shoulder, Vector3 elbow, Vector3 wrist, in MediaPipeTiming timing)
        {
            float stored = left ? _leftUserArm : _rightUserArm;
            if (!timing.IsNewSample) return stored;

            Vector3 upper = elbow - shoulder;
            Vector3 fore = wrist - elbow;
            float measured = upper.magnitude + fore.magnitude;
            if (!(measured > 1e-3f && measured < 100f)) return stored;

            if (!(stored > 1e-4f))
            {
                // Nothing to compare against yet. Take whatever we have, even if it is a poor look — a first
                // reading that is too short is corrected within a third of a second of the first good one.
                if (left) _leftUserArm = measured;
                else _rightUserArm = measured;
                return measured;
            }

            // Learn only from readings the camera was actually in a position to take. `trust` is 1 for an arm
            // lying in the image plane, where its length is plainly visible, and falls to 0 as the arm swings
            // round to point at the lens, where MediaPipe is guessing at depth and reads it short.
            //
            // This is why a slower decay could never have been the fix: foreshortening lasts as long as you hold
            // your arms out in front of you, which is minutes, so any time constant slow enough to survive it is
            // indistinguishable from never adapting at all. The estimate must decay on EVIDENCE, not on a clock.
            // Simply decline to learn from a measurement that cannot know what it is measuring.
            float trust = 1f - Foreshortening(upper, fore);
            if (!(trust > 0f)) return stored;   // NaN-safe: this frame teaches us nothing

            // An arm does not get a quarter longer between two samples. A reading that says it did is a landmark
            // that has come off the elbow, not a longer arm — and because the rise is the fast direction,
            // believing it even briefly is what would latch the estimate high.
            if (measured > stored * ArmLengthMaxJump) return stored;

            // Partial foreshortening still biases short, so on top of the gate, prefer the longest credible
            // reading: rise readily, let go reluctantly.
            float hz = (measured > stored ? ArmLengthRiseHz : ArmLengthFallHz) * trust;
            float updated = Mathf.Lerp(stored, measured, 1f - Mathf.Exp(-hz * timing.SampleDelta));

            if (left) _leftUserArm = updated;
            else _rightUserArm = updated;
            return updated;
        }

        /// <summary>
        /// How much of the arm points AT the camera: 0 when it lies in the image plane (its length is fully
        /// visible), 1 when it aims straight down the lens. Depth is MediaPipe's weakest axis, so a segment lying
        /// along camera Z is precisely the segment whose length it cannot see — and it reads it SHORT. The worse
        /// of the two segments governs, because either one coming back short shortens the whole chain.
        /// </summary>
        private static float Foreshortening(Vector3 upper, Vector3 fore)
        {
            float worst = Mathf.Max(DepthAlignment(upper), DepthAlignment(fore));

            // Within ~20 deg of the image plane the reading is honest enough to learn from at full rate; past
            // ~70 deg it says almost nothing about its own length. Ramp smoothly between, so the estimate never
            // flips between learning and not learning from one frame to the next.
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((worst - 0.34f) / 0.60f));
        }

        private static float DepthAlignment(Vector3 segment)
        {
            float len = segment.magnitude;
            return len > 1e-4f ? Mathf.Abs(segment.z) / len : 1f;   // degenerate segment: trust it with nothing
        }

        private static Quaternion LookFrom(Vector3 forward, Vector3 up) =>
            forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward.normalized, up) : Quaternion.identity;
    }
}
