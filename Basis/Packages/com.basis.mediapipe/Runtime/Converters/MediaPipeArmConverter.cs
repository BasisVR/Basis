using Basis.IK;
using UnityEngine;
namespace Basis.MediaPipe
{
    public sealed class MediaPipeArmConverter
    {
        public float HeadAnchor = 1f, MaxReach = 0.98f;
        public bool RejectGlitches = true;
        private const float ElbowOutward = 0.3f, BendTrustStart = 0.15f, BendTrustFull = 0.5f, MaxElbowTrust = 0.5f, ElbowTrustHz = 2f;
        // Asymmetric, because the measurement error is asymmetric — see TrackUserArm.
        private const float ArmLengthRiseHz = 3f;        // a straight side-on arm locks in within ~a third of a second
        private const float ArmLengthFallHz = 0.1f;      // ~10s to let go: only for a bad initial lock or a new user
        private const float ArmLengthMaxJump = 1.25f;    // an arm cannot grow 25% between samples; that is a blown landmark
        private const float FocalHeight = 0.8156f, MinDepth = 0.1f, MaxDepth = 5f, MinHandImage = 0.01f;
        public const float MinPoseWristVisibility = 0.5f;
        private const float TypicalUserArm = 0.6f, PoseAgreementPalms = 2f;
        private const float FaceBlendHz = 5f, FaceMinCutoff = 1.5f, FaceDepthMinCutoff = 0.7f, FaceSpeedBeta = 8f, FaceSpeedCutoff = 1f;
        private static readonly (int from, int to)[] HandSpans = { (0, 5), (0, 9), (0, 13), (0, 17), (5, 9), (9, 13), (13, 17) };
        private static readonly int[] PalmPoints = { 0, 5, 9, 13, 17 };
        private MediaPipePositionFilter leftWrist, leftElbow, rightWrist, rightElbow;
        private FaceRelativeHand leftFace, rightFace;
        public struct HandFaceInput
        {
            public Vector3[] HandImage, HandWorld;
            public Vector2 FaceImage, PoseWristImage;
            public float FaceDepth, Aspect;
            public bool HasPoseWrist;
        }
        private struct FaceRelativeHand
        {
            public MediaPipePositionFilter Filter;
            public Vector3 Smoothed, Speed, Target;
            public float Weight;
            public bool HasSmoothed;
            public Vector3 Smooth(Vector3 x, float dt)
            {
                if (!HasSmoothed || !MediaPipeSpace.IsFinite(Smoothed))
                {
                    Smoothed = x;
                    Speed = Vector3.zero;
                    HasSmoothed = true;
                    return x;
                }
                dt = Mathf.Max(dt, 1e-4f);
                Speed = Vector3.Lerp(Speed, (x - Smoothed) / dt, CutoffAlpha(FaceSpeedCutoff, dt));
                float planar = CutoffAlpha(FaceMinCutoff + FaceSpeedBeta * new Vector2(Speed.x, Speed.y).magnitude, dt);
                float depth = CutoffAlpha(FaceDepthMinCutoff + FaceSpeedBeta * Mathf.Abs(Speed.z), dt);
                Smoothed = new Vector3(Mathf.Lerp(Smoothed.x, x.x, planar), Mathf.Lerp(Smoothed.y, x.y, planar), Mathf.Lerp(Smoothed.z, x.z, depth));
                return Smoothed;
            }
        }
        private float leftUserArm, rightUserArm, leftElbowTrust, rightElbowTrust;
        public struct AvatarArmRig
        {
            public Vector3 LeftAnchor, RightAnchor;
            public float LeftUpperLen, LeftForeLen;
            public float RightUpperLen, RightForeLen;
            public Vector3 Right, Up, Forward;
            public Vector3 HeadLocal, FaceLocal;
            public bool Valid;
        }
        public int RejectedSamples => leftWrist.Rejected + leftElbow.Rejected + rightWrist.Rejected + rightElbow.Rejected;
        private float MaxSpeed => RejectGlitches ? MediaPipeFilterMath.MaxHandSpeed : 0f;
        public bool TryGetArm(Vector3[] pose, float[] visibility, in AvatarArmRig rig, bool avatarLeft, in MediaPipeTiming timing, out Vector3 wristLocal, out Vector3 elbowLocal, out Quaternion wristRotation)
            => TryGetArm(pose, visibility, default, in rig, avatarLeft, in timing, out wristLocal, out elbowLocal, out wristRotation);
        public bool TryGetArm(Vector3[] pose, float[] visibility, in HandFaceInput handFace, in AvatarArmRig rig, bool avatarLeft, in MediaPipeTiming timing, out Vector3 wristLocal, out Vector3 elbowLocal, out Quaternion wristRotation)
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
            float foreLen = avatarLeft ? rig.LeftForeLen : rig.RightForeLen, avatarArm = upperLen + foreLen;
            if (!(userArm > 1e-3f) || !(avatarArm > 1e-4f)) return false;

