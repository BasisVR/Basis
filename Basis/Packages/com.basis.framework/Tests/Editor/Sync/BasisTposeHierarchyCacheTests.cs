using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// The T-pose hierarchy slot on <see cref="BasisAvatarModelCache"/>, which lets an avatar
    /// install replay a recorded T-pose instead of paying the animator round trip that produced it
    /// (two runtimeAnimatorController assignments plus a humanoid Animator.Update).
    ///
    /// This is load-bearing for the transmit tick: a player leaving avatar range installs the
    /// loading dummy inline there, and that dummy is one prefab shared by every remote, so the
    /// replay path is the one that runs after the first install in a session. A replay that put
    /// bones anywhere other than where the animator did would silently mis-seed everything
    /// calibration captures next — receiver bone data, the body-fit rest pose, jiggle roots.
    /// </summary>
    public class BasisTposeHierarchyCacheTests
    {
        static List<Transform> Hierarchy(BasisHumanoidRigFixture rig)
        {
            var all = new List<Transform>();
            rig.Animator.transform.GetComponentsInChildren(true, all);
            return all;
        }

        static void Scramble(List<Transform> all)
        {
            for (int Index = 0; Index < all.Count; Index++)
            {
                all[Index].SetLocalPositionAndRotation(
                    new Vector3(Index * 0.13f, Index * -0.07f, Index * 0.21f),
                    Quaternion.Euler(Index * 11f, Index * 23f, Index * 37f));
            }
        }

        static void AssertSameLocal(Transform t, Vector3 position, Quaternion rotation, string what)
        {
            Assert.Less(Vector3.Distance(t.localPosition, position), 1e-4f, what + " localPosition");
            Assert.Less(Quaternion.Angle(t.localRotation, rotation), 1e-2f, what + " localRotation");
        }

        [Test]
        public void Replay_RestoresEveryBone_AndLeavesTheAnimatorRootAlone()
        {
            using var rig = BasisHumanoidRigFixture.Build("tpose-replay");
            EntityId key = BasisAvatarModelCache.GetKey(rig.Animator);
            BasisAvatarModelCache.Remove(key);
            try
            {
                List<Transform> all = Hierarchy(rig);
                var positions = new Vector3[all.Count];
                var rotations = new Quaternion[all.Count];
                for (int Index = 0; Index < all.Count; Index++)
                {
                    all[Index].GetLocalPositionAndRotation(out positions[Index], out rotations[Index]);
                }

                BasisAvatarModelCache.StoreTposeHierarchy(rig.Animator);
                Scramble(all);
                Vector3 spawnPosition = all[0].localPosition;
                Quaternion spawnRotation = all[0].localRotation;

                Assert.IsTrue(BasisAvatarModelCache.TryReplayTposeHierarchy(rig.Animator));

                for (int Index = 1; Index < all.Count; Index++)
                {
                    AssertSameLocal(all[Index], positions[Index], rotations[Index], all[Index].name);
                }
                AssertSameLocal(all[0], spawnPosition, spawnRotation, "animator root");
            }
            finally
            {
                BasisAvatarModelCache.Remove(key);
            }
        }

        /// <summary>
        /// A second install must not overwrite the recording with whatever pose that instance
        /// happens to be in — only the first one ran through the animator.
        /// </summary>
        [Test]
        public void Store_KeepsTheFirstRecording()
        {
            using var rig = BasisHumanoidRigFixture.Build("tpose-first-wins");
            EntityId key = BasisAvatarModelCache.GetKey(rig.Animator);
            BasisAvatarModelCache.Remove(key);
            try
            {
                List<Transform> all = Hierarchy(rig);
                Vector3 authored = all[1].localPosition;

                BasisAvatarModelCache.StoreTposeHierarchy(rig.Animator);
                Scramble(all);
                BasisAvatarModelCache.StoreTposeHierarchy(rig.Animator);

                Assert.IsTrue(BasisAvatarModelCache.TryReplayTposeHierarchy(rig.Animator));
                Assert.Less(Vector3.Distance(all[1].localPosition, authored), 1e-4f);
            }
            finally
            {
                BasisAvatarModelCache.Remove(key);
            }
        }

        [Test]
        public void Replay_RefusesWhenNothingRecorded()
        {
            using var rig = BasisHumanoidRigFixture.Build("tpose-cold");
            EntityId key = BasisAvatarModelCache.GetKey(rig.Animator);
            BasisAvatarModelCache.Remove(key);
            Assert.IsFalse(BasisAvatarModelCache.TryReplayTposeHierarchy(rig.Animator));
        }

        /// <summary>
        /// Two rigs cannot share an Avatar asset in practice, but a positional replay onto a
        /// hierarchy that is not the one recorded would pose bones from their neighbours. The
        /// count guard has to send that case back to the animator instead of writing anything.
        /// </summary>
        [Test]
        public void Replay_RefusesWhenTheHierarchyDoesNotMatch()
        {
            using var rig = BasisHumanoidRigFixture.Build("tpose-mismatch");
            EntityId key = BasisAvatarModelCache.GetKey(rig.Animator);
            BasisAvatarModelCache.Remove(key);
            try
            {
                List<Transform> all = Hierarchy(rig);
                BasisAvatarModelCache.StoreTposeHierarchy(rig.Animator);
                Scramble(all);
                Vector3 scrambled = all[1].localPosition;

                var extra = new GameObject("accessory");
                extra.transform.SetParent(all[1], false);

                Assert.IsFalse(BasisAvatarModelCache.TryReplayTposeHierarchy(rig.Animator));
                Assert.Less(Vector3.Distance(all[1].localPosition, scrambled), 1e-4f);
            }
            finally
            {
                BasisAvatarModelCache.Remove(key);
            }
        }
    }
}
