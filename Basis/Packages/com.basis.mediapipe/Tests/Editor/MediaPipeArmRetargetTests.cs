using Basis.MediaPipe;
using NUnit.Framework;
using UnityEngine;

namespace Basis.MediaPipe.Tests
{
    /// <summary>
    /// Ground truth for the webcam arm retarget.
    ///
    /// These landmarks are in CAMERA space, which is what the converters actually receive: the user stands in
    /// front of the camera FACING it, so their forward is -Z, their right hand is at -X (camera-right is the
    /// user's left), and their nose is nearer the camera (-Z) than their ears. Building the body the other way
    /// round — as if it were an avatar facing +Z — is self-consistent and hides a whole class of sign bugs,
    /// which is exactly how the "arms end up behind the body" regression slipped through.
    ///
    /// The user is a ~1.75 m person with a 0.55 m arm; the avatar is deliberately shorter with a 0.47 m arm,
    /// so nothing passes by accident on matched proportions.
    /// </summary>
    public class MediaPipeArmRetargetTests
    {
        private const float AvatarHipY = 0.88f;
        private const float AvatarShoulderY = 1.24f;
        private const float AvatarHeadY = 1.42f;
        private const float AvatarUpperLen = 0.24f;
        private const float AvatarForeLen = 0.23f;

        /// <summary>
        /// Hip-centred metric landmarks, as MediaPipe reports world landmarks. User faces the camera, arms
        /// hanging. Both arms are posed symmetrically so that reversing the left/right labels changes nothing
        /// but the sides — which isolates the forward flip the reversal causes.
        /// </summary>
        internal static Vector3[] ArmDown()
        {
            Vector3[] pose = new Vector3[MediaPipeSpace.PoseCount];
            pose[MediaPipeSpace.Nose] = new Vector3(0f, 0.58f, -0.10f);
            pose[MediaPipeSpace.LeftEar] = new Vector3(0.08f, 0.62f, 0.02f);
            pose[MediaPipeSpace.RightEar] = new Vector3(-0.08f, 0.62f, 0.02f);
            pose[MediaPipeSpace.LeftShoulder] = new Vector3(0.18f, 0.40f, 0f);
            pose[MediaPipeSpace.RightShoulder] = new Vector3(-0.18f, 0.40f, 0f);
            pose[MediaPipeSpace.LeftHip] = new Vector3(0.12f, 0f, 0f);
            pose[MediaPipeSpace.RightHip] = new Vector3(-0.12f, 0f, 0f);
            pose[MediaPipeSpace.LeftElbow] = new Vector3(0.22f, 0.14f, -0.03f);
            pose[MediaPipeSpace.LeftWrist] = new Vector3(0.24f, -0.12f, -0.06f);
            pose[MediaPipeSpace.LeftIndex] = new Vector3(0.27f, -0.20f, -0.05f);
            pose[MediaPipeSpace.LeftPinky] = new Vector3(0.21f, -0.20f, -0.07f);
            pose[MediaPipeSpace.RightElbow] = new Vector3(-0.22f, 0.14f, -0.03f);
            pose[MediaPipeSpace.RightWrist] = new Vector3(-0.24f, -0.12f, -0.06f);
            pose[MediaPipeSpace.RightIndex] = new Vector3(-0.27f, -0.20f, -0.05f);
            pose[MediaPipeSpace.RightPinky] = new Vector3(-0.21f, -0.20f, -0.07f);
            return pose;
        }

        /// <summary>Both hands held up beside the face: elbows out and up, wrists at the jaw, in front of the chest.</summary>
        internal static Vector3[] HandAtFace()
        {
            Vector3[] pose = ArmDown();
            pose[MediaPipeSpace.LeftElbow] = new Vector3(0.38f, 0.26f, -0.12f);
            pose[MediaPipeSpace.LeftWrist] = new Vector3(0.24f, 0.50f, -0.16f);
            pose[MediaPipeSpace.LeftIndex] = new Vector3(0.22f, 0.58f, -0.18f);
            pose[MediaPipeSpace.LeftPinky] = new Vector3(0.28f, 0.57f, -0.14f);
            pose[MediaPipeSpace.RightElbow] = new Vector3(-0.38f, 0.26f, -0.12f);
            pose[MediaPipeSpace.RightWrist] = new Vector3(-0.24f, 0.50f, -0.16f);
            pose[MediaPipeSpace.RightIndex] = new Vector3(-0.22f, 0.58f, -0.18f);
            pose[MediaPipeSpace.RightPinky] = new Vector3(-0.28f, 0.57f, -0.14f);
            return pose;
        }

