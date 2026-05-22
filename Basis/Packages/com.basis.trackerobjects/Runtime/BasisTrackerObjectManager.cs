using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;

namespace Basis.TrackerObjects
{
    public static class BasisTrackerObjectManager
    {
        public const int RenderPriority = 99;

        public static readonly List<BasisTrackerBinding> Bindings = new List<BasisTrackerBinding>();

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
            if (_subscribed)
            {
                return;
            }
            BasisLocalPlayer.AfterSimulateOnRender.AddAction(RenderPriority, OnAfterSimulateOnRender);
            BasisRuntimeSpawnRegistry.OnRegistryChanged += OnRegistryChanged;
            _subscribed = true;
            BasisDebug.Log("BasisTrackerObjectManager subscribed", BasisDebug.LogTag.TrackerObjects);
        }

        public static bool TryCreateBinding(BasisInput tracker, Transform target, string loadedNetID, out int id)
        {
            id = 0;
            if (tracker == null || target == null)
            {
                BasisDebug.LogError("TryCreateBinding: tracker or target was null", BasisDebug.LogTag.TrackerObjects);
                return false;
            }
            if (string.IsNullOrEmpty(loadedNetID))
            {
                BasisDebug.LogError("TryCreateBinding: loadedNetID was null/empty", BasisDebug.LogTag.TrackerObjects);
                return false;
            }
            if (TryGetBindingByLoadedNetID(loadedNetID, out _))
            {
                BasisDebug.LogWarning($"TryCreateBinding: a binding for LoadedNetID {loadedNetID} already exists", BasisDebug.LogTag.TrackerObjects);
                return false;
            }

            tracker.transform.GetPositionAndRotation(out Vector3 trackerPos, out Quaternion trackerRot);
            target.GetPositionAndRotation(out Vector3 targetPos, out Quaternion targetRot);
            Quaternion invRot = Quaternion.Inverse(trackerRot);

            id = _nextID++;
            BasisTrackerBinding binding = new BasisTrackerBinding
            {
                Id = id,
                Tracker = tracker,
                Target = target,
                UniqueDeviceIdentifier = tracker.UniqueDeviceIdentifier,
                LoadedNetID = loadedNetID,
                LocalPositionOffset = invRot * (targetPos - trackerPos),
                LocalRotationOffset = invRot * targetRot,
            };

            if (target.TryGetComponent(out BasisPickupInteractable pickup))
            {
                binding.PickupRef = pickup;
                pickup.CanHoverInjected.Add(_denyHover);
                pickup.CanInteractInjected.Add(_denyInteract);

                if (pickup.RigidRef != null)
                {
                    binding.RigidRef = pickup.RigidRef;
                    binding.PreBindKinematic = pickup.RigidRef.isKinematic;
                    binding.HasKinematicCaptured = true;
                    pickup.RigidRef.isKinematic = true;
                }
            }

            if (!target.TryGetComponent<BasisNetworkContentBase>(out _))
            {
                BasisDebug.LogWarning($"TryCreateBinding: target {target.name} has no BasisNetworkContentBase — local-only motion, remote players will not see the binding move", BasisDebug.LogTag.TrackerObjects);
            }

            Bindings.Add(binding);
            BasisDebug.Log($"Created tracker binding {id} for {tracker.UniqueDeviceIdentifier} -> {target.name} (netID {loadedNetID})", BasisDebug.LogTag.TrackerObjects);
            OnBindingCreated?.Invoke(binding);
            return true;
        }

        public static bool TryRemoveBinding(int id)
        {
            int count = Bindings.Count;
            for (int index = 0; index < count; index++)
            {
                if (Bindings[index].Id == id)
                {
                    RemoveAt(index);
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
            int count = Bindings.Count;
            for (int index = 0; index < count; index++)
            {
                BasisTrackerBinding b = Bindings[index];
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
            BasisTrackerBinding binding = Bindings[index];
            if (binding.PickupRef != null)
            {
                binding.PickupRef.CanHoverInjected.Remove(_denyHover);
                binding.PickupRef.CanInteractInjected.Remove(_denyInteract);
            }
            if (binding.HasKinematicCaptured && binding.RigidRef != null)
            {
                binding.RigidRef.isKinematic = binding.PreBindKinematic;
            }
            Bindings.RemoveAt(index);
            BasisDebug.Log($"Removed tracker binding {binding.Id}", BasisDebug.LogTag.TrackerObjects);
            OnBindingRemoved?.Invoke(binding);
        }

        private static void OnAfterSimulateOnRender()
        {
            int count = Bindings.Count;
            for (int index = 0; index < count; index++)
            {
                BasisTrackerBinding binding = Bindings[index];
                if (binding.Tracker == null || binding.Target == null)
                {
                    continue;
                }
                // BasisObjectSyncNetworking.Awake and ControlState both flip
                // isKinematic = false on locally-owned props, and ControlState can
                // re-fire on ownership-transfer events long after bind. If physics
                // touches the rigidbody between our writes, Scene view samples those
                // intermediate frames (out of step with onBeforeRender) and flickers
                // even when Game view stays clean. Re-asserting kinematic each frame
                // is cheap and avoids playing whack-a-mole with every external setter.
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
                case BasisRuntimeSpawnRegistry.RegistryChangeType.ClearedAll:
                    for (int index = Bindings.Count - 1; index >= 0; index--)
                    {
                        RemoveAt(index);
                    }
                    break;
            }
        }
    }
}
