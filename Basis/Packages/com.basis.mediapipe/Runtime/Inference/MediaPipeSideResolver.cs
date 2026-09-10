using UnityEngine;
namespace Basis.MediaPipe
{
    public struct MediaPipeSideLatch
    {
        public const int VotesToFlip = 3;
        public bool Swapped, Decided;
        public int Votes;
        public bool Update(float decision)
        {
            if (decision == 0f) return Swapped;
            bool wants = decision > 0f;
            if (!Decided)
            {
                Swapped = wants;
                Decided = true;
                Votes = 0;
                return Swapped;
            }
            if (wants == Swapped)
            {
                Votes = 0;
                return Swapped;
            }
            if (++Votes >= VotesToFlip)
            {
                Swapped = wants;
                Votes = 0;
            }
            return Swapped;
        }
    }
    public sealed class MediaPipeHandSideResolver
    {
        public const float Ambiguity = 0.02f, Continuity = 0.15f, DuplicateDistance = 0.03f;
        public const int ForgetAfter = 30;
        private static readonly Vector2 Missing = new Vector2(float.NaN, float.NaN);
        private Vector2 lastLeft = Missing, lastRight = Missing;
        private int leftAge = ForgetAfter, rightAge = ForgetAfter;
        public void Reset()
        {
            leftAge = ForgetAfter;
            rightAge = ForgetAfter;
        }
        public static bool IsDuplicate(Vector3[] first, Vector3[] second, float aspect)
        {
            if (first == null || second == null || first.Length < MediaPipeSpace.HandCount || second.Length < MediaPipeSpace.HandCount) return false;
            if (!(aspect > 0f) || !float.IsFinite(aspect)) aspect = 1f;
            return Near(first[MediaPipeSpace.HandWrist], second[MediaPipeSpace.HandWrist], aspect) && Near(first[MediaPipeSpace.HandMiddleMcp], second[MediaPipeSpace.HandMiddleMcp], aspect);
        }
        private static bool Near(Vector3 a, Vector3 b, float aspect)
        {
            float dx = (a.x - b.x) * aspect, dy = a.y - b.y, d = dx * dx + dy * dy;
            return float.IsFinite(d) && d < DuplicateDistance * DuplicateDistance;
        }
        public bool Resolve(Vector3[] pose, Vector3[] first, Vector3[] second, bool labelLeft, int found)
        {
            bool firstIsLeft = Decide(pose, first, second, labelLeft, found);
            Remember(first, second, firstIsLeft, found);
            return firstIsLeft;
        }
        private bool Decide(Vector3[] pose, Vector3[] first, Vector3[] second, bool labelLeft, int found)
        {
            if (Vote(first, second, found, PoseWrist(pose, true), PoseWrist(pose, false), 0f, out float vote)) return vote > 0f;
            if (Vote(first, second, found, leftAge < ForgetAfter ? lastLeft : Missing, rightAge < ForgetAfter ? lastRight : Missing, Continuity, out vote)) return vote > 0f;
            return labelLeft;
        }
        private void Remember(Vector3[] first, Vector3[] second, bool firstIsLeft, int found)
        {
            if (leftAge < ForgetAfter) leftAge++;
            if (rightAge < ForgetAfter) rightAge++;
            Vector3[] other = found == 2 ? second : null;
            Vector2 left = HandWrist(firstIsLeft ? first : other), right = HandWrist(firstIsLeft ? other : first);
            if (float.IsFinite(left.x))
            {
                lastLeft = left;
                leftAge = 0;
            }
            if (float.IsFinite(right.x))
            {
                lastRight = right;
                rightAge = 0;
            }
        }
        private static bool Vote(Vector3[] first, Vector3[] second, int found, Vector2 leftRef, Vector2 rightRef, float radius, out float vote)
        {
            vote = 0f;
            Vector2 f = HandWrist(first), s = found == 2 ? HandWrist(second) : Missing;
            float fl = Gap(f, leftRef), fr = Gap(f, rightRef), sl = Gap(s, leftRef), sr = Gap(s, rightRef);
            if (fl >= 0f && fr >= 0f && sl >= 0f && sr >= 0f)
            {
                vote = (fr + sl) - (fl + sr);
                if (radius > 0f && !(vote > 0f ? fl < radius && sr < radius : fr < radius && sl < radius)) return false;
            }
            else if (fl >= 0f && fr >= 0f)
            {
                vote = fr - fl;
                if (radius > 0f && Mathf.Min(fl, fr) >= radius) return false;
            }
            else if (radius > 0f && fl >= 0f && fl < radius) vote = Ambiguity;
            else if (radius > 0f && fr >= 0f && fr < radius) vote = -Ambiguity;
            else return false;
            return Mathf.Abs(vote) >= Ambiguity;
        }
        private static Vector2 PoseWrist(Vector3[] pose, bool left)
        {
            if (pose == null || pose.Length < MediaPipeSpace.PoseCount) return Missing;
            Vector3 wrist = pose[left ? MediaPipeSpace.LeftWrist : MediaPipeSpace.RightWrist];
            return new Vector2(wrist.x, wrist.y);
        }
        private static Vector2 HandWrist(Vector3[] hand)
        {
            if (hand == null || hand.Length <= MediaPipeSpace.HandWrist) return Missing;
            Vector3 wrist = hand[MediaPipeSpace.HandWrist];
            return new Vector2(wrist.x, wrist.y);
        }
        private static float Gap(Vector2 a, Vector2 b)
        {
            float d = Vector2.Distance(a, b);
            return float.IsFinite(d) ? d : -1f;
        }
    }
}
