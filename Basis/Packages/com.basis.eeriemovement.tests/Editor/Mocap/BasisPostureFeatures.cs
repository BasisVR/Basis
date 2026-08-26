using UnityEngine;
namespace Basis.IK.Mocap
{
    public struct BasisPostureSample
    {
        public float HeadDrop;    // in: (standHeadY - headY) / standHeadY
        public float HeadFwd;     // in: |headXZ - supportXZ| / standHeadY,  >= 0
        public float HipsDrop;    // out: (standHipsY - hipsY) / standHeadY
        public float HipsFwd;     // out: dot(hipsXZ - supportXZ, headLeanDir) / standHeadY   (signed)
        public float HeadPitch;
        public bool Valid;
    }
    public static class BasisPostureFeatures
    {
        public static void StandingReference(BasisMotionClip c, out float standHeadY, out float standHipsY)
        {
            standHeadY = float.MinValue;
            standHipsY = 0f;
            for (int f = 0; f < c.FrameCount; f++)
            {
                float hy = c.Get(f, BasisMocapJoint.Head).Position.y;
                if (hy > standHeadY)
                {
                    standHeadY = hy;
                    standHipsY = c.Get(f, BasisMocapJoint.Hips).Position.y;
                }
            }
        }
        public static Vector2 SupportXZ(BasisMotionClip c, int f)
        {
            Vector3 l = c.Get(f, BasisMocapJoint.LeftFoot).Position, r = c.Get(f, BasisMocapJoint.RightFoot).Position;
            return new Vector2(0.5f * (l.x + r.x), 0.5f * (l.z + r.z));
        }
        public static BasisPostureSample Extract(BasisMotionClip c, int f, float standHeadY, float standHipsY)
        {
            BasisPostureSample s = default;
            if (!(standHeadY > 0.1f)) return s;   // reject-unless-good: a NaN or absurd reference fails here

            Vector3 head = c.Get(f, BasisMocapJoint.Head).Position, hips = c.Get(f, BasisMocapJoint.Hips).Position;
            Vector2 sup = SupportXZ(c, f);

            var headXZ = new Vector2(head.x - sup.x, head.z - sup.y);
            var hipsXZ = new Vector2(hips.x - sup.x, hips.z - sup.y);

            float lean = headXZ.magnitude;

            s.HeadDrop = (standHeadY - head.y) / standHeadY;
            s.HeadFwd = lean / standHeadY;
            s.HipsDrop = (standHipsY - hips.y) / standHeadY;

            // Signed along the head's OWN lean direction. When the head is dead over the feet there is no
            // direction to project onto and the answer is genuinely undefined -- report 0 rather than invent
            // one, and let the near-zero HeadFwd tell the model there was nothing to counterbalance.
            s.HipsFwd = lean > 1e-4f ? Vector2.Dot(hipsXZ, headXZ / lean) / standHeadY : 0f;

            // The head's pitch below horizontal. The BVH rest pose has identity rotations, so the head bone's
            // forward IS the physical facing; a real avatar would need its head T-pose divided out first (the
            // same rig-convention division BasisSwivelHintCore does). Only worth plumbing if it earns its place.
            Vector3 fwd = c.Get(f, BasisMocapJoint.Head).Rotation * Vector3.forward;
            s.HeadPitch = Mathf.Asin(Mathf.Clamp(-fwd.y, -1f, 1f));

            s.Valid = !(float.IsNaN(s.HeadDrop) || float.IsNaN(s.HeadFwd) || float.IsNaN(s.HipsDrop) || float.IsNaN(s.HipsFwd) || float.IsNaN(s.HeadPitch));
            return s;
        }
    }
}