        private static Vector3[] WithReversedLabels(Vector3[] pose)
        {
            Vector3[] copy = (Vector3[])pose.Clone();
            MediaPipeSpace.SwapPoseSidesInPlace(copy);
            return copy;
        }

        /// <summary>What the backend does per frame: repair the sides from geometry before anything reads them.</summary>
        private static Vector3[] ResolveSides(Vector3[] pose)
        {
            Vector3[] copy = (Vector3[])pose.Clone();
            if (MediaPipeSpace.SideSwapNeeded(copy) > 0f)
            {
                MediaPipeSpace.SwapPoseSidesInPlace(copy);
            }
            return copy;
        }

        private static MediaPipeArmConverter.AvatarArmRig Rig()
        {
            Vector3 shoulderCenter = new Vector3(0f, AvatarShoulderY, 0f);
            Vector3 head = new Vector3(0f, AvatarHeadY, 0f);
            return new MediaPipeArmConverter.AvatarArmRig
            {
                LeftAnchor = new Vector3(-0.15f, AvatarShoulderY, 0f),
                RightAnchor = new Vector3(0.15f, AvatarShoulderY, 0f),
                LeftUpperLen = AvatarUpperLen,
                LeftForeLen = AvatarForeLen,
                RightUpperLen = AvatarUpperLen,
                RightForeLen = AvatarForeLen,
                Right = Vector3.right,
                Up = Vector3.up,
                Forward = Vector3.forward,
                HeadLocal = head,
                HeadMetric = Vector3.Distance(head, shoulderCenter),
                Valid = true,
            };
        }

       // A fresh camera sample rendered at 60 fps. Both filter stages return their first sample untouched, so a
        // fresh converter is a passthrough and these stay exact.
        private static readonly MediaPipeTiming Timing = new MediaPipeTiming(1f / 60f, 1f / 15f, true);

        private static MediaPipeArmConverter Converter(float headAnchor = 1f) =>
            new MediaPipeArmConverter { Smoothing = 0f, HeadAnchor = headAnchor };

        private static Vector3 Retarget(Vector3[] pose, float headAnchor = 1f)
        {
            MediaPipeArmConverter converter = Converter(headAnchor);
            MediaPipeArmConverter.AvatarArmRig rig = Rig();
            Assert.IsTrue(converter.TryGetArm(pose, in rig, false, in Timing, out Vector3 wrist, out _, out _),
                "pose retarget should succeed on a well-formed body");
            return wrist;
        }

        // The reported bug: the hand landed on a plane around the hips and could never climb past the ribs,
        // so a hand held at the face showed up at the waist. Anything at or below this is that bug returning.
        private const float WaistCeiling = AvatarHipY + 0.35f;

        [Test]
        public void HandBesideFace_LandsBesideAvatarHead_NotAtWaist()
        {
            Vector3 wrist = Retarget(HandAtFace());

            Assert.Greater(wrist.y, WaistCeiling,
                $"wrist at {wrist.y:F3} is still inside the old hips-anchored band (<= {WaistCeiling:F3})");
            Assert.That(wrist.y, Is.EqualTo(AvatarHeadY).Within(0.15f),
                $"hand held at the face should land near the avatar's head ({AvatarHeadY:F3}), got {wrist.y:F3}");
        }

        [Test]
        public void ArmDown_StaysDown()
        {
            Vector3 wrist = Retarget(ArmDown());

            Assert.Less(wrist.y, AvatarShoulderY - 0.3f,
                $"a hanging arm should stay low, got {wrist.y:F3}");
            Assert.Greater(wrist.y, 0.5f, "a hanging arm should not sink through the avatar");
        }

        [Test]
        public void UserRightHand_DrivesAvatarRightSide()
        {
            Vector3 wrist = Retarget(HandAtFace());
            Assert.Greater(wrist.x, 0f, "the user's right hand must stay on the avatar's right (+X)");
        }

        [Test]
        public void HandInFrontOfBody_KeepsItsDepth()
        {
            Vector3 wrist = Retarget(HandAtFace());
            Assert.Greater(wrist.z, 0.02f,
                $"the hand is held in front of the chest, so it should retarget forward (+Z), got {wrist.z:F3}");
        }

