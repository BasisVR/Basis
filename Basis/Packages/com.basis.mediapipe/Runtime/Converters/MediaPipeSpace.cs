using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>
    /// MediaPipe landmarks arrive in a y-down frame, and the backend mirrors the camera image before
    /// inference (selfie view), so the reported geometry is a mirror of the real body. Everything is
    /// converted into one Unity-space, un-mirrored, anatomically-labelled frame here so the converters
    /// never deal with raw MediaPipe conventions again.
    ///
    /// The hand landmarker already compensates for a mirrored image in its handedness LABEL (it assumes
    /// selfie input), but not in the landmark COORDINATES. The pose landmarker compensates for neither, so
    /// its left/right labels are repaired from geometry — see <see cref="SideSwapNeeded"/>.
    ///
    /// Depth needs no correction. MediaPipe's z grows AWAY from the camera, which is also the direction Unity's
    /// +Z runs once x and y are in true camera coordinates, so z passes through untouched. The user faces the
    /// camera, so their body frame's forward comes out as -Z, and a hand held toward the camera lands in front
    /// of the avatar. Nothing here depends on guessing that sign: x and y are unambiguous, and the frame's
    /// forward is derived from them.
    /// </summary>
    public static class MediaPipeSpace
    {
        public const int Nose = 0;
        public const int LeftEar = 7, RightEar = 8;
        public const int LeftShoulder = 11, RightShoulder = 12;
        public const int LeftElbow = 13, RightElbow = 14;
        public const int LeftWrist = 15, RightWrist = 16;
        public const int LeftPinky = 17, RightPinky = 18;
        public const int LeftIndex = 19, RightIndex = 20;
        public const int LeftHip = 23, RightHip = 24;
        public const int PoseCount = 33;

        public const int HandWrist = 0;
        public const int HandIndexMcp = 5;
        public const int HandMiddleMcp = 9;
        public const int HandPinkyMcp = 17;
        public const int HandCount = 21;

        private static readonly int[] LeftRightPairs =
        {
            1, 4, 2, 5, 3, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        };

        /// <summary>
        /// A landmark the maths is allowed to touch.
        ///
        /// The pose model can hand back a NaN, and NaN fails every ordered comparison — so a `< epsilon`
        /// degeneracy guard lets it straight through, the one-euro filters latch it and lerp it forward forever,
        /// and it finally reaches the Burst IK job where the arm-bend lookup does `(int)NaN` == int.MinValue and
        /// aborts the process. Every gateway below checks this before it computes anything.
        /// </summary>
        public static bool IsFinite(Vector3 v) =>
            float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        /// <summary>Metric world landmark (metres) into Unity space, undoing the selfie mirror.</summary>
        public static Vector3 World(Vector3 v, bool mirrored) =>
            new Vector3(mirrored ? -v.x : v.x, -v.y, v.z);

        /// <summary>
        /// Normalized image landmark into a y-up [0,1] frame, undoing the selfie mirror. A horizontal mirror
        /// does not touch depth, so z passes through.
        /// </summary>
        public static Vector3 Image(Vector3 v, bool mirrored) =>
            new Vector3(mirrored ? 1f - v.x : v.x, 1f - v.y, v.z);

        /// <summary>
        /// Restores anatomical left/right on pose landmarks whose sides came back reversed.
        /// </summary>
        public static void SwapPoseSidesInPlace(Vector3[] landmarks)
        {
            if (landmarks == null || landmarks.Length < PoseCount) return;
            for (int i = 0; i < LeftRightPairs.Length; i += 2)
            {
                int a = LeftRightPairs[i];
                int b = LeftRightPairs[i + 1];
                (landmarks[a], landmarks[b]) = (landmarks[b], landmarks[a]);
            }
        }

        public static void SwapPoseSidesInPlace(float[] values)
        {
            if (values == null || values.Length < PoseCount) return;
            for (int i = 0; i < LeftRightPairs.Length; i += 2)
            {
                int a = LeftRightPairs[i];
                int b = LeftRightPairs[i + 1];
                (values[a], values[b]) = (values[b], values[a]);
            }
        }

        /// <summary>
        /// Whether the pose model's left/right labels came back reversed, decided from geometry rather than
        /// from what the model called them. The user is sitting in front of the camera facing it, so their
        /// LEFT shoulder is the one on the camera's right (larger x) — an x axis we trust, because only the
        /// depth axis is ambiguous. Returns +1 to swap, -1 to keep, 0 when the shoulders are too edge-on to
        /// tell (turn sideways and the caller should just hold the last decision).
        ///
        /// Trusting the model's labels instead is what put the arms behind the body: reversed sides flip the
        /// shoulder axis, which flips the body frame's forward, which lands a hand held at your chest behind
        /// the avatar's back.
        /// </summary>
        public static float SideSwapNeeded(Vector3[] pose)
        {
            if (pose == null || pose.Length < PoseCount) return 0f;

            Vector3 left = pose[LeftShoulder];
            Vector3 right = pose[RightShoulder];
            float width = Vector3.Distance(left, right);
            float dx = left.x - right.x;
            if (!(width > 1e-3f) || !(Mathf.Abs(dx) > width * 0.3f)) return 0f;

            return dx < 0f ? 1f : -1f;
        }

        /// <summary>
        /// Lowest reported visibility across the shoulder, elbow and wrist of one arm. The pose model emits a
        /// full 33-landmark skeleton every frame whether or not it can actually see the limb, extrapolating
        /// the parts it cannot — so an arm out of frame or behind the back still produces confident-looking
        /// garbage. Returns -1 when the model reports no visibility at all, meaning "do not gate on this".
        /// </summary>
        public static float ArmVisibility(float[] visibility, bool left)
        {
            if (visibility == null || visibility.Length < PoseCount) return -1f;

            float shoulder = visibility[left ? LeftShoulder : RightShoulder];
            float elbow = visibility[left ? LeftElbow : RightElbow];
            float wrist = visibility[left ? LeftWrist : RightWrist];
            return Mathf.Min(shoulder, Mathf.Min(elbow, wrist));
        }


        /// <summary>
        /// Confidence in the body frame itself. Both shoulders define its right axis, so losing either one turns
        /// the frame that every target is placed in — worse than losing a single arm.
        /// </summary>
        public static float TorsoVisibility(float[] visibility)
        {
            if (visibility == null || visibility.Length < PoseCount) return -1f;
            return Mathf.Min(visibility[LeftShoulder], visibility[RightShoulder]);
        }

        /// <summary>
        /// Orthonormal body frame from the shoulder line and torso, built with the same rule the avatar rig
        /// uses so mapping between them is a plain change of basis. Up falls back to the head when the hips
        /// are off-frame (a seated webcam user), where the pose model extrapolates them badly.
        /// </summary>
        public static bool TryBodyFrame(Vector3[] pose, out Vector3 shoulderCenter, out Quaternion frame)
        {
            shoulderCenter = Vector3.zero;
            frame = Quaternion.identity;
            if (pose == null || pose.Length < PoseCount) return false;

            if (!IsFinite(pose[LeftShoulder]) || !IsFinite(pose[RightShoulder])
                || !IsFinite(pose[LeftHip]) || !IsFinite(pose[RightHip])) return false;

            shoulderCenter = (pose[LeftShoulder] + pose[RightShoulder]) * 0.5f;

            Vector3 right = pose[RightShoulder] - pose[LeftShoulder];
            if (!(right.sqrMagnitude > 1e-8f)) return false;
            right.Normalize();

            Vector3 hipCenter = (pose[LeftHip] + pose[RightHip]) * 0.5f;
            Vector3 up = shoulderCenter - hipCenter;
            if (up.sqrMagnitude < 1e-6f)
            {
                up = (pose[LeftEar] + pose[RightEar]) * 0.5f - shoulderCenter;
            }
            up = Vector3.ProjectOnPlane(up, right);
            if (!(up.sqrMagnitude > 1e-8f)) return false;
            up.Normalize();

            Vector3 forward = Vector3.Cross(right, up);
            if (!(forward.sqrMagnitude > 1e-8f)) return false;

            frame = Quaternion.LookRotation(forward.normalized, up);
            return true;
        }

        /// <summary>
        /// Palm frame: forward runs wrist to middle knuckle, up is the palm normal. The index/pinky
        /// ordering mirrors between hands, so the normal is negated on the left to give both hands the same
        /// "out of the back of the hand" convention.
        ///
        /// Called with MediaPipe landmarks for the user and with humanoid bone positions for the avatar, so
        /// both palm frames are defined by the same rule and mapping between them is a change of basis.
        /// </summary>
        public static bool TryPalmFrame(Vector3 wrist, Vector3 indexMcp, Vector3 middleMcp, Vector3 pinkyMcp,
            bool left, out Quaternion frame)
        {
            frame = Quaternion.identity;
            if (!IsFinite(wrist) || !IsFinite(indexMcp) || !IsFinite(middleMcp) || !IsFinite(pinkyMcp)) return false;

            Vector3 forward = middleMcp - wrist;
            Vector3 across = indexMcp - pinkyMcp;
            if (!(forward.sqrMagnitude > 1e-8f) || !(across.sqrMagnitude > 1e-8f)) return false;

            Vector3 normal = Vector3.Cross(forward, across);
            if (!(normal.sqrMagnitude > 1e-8f)) return false;
            if (left) normal = -normal;
            normal.Normalize();

            forward = Vector3.ProjectOnPlane(forward, normal);
            if (!(forward.sqrMagnitude > 1e-8f)) return false;

            frame = Quaternion.LookRotation(forward.normalized, normal);
            return true;
        }

        public static bool TryHandFrame(Vector3[] hand, bool left, out Quaternion frame)
        {
            frame = Quaternion.identity;
            if (hand == null || hand.Length < HandCount) return false;
            return TryPalmFrame(hand[HandWrist], hand[HandIndexMcp], hand[HandMiddleMcp], hand[HandPinkyMcp],
                left, out frame);
        }

        /// <summary>
        /// Palm frame from the BODY pose alone. The pose model carries a wrist, an index knuckle and a pinky
        /// knuckle per hand, which is enough for a coarse palm frame in the same space as the body frame — so
        /// the wrist still rotates with the body when the hand landmarker has nothing to say.
        /// </summary>
        public static bool TryPoseHandFrame(Vector3[] pose, bool left, out Quaternion frame)
        {
            frame = Quaternion.identity;
            if (pose == null || pose.Length < PoseCount) return false;

            Vector3 wrist = pose[left ? LeftWrist : RightWrist];
            Vector3 index = pose[left ? LeftIndex : RightIndex];
            Vector3 pinky = pose[left ? LeftPinky : RightPinky];
            return TryPalmFrame(wrist, index, (index + pinky) * 0.5f, pinky, left, out frame);
        }
    }
}
