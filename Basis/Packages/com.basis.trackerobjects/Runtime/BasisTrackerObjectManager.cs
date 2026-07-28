using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using UnityEngine;

namespace Basis.TrackerObjects
{
    public static class BasisTrackerObjectManager
    {
        public const int RenderPriority = 99;

        // Reach for props without a pickup surface (no colliders to measure against);
        // matches the BasisInteractableObject.InteractRange default.
        public const float FallbackBindRange = 1f;

        // Read-only outside: adding or removing entries directly would skip the
        // bind/unbind lifecycle (deny predicates, kinematic restore, DistanceReduction,
        // the events below). Go through TryCreateBindingAsync / TryRemoveBinding.
        private static readonly List<BasisTrackerBinding> _bindings = new List<BasisTrackerBinding>();
        public static IReadOnlyList<BasisTrackerBinding> Bindings => _bindings;

        public static event Action<BasisTrackerBinding> OnBindingCreated;
        public static event Action<BasisTrackerBinding> OnBindingRemoved;

        private static int _nextID = 1;
        private static bool _subscribed;

        // Single shared deny predicates — each binding lives on a distinct
        // BasisPickupInteractable (enforced by the LoadedNetID dedup), so the same
        // delegate instance is added once per pickup list and removed once on unbind.
        private static readonly Func<BasisInput, bool> _denyHover = static _ => false;
        private static readonly Func<BasisInput, bool> _denyInteract = static _ => false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            // Statics survive Play sessions when domain reload is disabled, and scene
            // reload can be disabled independently, so a carried-over binding's objects
            // may or may not still exist. Run the normal unbind path for each: its
            // Unity-null checks no-op on destroyed objects, and any survivor gets its
            // pickup predicates, kinematic state and send rate restored. Snapshot and
            // remove by ID so a subscriber mutating the list reentrantly can't get a
            // freshly created binding swept up by a stale index.
            if (_bindings.Count > 0)
            {
                BasisTrackerBinding[] carriedOver = _bindings.ToArray();
                for (int index = 0; index < carriedOver.Length; index++)
                {
                    TryRemoveBinding(carriedOver[index].Id);
                }
            }
            if (_subscribed)
            {
                return;
            }
            BasisLocalPlayer.AfterSimulateOnRender.AddAction(RenderPriority, OnAfterSimulateOnRender);
            BasisRuntimeSpawnRegistry.OnRegistryChanged += OnRegistryChanged;
            _subscribed = true;
            BasisDebug.Log("BasisTrackerObjectManager subscribed", BasisDebug.LogTag.TrackerObjects);
        }

