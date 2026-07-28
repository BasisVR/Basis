using Basis.MediaPipe;
using NUnit.Framework;
using UnityEngine;

namespace Basis.MediaPipe.Tests
{
    /// <summary>
    /// The coordinate normalization and the hand-rotation retarget. Between them these cover every sign that
    /// used to be hand-tuned: the selfie mirror, MediaPipe's y-down axis, its depth direction, and the palm
    /// frame that was previously hung off the hips.
    /// </summary>
    public class MediaPipeSpaceTests
    {
        private static Vector3[] Body() => MediaPipeArmRetargetTests.HandAtFace();

        [Test]
        public void BodyFrame_ForwardPointsOutOfTheChest()
        {
            Vector3[] pose = Body();
            Assert.IsTrue(MediaPipeSpace.TryBodyFrame(pose, out Vector3 shoulderCenter, out Quaternion frame));

            Vector3 forward = frame * Vector3.forward;
            Vector3 faceDir = pose[MediaPipeSpace.Nose] - shoulderCenter;
            Vector3 shoulderAxis = (pose[MediaPipeSpace.RightShoulder] - pose[MediaPipeSpace.LeftShoulder]).normalized;

            Assert.Greater(Vector3.Dot(forward, faceDir.normalized), 0.2f,
                "the body frame's forward must agree with the direction the face points");
            Assert.Less(forward.z, 0f,
                "the user is facing the camera, so their forward runs toward it (-Z)");
            Assert.Greater(Vector3.Dot(frame * Vector3.up, Vector3.up), 0.9f, "torso up should point up");
            Assert.Greater(Vector3.Dot(frame * Vector3.right, shoulderAxis), 0.9f,
                "the frame's right must match the shoulder line (left shoulder to right shoulder)");
        }

        /// <summary>
        /// Depth is never sign-corrected — MediaPipe's z already runs away from the camera, matching Unity's +Z.
        /// This pins the consequence the whole retarget leans on: a hand held toward the camera reads as being
        /// in FRONT of the torso, so it lands in front of the avatar.
        /// </summary>
        [Test]
        public void HandTowardTheCamera_ReadsAsInFrontOfTheTorso()
        {
            Vector3[] pose = Body();
            Assert.IsTrue(MediaPipeSpace.TryBodyFrame(pose, out _, out Quaternion frame));

            Vector3 shoulder = pose[MediaPipeSpace.RightShoulder];
            Vector3 wrist = pose[MediaPipeSpace.RightWrist];
            Vector3 inBody = Quaternion.Inverse(frame) * (wrist - shoulder);

            Assert.Less(wrist.z, shoulder.z, "the raised hand is nearer the camera than the shoulder");
            Assert.Greater(inBody.z, 0f,
                "and must therefore read as forward of the torso; negative here is the 'arms behind the body' bug");
        }

        [Test]
        public void SwapPoseSides_ExchangesAnatomicalPairs()
        {
            Vector3[] pose = Body();
            Vector3 leftShoulder = pose[MediaPipeSpace.LeftShoulder];
            Vector3 rightShoulder = pose[MediaPipeSpace.RightShoulder];

            MediaPipeSpace.SwapPoseSidesInPlace(pose);

            Assert.AreEqual(rightShoulder, pose[MediaPipeSpace.LeftShoulder]);
            Assert.AreEqual(leftShoulder, pose[MediaPipeSpace.RightShoulder]);
            Assert.AreEqual(Body()[MediaPipeSpace.Nose], pose[MediaPipeSpace.Nose], "the nose has no opposite side");
        }

        [Test]
        public void World_FlipsYAndUndoesTheMirror()
        {
            Vector3 raw = new Vector3(0.3f, 0.7f, 0.2f);

            Assert.AreEqual(new Vector3(0.3f, -0.7f, 0.2f), MediaPipeSpace.World(raw, false));
            Assert.AreEqual(new Vector3(-0.3f, -0.7f, 0.2f), MediaPipeSpace.World(raw, true));
        }

