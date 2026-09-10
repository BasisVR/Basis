using UnityEngine;
namespace Basis.IK
{
    public static class BasisArmPriorModel
    {
        public const float InputClamp = 1.2f;
        public static Vector3 ElbowDir(Vector3 hand)
        {
            float x = Mathf.Clamp(hand.x, -InputClamp, InputClamp), y = Mathf.Clamp(hand.y, -InputClamp, InputClamp), z = Mathf.Clamp(hand.z, -InputClamp, InputClamp);
            float ox = +0.48281f -0.25268f * x -0.02875f * y +0.65659f * z -0.37053f * x * x +0.20327f * x * y -0.13554f * x * z +0.48075f * y * y +0.52822f * y * z -0.80732f * z * z +0.07118f * x * x * x +0.78465f * x * x * y -0.42205f * x * x * z -0.23750f * x * y * y -0.17560f * x * y * z +0.10594f * x * z * z +1.04555f * y * y * y +0.05480f * y * y * z -0.22321f * y * z * z +0.27737f * z * z * z;
            float oy = -0.35105f -0.06797f * x -0.33970f * y -0.83376f * z -0.39934f * x * x -0.52218f * x * y -0.07975f * x * z +0.39797f * y * y +0.67714f * y * z +0.42190f * z * z +0.20848f * x * x * x +0.82587f * x * x * y -0.44714f * x * x * z -0.38703f * x * y * y -0.54093f * x * y * z +0.85452f * x * z * z +0.38735f * y * y * y +0.51135f * y * y * z +0.36968f * y * z * z +0.11820f * z * z * z;
            float oz = +0.01322f -1.33659f * x +1.58768f * y -0.27707f * z +0.39845f * x * x -0.67062f * x * y -0.21915f * x * z -0.08374f * y * y -0.64704f * y * z +0.35522f * z * z +0.24967f * x * x * x -0.52919f * x * x * y +1.16668f * x * x * z +0.77567f * x * y * y +0.43265f * x * y * z +0.86700f * x * z * z -0.69805f * y * y * y -0.45571f * y * y * z -0.60225f * y * z * z -0.15864f * z * z * z;
            return new Vector3(ox, oy, oz);
        }
    }
}
