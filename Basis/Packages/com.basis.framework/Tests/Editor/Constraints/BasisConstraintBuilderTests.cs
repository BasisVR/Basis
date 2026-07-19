using System.Collections.Generic;
using Basis.Scripts.Constraints;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Tests.Constraints
{
    /// <summary>
    /// Coverage for the authoring layer: how a <see cref="BasisConstraint"/> component projects onto the
    /// flat <see cref="BasisConstraintSlot"/> record, and how <see cref="BasisConstraintBuilder"/> flattens
    /// a set of components into the batch the solver consumes — transform de-duplication, dropping of
    /// unusable sources, and shallowest-first ordering so chained constraints solve in the right order.
    /// </summary>
    public class BasisConstraintBuilderTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                {
                    Object.DestroyImmediate(_spawned[i]);
                }
            }
            _spawned.Clear();
        }

        // ---------- component -> slot ----------

        [Test]
        public void ToSlot_CarriesTheAuthoredFieldsAcross()
        {
            BasisConstraint constraint = NewConstraint("target");
            constraint.Kind = BasisConstraintKind.Aim;
            constraint.Weight = 0.25f;
            constraint.Roll = 30f;
            constraint.TranslationOffset = new Vector3(1f, 2f, 3f);
            constraint.ScaleOffset = new Vector3(2f, 2f, 2f);
            constraint.TranslationAxis = BasisConstraintAxis.X | BasisConstraintAxis.Z;
            constraint.WorldUpKind = BasisWorldUpKind.Vector;

            BasisConstraintSlot slot = constraint.ToSlot(3, 7, 2, 5, 9, 42);

            Assert.AreEqual(BasisConstraintKind.Aim, slot.Kind, "kind must survive the projection");
            Assert.AreEqual(3, slot.TargetIndex);
            Assert.AreEqual(7, slot.SourceStart);
            Assert.AreEqual(2, slot.SourceCount);
            Assert.AreEqual(5, slot.WorldUpIndex);
            Assert.AreEqual(9, slot.Depth);
            Assert.AreEqual(42, slot.AvatarId);
            Assert.AreEqual(0.25f, slot.Weight, 1e-5f);
            Assert.AreEqual(30f, slot.Roll, 1e-5f);
            Assert.AreEqual((byte)(BasisConstraintAxis.X | BasisConstraintAxis.Z), slot.TranslationMask, "the axis mask is stored as its flag bits");
            Assert.AreEqual(BasisWorldUpKind.Vector, slot.WorldUpKind);
            Assert.AreEqual(1f, slot.TranslationOffset.x, 1e-5f);
            Assert.AreEqual(2f, slot.ScaleOffset.y, 1e-5f);
        }

        [Test]
        public void ToSlot_ClampsWeightIntoRange()
        {
            BasisConstraint constraint = NewConstraint("target");
            constraint.Weight = 5f;
            Assert.AreEqual(1f, constraint.ToSlot(0, 0, 1, -1, 0, -1).Weight, 1e-5f, "an over-range weight must clamp to 1");

            constraint.Weight = -2f;
            Assert.AreEqual(0f, constraint.ToSlot(0, 0, 1, -1, 0, -1).Weight, 1e-5f, "a negative weight must clamp to 0");
        }

        [Test]
        public void ToSlot_ConvertsEulerDegreesToRadianQuaternions()
        {
            BasisConstraint constraint = NewConstraint("target");
            constraint.RotationOffset = new Vector3(0f, 90f, 0f);

            BasisConstraintSlot slot = constraint.ToSlot(0, 0, 1, -1, 0, -1);
            float3 forward = math.mul(slot.RotationOffset, new float3(0f, 0f, 1f));

            Assert.AreEqual(1f, forward.x, 1e-3f, "a 90 degree yaw offset must rotate forward onto +X — degrees must be converted to radians");
            Assert.AreEqual(0f, forward.z, 1e-3f);
        }

        [Test]
        public void ToSlot_InactiveComponent_ProducesAnInactiveSlot()
        {
            BasisConstraint constraint = NewConstraint("target");
            constraint.ConstraintActive = false;

            Assert.AreEqual(0, constraint.ToSlot(0, 0, 1, -1, 0, -1).Active, "an inactive component must author an inactive slot");
        }

        [Test]
        public void ToSlot_DisabledGameObject_ProducesAnInactiveSlot()
        {
            BasisConstraint constraint = NewConstraint("target");
            constraint.ConstraintActive = true;
            constraint.gameObject.SetActive(false);

            Assert.AreEqual(0, constraint.ToSlot(0, 0, 1, -1, 0, -1).Active, "a disabled GameObject must not keep driving its target");
        }

        [Test]
        public void ToSlot_UseUpObject_TracksWhetherAnUpIndexWasSupplied()
        {
            BasisConstraint constraint = NewConstraint("target");

            Assert.AreEqual(1, constraint.ToSlot(0, 0, 1, 4, 0, -1).UseUpObject, "a real up index sets the flag");
            Assert.AreEqual(0, constraint.ToSlot(0, 0, 1, -1, 0, -1).UseUpObject, "no up index clears the flag");
        }

        [Test]
        public void CaptureRest_StoresTheCurrentPose()
        {
            BasisConstraint constraint = NewConstraint("target");
            constraint.transform.position = new Vector3(1f, 2f, 3f);
            constraint.transform.localScale = new Vector3(2f, 2f, 2f);

            constraint.CaptureRest();

            Assert.AreEqual(new Vector3(1f, 2f, 3f), constraint.TranslationAtRest, "rest position is captured from the transform");
            Assert.AreEqual(new Vector3(2f, 2f, 2f), constraint.ScaleAtRest, "rest scale is captured from the transform");
        }

        [Test]
        public void CaptureRest_OnAChild_CapturesWorldSpaceNotLocal()
        {
            // The solver resolves in world space, so a local rest pose would be silently wrong for
            // anything parented — the rest fallback would land in the wrong place.
            GameObject parent = NewObject("parent");
            parent.transform.position = new Vector3(10f, 0f, 0f);

            BasisConstraint constraint = NewConstraint("child");
            constraint.transform.SetParent(parent.transform);
            constraint.transform.localPosition = new Vector3(1f, 0f, 0f);

            constraint.CaptureRest();

            Assert.AreEqual(11f, constraint.TranslationAtRest.x, 1e-4f,
                "rest must be the world position (10 + 1), not the local offset");
        }

        // ---------- kind channel helpers (these drive which inspector cards show) ----------

        [Test]
        public void KindHelpers_ReportTheChannelsEachKindDrives(
            [Values(
                BasisConstraintKind.Position,
                BasisConstraintKind.Rotation,
                BasisConstraintKind.Scale,
                BasisConstraintKind.Parent,
                BasisConstraintKind.Aim,
                BasisConstraintKind.LookAt)]
            BasisConstraintKind kind)
        {
            BasisConstraint constraint = NewConstraint("target");
            constraint.Kind = kind;

            bool expectPosition = kind == BasisConstraintKind.Position || kind == BasisConstraintKind.Parent;
            bool expectScale = kind == BasisConstraintKind.Scale;
            bool expectAim = kind == BasisConstraintKind.Aim || kind == BasisConstraintKind.LookAt;
            bool expectRotation = kind == BasisConstraintKind.Rotation || kind == BasisConstraintKind.Parent || expectAim;

            Assert.AreEqual(expectPosition, constraint.DrivesPosition, $"{kind} position channel");
            Assert.AreEqual(expectRotation, constraint.DrivesRotation, $"{kind} rotation channel");
            Assert.AreEqual(expectScale, constraint.DrivesScale, $"{kind} scale channel");
            Assert.AreEqual(expectAim, constraint.UsesAim, $"{kind} aim settings");
        }

        [Test]
        public void CountValidSources_IgnoresEmptyAndZeroWeightEntries()
        {
            BasisConstraint constraint = NewConstraint("target");
            Transform a = NewObject("a").transform;

            constraint.Sources = new[]
            {
                new BasisConstraintSourceBinding { SourceTransform = a, Weight = 1f },
                new BasisConstraintSourceBinding { SourceTransform = null, Weight = 1f },
                new BasisConstraintSourceBinding { SourceTransform = a, Weight = 0f },
                null,
            };

            Assert.AreEqual(1, constraint.CountValidSources(), "only the assigned, non-zero-weight source counts");
        }

        // ---------- builder ----------

        [Test]
        public void Build_DropsConstraintsThatHaveNoUsableSource()
        {
            BasisConstraint empty = NewConstraint("empty");
            BasisConstraint zeroWeight = NewConstraint("zero");
            zeroWeight.Sources = new[] { new BasisConstraintSourceBinding { SourceTransform = NewObject("s").transform, Weight = 0f } };

            using BasisConstraintBatch batch = BasisConstraintBuilder.Build(new[] { empty, zeroWeight }, Allocator.Temp);

            Assert.AreEqual(0, batch.Slots.Length, "a constraint with nothing to follow must not reach the solver");
        }

        [Test]
        public void Build_SkipsNullEntries()
        {
            using BasisConstraintBatch batch = BasisConstraintBuilder.Build(new BasisConstraint[] { null, null }, Allocator.Temp);

            Assert.IsTrue(batch.IsCreated, "the batch is still created, just empty");
            Assert.AreEqual(0, batch.Slots.Length);
        }

        [Test]
        public void Build_HandlesANullList()
        {
            using BasisConstraintBatch batch = BasisConstraintBuilder.Build(null, Allocator.Temp);

            Assert.IsTrue(batch.IsCreated, "a null input must produce an empty batch rather than throwing");
            Assert.AreEqual(0, batch.Slots.Length);
        }

        [Test]
        public void Build_DeduplicatesTransformsAcrossConstraints()
        {
            Transform shared = NewObject("shared").transform;
            BasisConstraint first = NewConstraint("first");
            first.Sources = new[] { new BasisConstraintSourceBinding { SourceTransform = shared, Weight = 1f } };
            BasisConstraint second = NewConstraint("second");
            second.Sources = new[] { new BasisConstraintSourceBinding { SourceTransform = shared, Weight = 1f } };

            using BasisConstraintBatch batch = BasisConstraintBuilder.Build(new[] { first, second }, Allocator.Temp);

            // two targets plus the one shared source
            Assert.AreEqual(3, batch.Transforms.Length, "a transform used twice must occupy one slot in the table");
            Assert.AreEqual(batch.Sources[0].TransformIndex, batch.Sources[1].TransformIndex, "both sources must resolve to the same index");
        }

        [Test]
        public void Build_GivesEachConstraintItsOwnSourceRange()
        {
            BasisConstraint first = NewConstraint("first");
            first.Sources = new[]
            {
                new BasisConstraintSourceBinding { SourceTransform = NewObject("a").transform, Weight = 1f },
                new BasisConstraintSourceBinding { SourceTransform = NewObject("b").transform, Weight = 1f },
            };
            BasisConstraint second = NewConstraint("second");
            second.Sources = new[] { new BasisConstraintSourceBinding { SourceTransform = NewObject("c").transform, Weight = 1f } };

            using BasisConstraintBatch batch = BasisConstraintBuilder.Build(new[] { first, second }, Allocator.Temp);

            Assert.AreEqual(3, batch.Sources.Length, "every contributing source lands in the shared array");
            Assert.AreEqual(2, batch.Slots.Length);

            // ranges must not overlap
            BasisConstraintSlot a = batch.Slots[0];
            BasisConstraintSlot b = batch.Slots[1];
            Assert.IsTrue(a.SourceStart + a.SourceCount <= b.SourceStart || b.SourceStart + b.SourceCount <= a.SourceStart,
                "each slot must own a disjoint window of the source array");
        }

        [Test]
        public void Build_ExcludesZeroWeightSourcesFromTheRange()
        {
            BasisConstraint constraint = NewConstraint("target");
            constraint.Sources = new[]
            {
                new BasisConstraintSourceBinding { SourceTransform = NewObject("live").transform, Weight = 1f },
                new BasisConstraintSourceBinding { SourceTransform = NewObject("muted").transform, Weight = 0f },
            };

            using BasisConstraintBatch batch = BasisConstraintBuilder.Build(new[] { constraint }, Allocator.Temp);

            Assert.AreEqual(1, batch.Sources.Length, "a muted source must not take up room in the batch");
            Assert.AreEqual(1, batch.Slots[0].SourceCount);
        }

        [Test]
        public void Build_OrdersSlotsShallowestFirst()
        {
            // grandchild is deepest, so it must solve last even though it is listed first.
            GameObject root = NewObject("root");
            GameObject child = NewObject("child");
            child.transform.SetParent(root.transform);
            GameObject grandchild = NewObject("grandchild");
            grandchild.transform.SetParent(child.transform);

            BasisConstraint deep = WithSource(grandchild.AddComponent<BasisConstraint>());
            BasisConstraint shallow = WithSource(root.AddComponent<BasisConstraint>());
            BasisConstraint middle = WithSource(child.AddComponent<BasisConstraint>());

            using BasisConstraintBatch batch = BasisConstraintBuilder.Build(new[] { deep, shallow, middle }, Allocator.Temp);

            Assert.AreEqual(3, batch.Slots.Length);
            Assert.AreEqual(0, batch.Slots[0].Depth, "the root constraint solves first");
            Assert.AreEqual(1, batch.Slots[1].Depth);
            Assert.AreEqual(2, batch.Slots[2].Depth, "the deepest constraint solves last");
        }

        [Test]
        public void Build_OnlyRecordsAnUpObjectForAimingKinds()
        {
            Transform up = NewObject("up").transform;

            BasisConstraint aim = WithSource(NewConstraint("aim"));
            aim.Kind = BasisConstraintKind.Aim;
            aim.WorldUpObject = up;

            BasisConstraint position = WithSource(NewConstraint("position"));
            position.Kind = BasisConstraintKind.Position;
            position.WorldUpObject = up;

            using BasisConstraintBatch aimBatch = BasisConstraintBuilder.Build(new[] { aim }, Allocator.Temp);
            using BasisConstraintBatch positionBatch = BasisConstraintBuilder.Build(new[] { position }, Allocator.Temp);

            Assert.GreaterOrEqual(aimBatch.Slots[0].WorldUpIndex, 0, "an aim constraint registers its up object");
            Assert.AreEqual(-1, positionBatch.Slots[0].WorldUpIndex, "a position constraint has no use for an up object");
        }

        [Test]
        public void DepthOf_CountsParents()
        {
            GameObject root = NewObject("root");
            GameObject child = NewObject("child");
            child.transform.SetParent(root.transform);
            GameObject grandchild = NewObject("grandchild");
            grandchild.transform.SetParent(child.transform);

            Assert.AreEqual(0, BasisConstraintBuilder.DepthOf(root.transform));
            Assert.AreEqual(1, BasisConstraintBuilder.DepthOf(child.transform));
            Assert.AreEqual(2, BasisConstraintBuilder.DepthOf(grandchild.transform));
            Assert.AreEqual(0, BasisConstraintBuilder.DepthOf(null), "a null transform must not throw");
        }

        [Test]
        public void ReadWorld_CopiesLiveTransformPosesIntoTheBatch()
        {
            BasisConstraint constraint = NewConstraint("target");
            Transform source = NewObject("source").transform;
            source.position = new Vector3(3f, 4f, 5f);
            constraint.Sources = new[] { new BasisConstraintSourceBinding { SourceTransform = source, Weight = 1f } };

            BasisConstraintBatch batch = BasisConstraintBuilder.Build(new[] { constraint }, Allocator.Temp);
            try
            {
                BasisConstraintBuilder.ReadWorld(ref batch);

                int sourceIndex = batch.Sources[0].TransformIndex;
                Assert.AreEqual(3f, batch.World[sourceIndex].Position.x, 1e-4f, "the live source pose must reach the solver array");
                Assert.AreEqual(5f, batch.World[sourceIndex].Position.z, 1e-4f);
            }
            finally
            {
                batch.Dispose();
            }
        }

        [Test]
        public void BuildSolveWrite_MovesTheTargetOntoItsSource()
        {
            // End to end through the real pipeline: author a component, build, read, solve, write back.
            BasisConstraint constraint = NewConstraint("target");
            constraint.Kind = BasisConstraintKind.Position;
            Transform source = NewObject("source").transform;
            source.position = new Vector3(6f, 7f, 8f);
            constraint.Sources = new[] { new BasisConstraintSourceBinding { SourceTransform = source, Weight = 1f } };

            BasisConstraintBatch batch = BasisConstraintBuilder.Build(new[] { constraint }, Allocator.Temp);
            try
            {
                BasisConstraintBuilder.ReadWorld(ref batch);
                NativeArray<BasisConstraintWorld> world = batch.World;
                BasisConstraintSolver.SolveAll(ref world, batch.Slots, batch.Sources);
                BasisConstraintBuilder.WriteTargets(ref batch);
            }
            finally
            {
                batch.Dispose();
            }

            Assert.AreEqual(6f, constraint.transform.position.x, 1e-3f, "the constrained transform must end up on its source");
            Assert.AreEqual(7f, constraint.transform.position.y, 1e-3f);
            Assert.AreEqual(8f, constraint.transform.position.z, 1e-3f);
        }

        [Test]
        public void Dispose_IsSafeToCallTwice()
        {
            BasisConstraintBatch batch = BasisConstraintBuilder.Build(new[] { WithSource(NewConstraint("target")) }, Allocator.Temp);
            batch.Dispose();

            Assert.DoesNotThrow(() => batch.Dispose(), "disposing an already-released batch must be harmless");
        }

        // ---------- helpers ----------

        private GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go;
        }

        private BasisConstraint NewConstraint(string name) => NewObject(name).AddComponent<BasisConstraint>();

        private BasisConstraint WithSource(BasisConstraint constraint)
        {
            constraint.Sources = new[]
            {
                new BasisConstraintSourceBinding { SourceTransform = NewObject(constraint.name + "_source").transform, Weight = 1f },
            };
            return constraint;
        }
    }
}