        /// <summary>
        /// Sitting closer to the camera, or simply being a bigger person, scales every world landmark. The
        /// retarget divides through by the user's own arm length, so the avatar must not move at all.
        /// </summary>
        [Test]
        public void RetargetIsInvariantToUserScale()
        {
            Vector3[] pose = HandAtFace();
            Vector3[] scaled = new Vector3[pose.Length];
            for (int i = 0; i < pose.Length; i++)
            {
                scaled[i] = pose[i] * 1.35f;
            }

            Vector3 baseline = Retarget(pose);
            Vector3 bigger = Retarget(scaled);

            Assert.That(Vector3.Distance(baseline, bigger), Is.LessThan(1e-4f),
                $"scale changed the target: {baseline} vs {bigger}");
        }

        /// <summary>The head anchor is what pulls a raised hand up to the avatar's face rather than merely
        /// scaling it by arm length, so with it off the hand should sit lower.</summary>
        [Test]
        public void HeadAnchor_LiftsRaisedHandTowardTheHead()
        {
            float anchored = Retarget(HandAtFace(), 1f).y;
            float reachOnly = Retarget(HandAtFace(), 0f).y;

            Assert.Greater(anchored, reachOnly,
                "head anchoring should raise a face-height hand relative to pure reach matching");
            Assert.That(Mathf.Abs(anchored - AvatarHeadY), Is.LessThan(Mathf.Abs(reachOnly - AvatarHeadY)),
                "head anchoring should land closer to the avatar's head than pure reach matching");
        }

        /// <summary>The head anchor must not disturb a hand hanging at the waist, only a raised one.</summary>
        [Test]
        public void HeadAnchor_DoesNotAffectLoweredHand()
        {
            float anchored = Retarget(ArmDown(), 1f).y;
            float reachOnly = Retarget(ArmDown(), 0f).y;

            Assert.That(anchored, Is.EqualTo(reachOnly).Within(1e-5f),
                "below the shoulder the head anchor should fade out entirely");
        }

        [Test]
        public void WristNeverLeavesTheAvatarsReach()
        {
            Vector3[] pose = HandAtFace();
            pose[MediaPipeSpace.RightWrist] = new Vector3(3f, 3f, 3f);

            Vector3 wrist = Retarget(pose);
            float reach = Vector3.Distance(wrist, Rig().RightAnchor);

            Assert.LessOrEqual(reach, AvatarUpperLen + AvatarForeLen,
                $"target {reach:F3} m exceeds the avatar's {AvatarUpperLen + AvatarForeLen:F3} m arm");
        }

        [Test]
        public void CorrectlyLabelledBody_NeedsNoSideSwap()
        {
            Assert.AreEqual(-1f, MediaPipeSpace.SideSwapNeeded(HandAtFace()),
                "facing the camera, the user's left shoulder sits at the larger x; these labels are already right");
        }

        [Test]
        public void ReversedLabels_AreDetectedAndRepaired()
        {
            Vector3[] reversed = WithReversedLabels(HandAtFace());
            Assert.AreEqual(1f, MediaPipeSpace.SideSwapNeeded(reversed), "reversed sides must be detected");

            Vector3 repaired = Retarget(ResolveSides(reversed));
            Vector3 expected = Retarget(HandAtFace());

            Assert.That(Vector3.Distance(repaired, expected), Is.LessThan(1e-4f),
                "after repair the reversed body must retarget identically to the correct one");
            Assert.Greater(repaired.z, 0.02f, "hand must stay in front of the avatar");
            Assert.Greater(repaired.x, 0f, "hand must stay on the avatar's right");
        }

        /// <summary>
        /// The reported regression, pinned. Reversed left/right labels flip the shoulder axis, which flips the
        /// body frame's forward, which throws a hand held at the chest out behind the avatar's back — while the
        /// side and the height still look correct, so it reads as "the arms work but they're behind me".
        /// </summary>
        [Test]
        public void TrustingReversedLabels_PutsTheHandBehindTheBody()
        {
            Vector3 wrist = Retarget(WithReversedLabels(HandAtFace()));

            Assert.Less(wrist.z, 0f,
                "this is the bug the geometric side check exists to prevent");
            Assert.Greater(wrist.x, 0f,
                "and it is nasty precisely because the side still looks right");
        }

