using UnityEngine;
namespace Basis.IK
{
    public struct BasisHeadPitchSwingInput
    {
        public float PitchDeg;         // Unity Euler X on the gaze: POSITIVE looks DOWN, negative looks UP
        public float YawDeg;           // the gaze's heading within the playspace
        public Vector3 EyeFromNeck;    // T-pose eye minus T-pose neck, in avatar units (so the result scales with the avatar)
        public float Strength;         // 1 = the full geometric swing
        public float BackwardScale;    // look-UP swing as a fraction of the geometric value (see below)
    }

    public struct BasisHeadPitchSwingResult
    {
        public Vector3 Offset;         // horizontal playspace offset to add to the eye. Y is always 0.
        public float ForwardMeters;    // signed: + carried forward (looking down), - carried back (looking up)
    }

    /// <summary>
    /// Where the eye actually ends up when the gaze pitches.
    ///
    /// A head does not pitch about ITSELF. It pitches about the base of the neck, which is BELOW it — so the
    /// eye, sitting on that lever arm, is carried FORWARD as the gaze tips down. Look at your own feet and your
    /// eyes travel a good 10 cm out over them; that is not the spine leaning, it is just the neck.
    ///
    /// VR gets this for free, because the HMD is a physical object riding the same lever arm. The spine bend
    /// even has to SUPPRESS the resulting hunch when a chest tracker is present, precisely because a pure
    /// look-down swings the HMD forward and the bend would otherwise misread it as a torso lean (see
    /// BasisSpineLookDownHunchTests). Desktop had no such thing: the eye was pinned over the hips, so the gaze
    /// tipped, the cervical solve hunched the chest, and the head stayed exactly where it was — leaving the neck
    /// to absorb a translation the head should have made itself.
    ///
    /// HORIZONTAL ONLY, deliberately. Eye HEIGHT is left strictly alone: UnscaledDeviceCoord.y is what
    /// CapturePlayerHeight reads, and a pitch-varying eye height would feed straight into the avatar-rescale
    /// loop (the one that ends in 12 m players). The forward carry is the part that was missing and the part
    /// that is safe.
    /// </summary>
    public static class BasisHeadPitchSwingCore
    {
        const float k_Epsilon = 1e-5f;

        public static void Solve(in BasisHeadPitchSwingInput i, out BasisHeadPitchSwingResult r)
        {
            r = default;

            // Guards written as `!(x > lo && x < hi)` so a NaN takes the REJECT branch. NaN fails every ordered
            // comparison, so the natural phrasing would wave it straight through and put a NaN in the head
            // target — and a NaN'd bone persists in Unity, so the head would never come back.
            float lever = i.EyeFromNeck.y;
            if (!(lever > k_Epsilon)) return;                       // no lever arm above the pivot => no swing
            if (!(i.PitchDeg > -180f && i.PitchDeg < 180f)) return;
            if (!(i.YawDeg > -720f && i.YawDeg < 720f)) return;     // Quaternion.Euler(0, NaN, 0) yields a NaN heading
            if (!(i.Strength > 0f)) return;

            float p = i.PitchDeg * Mathf.Deg2Rad;
            float forwardRest = i.EyeFromNeck.z;

            // Rotate the neck->eye lever arm about the pitch axis and take how far the eye's FORWARD extent
            // moved from rest. Rotation about +X sends (0, y, z) to (0, y*cos - z*sin, y*sin + z*cos), so:
            float swung = lever * Mathf.Sin(p) + forwardRest * Mathf.Cos(p);
            float forward = (swung - forwardRest) * i.Strength;

            // Looking UP is not the mirror of looking down. Cervical extension has far less range than flexion,
            // and much of a look-up is taken by the thoracic spine arching rather than by the skull sliding
            // backwards over the shoulders — so the raw geometry over-reports it badly (a 60 deg look-up would
            // haul the camera ~19 cm straight back). Scale that side down on its own.
            if (forward < 0f) forward *= i.BackwardScale;

            if (!(forward > -4f && forward < 4f)) return;   // NaN / absurd: emit nothing rather than a bad head
            forward = Mathf.Clamp(forward, -1f, 1f);

            // The carry is along the direction the player is FACING, not along playspace +Z. Note this is only
            // the pitch-induced swing: the eye's static forward offset stays pinned by the caller, so turning on
            // the spot does not orbit the camera around the neck.
            Vector3 heading = Quaternion.Euler(0f, i.YawDeg, 0f) * Vector3.forward;

            r.ForwardMeters = forward;
            r.Offset = new Vector3(heading.x * forward, 0f, heading.z * forward);
        }
    }
}