        public static async Task<BasisTrackerBinding> TryCreateBindingAsync(BasisInput tracker, Transform target, string loadedNetID)
        {
            if (tracker == null || target == null)
            {
                BasisDebug.LogError("TryCreateBindingAsync: tracker or target was null", BasisDebug.LogTag.TrackerObjects);
                return null;
            }
            if (string.IsNullOrEmpty(loadedNetID))
            {
                BasisDebug.LogError("TryCreateBindingAsync: loadedNetID was null/empty", BasisDebug.LogTag.TrackerObjects);
                return null;
            }
            if (TryGetBindingByLoadedNetID(loadedNetID, out _))
            {
                BasisDebug.LogWarning($"TryCreateBindingAsync: a binding for LoadedNetID {loadedNetID} already exists", BasisDebug.LogTag.TrackerObjects);
                return null;
            }
            if (IsTrackerBound(tracker))
            {
                BasisDebug.LogWarning($"TryCreateBindingAsync: tracker {tracker.UniqueDeviceIdentifier} is already driving another binding", BasisDebug.LogTag.TrackerObjects);
                return null;
            }

            BasisPickupInteractable pickup = ResolvePickup(target);
            if (!IsWithinBindRange(tracker.transform.position, target, pickup))
            {
                BasisDebug.LogWarning($"TryCreateBindingAsync: tracker {tracker.UniqueDeviceIdentifier} is out of interaction range of {target.name}, refusing to bind", BasisDebug.LogTag.TrackerObjects);
                return null;
            }

            target.TryGetComponent(out BasisPickupSyncNetworking sync);
            if (sync == null)
            {
                BasisDebug.LogWarning($"TryCreateBindingAsync: target {target.name} has no BasisPickupSyncNetworking — local-only motion, remote players will not see the binding move", BasisDebug.LogTag.TrackerObjects);
            }
            else
            {
                if (sync.IsStatic)
                {
                    BasisDebug.LogWarning($"TryCreateBindingAsync: {target.name} is static/locked, refusing to bind", BasisDebug.LogTag.TrackerObjects);
                    return null;
                }
                if (!sync.CanNetworkSteal && !sync.IsOwnedLocallyOnClient && BasisNetworkConnection.LocalPlayerIsConnected)
                {
                    // Binding claims ownership, which is a steal when someone else owns
                    // the prop — respect the pickup's no-steal rule.
                    BasisDebug.LogWarning($"TryCreateBindingAsync: {target.name} is owned elsewhere and does not allow network steal", BasisDebug.LogTag.TrackerObjects);
                    return null;
                }
                if (BasisNetworkConnection.LocalPlayerIsConnected)
                {
                    // Only the owner's transform writes go on the wire, so claim ownership
                    // up front instead of driving a prop nobody streams. The name is
                    // captured because the ownership round-trip yields and the logs below
                    // must not dereference a since-destroyed target.
                    string targetName = target.name;
                    BasisOwnershipResult result;
                    try
                    {
                        result = await sync.TakeOwnershipAsync();
                    }
                    catch (Exception e)
                    {
                        // Keep the method's failure contract: every failed bind returns
                        // null instead of faulting the task up into a UI click handler.
                        BasisDebug.LogError($"TryCreateBindingAsync: TakeOwnershipAsync threw for {targetName}: {e}", BasisDebug.LogTag.TrackerObjects);
                        return null;
                    }
                    if (tracker == null || target == null || sync == null)
                    {
                        BasisDebug.LogWarning($"TryCreateBindingAsync: binding objects for {targetName} were destroyed while taking ownership", BasisDebug.LogTag.TrackerObjects);
                        return null;
                    }
                    if (!result.Success)
                    {
                        BasisDebug.LogWarning($"TryCreateBindingAsync: could not take ownership of {targetName}", BasisDebug.LogTag.TrackerObjects);
                        return null;
                    }
                    if (TryGetBindingByLoadedNetID(loadedNetID, out _) || IsTrackerBound(tracker))
                    {
                        // a second bind raced the ownership round-trip
                        return null;
                    }
                    if (!IsWithinBindRange(tracker.transform.position, target, pickup))
                    {
                        BasisDebug.LogWarning($"TryCreateBindingAsync: tracker {tracker.UniqueDeviceIdentifier} moved out of range of {targetName} during the ownership round-trip, refusing to bind", BasisDebug.LogTag.TrackerObjects);
                        return null;
                    }
                }
            }

            tracker.transform.GetPositionAndRotation(out Vector3 trackerPos, out Quaternion trackerRot);
            target.GetPositionAndRotation(out Vector3 targetPos, out Quaternion targetRot);
            Quaternion invRot = Quaternion.Inverse(trackerRot);

            BasisTrackerBinding binding = new BasisTrackerBinding
            {
                Id = _nextID++,
                Tracker = tracker,
                Target = target,
                UniqueDeviceIdentifier = tracker.UniqueDeviceIdentifier,
                LoadedNetID = loadedNetID,
                LocalPositionOffset = invRot * (targetPos - trackerPos),
                LocalRotationOffset = invRot * targetRot,
                SyncRef = sync,
            };

            if (pickup != null)
            {
                binding.PickupRef = pickup;
                // The deny predicates only block future grabs; a hold that's already in
                // progress keeps driving the transform, so end it before the tracker
                // takes over.
                pickup.Drop();
                pickup.CanHoverInjected.Add(_denyHover);
                pickup.CanInteractInjected.Add(_denyInteract);
            }

            // Pickup-less props can still carry a Rigidbody; freeze it too or physics
            // fights the render-time pose writes.
            Rigidbody rigid = pickup != null ? pickup.RigidRef : null;
            if (rigid == null)
            {
                target.TryGetComponent(out rigid);
            }
            if (rigid != null)
            {
                binding.RigidRef = rigid;
                binding.PreBindKinematic = rigid.isKinematic;
                binding.HasKinematicCaptured = true;
                rigid.isKinematic = true;
            }

            if (sync != null)
            {
                // A tracker-driven prop is being actively manipulated and watched like a
                // held one, so keep its send rate full instead of letting viewer distance
                // throttle it (same rationale as FullRateWhileHeld on the pickup sync).
                binding.PreBindDistanceReduction = sync.DistanceReduction;
                sync.DistanceReduction = false;
            }

            _bindings.Add(binding);
            BasisDebug.Log($"Created tracker binding {binding.Id} for {tracker.UniqueDeviceIdentifier} -> {target.name} (netID {loadedNetID})", BasisDebug.LogTag.TrackerObjects);
            InvokeSafely(OnBindingCreated, binding);
            return binding;
        }

