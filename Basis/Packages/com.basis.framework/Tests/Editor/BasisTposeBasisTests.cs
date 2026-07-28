using Basis.Scripts.Drivers;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Basis gate for the T-pose capture (<see cref="BasisLocalBoneDriver"/>.ConvertToAvatarSpaceInitial, used
    /// by BasisLocalAvatarDriver.SetInitialData to seed TposeLocal / TposeLocalScaled / the outgoing pose).
    ///
    /// The contract: TposeLocal is a ROOT-LOCAL offset. Everything downstream rotates it back by the root --
    /// the bone sim (BasisBoneSimJob's ParentMatrix), CreateRotationalLock's Offset, DriveTpose,
    /// BasisCalibrationMath.ComputeTposeAnchor, the seat driver's ToWorld. So the one invariant that must
    /// hold is a round trip:
    ///
    ///     rootPos + rootRot * TposeLocal.position  ==  the bone's world position at capture
    ///
    /// The capture used to subtract the root origin WITHOUT undoing the root rotation (the translation-only
    /// BasisHelpers.ConvertToLocalSpace overload) while the ROTATION beside it was properly inverse-rotated.
    /// That left the position in world axes, so the root rotation was applied to it a second time downstream.
    /// The error is exactly zero when the avatar is loaded facing world-forward -- which is how it survived --
    /// and grows with whatever yaw the player happened to be at when the avatar loaded or swapped. It also
    /// broke the spine-centreline snap in SetInitialData (`outgoingPosition.x = 0`), which is only the
    /// avatar's lateral axis if the offset is in avatar axes.
    ///
    /// The Legacy_* gate reproduces the old translation-only form and asserts it violates the round trip, so
    /// the failure mode is measured rather than remembered.
    /// </summary>
    public class BasisTposeBasisTests
    {
        const float TolMetres = 1e-4f;

        // Root poses an avatar can plausibly load at. Yaw is the realistic one (you swap avatars facing any
        // direction); pitch/roll cover a tilted root (play-space flip, seated in a rotated vehicle).
        static readonly Quaternion[] RootRotations =
        {
            Quaternion.identity,
            Quaternion.Euler(0f, 37f, 0f),
            Quaternion.Euler(0f, 90f, 0f),
            Quaternion.Euler(0f, 180f, 0f),
            Quaternion.Euler(0f, -120f, 0f),
            Quaternion.Euler(15f, 60f, -25f),
        };

        static readonly Vector3 RootPosition = new Vector3(3f, 0f, -2f);

        // A few bone world positions spread around the root: head-ish, hand-out-to-the-side, foot.
        static readonly Vector3[] WorldBones =
        {
            new Vector3(3.00f, 1.60f, -2.00f),
            new Vector3(3.65f, 1.35f, -2.05f),
            new Vector3(2.90f, 0.10f, -1.95f),
        };

        static Vector3 LegacyTranslationOnly(Transform root, Vector3 world) => world - root.position;

        [Test]
        public void TposeLocal_RoundTripsThroughTheRoot_AtAnyRootRotation()
        {
            GameObject go = new GameObject("BasisTposeBasisTests_root");
            try
            {
                Transform root = go.transform;
                foreach (Quaternion rot in RootRotations)
                {
                    root.SetPositionAndRotation(RootPosition, rot);
                    foreach (Vector3 world in WorldBones)
                    {
                        Vector3 local = BasisLocalBoneDriver.ConvertToAvatarSpaceInitial(root, world);

                        // Exactly what the bone sim does with it: parentMatrix * localPos.
                        Vector3 back = root.position + root.rotation * local;

                        Assert.That(Vector3.Distance(back, world), Is.LessThan(TolMetres),
                            $"TposeLocal did not round-trip at root yaw {rot.eulerAngles.y:F0}: stored {local}, " +
                            $"came back as {back}, expected {world} (off by {Vector3.Distance(back, world) * 100f:F1} cm)");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Legacy_TranslationOnlyCapture_BreaksTheRoundTrip_UnderRootYaw()
        {
            GameObject go = new GameObject("BasisTposeBasisTests_legacy_root");
            try
            {
                Transform root = go.transform;
                float worstM = 0f;

                foreach (Quaternion rot in RootRotations)
                {
                    root.SetPositionAndRotation(RootPosition, rot);
                    foreach (Vector3 world in WorldBones)
                    {
                        Vector3 local = LegacyTranslationOnly(root, world);
                        Vector3 back = root.position + root.rotation * local;
                        worstM = Mathf.Max(worstM, Vector3.Distance(back, world));
                    }
                }

                // A bone ~0.65 m off the root axis, double-rotated by a large yaw, lands the better part of a
                // metre away. That is the avatar-yawed-on-swap symptom.
                Assert.That(worstM, Is.GreaterThan(0.30f),
                    $"the legacy translation-only capture is expected to break the round trip (worst {worstM * 100f:F1} cm); " +
                    "if it no longer does, the defect model is wrong and the fix needs re-deriving");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// The eye and mouth roles reach SetInitialData through GetWorldSpacePos, which took an avatar-LOCAL
        /// point out to world. Both halves of that path used to be translation-only, so the two errors
        /// cancelled and eye/mouth came out correct by accident. Fixing only the world->local half would have
        /// silently broken them. This gate pins the round trip that must hold for BOTH halves.
        /// </summary>
        [Test]
        public void AvatarLocalPoint_SurvivesTheWorldRoundTrip_AtAnyRootRotation()
        {
            GameObject go = new GameObject("BasisTposeBasisTests_eye_root");
            try
            {
                Transform root = go.transform;
                Vector3 avatarLocalEye = new Vector3(0f, 1.62f, 0.08f);

                foreach (Quaternion rot in RootRotations)
                {
                    root.SetPositionAndRotation(RootPosition, rot);

                    // GetWorldSpacePos: origin + rotation * local
                    Vector3 world = root.position + root.rotation * avatarLocalEye;
                    // SetInitialData: back to root-local
                    Vector3 local = BasisLocalBoneDriver.ConvertToAvatarSpaceInitial(root, world);

                    Assert.That(Vector3.Distance(local, avatarLocalEye), Is.LessThan(TolMetres),
                        $"the eye/mouth avatar-local point did not survive the world round trip at root yaw " +
                        $"{rot.eulerAngles.y:F0}: {avatarLocalEye} -> {world} -> {local}");
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