        [Test]
        public void Image_KeepsLandmarksInRangeWithYUp()
        {
            Vector3 raw = new Vector3(0.25f, 0.75f, 0.1f);

            Vector3 plain = MediaPipeSpace.Image(raw, false);
            Assert.AreEqual(0.25f, plain.x, 1e-5f);
            Assert.AreEqual(0.25f, plain.y, 1e-5f);

            Vector3 mirrored = MediaPipeSpace.Image(raw, true);
            Assert.AreEqual(0.75f, mirrored.x, 1e-5f);
            Assert.AreEqual(0.25f, mirrored.y, 1e-5f);
        }
    }

    /// <summary>
    /// Torso lean/twist. The converter used to apply its own y-flip on top of the one the backend now does,
    /// which turned the torso frame upside-down; and what it returns is an OFFSET, so writing it straight onto
    /// the chest tracker pinned the chest instead of letting it ride on the body.
    /// </summary>
    public class MediaPipeBodyConverterTests
    {
        private static readonly MediaPipeTiming Timing = new MediaPipeTiming(1f / 60f, 1f / 15f, true);

        private static BasisMediaPipeResult Result(Vector3[] pose) =>
            new BasisMediaPipeResult { HasPose = true, PoseWorldLandmarks = pose };

        private static MediaPipeBodyConverter Converter(float strength = 1f) =>
            new MediaPipeBodyConverter { Smoothing = 0f, Strength = strength };

        private static Vector3[] Turned(Quaternion by)
        {
            Vector3[] neutral = MediaPipeArmRetargetTests.ArmDown();
            Vector3[] turned = new Vector3[neutral.Length];
            for (int i = 0; i < neutral.Length; i++)
            {
                turned[i] = by * neutral[i];
            }
            return turned;
        }

        private static Vector3 OffsetEuler(Quaternion by, float strength = 1f)
        {
            MediaPipeBodyConverter converter = Converter(strength);
            BasisMediaPipeResult rest = Result(MediaPipeArmRetargetTests.ArmDown());
            BasisMediaPipeResult moved = Result(Turned(by));

            converter.Calibrate(rest);
            Assert.IsTrue(converter.TryGetTorsoOffset(in moved, in Timing, out Quaternion offset));

            Vector3 euler = offset.eulerAngles;
            return new Vector3(Signed(euler.x), Signed(euler.y), Signed(euler.z));
        }

        private static float Signed(float angle) => angle > 180f ? angle - 360f : angle;

        [Test]
        public void TorsoFrameIsNotUpsideDown()
        {
            Vector3[] pose = MediaPipeArmRetargetTests.ArmDown();
            Assert.IsTrue(MediaPipeSpace.TryBodyFrame(pose, out _, out Quaternion frame));

            Assert.Greater(Vector3.Dot(frame * Vector3.up, Vector3.up), 0.9f,
                "a second y-flip on already-converted landmarks turns the torso frame over; this pins that");
        }

        [Test]
        public void NeutralTorso_ProducesNoOffset()
        {
            MediaPipeBodyConverter converter = Converter();
            BasisMediaPipeResult neutral = Result(MediaPipeArmRetargetTests.ArmDown());

            converter.Calibrate(neutral);
            Assert.IsTrue(converter.TryGetTorsoOffset(in neutral, in Timing, out Quaternion offset));

            Assert.Less(Quaternion.Angle(offset, Quaternion.identity), 0.1f,
                "sitting in the calibration pose must leave the chest exactly where the body puts it");
        }

        [Test]
        public void TwistingYourTorso_TurnsTheChestTheSameWay()
        {
            float yaw = OffsetEuler(Quaternion.AngleAxis(25f, Vector3.up)).y;

            Assert.That(yaw, Is.EqualTo(25f).Within(2f),
                $"a 25 degree torso twist should come back as a 25 degree yaw, got {yaw:F1}");
        }

        /// <summary>Side-lean used to be discarded outright (`Quaternion.Euler(lean, twist, 0f)`), and for someone
        /// sitting at a webcam it is the most visible thing their torso does.</summary>
        [Test]
        public void LeaningSideways_RollsTheChest()
        {
            float roll = OffsetEuler(Quaternion.AngleAxis(20f, Vector3.forward)).z;

            Assert.That(Mathf.Abs(roll), Is.EqualTo(20f).Within(3f),
                $"a 20 degree side lean must reach the chest, got {roll:F1}");
        }

