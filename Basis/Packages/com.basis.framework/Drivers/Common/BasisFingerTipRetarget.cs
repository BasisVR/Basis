using Unity.Mathematics;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    public sealed class BasisFingerTipRetarget
    {
        public const float ContinuityWeight = 0.003f, LocalAcceptDot = 0.9962f;
        const int RefineSteps = 4, LocalRadius = 2;
        float[] directions;
        int width, height, cellCount;
        float increment, inverseIncrement;
        public int FullScans;
        public bool IsCreated => directions != null;
        public void Clear() => directions = null;
        public static bool TryPalmInverse(Vector3 wrist, Vector3 indexMcp, Vector3 middleMcp, Vector3 littleMcp, out Quaternion inverse)
        {
            inverse = Quaternion.identity;
            Vector3 forward = middleMcp - wrist, across = indexMcp - littleMcp;
            float forwardLength = forward.magnitude, acrossLength = across.magnitude;
            if (forwardLength < 1e-9f || acrossLength < 1e-9f) return false;
            forward /= forwardLength;
            Vector3 normal = Vector3.Cross(forward, across / acrossLength);
            if (normal.sqrMagnitude < 1e-4f) return false;
            inverse = Quaternion.Inverse(Quaternion.LookRotation(forward, normal));
            return true;
        }
        public bool Build(Animator animator, BasisHandPoseGrid grid)
        {
            directions = null;
            if (animator == null || !animator.isHuman || grid == null || !grid.IsCreated) return false;
            width = grid.GridWidth;
            height = grid.GridHeight;
            increment = grid.Increment;
            inverseIncrement = 1f / increment;
            cellCount = width * height;
            float[] built = new float[BasisHandPoseGrid.FingerCount * cellCount * 3];
            Transform[] joints = new Transform[3];
            for (int finger = 0; finger < BasisHandPoseGrid.FingerCount; finger++)
            {
                bool left = finger < 5;
                Transform hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                Transform index = animator.GetBoneTransform(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal), middle = animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal), little = animator.GetBoneTransform(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
                if (hand == null || index == null || middle == null || little == null) return false;
                if (!TryPalmInverse(Vector3.zero, hand.InverseTransformPoint(index.position), hand.InverseTransformPoint(middle.position), hand.InverseTransformPoint(little.position), out Quaternion palmInverse)) return false;
                for (int j = 0; j < 3; j++)
                {
                    joints[j] = animator.GetBoneTransform((HumanBodyBones)((int)HumanBodyBones.LeftThumbProximal + finger * 3 + j));
                    if (joints[j] == null) return false;
                }
                Matrix4x4 link0 = hand.worldToLocalMatrix * joints[0].parent.localToWorldMatrix, link1 = joints[0].worldToLocalMatrix * joints[1].parent.localToWorldMatrix, link2 = joints[1].worldToLocalMatrix * joints[2].parent.localToWorldMatrix;
                Vector3 tipLocal = joints[2].childCount > 0 ? joints[2].GetChild(0).localPosition : joints[2].localPosition;
                if (tipLocal.sqrMagnitude < 1e-12f) tipLocal = joints[2].localPosition;
                Vector3 p0 = joints[0].localPosition, p1 = joints[1].localPosition, p2 = joints[2].localPosition, s0 = joints[0].localScale, s1 = joints[1].localScale, s2 = joints[2].localScale;
                for (int xi = 0; xi < width; xi++)
                {
                    for (int yi = 0; yi < height; yi++)
                    {
                        float2 percentage = new float2(-1f + xi * increment, -1f + yi * increment);
                        Matrix4x4 m = link0 * Matrix4x4.TRS(p0, grid.SampleJoint(finger, 0, percentage), s0);
                        Vector3 basePoint = m.MultiplyPoint3x4(Vector3.zero);
                        m = m * link1 * Matrix4x4.TRS(p1, grid.SampleJoint(finger, 1, percentage), s1);
                        m = m * link2 * Matrix4x4.TRS(p2, grid.SampleJoint(finger, 2, percentage), s2);
                        Vector3 d = (palmInverse * (m.MultiplyPoint3x4(tipLocal) - basePoint)).normalized;
                        int o = (finger * cellCount + xi * height + yi) * 3;
                        built[o] = d.x;
                        built[o + 1] = d.y;
                        built[o + 2] = d.z;
                    }
                }
            }
            directions = built;
            return true;
        }
        float SampleDot(int baseIndex, float x, float y, float tx0, float ty0, float tz0)
        {
            float fx = (x + 1f) * inverseIncrement, fy = (y + 1f) * inverseIncrement;
            int x0 = (int)fx, y0 = (int)fy;
            if (x0 > width - 2) x0 = width - 2;
            if (y0 > height - 2) y0 = height - 2;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            float tx = fx - x0, ty = fy - y0;
            if (tx < 0f) tx = 0f; else if (tx > 1f) tx = 1f;
            if (ty < 0f) ty = 0f; else if (ty > 1f) ty = 1f;
            float[] d = directions;
            int a = baseIndex + (x0 * height + y0) * 3, b = a + height * 3, c = a + 3, e = b + 3;
            float w00 = (1f - tx) * (1f - ty), w10 = tx * (1f - ty), w01 = (1f - tx) * ty, w11 = tx * ty;
            float vx = d[a] * w00 + d[b] * w10 + d[c] * w01 + d[e] * w11, vy = d[a + 1] * w00 + d[b + 1] * w10 + d[c + 1] * w01 + d[e + 1] * w11, vz = d[a + 2] * w00 + d[b + 2] * w10 + d[c + 2] * w01 + d[e + 2] * w11;
            float len = vx * vx + vy * vy + vz * vz;
            return len > 1e-12f ? (vx * tx0 + vy * ty0 + vz * tz0) / Mathf.Sqrt(len) : -1f;
        }
        public bool SolveHand(int fingerOffset, Vector3 wrist, Vector3 indexMcp, Vector3 middleMcp, Vector3 littleMcp, Vector3[] bases, Vector3[] tips, Vector2[] solved)
        {
            if (directions == null || !TryPalmInverse(wrist, indexMcp, middleMcp, littleMcp, out Quaternion palmInverse)) return false;
            for (int f = 0; f < 5; f++)
            {
                Vector3 direction = tips[f] - bases[f];
                float length = direction.magnitude;
                if (length < 1e-9f) continue;
                solved[f] = Solve(fingerOffset + f, palmInverse * (direction / length), solved[f]);
            }
            return true;
        }
        public static void Write(BasisFingerPose pose, Vector2[] solved)
        {
            pose.ThumbPercentage = solved[0];
            pose.IndexPercentage = solved[1];
            pose.MiddlePercentage = solved[2];
            pose.RingPercentage = solved[3];
            pose.LittlePercentage = solved[4];
        }
        public enum ThumbTouch { Lifted, Thumbrest, Primary, Secondary, Stick }
        static readonly Vector3[] controllerThumb = { ThumbDirection(25f, 28f), ThumbDirection(15f, 38f), ThumbDirection(12f, 28f), ThumbDirection(10f, 31f), ThumbDirection(23f, 21f) };
        static Vector3 ThumbDirection(float forwardDegrees, float upDegrees)
        {
            float f = forwardDegrees * Mathf.Deg2Rad, u = upDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(f) * Mathf.Cos(u), Mathf.Sin(u), Mathf.Cos(f) * Mathf.Cos(u));
        }
        public static Vector3 ControllerThumb(ThumbTouch touch, bool left)
        {
            Vector3 d = controllerThumb[(int)touch];
            return left ? new Vector3(d.x, -d.y, d.z) : d;
        }
        public static Vector2[] NewState() => new[] { new Vector2(0.75f, 0f), new Vector2(0.75f, 0f), new Vector2(0.75f, 0f), new Vector2(0.75f, 0f), new Vector2(0.75f, 0f) };
        public Vector2 Solve(int finger, Vector3 target, Vector2 previous)
        {
            float[] d = directions;
            int baseIndex = finger * cellCount * 3;
            float tx0 = target.x, ty0 = target.y, tz0 = target.z, best = float.NegativeInfinity, bestDot = -1f;
            int cx = Mathf.Clamp(Mathf.RoundToInt((previous.x + 1f) * inverseIncrement), 0, width - 1), cy = Mathf.Clamp(Mathf.RoundToInt((previous.y + 1f) * inverseIncrement), 0, height - 1);
            int bx = cx, by = cy;
            int xMin = Mathf.Max(0, cx - LocalRadius), xMax = Mathf.Min(width - 1, cx + LocalRadius), yMin = Mathf.Max(0, cy - LocalRadius), yMax = Mathf.Min(height - 1, cy + LocalRadius);
            for (int xi = xMin; xi <= xMax; xi++)
            {
                for (int yi = yMin; yi <= yMax; yi++)
                {
                    int o = baseIndex + (xi * height + yi) * 3;
                    float dot = d[o] * tx0 + d[o + 1] * ty0 + d[o + 2] * tz0;
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        bx = xi;
                        by = yi;
                    }
                }
            }
            bool interior = bx > xMin && bx < xMax && by > yMin && by < yMax || bx == 0 || bx == width - 1 || by == 0 || by == height - 1;
            if (bestDot < LocalAcceptDot || !interior && bestDot < LocalAcceptDot + (1f - LocalAcceptDot) * 0.5f)
            {
                FullScans++;
                for (int xi = 0; xi < width; xi++)
                {
                    float dx = -1f + xi * increment - previous.x;
                    int row = baseIndex + xi * height * 3;
                    for (int yi = 0; yi < height; yi++)
                    {
                        float dy = -1f + yi * increment - previous.y;
                        int o = row + yi * 3;
                        float score = d[o] * tx0 + d[o + 1] * ty0 + d[o + 2] * tz0 - ContinuityWeight * (dx * dx + dy * dy);
                        if (score > best)
                        {
                            best = score;
                            bx = xi;
                            by = yi;
                        }
                    }
                }
            }
            float px = -1f + bx * increment, py = -1f + by * increment;
            float ddx = px - previous.x, ddy = py - previous.y;
            best = SampleDot(baseIndex, px, py, tx0, ty0, tz0) - ContinuityWeight * (ddx * ddx + ddy * ddy);
            float step = increment * 0.5f;
            for (int r = 0; r < RefineSteps; r++, step *= 0.5f)
            {
                float ox = px, oy = py;
                for (int sx = -1; sx <= 1; sx++)
                {
                    for (int sy = -1; sy <= 1; sy++)
                    {
                        if (sx == 0 && sy == 0) continue;
                        float qx = ox + sx * step, qy = oy + sy * step;
                        if (qx < -1f) qx = -1f; else if (qx > 1f) qx = 1f;
                        if (qy < -1f) qy = -1f; else if (qy > 1f) qy = 1f;
                        float ex = qx - previous.x, ey = qy - previous.y;
                        float score = SampleDot(baseIndex, qx, qy, tx0, ty0, tz0) - ContinuityWeight * (ex * ex + ey * ey);
                        if (score > best)
                        {
                            best = score;
                            px = qx;
                            py = qy;
                        }
                    }
                }
            }
            return new Vector2(px, py);
        }
    }
}
