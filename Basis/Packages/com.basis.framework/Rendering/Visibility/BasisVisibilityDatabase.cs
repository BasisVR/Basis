using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Basis.Scripts.Rendering
{
    public sealed class BasisVisibilityBinding
    {
        public Renderer[] Renderers;
    }

    [AutoStaticsCleanup]
    public static partial class BasisVisibilityDatabase
    {
        public const int InvalidHandle = -1;
        private const int InitialCapacity = 256;

        [NoAutoStaticsCleanup] public static NativeArray<float3> Centers;

        /// <summary>Unscaled extents as registered. The bounds job scales these into <see cref="Extents"/>.</summary>
        [NoAutoStaticsCleanup] public static NativeArray<float3> BaseExtents;

        /// <summary>World extents the cull actually tests against.</summary>
        [NoAutoStaticsCleanup] public static NativeArray<float3> Extents;
        [NoAutoStaticsCleanup] public static NativeArray<uint> Flags;
        [NoAutoStaticsCleanup] public static NativeArray<uint> VisibleMask;
        [NoAutoStaticsCleanup] public static NativeArray<byte> AppliedVisible;

        /// <summary>
        /// Roots of every entry that has one, packed dense for <c>IJobParallelForTransform</c>.
        /// <see cref="DenseToSlot"/> maps a row here back to its database slot; <see cref="SlotToDense"/>
        /// is the inverse so removal is O(1) instead of a scan.
        /// </summary>
        [NoAutoStaticsCleanup] public static TransformAccessArray Roots;
        [NoAutoStaticsCleanup] public static NativeList<int> DenseToSlot;
        [NoAutoStaticsCleanup] public static NativeArray<int> SlotToDense;

        public static BasisVisibilityBinding[] Bindings = Array.Empty<BasisVisibilityBinding>();

        private static readonly Stack<int> FreeSlots = new Stack<int>();
        private static int _capacity;
        private static int _count;
        private static int _cullableCount;
        private static int _culledCount;

        public static int Count => _count;
        public static int Capacity => _capacity;
        public static int DynamicCount => DenseToSlot.IsCreated ? DenseToSlot.Length : 0;
        public static bool IsCreated => Centers.IsCreated;

        public static int CullableCount => _cullableCount;
        public static int CulledCount => _culledCount;

        public static void EnsureCreated()
        {
            if (Centers.IsCreated)
            {
                return;
            }
            Allocate(InitialCapacity);
            Roots = new TransformAccessArray(InitialCapacity);
            DenseToSlot = new NativeList<int>(InitialCapacity, Allocator.Persistent);
        }

        public static void Dispose()
        {
            BasisVisibilitySystem.CompleteIfScheduled();

            if (Centers.IsCreated) Centers.Dispose();
            if (BaseExtents.IsCreated) BaseExtents.Dispose();
            if (Extents.IsCreated) Extents.Dispose();
            if (Flags.IsCreated) Flags.Dispose();
            if (VisibleMask.IsCreated) VisibleMask.Dispose();
            if (AppliedVisible.IsCreated) AppliedVisible.Dispose();
            if (SlotToDense.IsCreated) SlotToDense.Dispose();
            if (DenseToSlot.IsCreated) DenseToSlot.Dispose();
            if (Roots.isCreated) Roots.Dispose();

            Bindings = Array.Empty<BasisVisibilityBinding>();
            FreeSlots.Clear();
            _capacity = 0;
            _count = 0;
            _cullableCount = 0;
            _culledCount = 0;
        }

        public static int Register(Renderer[] renderers, Transform root, float3 center, float3 extents, BasisVisibilityFlags flags)
        {
            BasisVisibilitySystem.CompleteIfScheduled();
            EnsureCreated();

            if (root == null && (flags & BasisVisibilityFlags.Static) == 0)
            {
                flags |= BasisVisibilityFlags.AlwaysVisible;
            }

            int slot;
            if (FreeSlots.Count > 0)
            {
                slot = FreeSlots.Pop();
            }
            else
            {
                if (_count >= _capacity)
                {
                    Allocate(_capacity * 2);
                }
                slot = _count;
                _count++;
            }

            uint stored = (uint)(flags | BasisVisibilityFlags.Active);

            Centers[slot] = center;
            BaseExtents[slot] = extents;
            Extents[slot] = extents;
            Flags[slot] = stored;
            VisibleMask[slot] = uint.MaxValue;
            AppliedVisible[slot] = 1;
            SlotToDense[slot] = InvalidHandle;

            if (IsCullable(stored))
            {
                _cullableCount++;
            }

            Bindings[slot] = new BasisVisibilityBinding
            {
                Renderers = renderers,
            };

            if (root != null)
            {
                Roots.Add(root);
                DenseToSlot.Add(slot);
                SlotToDense[slot] = DenseToSlot.Length - 1;
            }
            return slot;
        }

        public static void Unregister(int handle)
        {
            BasisVisibilitySystem.CompleteIfScheduled();
            if (!IsValid(handle))
            {
                return;
            }

            RestoreRenderers(Bindings[handle]);
            RemoveRoot(handle);

            if (IsCullable(Flags[handle]))
            {
                _cullableCount--;
            }
            if (AppliedVisible[handle] == 0)
            {
                _culledCount--;
            }

            Flags[handle] = (uint)BasisVisibilityFlags.None;
            VisibleMask[handle] = uint.MaxValue;
            AppliedVisible[handle] = 1;
            Bindings[handle] = null;
            FreeSlots.Push(handle);
        }

        private static void RemoveRoot(int handle)
        {
            int dense = SlotToDense[handle];
            if (dense < 0)
            {
                return;
            }

            int last = DenseToSlot.Length - 1;
            int movedSlot = DenseToSlot[last];

            Roots.RemoveAtSwapBack(dense);
            DenseToSlot.RemoveAtSwapBack(dense);

            if (dense != last)
            {
                SlotToDense[movedSlot] = dense;
            }
            SlotToDense[handle] = InvalidHandle;
        }

        public static void SetBounds(int handle, float3 center, float3 extents)
        {
            BasisVisibilitySystem.CompleteIfScheduled();
            if (!IsValid(handle))
            {
                return;
            }
            Centers[handle] = center;
            BaseExtents[handle] = extents;
            Extents[handle] = extents;
        }

        public static void SetFlags(int handle, BasisVisibilityFlags flags)
        {
            BasisVisibilitySystem.CompleteIfScheduled();
            if (!IsValid(handle))
            {
                return;
            }
            uint previous = Flags[handle];
            uint next = (uint)(flags | BasisVisibilityFlags.Active);
            Flags[handle] = next;
            ApplyCullableDelta(previous, next);
        }

        public static void SetCullEligible(int handle, bool eligible)
        {
            BasisVisibilitySystem.CompleteIfScheduled();
            if (!IsValid(handle))
            {
                return;
            }
            uint previous = Flags[handle];
            uint current = previous;
            if (eligible)
            {
                current |= (uint)BasisVisibilityFlags.CullEligible;
            }
            else
            {
                current &= ~(uint)BasisVisibilityFlags.CullEligible;
            }
            Flags[handle] = current;
            ApplyCullableDelta(previous, current);
        }

        public static void SetApplied(int handle, bool visible)
        {
            byte value = visible ? (byte)1 : (byte)0;
            if (AppliedVisible[handle] == value)
            {
                return;
            }
            AppliedVisible[handle] = value;
            _culledCount += visible ? -1 : 1;
        }

        private static void ApplyCullableDelta(uint previous, uint next)
        {
            bool was = IsCullable(previous);
            bool now = IsCullable(next);
            if (was != now)
            {
                _cullableCount += now ? 1 : -1;
            }
        }

        private static bool IsCullable(uint flags)
        {
            const uint Required = (uint)(BasisVisibilityFlags.Active | BasisVisibilityFlags.CullEligible);
            return (flags & Required) == Required
                && (flags & (uint)BasisVisibilityFlags.AlwaysVisible) == 0;
        }

        public static bool IsValid(int handle)
        {
            return handle >= 0 && handle < _count && Centers.IsCreated
                && (Flags[handle] & (uint)BasisVisibilityFlags.Active) != 0;
        }

        public static void RestoreAll()
        {
            BasisVisibilitySystem.CompleteIfScheduled();
            for (int index = 0; index < _count; index++)
            {
                RestoreRenderers(Bindings[index]);
                if (AppliedVisible.IsCreated)
                {
                    AppliedVisible[index] = 1;
                }
            }
            _culledCount = 0;
        }

        private static void RestoreRenderers(BasisVisibilityBinding binding)
        {
            Renderer[] renderers = binding?.Renderers;
            if (renderers == null)
            {
                return;
            }
            for (int index = 0; index < renderers.Length; index++)
            {
                Renderer renderer = renderers[index];
                if (renderer != null && renderer.forceRenderingOff)
                {
                    renderer.forceRenderingOff = false;
                }
            }
        }

        private static void Allocate(int capacity)
        {
            if (capacity < InitialCapacity)
            {
                capacity = InitialCapacity;
            }

            int previous = _capacity;

            Resize(ref Centers, capacity);
            Resize(ref BaseExtents, capacity);
            Resize(ref Extents, capacity);
            Resize(ref Flags, capacity);
            Resize(ref VisibleMask, capacity);
            Resize(ref AppliedVisible, capacity);
            Resize(ref SlotToDense, capacity);

            for (int index = previous; index < capacity; index++)
            {
                SlotToDense[index] = InvalidHandle;
            }

            Array.Resize(ref Bindings, capacity);
            _capacity = capacity;
        }

        private static void Resize<T>(ref NativeArray<T> array, int capacity) where T : struct
        {
            var next = new NativeArray<T>(capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            if (array.IsCreated)
            {
                int copy = array.Length < capacity ? array.Length : capacity;
                NativeArray<T>.Copy(array, next, copy);
                array.Dispose();
            }
            array = next;
        }
    }
}
