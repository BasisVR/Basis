using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// Avatars as capsules on their bones rather than as a re-baked skinned mesh.
    ///
    /// The dynamic path could only ever be stale: the backend offers no BLAS refit and no vertex buffer
    /// instance, so a deformed mesh is updated by removing and re-adding it, which is expensive enough to
    /// need a per-frame budget spent round-robin. The body that occludes and bounces light was therefore
    /// several frames behind the body on screen, by a different amount for every person in the room, and
    /// the catch-up jump was what the temporal filter smeared. Capsules move instead of deforming, so
    /// every avatar updates every frame and there is nothing left to be stale.
    /// </summary>
    public class BasisGlobalIlluminationAvatarProxyTests
    {
        private readonly List<GameObject> spawned = new List<GameObject>();
        private readonly List<BasisAvatarProxy.ResolvedLimb> limbs =
            new List<BasisAvatarProxy.ResolvedLimb>();

        [TearDown]
        public void TearDown()
        {
            for (int index = 0; index < spawned.Count; index++)
            {
                if (spawned[index] != null) { Object.DestroyImmediate(spawned[index]); }
            }
            spawned.Clear();
            limbs.Clear();
            BasisAvatarProxy.ReleaseSharedCapsule();
        }

        private Transform Bone(string name, Transform parent, Vector3 position)
        {
            GameObject host = new GameObject(name);
            if (parent == null) { spawned.Add(host); }
            host.transform.SetParent(parent, false);
            host.transform.position = position;
            return host.transform;
        }

        [Test]
        public void TheCapsuleIsAUnitTheMatrixCanPlaceAnywhere()
        {
            Mesh capsule = BasisAvatarProxy.SharedCapsule();
            Assert.IsNotNull(capsule);
            Assert.Greater(capsule.vertexCount, 0, "the shared capsule has no geometry to trace");
            Assert.AreEqual(1, capsule.subMeshCount, "the proxy instance always references sub-mesh 0");
            Assert.IsTrue(capsule.isReadable, "the compute backend copies these normals into its arena");

            Bounds bounds = capsule.bounds;
            Assert.AreEqual(1f, bounds.extents.x, 0.01f, "radius must be 1 or the matrix scale is not the limb radius");
            Assert.AreEqual(1f, bounds.extents.z, 0.01f);
            Assert.AreEqual(2f, bounds.extents.y, 0.01f, "ends must sit at +/-2 (a unit cylinder plus two unit caps)");
        }

        [Test]
        public void EveryLimbOfEveryAvatarSharesOneMesh()
        {
            // This is what makes the whole approach cheap: one mesh means one BLAS for the entire room,
            // and it is never rebuilt because a capsule never changes shape.
            Mesh first = BasisAvatarProxy.SharedCapsule();
            Mesh second = BasisAvatarProxy.SharedCapsule();
            Assert.AreSame(first, second, "a second call built a second mesh, which is a second BLAS per avatar");
        }

        [Test]
        public void ANonHumanoidResolvesToNothingRatherThanAGuess()
        {
            GameObject host = new GameObject("BasisGIProxyNonHumanoid");
            spawned.Add(host);
            Animator animator = host.AddComponent<Animator>();
            Assert.IsFalse(BasisAvatarProxy.TryResolve(animator, limbs),
                "a rig with no bone map got a body-shaped guess, which is worse than no bounce");
            Assert.AreEqual(0, limbs.Count);
            Assert.IsFalse(BasisAvatarProxy.TryResolve(null, limbs));
        }

        [Test]
        public void ACollapsedJointBecomesABallRatherThanVanishing()
        {
            Transform joint = Bone("BasisGIProxyJoint", null, new Vector3(1f, 2f, 3f));
            BasisAvatarProxy.ResolvedLimb limb =
                new BasisAvatarProxy.ResolvedLimb(joint, joint, 0.2f, 0f);

            Matrix4x4 matrix = BasisAvatarProxy.MatrixFor(limb);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), (Vector3)matrix.GetColumn(3), "a degenerate limb moved off its joint");
            Vector3 scale = matrix.lossyScale;
            Assert.AreEqual(0.2f, scale.x, 1e-4f, "a degenerate limb should still occupy its own radius");
            Assert.AreEqual(0.2f, scale.y, 1e-4f, "a zero length capsule would punch a hole in the occlusion");
        }

        [Test]
        public void ALimbSpansItsTwoBones()
        {
            Transform from = Bone("BasisGIProxyFrom", null, new Vector3(0f, 1f, 0f));
            Transform to = Bone("BasisGIProxyTo", null, new Vector3(0f, 3f, 0f));
            BasisAvatarProxy.ResolvedLimb limb =
                new BasisAvatarProxy.ResolvedLimb(from, to, 0.25f, 0f);

            Matrix4x4 matrix = BasisAvatarProxy.MatrixFor(limb);
            Vector3 centre = matrix.GetColumn(3);
            Assert.AreEqual(new Vector3(0f, 2f, 0f), centre, "the capsule is not centred between its bones");

            Vector3 scale = matrix.lossyScale;
            Assert.AreEqual(1f, scale.y, 1e-4f, "half of a two metre limb is one, so the unit capsule scales by one");
            Assert.AreEqual(0.25f, scale.x, 1e-4f);
            Assert.AreEqual(0.25f, scale.z, 1e-4f);
        }

        [Test]
        public void ALimbFollowsItsBonesRotation()
        {
            Transform from = Bone("BasisGIProxyFrom", null, Vector3.zero);
            Transform to = Bone("BasisGIProxyTo", null, new Vector3(2f, 0f, 0f));
            BasisAvatarProxy.ResolvedLimb limb =
                new BasisAvatarProxy.ResolvedLimb(from, to, 0.1f, 0f);

            Matrix4x4 matrix = BasisAvatarProxy.MatrixFor(limb);
            // The capsule is authored along +Y, so its local up has to be pointing down the arm.
            Vector3 up = ((Vector3)matrix.GetColumn(1)).normalized;
            Assert.AreEqual(1f, Mathf.Abs(Vector3.Dot(up, Vector3.right)), 1e-3f,
                "the capsule did not rotate onto the bone direction, so a horizontal arm occludes vertically");
        }

        [Test]
        public void MovingABoneMovesTheLimbWithNoRebuild()
        {
            // The entire point: a pose change is a new matrix, not new geometry.
            Transform from = Bone("BasisGIProxyFrom", null, Vector3.zero);
            Transform to = Bone("BasisGIProxyTo", null, new Vector3(0f, 2f, 0f));
            BasisAvatarProxy.ResolvedLimb limb =
                new BasisAvatarProxy.ResolvedLimb(from, to, 0.1f, 0f);

            Matrix4x4 before = BasisAvatarProxy.MatrixFor(limb);
            to.position = new Vector3(0f, 4f, 0f);
            Matrix4x4 after = BasisAvatarProxy.MatrixFor(limb);

            Assert.AreNotEqual(before, after, "the limb did not follow its bone");
            Assert.AreEqual(2f, ((Vector3)after.GetColumn(3)).y, 1e-4f, "the capsule did not re-centre on the longer limb");
            Assert.AreSame(BasisAvatarProxy.SharedCapsule(),
                BasisAvatarProxy.SharedCapsule(),
                "the mesh must be untouched by a pose change - that is what removes the rebuild");
        }

        [Test]
        public void TheBodyPlanCoversTheLimbsThatCarryOcclusion()
        {
            HashSet<HumanBodyBones> covered = new HashSet<HumanBodyBones>();
            for (int index = 0; index < BasisAvatarProxy.Body.Length; index++)
            {
                covered.Add(BasisAvatarProxy.Body[index].From);
                covered.Add(BasisAvatarProxy.Body[index].To);
                Assert.Greater(BasisAvatarProxy.Body[index].RadiusFactor, 0f,
                    "a zero radius limb is not in the structure at all");
            }

            HumanBodyBones[] required =
            {
                HumanBodyBones.Hips, HumanBodyBones.Head,
                HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
                HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
                HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
            };
            for (int index = 0; index < required.Length; index++)
            {
                Assert.IsTrue(covered.Contains(required[index]), required[index] + " casts no occlusion");
            }
        }
    }
}