            Quaternion toBody = Quaternion.Inverse(bodyFrame);
            Vector3 wristBody = toBody * (wrist - shoulder), elbowBody = toBody * (elbow - shoulder);
            Vector3 headBody = toBody * (pose[MediaPipeSpace.Nose] - shoulder);
            float maxSpeed = MaxSpeed;
            wristBody = avatarLeft ? leftWrist.Apply(wristBody, in timing, maxSpeed) : rightWrist.Apply(wristBody, in timing, maxSpeed);
            elbowBody = avatarLeft ? leftElbow.Apply(elbowBody, in timing, maxSpeed) : rightElbow.Apply(elbowBody, in timing, maxSpeed);

            Vector3 anchor = avatarLeft ? rig.LeftAnchor : rig.RightAnchor;
            float reach = avatarArm / userArm;
            float lift = VerticalScale(headBody, Vector3.Dot(rig.HeadLocal - anchor, rig.Up), wristBody.y, reach);
            Vector3 wristTarget = Place(anchor, wristBody, reach, lift, in rig);
            wristTarget = ClampReach(anchor, BlendFaceWrist(in handFace, in rig, avatarLeft, reach, wristTarget, in timing), avatarArm);

            // The elbow scales UNIFORMLY by reach — never by lift. lift is the head-anchor scale derived from the
            // WRIST's height; it exists to pull the HAND up to the avatar's face and means nothing for the elbow.
            // Applying it here stretches the elbow's vertical component against its lateral/forward ones, which
            // does not merely move the elbow — it ROTATES the swivel, and the swivel is the whole ballgame (the
            // solver reads only the hint's DIRECTION in the swing plane).
            Vector3 measuredElbow = Place(anchor, elbowBody, reach, reach, in rig);