        [Test]
        public void ChestMotionStrength_ScalesTheOffset()
        {
            float full = OffsetEuler(Quaternion.AngleAxis(25f, Vector3.up), 1f).y;
            float half = OffsetEuler(Quaternion.AngleAxis(25f, Vector3.up), 0.5f).y;

            Assert.That(half, Is.EqualTo(full * 0.5f).Within(1f),
                "Chest Motion must scale the offset, so it can read as a suggestion rather than a copy");
        }

        [Test]
        public void AWildFrameCannotThrowTheTorso()
        {
            Vector3 euler = OffsetEuler(Quaternion.AngleAxis(150f, Vector3.up));

            Assert.LessOrEqual(Mathf.Abs(euler.y), 36f,
                "each axis is clamped before Strength, so a bad frame cannot put the chest somewhere a spine will not go");
        }
    }

    /// <summary>
    /// The wrist rotation. It used to be built on the HIPS rotation, so a hand at rest adopted the body's
    /// orientation instead of the hand bone's. It is now a change of basis between the user's palm frame and
    /// the avatar's, corrected by a constant derived from the avatar's own knuckles.
    /// </summary>
    public class MediaPipeHandRotationTests
    {
        private static readonly Quaternion AvatarHandRest = Quaternion.Euler(20f, 35f, 50f);
        private static readonly Vector3 AvatarHandPos = new Vector3(0.4f, 1.1f, 0f);

        private static readonly Vector3 MiddleOffset = new Vector3(0.09f, 0f, 0f);
        private static readonly Vector3 IndexOffset = new Vector3(0.085f, 0f, 0.02f);
        private static readonly Vector3 PinkyOffset = new Vector3(0.08f, 0f, -0.02f);

        private static Vector3[] Body() => MediaPipeArmRetargetTests.ArmDown();

        private static Quaternion UserBodyFrame()
        {
            Assert.IsTrue(MediaPipeSpace.TryBodyFrame(Body(), out _, out Quaternion frame));
            return frame;
        }

        /// <summary>A hand whose knuckles sit exactly where the avatar's do, rotated by <paramref name="orientation"/>.</summary>
        private static Vector3[] Hand(Quaternion orientation)
        {
            Vector3[] hand = new Vector3[MediaPipeSpace.HandCount];
            hand[MediaPipeSpace.HandWrist] = Vector3.zero;
            hand[MediaPipeSpace.HandMiddleMcp] = orientation * MiddleOffset;
            hand[MediaPipeSpace.HandIndexMcp] = orientation * IndexOffset;
            hand[MediaPipeSpace.HandPinkyMcp] = orientation * PinkyOffset;
            return hand;
        }

        private static MediaPipeHandConverter.AvatarHandRig Rig()
        {
            Assert.IsTrue(MediaPipeSpace.TryPalmFrame(
                AvatarHandPos,
                AvatarHandPos + AvatarHandRest * IndexOffset,
                AvatarHandPos + AvatarHandRest * MiddleOffset,
                AvatarHandPos + AvatarHandRest * PinkyOffset,
                false, out Quaternion palm));

            Quaternion correction = Quaternion.Inverse(palm) * AvatarHandRest;
            return new MediaPipeHandConverter.AvatarHandRig
            {
                Body = Quaternion.LookRotation(Vector3.forward, Vector3.up),
                LeftCorrection = correction,
                RightCorrection = correction,
                // Identity on purpose: these tests cover the palm -> hand-bone RETARGET, not the cancellation of the
                // IK's own palm->bone offset (which needs a live rig). Identity == "no cancellation", so the
                // expectations below are unchanged. Set explicitly rather than left to default -- a Quaternion's
                // default is (0,0,0,0), which would zero the rotation outright.
                LeftIkOffsetInverse = Quaternion.identity,
                RightIkOffsetInverse = Quaternion.identity,
                Valid = true,
            };
        }

