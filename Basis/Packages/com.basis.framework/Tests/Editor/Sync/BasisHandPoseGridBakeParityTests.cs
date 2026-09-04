using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Player;
using NUnit.Framework;
using System;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// The bake sweep was rewritten to stop allocating a BasisPoseData and its ten Quaternion[3]
    /// arrays per grid cell, to write the finger muscles straight into the HumanPose instead of
    /// through per-finger float[4] slices, and to gather the thirty finger bones off the Animator
    /// instead of building a whole BasisTransformMapping. None of that is allowed to move a single
    /// cell, so the reference sweep below is the ORIGINAL algorithm kept verbatim and the baked
    /// grid is compared against it bit for bit.
    ///
    /// It also pins the finger ORDER. The rewrite replaced the mapping's per-finger arrays with a
    /// flat HumanBodyBones table, and a table in the wrong order would swap two fingers' cells
    /// while leaving every other property of the grid intact.
    /// </summary>
    public class BasisHandPoseGridBakeParityTests
    {
        const int Fingers = BasisHandPoseGrid.FingerCount;
        const int Joints = BasisHandPoseGrid.JointsPerFinger;
        const int JointCount = BasisHandPoseGrid.JointCount;
        const int MuscleLeftThumb = 55;

        [Test]
        public void Bake_MatchesTheOriginalPerCellSweepBitForBit()
        {
            using var rig = BasisHumanoidRigFixture.Build("parity");
            using var grid = new BasisHandPoseGrid();
            Assert.IsTrue(grid.TryBake(rig.Animator, BasisHandPoseGrid.DefaultIncrement, out var bake));
            Assert.IsTrue(grid.IsCreated);

            Quaternion[] reference = ReferenceSweep(
                rig.Animator, grid.Increment, grid.GridWidth, grid.GridHeight, grid.FingerStride, out BasisPoseData referenceRest);

            Assert.AreEqual(reference.Length, grid.Cells.Length, "reference sweep produced a different cell count");
            for (int i = 0; i < reference.Length; i++)
            {
                float4 a = ((quaternion)reference[i]).value;
                float4 b = grid.Cells[i].value;
                Assert.AreEqual(a.x, b.x, $"cell {i}.x");
                Assert.AreEqual(a.y, b.y, $"cell {i}.y");
                Assert.AreEqual(a.z, b.z, $"cell {i}.z");
                Assert.AreEqual(a.w, b.w, $"cell {i}.w");
            }

            Quaternion[][] mine =
            {
                bake.RestPose.LeftThumb, bake.RestPose.LeftIndex, bake.RestPose.LeftMiddle, bake.RestPose.LeftRing, bake.RestPose.LeftLittle,
                bake.RestPose.RightThumb, bake.RestPose.RightIndex, bake.RestPose.RightMiddle, bake.RestPose.RightRing, bake.RestPose.RightLittle,
            };
            Quaternion[][] theirs =
            {
                referenceRest.LeftThumb, referenceRest.LeftIndex, referenceRest.LeftMiddle, referenceRest.LeftRing, referenceRest.LeftLittle,
                referenceRest.RightThumb, referenceRest.RightIndex, referenceRest.RightMiddle, referenceRest.RightRing, referenceRest.RightLittle,
            };
            for (int finger = 0; finger < Fingers; finger++)
            {
                for (int joint = 0; joint < Joints; joint++)
                {
                    Assert.AreEqual(theirs[finger][joint], mine[finger][joint], $"rest pose finger {finger} joint {joint}");
                }
            }
        }

        /// <summary>
        /// The sweep used to fill the very arrays it had just captured the T-pose muscles into, so
        /// what reached the cache was the last grid cell's muscle values (curl and splay both at
        /// their maximum) rather than the T-pose. Nothing reads them today, which is why it went
        /// unnoticed; writing the muscles straight into the HumanPose leaves the capture intact.
        /// </summary>
        [Test]
        public void Bake_KeepsTheTposeMuscleCaptureInsteadOfTheLastCell()
        {
            using var rig = BasisHumanoidRigFixture.Build("muscles");
            using var grid = new BasisHandPoseGrid();
            Assert.IsTrue(grid.TryBake(rig.Animator, BasisHandPoseGrid.DefaultIncrement, out var bake));

            float[] tpose = TposeFingerMuscles(rig.Animator);
            float[][] captured =
            {
                bake.LeftThumb, bake.LeftIndex, bake.LeftMiddle, bake.LeftRing, bake.LeftLittle,
                bake.RightThumb, bake.RightIndex, bake.RightMiddle, bake.RightRing, bake.RightLittle,
            };
            for (int finger = 0; finger < Fingers; finger++)
            {
                Assert.IsNotNull(captured[finger], $"finger {finger} muscles were never captured");
                for (int muscle = 0; muscle < 4; muscle++)
                {
                    Assert.AreEqual(tpose[finger * 4 + muscle], captured[finger][muscle], 1e-6f,
                        $"finger {finger} muscle {muscle} is not the T-pose value");
                }
            }
        }

        /// <summary>
        /// 441 cells used to cost 442 BasisPoseData plus 4,420 Quaternion[3] arrays — around 330 KB
        /// of managed garbage per avatar MODEL, on the main thread, inside the calibration spike
        /// that every first wearer of an avatar pays.
        /// </summary>
        [Test]
        public void Bake_DoesNotAllocatePerGridCell()
        {
            using var rig = BasisHumanoidRigFixture.Build("alloc");
            using (var warm = new BasisHandPoseGrid())
            {
                Assert.IsTrue(warm.TryBake(rig.Animator, BasisHandPoseGrid.DefaultIncrement, out _));
            }

            using var grid = new BasisHandPoseGrid();
            long before = GC.GetTotalMemory(false);
            Assert.IsTrue(grid.TryBake(rig.Animator, BasisHandPoseGrid.DefaultIncrement, out _));
            long allocated = GC.GetTotalMemory(false) - before;

            int cells = grid.GridWidth * grid.GridHeight;
            UnityEngine.Debug.Log($"[finger] grid bake managed alloc: {allocated / 1024} KB over {cells} cells ({allocated / cells} B/cell)");
            Assert.Less(allocated, 128 * 1024, "the bake sweep is allocating per grid cell again");
        }

        /// <summary>
        /// The far LOD skeleton is twenty core bones with arms ending at the wrist, so every cell it
        /// could ever record is identity — and it was paying 441 SetHumanPose calls to find that
        /// out, once per far LOD version. The grid still has to EXIST and still has to be identity:
        /// the allocation zero-inits to (0,0,0,0), which slerps to NaN and would drive a remote's
        /// fingers to a garbage rotation rather than leaving them at the bind pose.
        /// </summary>
        [Test]
        public void Bake_SkipsTheSweepForARigWithNoFingerBones()
        {
            using var fingered = BasisHumanoidRigFixture.Build("fingered");
            using var fingerless = BasisHumanoidRigFixture.Build("fingerless", fingers: false);

            using (var warm = new BasisHandPoseGrid())
            {
                Assert.IsTrue(warm.TryBake(fingered.Animator, BasisHandPoseGrid.DefaultIncrement, out _));
            }

            using var full = new BasisHandPoseGrid();
            var sw = Stopwatch.StartNew();
            Assert.IsTrue(full.TryBake(fingered.Animator, BasisHandPoseGrid.DefaultIncrement, out _));
            double fullMs = sw.Elapsed.TotalMilliseconds;

            using var empty = new BasisHandPoseGrid();
            sw.Restart();
            Assert.IsTrue(empty.TryBake(fingerless.Animator, BasisHandPoseGrid.DefaultIncrement, out var bake));
            double emptyMs = sw.Elapsed.TotalMilliseconds;

            UnityEngine.Debug.Log($"[finger] fingerless bake {emptyMs:F3} ms vs fingered {fullMs:F3} ms");
            Assert.IsTrue(empty.IsCreated, "a fingerless rig still needs a samplable grid");
            Assert.AreEqual(full.Cells.Length, empty.Cells.Length);

            for (int i = 0; i < empty.Cells.Length; i++)
            {
                float4 v = empty.Cells[i].value;
                Assert.AreEqual(0f, v.x, $"cell {i}.x"); Assert.AreEqual(0f, v.y, $"cell {i}.y");
                Assert.AreEqual(0f, v.z, $"cell {i}.z"); Assert.AreEqual(1f, v.w, $"cell {i}.w");
            }
            for (int finger = 0; finger < Fingers; finger++)
            {
                for (int joint = 0; joint < Joints; joint++)
                {
                    quaternion sampled = empty.SampleJoint(finger, joint, new float2(0.37f, -0.61f));
                    Assert.IsFalse(math.any(math.isnan(sampled.value)), $"finger {finger} joint {joint} sampled NaN");
                    Assert.AreEqual(1f, sampled.value.w, 1e-6f, $"finger {finger} joint {joint} is not identity");
                }
            }

            Quaternion[][] rest =
            {
                bake.RestPose.LeftThumb, bake.RestPose.LeftIndex, bake.RestPose.LeftMiddle, bake.RestPose.LeftRing, bake.RestPose.LeftLittle,
                bake.RestPose.RightThumb, bake.RestPose.RightIndex, bake.RestPose.RightMiddle, bake.RestPose.RightRing, bake.RestPose.RightLittle,
            };
            for (int finger = 0; finger < Fingers; finger++)
            {
                for (int joint = 0; joint < Joints; joint++)
                {
                    Assert.AreEqual(Quaternion.identity, rest[finger][joint], $"rest finger {finger} joint {joint}");
                }
            }

            Assert.Less(emptyMs, fullMs * 0.5,
                "the fingerless bake is still running the sweep; every far LOD version pays it");
        }

        static float[] TposeFingerMuscles(Animator source)
        {
            var muscles = new float[Fingers * 4];
            GameObject copy = UnityEngine.Object.Instantiate(source.gameObject);
            copy.SetActive(false);
            try
            {
                Animator animator = copy.GetComponent<Animator>();
                animator.logWarnings = false;
                animator.runtimeAnimatorController = BasisPlayerFactory.TposeController;
                animator.Update(Time.deltaTime);

                var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
                try
                {
                    var pose = new HumanPose();
                    poseHandler.GetHumanPose(ref pose);
                    Array.Copy(pose.muscles, MuscleLeftThumb, muscles, 0, muscles.Length);
                }
                finally
                {
                    poseHandler.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
            return muscles;
        }

        /// <summary>
        /// The pre-rewrite bake, kept verbatim: BasisTransformMapping detection, a float[4] slice
        /// per finger copied into the pose, and a fresh BasisPoseData per cell.
        /// </summary>
        static Quaternion[] ReferenceSweep(
            Animator source, float increment, int gridWidth, int gridHeight, int fingerStride, out BasisPoseData rest)
        {
            var cells = new Quaternion[Fingers * fingerStride];
            rest = null;

            GameObject copy = UnityEngine.Object.Instantiate(source.gameObject);
            copy.SetActive(false);
            try
            {
                Animator animator = copy.GetComponent<Animator>();
                BasisTransformMapping mapping = new BasisTransformMapping();
                Assert.IsTrue(BasisTransformMapping.AutoDetectReferences(animator, animator.transform, ref mapping, detectArmTwist: false));

                Transform[][] mapped =
                {
                    mapping.LeftThumb, mapping.LeftIndex, mapping.LeftMiddle, mapping.LeftRing, mapping.LeftLittle,
                    mapping.RightThumb, mapping.RightIndex, mapping.RightMiddle, mapping.RightRing, mapping.RightLittle,
                };
                bool[][] mappedHas =
                {
                    mapping.HasLeftThumb, mapping.HasLeftIndex, mapping.HasLeftMiddle, mapping.HasLeftRing, mapping.HasLeftLittle,
                    mapping.HasRightThumb, mapping.HasRightIndex, mapping.HasRightMiddle, mapping.HasRightRing, mapping.HasRightLittle,
                };
                var joints = new Transform[JointCount];
                var present = new bool[JointCount];
                for (int finger = 0; finger < Fingers; finger++)
                {
                    for (int joint = 0; joint < Joints; joint++)
                    {
                        joints[finger * Joints + joint] = mapped[finger][joint];
                        present[finger * Joints + joint] = mappedHas[finger][joint];
                    }
                }

                animator.logWarnings = false;
                animator.runtimeAnimatorController = BasisPlayerFactory.TposeController;
                animator.Update(Time.deltaTime);

                var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
                try
                {
                    var pose = new HumanPose();
                    poseHandler.GetHumanPose(ref pose);

                    rest = ReferenceRecord(joints, present);

                    var muscles = new float[Fingers][];
                    for (int finger = 0; finger < Fingers; finger++)
                    {
                        muscles[finger] = new float[4];
                        Array.Copy(pose.muscles, MuscleLeftThumb + finger * 4, muscles[finger], 0, 4);
                    }

                    for (int xi = 0; xi < gridWidth; xi++)
                    {
                        for (int yi = 0; yi < gridHeight; yi++)
                        {
                            float curl = -1f + xi * increment;
                            float splay = -1f + yi * increment;
                            for (int finger = 0; finger < Fingers; finger++)
                            {
                                float[] slice = muscles[finger];
                                Array.Fill(slice, curl);
                                slice[1] = splay;
                                Array.Copy(slice, 0, pose.muscles, MuscleLeftThumb + finger * 4, 4);
                            }
                            poseHandler.SetHumanPose(ref pose);

                            BasisPoseData recorded = ReferenceRecord(joints, present);
                            Quaternion[][] recordedFingers =
                            {
                                recorded.LeftThumb, recorded.LeftIndex, recorded.LeftMiddle, recorded.LeftRing, recorded.LeftLittle,
                                recorded.RightThumb, recorded.RightIndex, recorded.RightMiddle, recorded.RightRing, recorded.RightLittle,
                            };
                            int gridIndex = xi * gridHeight + yi;
                            for (int finger = 0; finger < Fingers; finger++)
                            {
                                int cell = finger * fingerStride + gridIndex * Joints;
                                cells[cell] = recordedFingers[finger][0];
                                cells[cell + 1] = recordedFingers[finger][1];
                                cells[cell + 2] = recordedFingers[finger][2];
                            }
                        }
                    }
                }
                finally
                {
                    poseHandler.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
            return cells;
        }

        static BasisPoseData ReferenceRecord(Transform[] joints, bool[] present)
        {
            var pose = new BasisPoseData();
            Quaternion[][] fingers =
            {
                pose.LeftThumb, pose.LeftIndex, pose.LeftMiddle, pose.LeftRing, pose.LeftLittle,
                pose.RightThumb, pose.RightIndex, pose.RightMiddle, pose.RightRing, pose.RightLittle,
            };
            for (int finger = 0; finger < Fingers; finger++)
            {
                for (int joint = 0; joint < Joints; joint++)
                {
                    int index = finger * Joints + joint;
                    fingers[finger][joint] = present[index] ? joints[index].localRotation : Quaternion.identity;
                }
            }
            return pose;
        }
    }
}
