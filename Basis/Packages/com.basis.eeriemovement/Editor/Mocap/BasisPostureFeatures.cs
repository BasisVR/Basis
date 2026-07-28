using UnityEngine;

namespace Basis.IK.Mocap
{
    /// <summary>
    /// The pelvis's posture problem, reduced to four numbers -- and the SAME four the live rig can actually
    /// compute, which is the only reason any of this transfers.
    ///
    /// ================================================================================================
    /// THE PROBLEM. A VR user gives you a head and nothing else. Two completely different bodies produce the
    /// same low head:
    ///
    ///     WAIST-BEND   spine folds forward, pelvis stays HIGH and slides BACK over the heels.
    ///     SQUAT / SIT  spine stays stacked, pelvis rides the head almost straight DOWN.
    ///
    /// Measured on the CMU corpus, the pelvis-drop-per-head-drop coupling is 0.23 for the first and 0.88 for
    /// the second. The shipped rig applies ONE saturating law to both, so it cannot help being wrong twice --
    /// and it is: on a deep squat it holds the pelvis 32.8 cm above where a real one goes, and the head is
    /// welded to the HMD, so the spine and neck have to find every one of those centimetres by ROTATING.
    /// That is the "tortoise", and it is arithmetic.
    ///
    /// THE DISCRIMINATOR IS HOW FAR FORWARD THE HEAD WENT. A squat barely travels; a waist-bend travels a
    /// long way. The rig's vertical law never looks at that, which is precisely why it cannot tell them apart.
    /// ================================================================================================
    ///
    /// EVERY QUANTITY HERE IS AVAILABLE AT RUNTIME, and everything is normalised by the user's own standing
    /// head height, so the model is SCALE-FREE -- a child avatar and a giant get the same posture, not the
    /// same centimetres. (The swivel models normalise by limb length for the identical reason.)
    ///
    ///   Origin   the SUPPORT BASE: the midpoint of the feet. The rig already has this
    ///            (BasisLocalVirtualSpineDriver uses feetMidXZ when the feet are tracked, and HeadBaselineXZ
    ///            -- a slow horizontal baseline of the standing spot -- when they are not).
    ///   Drop     (standing head height - current head height) / standing head height.
    ///   Fwd      how far the head has travelled horizontally FROM that support base, over the same height.
    ///            Unsigned: the DIRECTION is carried separately, so no body-facing frame is needed anywhere.
    ///            This matters -- a facing built from the pelvis would be circular, since the pelvis is the
    ///            thing being solved.
    ///
    /// The two OUTPUTS are in the same units, and the horizontal one is SIGNED ALONG THE HEAD'S OWN LEAN
    /// DIRECTION, so a negative value means the pelvis is BEHIND the support base -- counterbalancing, which
    /// is what a bending human's pelvis actually does.
    /// </summary>
    public struct BasisPostureSample
    {
        public float HeadDrop;    // in: (standHeadY - headY) / standHeadY
        public float HeadFwd;     // in: |headXZ - supportXZ| / standHeadY,  >= 0
        public float HipsDrop;    // out: (standHipsY - hipsY) / standHeadY
        public float HipsFwd;     // out: dot(hipsXZ - supportXZ, headLeanDir) / standHeadY   (signed)

        /// <summary>
        /// CANDIDATE THIRD FEATURE: the head's pitch below horizontal, in radians. Positive = looking down.
        ///
        /// The hope is that it separates the two postures where (drop, lean) cannot. Squat down to pick
        /// something up and your torso stays upright, so your head only has to TILT to see the floor. Bend at
        /// the waist to do the same job and your whole head is CARRIED down by a pitching torso. Same head
        /// height, same lean, different head angle -- in principle.
        ///
        /// In principle. Gaze is voluntary, so this may be mostly noise. That is what the fit is for; it is
        /// dumped and TESTED before a single line of runtime plumbing is written for it.
        /// </summary>
        public float HeadPitch;

        public bool Valid;
    }

    public static class BasisPostureFeatures
    {
        /// <summary>
        /// The clip's own upright reference: the frame where the head is HIGHEST. A median would be dragged
        /// down by clips that spend most of their time folded (every sit clip in the corpus does).
        /// </summary>
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

        /// <summary>The support base -- the midpoint of the feet, horizontal.</summary>
        public static Vector2 SupportXZ(BasisMotionClip c, int f)
        {
            Vector3 l = c.Get(f, BasisMocapJoint.LeftFoot).Position;
            Vector3 r = c.Get(f, BasisMocapJoint.RightFoot).Position;
            return new Vector2(0.5f * (l.x + r.x), 0.5f * (l.z + r.z));
        }

        public static BasisPostureSample Extract(BasisMotionClip c, int f, float standHeadY, float standHipsY)
        {
            BasisPostureSample s = default;
            if (!(standHeadY > 0.1f)) return s;   // reject-unless-good: a NaN or absurd reference fails here

            Vector3 head = c.Get(f, BasisMocapJoint.Head).Position;
            Vector3 hips = c.Get(f, BasisMocapJoint.Hips).Position;
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

            s.Valid = !(float.IsNaN(s.HeadDrop) || float.IsNaN(s.HeadFwd)
                        || float.IsNaN(s.HipsDrop) || float.IsNaN(s.HipsFwd) || float.IsNaN(s.HeadPitch));
            return s;
        }
    }
}