        /// <summary>
        /// A Burst abort, pinned. NaN fails every ordered comparison, so a `x &lt; epsilon` degeneracy guard lets
        /// it straight through; it then latches into the one-euro filters (which lerp it forward forever) and
        /// finally reaches the IK job, where the arm-bend lookup does `(int)NaN` == int.MinValue and kills the
        /// process with no managed stack. Refusing the frame costs one frame of arm and is recoverable.
        /// </summary>
        [Test]
        public void ANaNLandmark_IsRefused_NotRetargeted()
        {
            foreach (int joint in new[] { MediaPipeSpace.RightShoulder, MediaPipeSpace.RightElbow, MediaPipeSpace.RightWrist })
            {
                Vector3[] pose = HandAtFace();
                pose[joint] = new Vector3(float.NaN, float.NaN, float.NaN);

                MediaPipeArmConverter converter = Converter();
                MediaPipeArmConverter.AvatarArmRig rig = Rig();

                Assert.IsFalse(converter.TryGetArm(pose, in rig, false, in Timing, out _, out _, out _),
                    $"a NaN at pose landmark {joint} must be refused outright");
            }
        }

        /// <summary>
        /// And it must not poison the converter either: the old arm-length tracker did `Mathf.Max(stored, NaN)`,
        /// which returns NaN and pinned the arm length forever, so `reach = avatarArm / NaN` made every target
        /// after it NaN — long after the bad frame was gone.
        /// </summary>
        [Test]
        public void ANaNFrame_DoesNotPoisonTheFramesAfterIt()
        {
            MediaPipeArmConverter converter = Converter();
            MediaPipeArmConverter.AvatarArmRig rig = Rig();

            Vector3[] bad = HandAtFace();
            bad[MediaPipeSpace.RightWrist] = new Vector3(float.NaN, 0f, 0f);
            converter.TryGetArm(bad, in rig, false, in Timing, out _, out _, out _);

            Assert.IsTrue(converter.TryGetArm(HandAtFace(), in rig, false, in Timing, out Vector3 wrist, out Vector3 elbow, out _),
                "the converter must recover on the next good frame");
            Assert.IsTrue(float.IsFinite(wrist.x) && float.IsFinite(wrist.y) && float.IsFinite(wrist.z),
                $"and hand back a finite wrist, got {wrist}");
            Assert.IsTrue(float.IsFinite(elbow.x) && float.IsFinite(elbow.y) && float.IsFinite(elbow.z),
                $"and a finite elbow, got {elbow}");
        }

        [Test]
        public void ArmVisibility_ReportsTheWeakestJoint()
        {
            float[] visibility = new float[MediaPipeSpace.PoseCount];
            for (int i = 0; i < visibility.Length; i++)
            {
                visibility[i] = 0.9f;
            }
            visibility[MediaPipeSpace.RightElbow] = 0.2f;

            Assert.AreEqual(0.2f, MediaPipeSpace.ArmVisibility(visibility, false), 1e-5f,
                "an unseen elbow must drag the arm's confidence down, not be averaged away");
            Assert.AreEqual(0.9f, MediaPipeSpace.ArmVisibility(visibility, true), 1e-5f);
        }

        [Test]
        public void ArmVisibility_OptsOutWhenTheModelReportsNothing()
        {
            Assert.AreEqual(-1f, MediaPipeSpace.ArmVisibility(null, true),
                "no visibility data must mean 'do not gate', not 'never track'");
        }

        /// <summary>
        /// The elbow must be a pose the avatar's own bones can actually reach: exactly an upper-arm from the
        /// shoulder AND exactly a forearm from the wrist. Scaling the measured elbow straight through (as this
        /// used to) satisfies neither, so the LowerArm tracker ends up fighting the arm solver.
        /// </summary>
        [Test]
        public void ElbowIsKinematicallyReachable()
        {
            MediaPipeArmConverter converter = Converter();
            MediaPipeArmConverter.AvatarArmRig rig = Rig();
            Assert.IsTrue(converter.TryGetArm(HandAtFace(), in rig, false, in Timing, out Vector3 wrist, out Vector3 elbow, out _));

            Assert.That(Vector3.Distance(rig.RightAnchor, elbow), Is.EqualTo(AvatarUpperLen).Within(1e-3f),
                "elbow must sit exactly one upper-arm from the shoulder");
            Assert.That(Vector3.Distance(elbow, wrist), Is.EqualTo(AvatarForeLen).Within(1e-3f),
                "and exactly one forearm from the wrist");
            Assert.Greater(elbow.x, wrist.x,
                "with the hand tucked in at the face the elbow should still swing out wider than the wrist");
        }