        /// <summary>
        /// Holds the hand at <paramref name="handInBody"/> relative to the USER's torso and returns the rotation
        /// the avatar's hand bone is given. The landmarks live in camera space, so the body-relative orientation
        /// is lifted into it first — which is the whole point: what transfers is the pose relative to the body,
        /// not relative to the camera.
        /// </summary>
        private static Quaternion Retarget(Quaternion handInBody)
        {
            BasisMediaPipeResult result = new BasisMediaPipeResult
            {
                HasPose = true,
                HasRightHand = true,
                PoseWorldLandmarks = Body(),
                RightHandWorldLandmarks = Hand(UserBodyFrame() * handInBody),
            };

            MediaPipeHandConverter converter = new MediaPipeHandConverter { UseRotation = true, PoseSmoothing = 0f };
            MediaPipeHandConverter.AvatarHandRig rig = Rig();

            Assert.IsTrue(converter.TryGetHandRotation(in result, in rig, false, new MediaPipeTiming(1f / 60f, 1f / 15f, true), out Quaternion rotation));
            return rotation;
        }

        [Test]
        public void HandMatchingTheAvatarsPose_ReproducesTheHandBoneRotation()
        {
            Quaternion rotation = Retarget(AvatarHandRest);

            Assert.Less(Quaternion.Angle(rotation, AvatarHandRest), 0.1f,
                $"a palm held like the avatar's should reproduce its hand rotation, got {rotation.eulerAngles}");
        }

        [Test]
        public void RotatingYourWrist_RotatesTheAvatarsWristTheSameWay()
        {
            Quaternion twist = Quaternion.AngleAxis(40f, Vector3.up);
            Quaternion rotation = Retarget(twist * AvatarHandRest);

            Assert.Less(Quaternion.Angle(rotation, twist * AvatarHandRest), 0.1f,
                "a wrist rotation in body space should apply the same rotation to the avatar's hand");
        }

        [Test]
        public void RollingYourPalmOver_RollsTheAvatarsPalmOver()
        {
            Quaternion roll = Quaternion.AngleAxis(90f, Vector3.forward);
            Quaternion rotation = Retarget(roll * AvatarHandRest);

            Assert.Less(Quaternion.Angle(rotation, roll * AvatarHandRest), 0.1f,
                "palm roll must not come out inverted, which is what the y-down palm normal used to cause");
        }

        [Test]
        public void RotationIsNotTheBodyRotation()
        {
            Quaternion rotation = Retarget(AvatarHandRest);

            Assert.Greater(Quaternion.Angle(rotation, Quaternion.identity), 5f,
                "the old code anchored the wrist to the hips, so a neutral hand collapsed onto the body rotation");
        }

        /// <summary>
        /// Why the hand's side may not be guessed from a label. Chirality is baked into the palm frame — the
        /// normal is negated for a left hand — so reading a hand as the wrong side returns a frame rolled 180
        /// degrees about the forearm. The position still lands correctly, which is what makes it present as
        /// "the arms work but the wrists are wrong".
        /// </summary>
        [Test]
        public void ReadingAHandAsTheWrongSide_RollsThePalmOver()
        {
            Vector3[] hand = Hand(Quaternion.identity);

            Assert.IsTrue(MediaPipeSpace.TryPalmFrame(hand[MediaPipeSpace.HandWrist], hand[MediaPipeSpace.HandIndexMcp],
                hand[MediaPipeSpace.HandMiddleMcp], hand[MediaPipeSpace.HandPinkyMcp], false, out Quaternion asRight));
            Assert.IsTrue(MediaPipeSpace.TryPalmFrame(hand[MediaPipeSpace.HandWrist], hand[MediaPipeSpace.HandIndexMcp],
                hand[MediaPipeSpace.HandMiddleMcp], hand[MediaPipeSpace.HandPinkyMcp], true, out Quaternion asLeft));

            Assert.That(Quaternion.Angle(asRight, asLeft), Is.EqualTo(180f).Within(1f),
                "a mislabelled hand must show up as a 180 degree wrist roll, which is why the side is matched "
                + "against the pose wrists rather than taken from the handedness label");
        }

        [Test]
        public void PoseAloneCanStillOrientTheWrist()
        {
            Vector3[] pose = MediaPipeArmRetargetTests.HandAtFace();

            Assert.IsTrue(MediaPipeSpace.TryPoseHandFrame(pose, false, out Quaternion frame),
                "the pose carries a wrist, an index knuckle and a pinky knuckle, which is enough for a palm frame");
            Assert.That(Quaternion.Angle(frame, Quaternion.identity), Is.GreaterThan(0f));
        }
    }
}