        // Bind/unbind frequency only, never per-frame: RemoveAt runs inside loops
        // (render tick, ClearedAll), so a throwing subscriber must not abort the
        // remaining bindings' cleanup — and GetInvocationList's allocation is fine
        // at this rate.
        private static void InvokeSafely(Action<BasisTrackerBinding> handlers, BasisTrackerBinding binding)
        {
            if (handlers == null)
            {
                return;
            }
            foreach (Delegate subscriber in handlers.GetInvocationList())
            {
                Action<BasisTrackerBinding> handler = (Action<BasisTrackerBinding>)subscriber;
                try
                {
                    handler(binding);
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"Tracker binding subscriber {handler.Method.DeclaringType?.FullName}.{handler.Method.Name} threw: {e}", BasisDebug.LogTag.TrackerObjects);
                }
            }
        }

        /// <summary>
        /// The prop's pickup surface: the sync component's cached reference when there is
        /// one (it discovers pickups on children too), else a pickup on the root.
        /// </summary>
        public static BasisPickupInteractable ResolvePickup(Transform target)
        {
            if (target == null)
            {
                return null;
            }
            if (target.TryGetComponent(out BasisPickupSyncNetworking sync) && sync.BasisPickupInteractable != null)
            {
                return sync.BasisPickupInteractable;
            }
            target.TryGetComponent(out BasisPickupInteractable pickup);
            return pickup;
        }

        /// <summary>
        /// Same reach rule as grabbing: within the pickup's interact range of its
        /// colliders, or within <see cref="FallbackBindRange"/> of the transform when
        /// the prop has no pickup surface. Gates both the bind itself and which
        /// trackers a picker should offer.
        /// </summary>
        public static bool IsWithinBindRange(Vector3 sourcePosition, Transform target, BasisPickupInteractable pickup)
        {
            if (pickup != null)
            {
                return pickup.IsWithinRange(sourcePosition, pickup.InteractRange);
            }
            return target != null && Vector3.Distance(target.position, sourcePosition) <= FallbackBindRange;
        }

        public static bool TryRemoveBinding(int id)
        {
            int count = _bindings.Count;
            for (int index = 0; index < count; index++)
            {
                if (_bindings[index].Id == id)
                {
                    RemoveAt(index);
                    return true;
                }
            }
            return false;
        }

        /// <summary>One tracker drives at most one prop; true if this one is taken.</summary>
        public static bool IsTrackerBound(BasisInput tracker)
        {
            if (tracker == null)
            {
                return false;
            }
            int count = _bindings.Count;
            for (int index = 0; index < count; index++)
            {
                if (_bindings[index].Tracker == tracker)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool TryGetBindingByLoadedNetID(string loadedNetID, out BasisTrackerBinding binding)
        {
            binding = null;
            if (string.IsNullOrEmpty(loadedNetID))
            {
                return false;
            }
            int count = _bindings.Count;
            for (int index = 0; index < count; index++)
            {
                BasisTrackerBinding b = _bindings[index];
                if (b.LoadedNetID == loadedNetID)
                {
                    binding = b;
                    return true;
                }
            }
            return false;
        }

        private static void RemoveAt(int index)
        {
            BasisTrackerBinding binding = _bindings[index];
            if (binding.PickupRef != null)
            {
                binding.PickupRef.CanHoverInjected.Remove(_denyHover);
                binding.PickupRef.CanInteractInjected.Remove(_denyInteract);
            }
            // Restore the captured kinematic state first; for a rigidbody the sync
            // actually manages, ControlState below overrides it with the state derived
            // from current ownership/static. The order matters because ControlState
            // no-ops when the sync has no pickup rigidbody (SetIsKinematicOnPickup),
            // and the capture is then the only restore path.
            if (binding.HasKinematicCaptured && binding.RigidRef != null)
            {
                binding.RigidRef.isKinematic = binding.PreBindKinematic;
            }
            if (binding.SyncRef != null)
            {
                binding.SyncRef.DistanceReduction = binding.PreBindDistanceReduction;
                binding.SyncRef.ControlState();
            }
            _bindings.RemoveAt(index);
            BasisDebug.Log($"Removed tracker binding {binding.Id}", BasisDebug.LogTag.TrackerObjects);
            InvokeSafely(OnBindingRemoved, binding);
        }

        private static void OnAfterSimulateOnRender()
        {
            // AfterSimulateOnRender runs the framework's post-sim subscribers as one
            // sequential chain; contain any failure here so the rest still tick.
            try
            {
                for (int index = _bindings.Count - 1; index >= 0; index--)
                {
                    if (index >= _bindings.Count)
                    {
                        // An OnBindingRemoved subscriber removed entries during this pass;
                        // fall through until the index is valid again.
                        continue;
                    }
                    BasisTrackerBinding binding = _bindings[index];
                    if (binding.Tracker == null || binding.Target == null)
                    {
                        // Tracker disconnected or prop destroyed out from under us — release
                        // the binding so the deny predicates lift and the netID can rebind.
                        RemoveAt(index);
                        continue;
                    }
                    if (binding.SyncRef != null && !binding.SyncRef.IsOwnedLocallyOnClient)
                    {
                        // Another client took ownership (steal is evaluated on the stealing
                        // client, so it can't be locked out from here). Their stream drives
                        // the prop now; dissolve the binding rather than fight it.
                        RemoveAt(index);
                        continue;
                    }
                    // BasisPickupSyncNetworking.ControlState flips isKinematic = false on
                    // locally-owned props and re-fires on ownership events long after bind.
                    // Re-asserting kinematic each frame is cheap and keeps physics from
                    // advancing the prop between our writes.
                    if (binding.HasKinematicCaptured && binding.RigidRef != null)
                    {
                        binding.RigidRef.isKinematic = true;
                    }
                    binding.Tracker.transform.GetPositionAndRotation(out Vector3 trackerPos, out Quaternion trackerRot);
                    binding.Target.SetPositionAndRotation(
                        trackerPos + trackerRot * binding.LocalPositionOffset,
                        trackerRot * binding.LocalRotationOffset);
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogErrorOnce($"Tracker binding drive failed: {e}", BasisDebug.LogTag.TrackerObjects);
            }
        }

        private static void OnRegistryChanged(BasisRuntimeSpawnRegistry.RegistryChangeType type, BasisRuntimeSpawnRegistry.SpawnInstance instance)
        {
            switch (type)
            {
                case BasisRuntimeSpawnRegistry.RegistryChangeType.Removed:
                case BasisRuntimeSpawnRegistry.RegistryChangeType.ClearedUrl:
                    if (instance != null && TryGetBindingByLoadedNetID(instance.LoadedNetID, out BasisTrackerBinding binding))
                    {
                        TryRemoveBinding(binding.Id);
                    }
                    break;
                case BasisRuntimeSpawnRegistry.RegistryChangeType.Modified:
                    // The server-authoritative static lock freezes the prop for everyone;
                    // release the tracker when it lands on a bound prop.
                    if (instance != null && instance.Static && TryGetBindingByLoadedNetID(instance.LoadedNetID, out BasisTrackerBinding lockedBinding))
                    {
                        TryRemoveBinding(lockedBinding.Id);
                    }
                    break;
                case BasisRuntimeSpawnRegistry.RegistryChangeType.ClearedAll:
                {
                    // Snapshot and remove by ID: OnBindingRemoved subscribers can mutate
                    // the list reentrantly, and index-based removal could sweep up a
                    // binding created mid-clear. Rare event, so the allocation is fine.
                    BasisTrackerBinding[] cleared = _bindings.ToArray();
                    for (int index = 0; index < cleared.Length; index++)
                    {
                        TryRemoveBinding(cleared[index].Id);
                    }
                    break;
                }
            }
        }
    }
}