        /// <summary>
        /// The reported bug. The solver reads only the hint's DIRECTION in the swing plane, and that direction is
        /// dominated by the elbow's DEPTH — the worst number a monocular pose model produces. A depth error does
        /// not nudge the elbow, it rotates it around the arm, and it surfaces as the elbow riding up.
        /// </summary>
        [Test]
        public void ElbowHangsBelowTheShoulder_WithTheHandAtTheFace()
        {
            MediaPipeArmConverter converter = Converter();
            MediaPipeArmConverter.AvatarArmRig rig = Rig();
            Assert.IsTrue(converter.TryGetArm(HandAtFace(), in rig, false, in Timing, out _, out Vector3 elbow, out _));

            Assert.Less(elbow.y, AvatarShoulderY,
                $"a hand at the face bends the elbow DOWN and out; elbow at {elbow.y:F3} is above the shoulder ({AvatarShoulderY:F3})");
        }

        [Test]
        public void ElbowRestBias_LowersTheElbow()
        {
            MediaPipeArmConverter.AvatarArmRig rig = Rig();

            MediaPipeArmConverter trusting = new MediaPipeArmConverter { Smoothing = 0f, ElbowRestBias = 0f };
            Assert.IsTrue(trusting.TryGetArm(HandAtFace(), in rig, false, in Timing, out _, out Vector3 fromCamera, out _));

            MediaPipeArmConverter resting = new MediaPipeArmConverter { Smoothing = 0f, ElbowRestBias = 1f };
            Assert.IsTrue(resting.TryGetArm(HandAtFace(), in rig, false, in Timing, out _, out Vector3 fromRest, out _));

            Assert.Less(fromRest.y, fromCamera.y,
                "raising the rest bias must pull the elbow down — that is the entire purpose of the knob");
            Assert.That(Vector3.Distance(rig.RightAnchor, fromRest), Is.EqualTo(AvatarUpperLen).Within(1e-3f),
                "and it must stay on the reachable circle, not just slide down");
        }

        [Test]
        public void ElbowStaysReachableWhenTheArmIsFullyExtended()
        {
            Vector3[] pose = HandAtFace();
            pose[MediaPipeSpace.RightWrist] = new Vector3(-3f, 3f, -3f);

            MediaPipeArmConverter converter = Converter();
            MediaPipeArmConverter.AvatarArmRig rig = Rig();
            Assert.IsTrue(converter.TryGetArm(pose, in rig, false, in Timing, out Vector3 wrist, out Vector3 elbow, out _));

            Assert.That(Vector3.Distance(rig.RightAnchor, elbow), Is.EqualTo(AvatarUpperLen).Within(1e-3f));
            Assert.That(Vector3.Distance(elbow, wrist), Is.LessThanOrEqualTo(AvatarForeLen + 1e-3f),
                "a straight arm must not stretch the forearm");
        }

        /// <summary>Right arm straight out to the user's right (camera -X). Side-on, so its length is fully visible.</summary>
        private static Vector3[] ArmOutSideways()
        {
            Vector3[] pose = ArmDown();
            pose[MediaPipeSpace.RightElbow] = new Vector3(-0.44f, 0.40f, 0f);
            pose[MediaPipeSpace.RightWrist] = new Vector3(-0.71f, 0.40f, 0f);
            pose[MediaPipeSpace.RightIndex] = new Vector3(-0.78f, 0.38f, 0f);
            pose[MediaPipeSpace.RightPinky] = new Vector3(-0.76f, 0.42f, 0f);
            return pose;   // |sh->el| + |el->wr| = 0.26 + 0.27 = 0.53 m: the arm's true length
        }

        /// <summary>
        /// The same right arm, pointed AT the camera (-Z) — and reported the way MediaPipe actually reports it:
        /// short. Depth is the model's weakest axis, so the shoulder->elbow->wrist chain comes back measuring
        /// 0.32 m instead of its true 0.53 m. The POSE is real; the LENGTH it comes back with is not.
        /// </summary>
        private static Vector3[] ArmForeshortened()
        {
            Vector3[] pose = ArmDown();
            pose[MediaPipeSpace.RightElbow] = new Vector3(-0.18f, 0.40f, -0.16f);
            pose[MediaPipeSpace.RightWrist] = new Vector3(-0.18f, 0.40f, -0.32f);
            pose[MediaPipeSpace.RightIndex] = new Vector3(-0.20f, 0.38f, -0.38f);
            pose[MediaPipeSpace.RightPinky] = new Vector3(-0.16f, 0.42f, -0.38f);
            return pose;
        }

