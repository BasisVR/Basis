using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Constraints;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Basis.Scripts.Constraints
{
    /// <summary>
    /// Batched solver behind the <see cref="BasisConstraintBase"/> components. Every enabled
    /// constraint in the process — avatar or scene — is flattened into one set of native containers
    /// and solved by three jobs per frame (sample, solve, write), instead of each component paying
    /// for its own <c>Update</c> and its own scattered transform reads.
    ///
    /// Components announce themselves through the SDK-side static events rather than calling in
    /// directly, because the SDK assembly cannot reference this one. The subscription is installed
    /// once at <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>, before any scene
    /// loads, so no component can enable ahead of the listener.
    ///
    /// Structural changes (a component joining, leaving, or reshaping its source list) set
    /// <c>sDirty</c> and rebuild the whole table on the next <see cref="Schedule"/>. That is a
    /// deliberate simplification over the incremental append <c>BasisAuthoredMotionSystem</c> does:
    /// constraint counts are small and structural churn is rare, whereas the shared source buffer
    /// makes incremental compaction fiddly to get right. Scalar edits — weight, offsets, masks,
    /// rest pose, source weights — are re-read every frame and never trigger a rebuild, so
    /// animating a constraint's weight stays free.
    ///
    /// Reparenting a constrained transform at runtime does not invalidate anything on its own;
    /// call <see cref="BasisConstraintBase.SetDirty"/> after doing so, since the cached parent row
    /// is resolved at rebuild.
    /// </summary>
    public static class BasisConstraintSystem
    {
        private sealed class Registration
        {
            public BasisConstraintBase Component;
            public int SlotIndex;
            public int SourceStart;
            public int SourceCount;

            /// <summary>
            /// Flattened source i came from this index of the component's list. Sources with a null
            /// transform are dropped during flatten, so the two orderings are not interchangeable —
            /// the parent constraint's per-source offset arrays are indexed through this.
            /// </summary>
            public int[] SourceMap = Array.Empty<int>();

            /// <summary>
            /// The world-up reference this slot's WorldUpIndex was resolved against. Assigning or
            /// swapping worldUpObject at runtime is a structural change — the index is only
            /// resolvable while the intern table is being built — so it is diffed every frame.
            /// </summary>
            public Transform WorldUpObject;
        }

        private static readonly List<Registration> sRegistrations = new List<Registration>();
        private static readonly Dictionary<BasisConstraintBase, Registration> sLookup =
            new Dictionary<BasisConstraintBase, Registration>();

        /// <summary>Interning table for every transform the solve touches, mapped to its row index.</summary>
        private static readonly Dictionary<Transform, int> sTransformLookup = new Dictionary<Transform, int>();
        private static readonly List<Transform> sTrackedScratch = new List<Transform>();
        private static readonly List<Transform> sTargetScratch = new List<Transform>();
        /// <summary>Target transform to its row in the results/write arrays; deduplicates stacked constraints.</summary>
        private static readonly Dictionary<Transform, int> sTargetRowLookup = new Dictionary<Transform, int>();
        private static readonly List<int> sOrderScratch = new List<int>();
        private static readonly List<int> sDepthScratch = new List<int>();
        private static readonly List<int> sSourceMapScratch = new List<int>();
        /// <summary>Transform row → the slots that drive it, for the dependency sort.</summary>
        private static readonly Dictionary<int, List<int>> sProducers = new Dictionary<int, List<int>>();
        private static readonly List<List<int>> sConsumers = new List<List<int>>();
        private static readonly List<int> sInDegree = new List<int>();
        private static readonly List<bool> sEmitted = new List<bool>();
        private static readonly List<BasisConstraintSourceEntry> sSourceScratch =
            new List<BasisConstraintSourceEntry>();

        private static NativeList<BasisConstraintSlot> sSlots;
        private static NativeList<BasisConstraintSource> sSources;
        private static NativeList<BasisConstraintWorld> sWorld;
        private static NativeList<BasisConstraintTransform> sLocal;
        private static NativeList<BasisConstraintResult> sResults;
        private static NativeList<int> sOrder;
        private static NativeList<int> sTargetRow;
        private static TransformAccessArray sTracked;
        private static TransformAccessArray sTargets;

        private static JobHandle sPending;
        private static bool sInitialized;
        private static bool sDirty;
        private static bool sSubscribed;

        /// <summary>Number of constraints currently solved each frame.</summary>
        public static int SlotCount => sInitialized ? sSlots.Length : 0;

        /// <summary>Distinct transforms sampled each frame (targets, sources, up objects, parents).</summary>
        public static int TrackedTransformCount => sInitialized ? sTracked.length : 0;

        /// <summary>
        /// Installs the cross-assembly subscription and clears anything a disabled domain reload
        /// left behind. Mandatory: with reload disabled, statics survive exiting play mode, and a
        /// leftover <see cref="NativeList{T}"/> would both leak and be handed to the next session.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Dispose();

            sRegistrations.Clear();
            sLookup.Clear();
            sTransformLookup.Clear();
            sDirty = false;
            sPending = default;

            if (!sSubscribed)
            {
                BasisConstraintBase.ActiveStateChanged += OnActiveStateChanged;
                BasisConstraintBase.StructureChanged += OnStructureChanged;
                sSubscribed = true;
            }
        }

        public static void Initialize(int initialCapacity = 0)
        {
            if (sInitialized)
            {
                return;
            }
            sSlots = new NativeList<BasisConstraintSlot>(initialCapacity, Allocator.Persistent);
            sSources = new NativeList<BasisConstraintSource>(initialCapacity, Allocator.Persistent);
            sWorld = new NativeList<BasisConstraintWorld>(initialCapacity, Allocator.Persistent);
            sLocal = new NativeList<BasisConstraintTransform>(initialCapacity, Allocator.Persistent);
            sResults = new NativeList<BasisConstraintResult>(initialCapacity, Allocator.Persistent);
            sOrder = new NativeList<int>(initialCapacity, Allocator.Persistent);
            sTargetRow = new NativeList<int>(initialCapacity, Allocator.Persistent);
            sTracked = new TransformAccessArray(math.max(1, initialCapacity));
            sTargets = new TransformAccessArray(math.max(1, initialCapacity));
            sInitialized = true;
        }

        public static void Dispose()
        {
            if (!sInitialized)
            {
                return;
            }
            sPending.Complete();
            sPending = default;

            if (sSlots.IsCreated) sSlots.Dispose();
            if (sSources.IsCreated) sSources.Dispose();
            if (sWorld.IsCreated) sWorld.Dispose();
            if (sLocal.IsCreated) sLocal.Dispose();
            if (sResults.IsCreated) sResults.Dispose();
            if (sOrder.IsCreated) sOrder.Dispose();
            if (sTargetRow.IsCreated) sTargetRow.Dispose();
            if (sTracked.isCreated) sTracked.Dispose();
            if (sTargets.isCreated) sTargets.Dispose();

            // Drop the managed side too. Otherwise a component enabling after teardown re-enters
            // Register, re-Initializes fresh persistent containers that nothing will ever schedule
            // or dispose, and holds references to destroyed objects until the next domain reset.
            sRegistrations.Clear();
            sLookup.Clear();
            sTransformLookup.Clear();
            sTargetRowLookup.Clear();
            sTrackedScratch.Clear();
            sTargetScratch.Clear();
            sOrderScratch.Clear();
            sDepthScratch.Clear();
            sSourceScratch.Clear();
            sProducers.Clear();
            sConsumers.Clear();
            sDirty = false;

            sInitialized = false;
        }

        private static void OnActiveStateChanged(BasisConstraintBase component, bool active)
        {
            if (active)
            {
                Register(component);
            }
            else
            {
                Unregister(component);
            }
        }

        private static void OnStructureChanged(BasisConstraintBase component)
        {
            if (component != null && sLookup.ContainsKey(component))
            {
                sDirty = true;
            }
        }

        public static void Register(BasisConstraintBase component)
        {
            if (component == null)
            {
                return;
            }
            if (!sInitialized)
            {
                Initialize();
            }
            if (sLookup.ContainsKey(component))
            {
                return;
            }

            Registration registration = new Registration { Component = component };
            sRegistrations.Add(registration);
            sLookup[component] = registration;
            sDirty = true;
        }

        public static void Unregister(BasisConstraintBase component)
        {
            if (component == null || !sLookup.TryGetValue(component, out Registration registration))
            {
                return;
            }
            sLookup.Remove(component);
            sRegistrations.Remove(registration);
            sDirty = true;
        }

        /// <summary>
        /// Samples, solves and writes every constraint. Call once per frame after the pose the
        /// constraints should read is final — after IK and authored motion, before jiggle samples
        /// the bones — and pair it with <see cref="Complete"/> in the same frame: the returned
        /// handle owns the transform arrays until then, so touching a constrained transform on the
        /// main thread in between is a race.
        /// </summary>
        public static JobHandle Schedule()
        {
            if (!sInitialized)
            {
                return default;
            }
            // Never mutate the containers under a live job: a frame that early-returns below would
            // otherwise leave sPending in flight while the next Rebuild clears everything.
            CompletePending();

            // A pending rebuild is honoured even with no registrations left, so unregistering the
            // last constraint actually clears the tables instead of stranding stale slots.
            // A component destroyed without an OnDisable (scene teardown, DestroyImmediate) has to
            // be caught here too: its transform would otherwise still be in the write array.
            if (sDirty || HasDestroyedRegistration() || HasDesyncedTransforms())
            {
                Rebuild();
            }
            if (sSlots.Length == 0)
            {
                return default;
            }

            RefreshDynamicState();

            JobHandle read = new BasisConstraintReadJob
            {
                World = sWorld.AsArray(),
                Local = sLocal.AsArray(),
            }.Schedule(sTracked);

            JobHandle solve = new BasisConstraintSolveJob
            {
                Slots = sSlots.AsArray(),
                Sources = sSources.AsArray(),
                Local = sLocal.AsArray(),
                Order = sOrder.AsArray(),
                TargetRow = sTargetRow.AsArray(),
                World = sWorld.AsArray(),
                Results = sResults.AsArray(),
            }.Schedule(read);

            sPending = new BasisConstraintWriteJob
            {
                Results = sResults.AsArray(),
            }.Schedule(sTargets, solve);

            return sPending;
        }

        public static void Complete(JobHandle handle)
        {
            handle.Complete();
            // Only retire the tracked handle if this is it; a caller completing some unrelated
            // handle must not convince the system that its own solve has landed.
            if (handle.Equals(sPending))
            {
                sPending = default;
            }
        }

        /// <summary>
        /// Cheap per-frame sweep for components destroyed behind the system's back. The Unity
        /// fake-null check is the whole cost, over a list that is tens of entries in practice.
        /// </summary>
        private static bool HasDestroyedRegistration()
        {
            for (int Index = 0; Index < sRegistrations.Count; Index++)
            {
                if (sRegistrations[Index].Component == null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// A TransformAccessArray silently drops destroyed transforms and compacts, which desyncs it
        /// from the parallel native arrays — the read job would then sample shifted rows and the
        /// write job would push solved poses onto the wrong objects, with no exception to show for
        /// it. Length disagreement is the tell; rebuilding resyncs.
        /// </summary>
        private static bool HasDesyncedTransforms()
        {
            return sTracked.length != sWorld.Length || sTargets.length != sResults.Length;
        }

        /// <summary>Completes any in-flight solve. Safe to call when nothing is scheduled.</summary>
        public static void CompletePending()
        {
            sPending.Complete();
            sPending = default;
        }

        /// <summary>
        /// Rebuilds the flattened tables from the live component set. Prunes destroyed components,
        /// interns every transform once, and topologically orders the slots so whatever drives a
        /// constraint resolves before it.
        /// </summary>
        private static void Rebuild()
        {
            CompletePending();

            sSlots.Clear();
            sSources.Clear();
            sWorld.Clear();
            sLocal.Clear();
            sOrder.Clear();
            sTargetRow.Clear();
            sTransformLookup.Clear();
            sTrackedScratch.Clear();
            sTargetScratch.Clear();
            sTargetRowLookup.Clear();
            sOrderScratch.Clear();
            sDepthScratch.Clear();

            for (int Index = sRegistrations.Count - 1; Index >= 0; Index--)
            {
                // Destroyed without an OnDisable (scene teardown) leaves a fake-null behind.
                if (sRegistrations[Index].Component == null)
                {
                    sRegistrations.RemoveAt(Index);
                }
            }

            for (int Index = 0; Index < sRegistrations.Count; Index++)
            {
                Registration registration = sRegistrations[Index];
                BasisConstraintBase component = registration.Component;
                Transform target = component.transform;

                int targetIndex = InternTransform(target);
                int parentIndex = target.parent != null ? InternTransform(target.parent) : -1;

                // ParentIndex rides in the local-pose row; the sample job preserves it.
                BasisConstraintTransform local = sLocal[targetIndex];
                local.ParentIndex = parentIndex;
                sLocal[targetIndex] = local;

                registration.SlotIndex = sSlots.Length;
                registration.SourceStart = sSources.Length;

                component.GetSources(sSourceScratch);
                sSourceMapScratch.Clear();
                for (int SourceIndex = 0; SourceIndex < sSourceScratch.Count; SourceIndex++)
                {
                    BasisConstraintSourceEntry entry = sSourceScratch[SourceIndex];
                    if (entry.sourceTransform == null)
                    {
                        continue;
                    }
                    sSources.Add(new BasisConstraintSource
                    {
                        TransformIndex = InternTransform(entry.sourceTransform),
                        Weight = math.max(0f, entry.weight),
                        PositionOffset = float3.zero,
                        RotationOffset = quaternion.identity,
                    });
                    sSourceMapScratch.Add(SourceIndex);
                }
                registration.SourceCount = sSourceMapScratch.Count;
                registration.SourceMap = sSourceMapScratch.ToArray();

                BasisConstraintSlot slot = BasisConstraintDefaults.Identity(ToKind(component.constraintType));
                slot.TargetIndex = targetIndex;
                slot.SourceStart = registration.SourceStart;
                slot.SourceCount = registration.SourceCount;
                slot.Depth = ToDepth(target);
                registration.WorldUpObject = GetWorldUpObject(component);
                slot.WorldUpIndex = registration.WorldUpObject != null
                    ? InternTransform(registration.WorldUpObject)
                    : -1;
                FillScalarState(component, ref slot);
                sSlots.Add(slot);
                FillSourceState(component, registration);

                // Several constraints may share one target (position + rotation is routine); they
                // all write through a single row so the parallel write job never doubles up.
                if (!sTargetRowLookup.TryGetValue(target, out int targetRow))
                {
                    targetRow = sTargetScratch.Count;
                    sTargetRowLookup[target] = targetRow;
                    sTargetScratch.Add(target);
                }
                sTargetRow.Add(targetRow);

                sOrderScratch.Add(registration.SlotIndex);
                sDepthScratch.Add(slot.Depth);
            }

            BuildSolveOrder();
            for (int Index = 0; Index < sOrderScratch.Count; Index++)
            {
                sOrder.Add(sOrderScratch[Index]);
            }

            sResults.Resize(sTargetScratch.Count, NativeArrayOptions.ClearMemory);
            sTracked.SetTransforms(sTrackedScratch.ToArray());
            sTargets.SetTransforms(sTargetScratch.ToArray());

            sDirty = false;
        }

        /// <summary>
        /// Orders the solve so a constraint always runs after whatever drives it. Hierarchy depth
        /// alone is not enough: a root-level prop constrained to a deep, itself-constrained hand
        /// bone is shallower than its own dependency, and would read a stale pose forever. So this
        /// is a real topological sort over source→target edges, with depth only as the tie-break
        /// among slots that are equally ready (which keeps sibling order stable and natural).
        ///
        /// A cycle has no valid order; rather than drop those constraints, it is broken at the
        /// shallowest member, which costs that one slot a frame of lag.
        /// </summary>
        private static void BuildSolveOrder()
        {
            int count = sSlots.Length;

            // Which slots drive each transform row. Several may drive one row (stacked constraints).
            sProducers.Clear();
            for (int Index = 0; Index < count; Index++)
            {
                int targetIndex = sSlots[Index].TargetIndex;
                if (targetIndex < 0)
                {
                    continue;
                }
                if (!sProducers.TryGetValue(targetIndex, out List<int> producers))
                {
                    producers = new List<int>();
                    sProducers[targetIndex] = producers;
                }
                producers.Add(Index);
            }

            sConsumers.Clear();
            sInDegree.Clear();
            sEmitted.Clear();
            for (int Index = 0; Index < count; Index++)
            {
                sConsumers.Add(new List<int>());
                sInDegree.Add(0);
                sEmitted.Add(false);
            }

            for (int Index = 0; Index < count; Index++)
            {
                BasisConstraintSlot slot = sSlots[Index];
                for (int SourceIndex = 0; SourceIndex < slot.SourceCount; SourceIndex++)
                {
                    int transformIndex = sSources[slot.SourceStart + SourceIndex].TransformIndex;
                    if (transformIndex < 0 || !sProducers.TryGetValue(transformIndex, out List<int> producers))
                    {
                        continue;
                    }
                    for (int ProducerIndex = 0; ProducerIndex < producers.Count; ProducerIndex++)
                    {
                        int producer = producers[ProducerIndex];
                        if (producer == Index)
                        {
                            // A constraint sourcing its own target imposes no ordering on itself.
                            continue;
                        }
                        sConsumers[producer].Add(Index);
                        sInDegree[Index]++;
                    }
                }
            }

            sOrderScratch.Clear();
            for (int Step = 0; Step < count; Step++)
            {
                int chosen = PickShallowest(count, readyOnly: true);
                if (chosen < 0)
                {
                    chosen = PickShallowest(count, readyOnly: false);
                }
                if (chosen < 0)
                {
                    break;
                }

                sEmitted[chosen] = true;
                sOrderScratch.Add(chosen);

                List<int> consumers = sConsumers[chosen];
                for (int ConsumerIndex = 0; ConsumerIndex < consumers.Count; ConsumerIndex++)
                {
                    sInDegree[consumers[ConsumerIndex]]--;
                }
            }
        }

        /// <summary>
        /// Shallowest un-emitted slot, optionally restricted to those with no unmet dependency.
        /// In-degree is compared with &gt; 0 rather than != 0 so a cycle broken above, which can push
        /// a counter negative, still leaves its dependents selectable.
        /// </summary>
        private static int PickShallowest(int count, bool readyOnly)
        {
            int chosen = -1;
            for (int Index = 0; Index < count; Index++)
            {
                if (sEmitted[Index] || (readyOnly && sInDegree[Index] > 0))
                {
                    continue;
                }
                if (chosen < 0 || sDepthScratch[Index] < sDepthScratch[chosen])
                {
                    chosen = Index;
                }
            }
            return chosen;
        }

        /// <summary>
        /// Interns a transform, growing the per-transform world/local rows to match. Returns the
        /// row index every slot and source refers to.
        /// </summary>
        private static int InternTransform(Transform transform)
        {
            if (sTransformLookup.TryGetValue(transform, out int existing))
            {
                return existing;
            }
            int index = sTrackedScratch.Count;
            sTransformLookup[transform] = index;
            sTrackedScratch.Add(transform);

            sWorld.Resize(index + 1, NativeArrayOptions.ClearMemory);
            sLocal.Resize(index + 1, NativeArrayOptions.ClearMemory);
            sLocal[index] = new BasisConstraintTransform
            {
                LocalPosition = float3.zero,
                LocalRotation = quaternion.identity,
                LocalScale = new float3(1f, 1f, 1f),
                ParentIndex = -1,
            };
            return index;
        }

        private static Transform GetWorldUpObject(BasisConstraintBase component)
        {
            return component switch
            {
                BasisAimConstraint aim => aim.worldUpObject,
                BasisLookAtConstraint lookAt => lookAt.worldUpObject,
                _ => null,
            };
        }

        /// <summary>
        /// Re-reads the cheap per-frame state — weights, offsets, masks, rest poses, source weights
        /// — straight off the components, so inspector and script edits take effect the same frame
        /// without a rebuild.
        /// </summary>
        private static void RefreshDynamicState()
        {
            for (int Index = 0; Index < sRegistrations.Count; Index++)
            {
                Registration registration = sRegistrations[Index];
                BasisConstraintBase component = registration.Component;
                if (component == null)
                {
                    sDirty = true;
                    continue;
                }

                // Swapping or assigning worldUpObject only takes effect through a rebuild, and
                // nothing on the components marks it dirty, so diff it here.
                if (GetWorldUpObject(component) != registration.WorldUpObject)
                {
                    sDirty = true;
                }

                BasisConstraintSlot slot = sSlots[registration.SlotIndex];
                FillScalarState(component, ref slot);
                sSlots[registration.SlotIndex] = slot;
                FillSourceState(component, registration);
            }
        }

        /// <summary>
        /// Copies every non-structural field off the component into its slot. Shared by the rebuild
        /// and the per-frame refresh so the two can never drift apart.
        /// </summary>
        private static void FillScalarState(BasisConstraintBase component, ref BasisConstraintSlot slot)
        {
            slot.Weight = Mathf.Clamp01(component.weight);
            slot.Active = (byte)(component.constraintActive ? 1 : 0);
            slot.Locked = (byte)(component.locked ? 1 : 0);

            switch (component)
            {
                case BasisPositionConstraint position:
                    slot.TranslationAtRest = position.translationAtRest;
                    slot.TranslationOffset = position.translationOffset;
                    slot.TranslationMask = (byte)position.translationAxis;
                    break;

                case BasisRotationConstraint rotation:
                    slot.RotationAtRest = ToQuaternion(rotation.rotationAtRest);
                    slot.RotationOffset = ToQuaternion(rotation.rotationOffset);
                    slot.RotationMask = (byte)rotation.rotationAxis;
                    break;

                case BasisScaleConstraint scale:
                    slot.ScaleAtRest = scale.scaleAtRest;
                    slot.ScaleOffset = scale.scaleOffset;
                    slot.ScaleMask = (byte)scale.scalingAxis;
                    break;

                case BasisParentConstraint parent:
                    slot.TranslationAtRest = parent.translationAtRest;
                    slot.RotationAtRest = ToQuaternion(parent.rotationAtRest);
                    slot.TranslationOffset = float3.zero;
                    slot.RotationOffset = quaternion.identity;
                    slot.TranslationMask = (byte)parent.translationAxis;
                    slot.RotationMask = (byte)parent.rotationAxis;
                    break;

                case BasisAimConstraint aim:
                    slot.RotationAtRest = ToQuaternion(aim.rotationAtRest);
                    slot.RotationOffset = ToQuaternion(aim.rotationOffset);
                    slot.RotationMask = (byte)aim.rotationAxis;
                    slot.AimVector = aim.aimVector;
                    slot.UpVector = aim.upVector;
                    slot.WorldUpVector = aim.worldUpVector;
                    slot.WorldUpKind = (BasisWorldUpKind)aim.worldUpType;
                    slot.UseUpObject = (byte)(aim.worldUpObject != null ? 1 : 0);
                    slot.Roll = 0f;
                    break;

                case BasisLookAtConstraint lookAt:
                    slot.RotationAtRest = ToQuaternion(lookAt.rotationAtRest);
                    slot.RotationOffset = ToQuaternion(lookAt.rotationOffset);
                    slot.RotationMask = (byte)BasisConstraintAxis.All;
                    // Look-at is an aim constraint pinned to Unity's convention: +Z at the target,
                    // +Y rolled up, with roll layered on top of the resolved basis.
                    slot.AimVector = new float3(0f, 0f, 1f);
                    slot.UpVector = new float3(0f, 1f, 0f);
                    slot.WorldUpVector = new float3(0f, 1f, 0f);
                    slot.WorldUpKind = lookAt.useUpObject && lookAt.worldUpObject != null
                        ? BasisWorldUpKind.ObjectUp
                        : BasisWorldUpKind.SceneUp;
                    slot.UseUpObject = (byte)(lookAt.useUpObject && lookAt.worldUpObject != null ? 1 : 0);
                    slot.Roll = lookAt.roll;
                    break;
            }
        }

        /// <summary>
        /// Refreshes the flattened source rows: weights for every kind, plus the per-source pose
        /// offsets only the parent constraint carries.
        /// </summary>
        private static void FillSourceState(BasisConstraintBase component, Registration registration)
        {
            if (registration.SourceCount == 0)
            {
                return;
            }

            BasisParentConstraint parent = component as BasisParentConstraint;
            for (int Index = 0; Index < registration.SourceCount; Index++)
            {
                int flattened = registration.SourceStart + Index;
                int authored = registration.SourceMap[Index];

                BasisConstraintSource source = sSources[flattened];
                source.Weight = math.max(0f, component.GetSource(authored).weight);

                if (parent != null)
                {
                    source.PositionOffset = authored < parent.translationOffsets.Length
                        ? parent.translationOffsets[authored]
                        : Vector3.zero;
                    source.RotationOffset = authored < parent.rotationOffsets.Length
                        ? ToQuaternion(parent.rotationOffsets[authored])
                        : quaternion.identity;
                }

                sSources[flattened] = source;
            }
        }

        /// <summary>
        /// The SDK and framework kind enums are declared in lockstep; the cast is the mapping.
        /// </summary>
        private static BasisConstraintKind ToKind(BasisConstraintType type) => (BasisConstraintKind)type;

        private static int ToDepth(Transform transform)
        {
            int depth = 0;
            Transform cursor = transform.parent;
            while (cursor != null)
            {
                depth++;
                cursor = cursor.parent;
            }
            return depth;
        }

        private static quaternion ToQuaternion(Vector3 eulerDegrees) => Quaternion.Euler(eulerDegrees);
    }
}
