using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Constraints
{
    /// <summary>
    /// A solved-together set of <see cref="BasisConstraint"/> components: the flat slot and source
    /// arrays plus the transform table their indices point into.
    ///
    /// Slots are ordered shallowest-first by hierarchy depth so that when a constraint follows a
    /// transform that is itself constrained, the parent has already been solved this pass. Owns
    /// native memory — call <see cref="Dispose"/>.
    /// </summary>
    public struct BasisConstraintBatch : IDisposable
    {
        public NativeArray<BasisConstraintSlot> Slots;
        public NativeArray<BasisConstraintSource> Sources;
        public NativeArray<BasisConstraintWorld> World;

        /// <summary>Transform for each index used by <see cref="Slots"/> and <see cref="Sources"/>.</summary>
        public Transform[] Transforms;

        public bool IsCreated => Slots.IsCreated;

        public void Dispose()
        {
            if (Slots.IsCreated)
            {
                Slots.Dispose();
            }
            if (Sources.IsCreated)
            {
                Sources.Dispose();
            }
            if (World.IsCreated)
            {
                World.Dispose();
            }
            Transforms = null;
        }
    }

    /// <summary>
    /// Flattens <see cref="BasisConstraint"/> components into a <see cref="BasisConstraintBatch"/>.
    /// Kept free of scene traversal so it can be driven from tests with hand-built hierarchies.
    /// </summary>
    public static class BasisConstraintBuilder
    {
        /// <summary>
        /// Builds a batch from the given constraints. Constraints with no usable source are dropped, as
        /// are null entries; the returned batch is empty (but created) when nothing survives.
        /// </summary>
        public static BasisConstraintBatch Build(IReadOnlyList<BasisConstraint> constraints, Allocator allocator, int avatarId = -1)
        {
            var transforms = new List<Transform>();
            var transformIndex = new Dictionary<Transform, int>();
            var slots = new List<BasisConstraintSlot>();
            var sources = new List<BasisConstraintSource>();

            int count = constraints?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                BasisConstraint constraint = constraints[i];
                if (constraint == null || constraint.CountValidSources() == 0)
                {
                    continue;
                }

                int sourceStart = sources.Count;
                int sourceCount = 0;
                for (int s = 0; s < constraint.Sources.Length; s++)
                {
                    BasisConstraintSourceBinding binding = constraint.Sources[s];
                    if (binding == null || binding.SourceTransform == null || binding.Weight <= 0f)
                    {
                        continue;
                    }
                    sources.Add(new BasisConstraintSource
                    {
                        TransformIndex = IndexOf(binding.SourceTransform, transforms, transformIndex),
                        Weight = Mathf.Clamp01(binding.Weight),
                        PositionOffset = binding.PositionOffset,
                        RotationOffset = quaternion.Euler(math.radians((float3)binding.RotationOffset)),
                    });
                    sourceCount++;
                }

                int targetIndex = IndexOf(constraint.transform, transforms, transformIndex);
                int worldUpIndex = constraint.UsesAim && constraint.WorldUpObject != null
                    ? IndexOf(constraint.WorldUpObject, transforms, transformIndex)
                    : -1;

                slots.Add(constraint.ToSlot(
                    targetIndex,
                    sourceStart,
                    sourceCount,
                    worldUpIndex,
                    DepthOf(constraint.transform),
                    avatarId));
            }

            slots.Sort(CompareByDepth);

            return new BasisConstraintBatch
            {
                Slots = new NativeArray<BasisConstraintSlot>(slots.ToArray(), allocator),
                Sources = new NativeArray<BasisConstraintSource>(sources.ToArray(), allocator),
                World = new NativeArray<BasisConstraintWorld>(transforms.Count, allocator),
                Transforms = transforms.ToArray(),
            };
        }

        /// <summary>Sorts shallowest-first, keeping authoring order stable within one depth.</summary>
        private static int CompareByDepth(BasisConstraintSlot a, BasisConstraintSlot b)
        {
            int byDepth = a.Depth.CompareTo(b.Depth);
            return byDepth != 0 ? byDepth : a.TargetIndex.CompareTo(b.TargetIndex);
        }

        /// <summary>Reads every transform in the table into the batch's world pose array.</summary>
        public static void ReadWorld(ref BasisConstraintBatch batch)
        {
            if (!batch.IsCreated || batch.Transforms == null)
            {
                return;
            }
            for (int i = 0; i < batch.Transforms.Length; i++)
            {
                Transform t = batch.Transforms[i];
                if (t == null)
                {
                    continue;
                }
                batch.World[i] = new BasisConstraintWorld
                {
                    Position = t.position,
                    Rotation = t.rotation,
                    Scale = t.lossyScale,
                };
            }
        }

        /// <summary>Writes solved poses back onto the transforms that constraints actually target.</summary>
        public static void WriteTargets(ref BasisConstraintBatch batch)
        {
            if (!batch.IsCreated || batch.Transforms == null)
            {
                return;
            }
            for (int i = 0; i < batch.Slots.Length; i++)
            {
                BasisConstraintSlot slot = batch.Slots[i];
                if (slot.TargetIndex < 0 || slot.TargetIndex >= batch.Transforms.Length)
                {
                    continue;
                }
                Transform t = batch.Transforms[slot.TargetIndex];
                if (t == null)
                {
                    continue;
                }
                BasisConstraintWorld solved = batch.World[slot.TargetIndex];
                t.SetPositionAndRotation(solved.Position, solved.Rotation);
            }
        }

        /// <summary>Number of parents between this transform and its root — the batch's solve order key.</summary>
        public static int DepthOf(Transform transform)
        {
            int depth = 0;
            Transform cursor = transform != null ? transform.parent : null;
            while (cursor != null)
            {
                depth++;
                cursor = cursor.parent;
            }
            return depth;
        }

        private static int IndexOf(Transform transform, List<Transform> transforms, Dictionary<Transform, int> map)
        {
            if (transform == null)
            {
                return -1;
            }
            if (map.TryGetValue(transform, out int existing))
            {
                return existing;
            }
            int index = transforms.Count;
            transforms.Add(transform);
            map.Add(transform, index);
            return index;
        }
    }
}