        /// <summary>
        /// The arm-length error is ONE-SIDED: foreshortening makes the model read the arm short, and essentially
        /// nothing makes it read long. So a symmetric average of the measurements does not average the ARM — it
        /// averages the foreshortening. The estimate sags toward whatever pose you hold most, and because
        /// reach = avatarArm / userArm, a sagging estimate inflates reach and throws the avatar's hand further
        /// and further out — worst exactly when you reach toward the camera, which is most of the time.
        ///
        /// So: hold a foreshortened arm perfectly still. The landmarks never change. The retarget must not move.
        /// </summary>
        [Test]
        public void AForeshortenedArm_DoesNotInflateTheRetargetOverTime()
        {
            MediaPipeArmConverter converter = Converter();
            MediaPipeArmConverter.AvatarArmRig rig = Rig();

            // Learn the arm from a clean side-on view, where its length really is visible.
            for (int f = 0; f < 60; f++) converter.TryGetArm(ArmOutSideways(), in rig, false, in Timing, out _, out _, out _);

            // Point it at the camera, and let the one-euro filters settle on the new pose first — otherwise we
            // would be measuring the smoother still catching up, not the arm-length estimate underneath it.
            Vector3[] fore = ArmForeshortened();
            for (int f = 0; f < 60; f++) converter.TryGetArm(fore, in rig, false, in Timing, out _, out _, out _);

            Assert.IsTrue(converter.TryGetArm(fore, in rig, false, in Timing, out Vector3 settled, out _, out _));
            float settledReach = Vector3.Distance(settled, rig.RightAnchor);

            // Now just hold it. Identical landmarks every frame from here on, so an identical retarget every
            // frame is the only correct answer. Whatever moves is the arm-length estimate sagging.
            float worst = settledReach;
            for (int f = 0; f < 600; f++)
            {
                converter.TryGetArm(fore, in rig, false, in Timing, out Vector3 w, out _, out _);
                worst = Mathf.Max(worst, Vector3.Distance(w, rig.RightAnchor));
            }

            float creep = worst - settledReach;
            Assert.That(creep, Is.LessThan(0.01f),
                $"holding a foreshortened arm perfectly still for {600f / 15f:F0}s pushed the avatar's hand " +
                $"{creep * 100f:F1} cm further out — the arm-length estimate is following the model's under-read " +
                "down, and reach = avatarArm/userArm is inflating to compensate");
        }

        /// <summary>
        /// The guardrail on the test above. Refusing to believe under-reads is only correct because an arm is a
        /// constant — it must not decay into refusing to learn at all. Sit a genuinely longer-armed person down
        /// and the estimate still has to follow them, or the taller user's avatar under-reaches forever.
        /// </summary>
        [Test]
        public void AGenuinelyLongerArm_IsStillLearned()
        {
            MediaPipeArmConverter converter = Converter();
            MediaPipeArmConverter.AvatarArmRig rig = Rig();

            for (int f = 0; f < 60; f++) converter.TryGetArm(ArmOutSideways(), in rig, false, in Timing, out _, out _, out _);
            converter.TryGetArm(ArmOutSideways(), in rig, false, in Timing, out Vector3 shortArm, out _, out _);

            // Same pose, but a person with a 20% longer arm: elbow and wrist both further out.
            Vector3[] longer = ArmOutSideways();
            longer[MediaPipeSpace.RightElbow] = new Vector3(-0.49f, 0.40f, 0f);
            longer[MediaPipeSpace.RightWrist] = new Vector3(-0.82f, 0.40f, 0f);
            for (int f = 0; f < 120; f++) converter.TryGetArm(longer, in rig, false, in Timing, out _, out _, out _);
            converter.TryGetArm(longer, in rig, false, in Timing, out Vector3 longArm, out _, out _);

            // A longer real arm at the same fraction of extension retargets to the SAME avatar reach — that is
            // the entire point of normalising by the user's own arm. If the estimate had not been re-learned,
            // the longer arm would read as "reaching further" and the avatar's hand would shoot out past it.
            float shortReach = Vector3.Distance(shortArm, rig.RightAnchor);
            float longReach = Vector3.Distance(longArm, rig.RightAnchor);

            Assert.That(longReach, Is.EqualTo(shortReach).Within(0.02f),
                $"a 20% longer arm retargeted to {longReach * 100f:F1} cm of avatar reach instead of " +
                $"{shortReach * 100f:F1} cm — the arm-length estimate stopped adapting, so body size is leaking " +
                "into the pose");
        }
    }
}