            wristLocal = wristTarget;
            elbowLocal = SolveElbow(anchor, wristTarget, measuredElbow, upperLen, foreLen, in rig, avatarLeft, SmoothElbowTrust(avatarLeft, ElbowTrust(visibility, avatarLeft, elbowBody, wristBody), timing.RenderDelta));
            wristRotation = LookFrom(wristLocal - elbowLocal, rig.Up);
            return true;
        }
        public bool TryGetArmFromFace(in HandFaceInput handFace, in AvatarArmRig rig, bool avatarLeft, in MediaPipeTiming timing, out Vector3 wristLocal, out Quaternion wristRotation)
        {
            wristLocal = Vector3.zero;
            wristRotation = Quaternion.identity;
            ref FaceRelativeHand face = ref avatarLeft ? ref leftFace : ref rightFace;
            float avatarArm = avatarLeft ? rig.LeftUpperLen + rig.LeftForeLen : rig.RightUpperLen + rig.RightForeLen;
            float userArm = avatarLeft ? leftUserArm : rightUserArm;
            float reach = avatarArm / (userArm > 1e-4f ? userArm : TypicalUserArm);
            if (!rig.Valid || !(avatarArm > 1e-4f) || !TryUpdateFaceTarget(ref face, in handFace, in rig, reach, in timing))
            {
                face = default;
                return false;
            }

            face.Weight = 1f;
            if (avatarLeft) { leftWrist.Reset(); leftElbow.Reset(); }
            else { rightWrist.Reset(); rightElbow.Reset(); }

            Vector3 anchor = avatarLeft ? rig.LeftAnchor : rig.RightAnchor;
            wristLocal = ClampReach(anchor, face.Target, avatarArm);
            wristRotation = LookFrom(wristLocal - anchor, rig.Up);
            return true;
        }
        public void Reset()
        {
            leftWrist.Reset();
            leftElbow.Reset();
            rightWrist.Reset();
            rightElbow.Reset();
            leftFace = rightFace = default;
            leftUserArm = rightUserArm = leftElbowTrust = rightElbowTrust = 0f;
        }
        private Vector3 BlendFaceWrist(in HandFaceInput handFace, in AvatarArmRig rig, bool avatarLeft, float reach, Vector3 poseTarget, in MediaPipeTiming timing)
        {
            ref FaceRelativeHand face = ref avatarLeft ? ref leftFace : ref rightFace;
            bool measured = TryUpdateFaceTarget(ref face, in handFace, in rig, reach, in timing);
            if (!measured && face.Weight <= 0f)
            {
                face = default;
                return poseTarget;
            }
            face.Weight = Approach(face.Weight, measured ? 1f : 0f, FaceBlendHz, timing.RenderDelta);
            if (face.Weight < 0.001f) face.Weight = 0f;
            else if (face.Weight > 0.999f) face.Weight = 1f;
            return Vector3.Lerp(poseTarget, face.Target, face.Weight);
        }
        private bool TryUpdateFaceTarget(ref FaceRelativeHand face, in HandFaceInput handFace, in AvatarArmRig rig, float reach, in MediaPipeTiming timing)
        {
            if (!TryMeasureHandFromFace(in handFace, out Vector3 offset)) return false;
            offset = face.Smooth(face.Filter.Apply(offset, in timing, MaxSpeed), timing.RenderDelta);
            face.Target = rig.FaceLocal + (offset.x * rig.Right + offset.y * rig.Up + offset.z * rig.Forward) * reach;
            return true;
        }
        public static bool TryMeasureHandFromFace(in HandFaceInput input, out Vector3 offset)
        {
            offset = Vector3.zero;
            if (input.HandImage == null || input.HandWorld == null || input.HandImage.Length < MediaPipeSpace.HandCount
                || input.HandWorld.Length < MediaPipeSpace.HandCount || !(input.Aspect > 0f) || !InDepthRange(input.FaceDepth))
            {
                return false;
            }
            if (!AgreesWithPoseWrist(in input)) return false;

            float metric = 0f, image = 0f;
            foreach ((int from, int to) in HandSpans)
            {
                Vector3 w = input.HandWorld[to] - input.HandWorld[from], p = input.HandImage[to] - input.HandImage[from];
                metric += new Vector2(w.x, w.y).magnitude;
                image += new Vector2(p.x * input.Aspect, p.y).magnitude;
            }
            if (!(image > MinHandImage) || !(metric > 0f)) return false;

            float handScale = metric / image, handDepth = FocalHeight * handScale;
            if (!InDepthRange(handDepth)) return false;

            Vector3 palmImage = Vector3.zero, palmWorld = Vector3.zero;
            foreach (int i in PalmPoints)
            {
                palmImage += input.HandImage[i];
                palmWorld += input.HandWorld[i];
            }
            palmImage /= PalmPoints.Length;
            palmWorld /= PalmPoints.Length;

            Vector2 hand = ImageToMetric(palmImage, input.Aspect, handScale) + (Vector2)(input.HandWorld[MediaPipeSpace.HandWrist] - palmWorld);
            Vector2 face = ImageToMetric(input.FaceImage, input.Aspect, input.FaceDepth / FocalHeight);
            offset = new Vector3(face.x - hand.x, hand.y - face.y, input.FaceDepth - handDepth);
            return MediaPipeSpace.IsFinite(offset);
        }
        private static bool AgreesWithPoseWrist(in HandFaceInput input)
        {
            if (!input.HasPoseWrist) return true;
            Vector2 wrist = input.HandImage[MediaPipeSpace.HandWrist];
            float palm = Vector2.Scale((Vector2)input.HandImage[MediaPipeSpace.HandMiddleMcp] - wrist, new Vector2(input.Aspect, 1f)).magnitude;
            float gap = Vector2.Scale(input.PoseWristImage - wrist, new Vector2(input.Aspect, 1f)).magnitude;
            return gap <= PoseAgreementPalms * palm;
        }
        private static bool InDepthRange(float depth) => depth > MinDepth && depth < MaxDepth;
        private static Vector2 ImageToMetric(Vector2 image, float aspect, float scale) => new Vector2((image.x - 0.5f) * aspect, image.y - 0.5f) * scale;
        private static float CutoffAlpha(float cutoff, float dt) => 1f / (1f + 1f / (2f * Mathf.PI * cutoff * dt));
        private static float Approach(float current, float target, float hz, float dt) => Mathf.Lerp(current, target, 1f - Mathf.Exp(-hz * Mathf.Max(dt, 1e-4f)));
        private float SmoothElbowTrust(bool avatarLeft, float target, float dt)
        {
            float trust = avatarLeft ? leftElbowTrust : rightElbowTrust;
            trust = Approach(trust, target, ElbowTrustHz, dt);
            if (avatarLeft) leftElbowTrust = trust;
            else rightElbowTrust = trust;
            return trust;
        }
        public static float ElbowTrust(float[] visibility, bool avatarLeft, Vector3 elbowBody, Vector3 wristBody)
        {
            float arm = MediaPipeSpace.ArmVisibility(visibility, avatarLeft), torso = MediaPipeSpace.TorsoVisibility(visibility);
            float seen = arm < 0f || torso < 0f ? 1f : Mathf.Clamp01((Mathf.Min(arm, torso) - 0.5f) / 0.4f);
            Vector3 upper = elbowBody, fore = wristBody - elbowBody;
            if (upper.sqrMagnitude < 1e-8f || fore.sqrMagnitude < 1e-8f) return 0f;
            float bend = Vector3.Cross(upper.normalized, fore.normalized).magnitude;
            return MaxElbowTrust * seen * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(BendTrustStart, BendTrustFull, bend));
        }
        private static Vector3 SolveElbow(Vector3 shoulder, Vector3 wrist, Vector3 measured, float upperLen, float foreLen, in AvatarArmRig rig, bool avatarLeft, float trust)
        {
            Vector3 toWrist = wrist - shoulder;
            float span = toWrist.magnitude;
            if (span < 1e-4f) return measured;

            Vector3 axis = toWrist / span;
            float along = span >= upperLen + foreLen - 1e-4f ? upperLen : (upperLen * upperLen - foreLen * foreLen + span * span) / (2f * span);
            Vector3 center = shoulder + axis * along;
            float radiusSq = upperLen * upperLen - along * along;
            if (radiusSq <= 1e-8f) return center;

            Vector3 outward = avatarLeft ? -rig.Right : rig.Right, toHand = toWrist / Mathf.Max(upperLen + foreLen, 1e-4f);
            Vector3 prior = BasisArmPriorModel.ElbowDir(new Vector3(Vector3.Dot(toHand, outward), Vector3.Dot(toHand, rig.Up), Vector3.Dot(toHand, rig.Forward)));
            Vector3 rest = Vector3.ProjectOnPlane(outward * prior.x + rig.Up * prior.y + rig.Forward * prior.z, axis);
            if (rest.sqrMagnitude < 1e-8f) rest = Vector3.ProjectOnPlane(-rig.Up + outward * ElbowOutward, axis);
            Vector3 swivel = Vector3.ProjectOnPlane(measured - center, axis);
            bool hasRest = rest.sqrMagnitude > 1e-8f, hasSwivel = swivel.sqrMagnitude > 1e-8f;
            if (!hasSwivel && !hasRest) return center;

            if (!hasSwivel)
            {
                swivel = rest;
            }
            else if (hasRest)
            {
                swivel = Vector3.Slerp(rest.normalized, swivel.normalized, Mathf.Clamp01(trust));
            }
            if (swivel.sqrMagnitude < 1e-8f) return center;

            return center + swivel.normalized * Mathf.Sqrt(radiusSq);
        }
        private float VerticalScale(Vector3 headBody, float avatarHeadUp, float wristUp, float reach)
        {
            if (HeadAnchor <= 0f || headBody.y < 1e-3f || avatarHeadUp < 1e-4f) return reach;

            float headScale = avatarHeadUp / headBody.y;
            float t = Mathf.Clamp01(wristUp / headBody.y) * Mathf.Clamp01(HeadAnchor);
            return Mathf.Lerp(reach, headScale, t);
        }
        private static Vector3 Place(Vector3 anchor, Vector3 body, float reach, float lift, in AvatarArmRig rig) => anchor + (body.x * reach) * rig.Right + (body.y * lift) * rig.Up + (body.z * reach) * rig.Forward;
        private Vector3 ClampReach(Vector3 anchor, Vector3 target, float limit)
        {
            Vector3 delta = target - anchor;
            float max = limit * Mathf.Max(0.1f, MaxReach), distance = delta.magnitude;
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
            float stored = left ? leftUserArm : rightUserArm;
            if (!timing.IsNewSample) return stored;

            Vector3 upper = elbow - shoulder, fore = wrist - elbow;
            float measured = upper.magnitude + fore.magnitude;
            if (!(measured > 1e-3f && measured < 100f)) return stored;

            if (!(stored > 1e-4f))
            {
                // Nothing to compare against yet. Take whatever we have, even if it is a poor look — a first
                // reading that is too short is corrected within a third of a second of the first good one.
                if (left) leftUserArm = measured;
                else rightUserArm = measured;
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

            if (left) leftUserArm = updated;
            else rightUserArm = updated;
            return updated;
        }
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
        private static Quaternion LookFrom(Vector3 forward, Vector3 up) => forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward.normalized, up) : Quaternion.identity;
    }
}
