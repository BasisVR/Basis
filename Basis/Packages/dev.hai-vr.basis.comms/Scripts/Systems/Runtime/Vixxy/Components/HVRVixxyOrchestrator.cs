using System.Collections.Generic;
using System.Linq;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using HVR.Basis.Comms;
using HVR.Basis.Comms.HVRUtility;
using UnityEngine;

namespace HVR.Vixxy
{
    /// There is one instance of this **per avatar** or **per world object**.
    [DefaultExecutionOrder(-10)] // FIXME: acquisitionService can be null if the dependents become awake before this
    [AddComponentMenu("HVR.Basis/Comms/HVRVixxyOrchestrator")]
    public class HVRVixxyOrchestrator : MonoBehaviour
    {
        // TODO:
        // - Collect arriving data.
        // - When data arrives, we mark the aggregators and the actuators of that data.
        // - When all data arrived, and we're starting the update cycle, we wake up all aggregators of that data.

        [SerializeField] public Transform context; // Can be null. If it is null, the orchestrator *is* the context.
        public HVRVariableStore VariableStore;

        private readonly HashSet<IHVRVixxyAggregator> _aggregatorsToUpdateThisTick = new();
        private readonly HashSet<IHVRVixxyActuator> _actuatorsWithFiltersToCheckThisTick = new();
        private readonly HashSet<IHVRVixxyActuator> _actuatorsToUpdateThisTick = new();
        private bool _anythingNeedsUpdating;

        private readonly Dictionary<int, HashSet<IHVRVixxyAggregator>> _addressIdToAggregators = new();
        private readonly Dictionary<int, HashSet<IHVRVixxyActuator>> _addressIdToActuators = new();
        private readonly Dictionary<GameObject, MaterialPropertyBlock> _objectToMaterialPropertyBlock = new();
        private readonly Dictionary<GameObject, Renderer> _objectToRenderer_mayContainNullObjects = new();
        private readonly HashSet<GameObject> _stagedBlocks = new(); // FIXME: We should really just be binding tuples into _objectToMaterialPropertyBlock

        private readonly HashSet<IHVRVixxyAggregator> _workAggregators = new();

        private readonly List<HVRVixxyToBeNetworked> _toBeNetworked = new();
        private bool _needsReevaluateSystemAddresses;

        // Basis-specific
        private HVRBasisBuiltInAddresses _builtInAddressesNullable;
        private HashSet<int> _measurementAddressIds;

        /// Contrary to AcquisitionService, which only references data pertaining to the local user, implicit addresses can refer to data
        /// coming from other users to drive that the avatar of that user.
        public delegate void ImplicitAddressUpdated(float value);

        public Transform Context()
        {
            return context != null ? context : transform;
        }

        public void PassAddressUpdated(int addressId)
        {
            // This cannot be cached outside of this lambda (unless we're smart about it),
            // as new aggregators and actuators may be added.
            // Might need to add a baking phase so that we don't do a string lookup every time
            // (consider switching to an int lookup).

            // In AcquisitionService, acquisition events are raised as soon as the data arrives.
            // We don't want to process that new data when it arrives, instead we want to process
            // only after all data has arrived for that frame, all at once.

            // FIXME: AcquisitionService "OnAddressUpdated" fires when ANY data is received on that line.
            // The value may have not changed. We need to track it so that we don't send unnecessarily update actuators,
            // like that of face tracking.
            // OR, modify AcquisitionService to have OnAddressValueChanged.

            // Iterated as concrete HashSets: this fires per changed address per frame, and going
            // through IEnumerable boxes the set's struct enumerator to the heap each time.
            if (_addressIdToAggregators.TryGetValue(addressId, out var aggregators))
            {
                foreach (var aggregator in aggregators)
                {
                    _aggregatorsToUpdateThisTick.Add(aggregator);
                }
            }
            if (_addressIdToActuators.TryGetValue(addressId, out var actuators))
            {
                foreach (var actuator in actuators)
                {
                    if (actuator.HasFilters())
                    {
                        _actuatorsWithFiltersToCheckThisTick.Add(actuator);
                    }
                    else
                    {
                        _actuatorsToUpdateThisTick.Add(actuator);
                    }
                }
            }
            _anythingNeedsUpdating = true;
        }

        private void OnEnable() => HVRCommsUpdateDriver.Register(this);
        private void OnDisable() => HVRCommsUpdateDriver.Unregister(this);

        // Distance gating for remote avatars, riding the same LOD cadence the pose path uses
        // (SMModuleDistanceBasedReductions.PoseSkipByLod — all zeros until the user engages the
        // distance-reduction setting, so this is inert by default). Skipped ticks lose nothing:
        // address updates keep accumulating in the pending sets, which are latest-wins, so the
        // next tick actuates once with the newest values. Wearer and world-object orchestrators
        // never gate.
        private bool _avatarProbed;
        private bool _neverGate;
        private BasisAvatar _avatarOrNull;
        private BasisRemotePlayer _remotePlayerOrNull;
        private int _lodSkipCounter;

        private bool ShouldSkipThisTick()
        {
            if (_neverGate) return false;
            if (_avatarProbed == false)
            {
                _avatarProbed = true;
                _avatarOrNull = HVRCommsUtil.GetAvatar(this);
                if (_avatarOrNull == null || _avatarOrNull.IsOwnedLocally)
                {
                    _neverGate = true;
                    return false;
                }
            }
            if (_remotePlayerOrNull == null)
            {
                // The avatar→player mapping may not exist yet during join; tick normally and
                // keep trying — a dictionary lookup per attempt.
                if (BasisNetworkPlayers.AvatarToPlayer(_avatarOrNull, out _, out var netPlayer)
                    && netPlayer is BasisNetworkReceiver receiver)
                {
                    _remotePlayerOrNull = receiver.RemotePlayer;
                }
                if (_remotePlayerOrNull == null) return false;
            }

            if (_lodSkipCounter > 0)
            {
                _lodSkipCounter--;
                return true;
            }
            int lod = Mathf.Clamp(_remotePlayerOrNull.CurrentLodLevel, 0, 3);
            _lodSkipCounter = SMModuleDistanceBasedReductions.PoseSkipByLod[lod];
            return false;
        }

        internal void SimulateTick()
        {
            if (ShouldSkipThisTick()) return;

            if (_needsReevaluateSystemAddresses)
            {
                var systemAddresses = _addressIdToActuators.Keys
                    .Concat(_addressIdToAggregators.Keys)
                    .Distinct()
                    .Where(HVRAddress.IsSystemAddressId)
                    .ToHashSet();

                if (systemAddresses.Count > 0 && _builtInAddressesNullable == null)
                {
                    _builtInAddressesNullable = new HVRBasisBuiltInAddresses(HVRCommsUtil.GetComms(this), HVRCommsUtil.GetAvatar(this).IsOwnedLocally);
                }
                if (systemAddresses.Count > 0)
                {
                    _builtInAddressesNullable.DeclareAllRequired(systemAddresses);
                }
                _needsReevaluateSystemAddresses = false;
            }
            Simulate();
            Apply();
            HVRVixxyPersistentStore.Tick();
        }

        private readonly HashSet<IHVRVixxyActuator> L_actuatorsWithFiltersToCheckNextTick = new(); // is field due to PR guidelines
        /// Calculate aggregators and filters. This may be jobified in the future.
        public void Simulate()
        {
            if (!_anythingNeedsUpdating) return;

            // Randomness in the number of iteration cycles is an attempt to ensure we don't get implementation-specific
            // behaviour that expects a specific number of cycles to happen.
            var randomIterations = UnityEngine.Random.Range(5, 10);
            while (randomIterations > 0 && _aggregatorsToUpdateThisTick.Count > 0)
            {
                randomIterations--;
                // Starting a new cycle. Copied by hand — UnionWith between two HashSets still
                // enumerates through IEnumerable and boxes the enumerator.
                _workAggregators.Clear();
                foreach (var aggregator in _aggregatorsToUpdateThisTick)
                {
                    _workAggregators.Add(aggregator);
                }
                _aggregatorsToUpdateThisTick.Clear();

                foreach (var aggregator in _workAggregators)
                {
                    if (aggregator.TryAggregate(out var newAggregators, out var newActuators))
                    {
                        _aggregatorsToUpdateThisTick.UnionWith(newAggregators);
                        _actuatorsToUpdateThisTick.UnionWith(newActuators);
                    }
                }
            }

            if (_actuatorsWithFiltersToCheckThisTick.Count > 0)
            {
                L_actuatorsWithFiltersToCheckNextTick.Clear();

                foreach (var actuator in _actuatorsWithFiltersToCheckThisTick)
                {
                    var filterResult = actuator.ApplyFilters();
                    if (filterResult.filterNeedsCheckNextTick)
                    {
                        L_actuatorsWithFiltersToCheckNextTick.Add(actuator);
                    }
                    if (filterResult.actuatorNeedsUpdate)
                    {
                        _actuatorsToUpdateThisTick.Add(actuator);
                    }
                }

                _actuatorsWithFiltersToCheckThisTick.Clear();
                foreach (var actuator in L_actuatorsWithFiltersToCheckNextTick)
                {
                    _actuatorsWithFiltersToCheckThisTick.Add(actuator);
                }
            }

            // Deck remaining aggregations for next frame. We already gave it a bunch of chances.
            _anythingNeedsUpdating = _aggregatorsToUpdateThisTick.Count > 0 || _actuatorsWithFiltersToCheckThisTick.Count > 0;

            // TODO: Calculating the effective lerp value of an Actuator should probably be done in this step.
        }

        /// Applies effects to GameObject and Components and sets MaterialPropertyBlock to renderers.
        public void Apply()
        {
            // TODO: It may be possible to do a reverse graph traversal, where we deny listening to addresses
            // or processing aggregators if there are no actuators that listen to that data in the first place.
            if (_actuatorsToUpdateThisTick.Count > 0)
            {
                foreach (var actuator in _actuatorsToUpdateThisTick)
                {
                    actuator.Actuate();
                }

                _actuatorsToUpdateThisTick.Clear();
            }

            if (_stagedBlocks.Count > 0)
            {
                foreach (var stagedBlock in _stagedBlocks)
                {
                    // No ContainsKey checks: The objects should always exist in the dictionaries. If they don't, it's a programming error.
                    var stagedRenderer = _objectToRenderer_mayContainNullObjects[stagedBlock];
                    if (stagedRenderer != null)
                    {
                        stagedRenderer.SetPropertyBlock(_objectToMaterialPropertyBlock[stagedBlock]);
                    }
                }
                _stagedBlocks.Clear();
            }
        }

        public HVRActuatorRegistrationToken RegisterActuator(int addressId, IHVRVixxyActuator actuator, ImplicitAddressUpdated implicitAddressUpdatedFn)
        {
            if (_addressIdToActuators.TryGetValue(addressId, out var existingActuators))
            {
                existingActuators.Add(actuator);
            }
            else
            {
                var newActuators = new HashSet<IHVRVixxyActuator> { actuator };
                _addressIdToActuators.Add(addressId, newActuators);
            }

            // When an actuator is added, it is scheduled to be updated for initialization purposes.
            _anythingNeedsUpdating = true;
            _actuatorsToUpdateThisTick.Add(actuator);

            HVRVariableStore.AddressUpdated addressUpdatedFn = (_, value) => implicitAddressUpdatedFn.Invoke(value);
            VariableStore.RegisterAddresses(new [] { addressId }, addressUpdatedFn);

            if (HVRAddress.IsSystemAddressId(addressId))
            {
                _needsReevaluateSystemAddresses = true;
            }

            return new HVRActuatorRegistrationToken
            {
                registeredAddressId = addressId,
                registeredCallback = addressUpdatedFn,
                registeredActuator = actuator,
                initialValue = VariableStore.GetValue(addressId)
            };
        }

        public void UnregisterActuator(HVRActuatorRegistrationToken actuatorRegistrationToken)
        {
            if (_addressIdToActuators.TryGetValue(actuatorRegistrationToken.registeredAddressId, out var existingActuator))
            {
                existingActuator.Remove(actuatorRegistrationToken.registeredActuator);
                if (existingActuator.Count == 0)
                {
                    _addressIdToActuators.Remove(actuatorRegistrationToken.registeredAddressId);
                }
            }

            VariableStore.UnregisterAddresses(new []{ actuatorRegistrationToken.registeredAddressId }, actuatorRegistrationToken.registeredCallback);

            if (HVRAddress.IsSystemAddressId(actuatorRegistrationToken.registeredAddressId))
            {
                _needsReevaluateSystemAddresses = true;
            }
        }

        public void RegisterAggregator(string address, IHVRVixxyAggregator actuator)
        {
            RegisterAggregator(HVRAddress.AddressToId(address), actuator);
        }

        public void RegisterAggregator(int addressId, IHVRVixxyAggregator actuator)
        {
            if (_addressIdToAggregators.TryGetValue(addressId, out var existingAggregators))
            {
                existingAggregators.Add(actuator);
            }
            else
            {
                var newAggregators = new HashSet<IHVRVixxyAggregator> { actuator };
                _addressIdToAggregators.Add(addressId, newAggregators);
            }

            // When an aggregator is added, it is scheduled to be updated for initialization purposes.
            _anythingNeedsUpdating = true;
            _aggregatorsToUpdateThisTick.Add(actuator);
        }

        public void UnregisterAggregator(string address, IHVRVixxyAggregator aggregator)
        {
            UnregisterAggregator(HVRAddress.AddressToId(address), aggregator);
        }

        public void UnregisterAggregator(int addressId, IHVRVixxyAggregator aggregator)
        {
            if (_addressIdToAggregators.TryGetValue(addressId, out var existingActuator))
            {
                existingActuator.Remove(aggregator);
                if (existingActuator.Count == 0)
                {
                    _addressIdToAggregators.Remove(addressId);
                }
            }
        }

        /// Inform the orchestrator that the object will need a material property block assigned to it.
        /// If this object does not have a Renderer component, it is not considered to be an error.
        public void RequireMaterialPropertyBlock(GameObject bakedObject)
        {
            if (!_objectToMaterialPropertyBlock.ContainsKey(bakedObject))
            {
                _objectToMaterialPropertyBlock.Add(bakedObject, new MaterialPropertyBlock());
                _objectToRenderer_mayContainNullObjects.Add(bakedObject, bakedObject.TryGetComponent<Renderer>(out var result) ? result : null);
            }
        }

        /// Obtain the material property block for the object.
        public MaterialPropertyBlock GetMaterialPropertyBlockForBakedObject(GameObject bakedObject)
        {
            // If the key doesn't exist, it is a programming error. Callers should only call GetMaterialPropertyBlockFor
            // if that subject is guaranteed to have a MaterialPropertyBlock declared, as it is required by Awake.
            // (Live edits not currently supported)
            if (!_objectToMaterialPropertyBlock.ContainsKey(bakedObject))
            {
                // DEFENSIVE for live edits only. This condition should not be entered by design.
                HVR_VixxyUtil.LogUnusual(this, "A MaterialPropertyBlock object was not found. This is either a programming error, or the user is currently doing a live edit," +
                                       " and MaterialPropertyBlock are not normally cached if the control did not previously make use of materials. We will create one," +
                                       " however, if this wasn't a live edit, then it needs fixing.");
                _objectToMaterialPropertyBlock.Add(bakedObject, new MaterialPropertyBlock());
                _objectToRenderer_mayContainNullObjects.Add(bakedObject, bakedObject.TryGetComponent<Renderer>(out var result) ? result : null);
            }

            return _objectToMaterialPropertyBlock[bakedObject];
        }

        /// Inform the orchestrator that the material property block needs to be applied on the object.
        public void StagePropertyBlock(GameObject bakedObject)
        {
            _stagedBlocks.Add(bakedObject);
        }

        public bool IsMeasurementAddress(int addressId)
        {
            _measurementAddressIds ??= HVR_VixxyUtil.FindAllMeasurementAddresses(context.GetComponentsInChildren<HVRMeasure>(true).ToList())
                .Select(HVRAddress.AddressToId)
                .ToHashSet();

            return _measurementAddressIds.Contains(addressId);
        }

        public void RequireNetworked(int addressId, HVRVixxyNetworkingType networkingType, float defaultValue, float min, float max)
        {
            foreach (var existing in _toBeNetworked)
            {
                if (existing.addressId == addressId)
                {
                    if (networkingType == HVRVixxyNetworkingType.UpdatedExtremelyFrequently)
                    {
                        existing.networkingType = networkingType;
                    }
                    if (min < existing.min)
                    {
                        existing.min = min;
                    }
                    if (max > existing.max)
                    {
                        existing.max = max;
                    }
                    return;
                }
            }

            _toBeNetworked.Add(new HVRVixxyToBeNetworked
            {
                addressId = addressId,
                networkingType = networkingType,
                defaultValue = defaultValue,
                min = min,
                max = max,
            });
        }

        public void SignalHVRReadyBothAvatarAndNetwork(bool isWearer)
        {
            var comms = HVRCommsUtil.GetComms(this);
            foreach (var toBeNetworked in _toBeNetworked)
            {
                comms.RequireVariable(new HVRVariable
                {
                    addressId = toBeNetworked.addressId,
                    initialValue = VariableStore.GetValue(toBeNetworked.addressId),
                    variableTypeCode = HVRVariableTypeCode.Float,
                    needsInterpolation = false,
                    min = toBeNetworked.min,
                    max = toBeNetworked.max
                });
            }
        }

        private void OnDestroy()
        {
            if (_builtInAddressesNullable != null)
            {
                _builtInAddressesNullable.Destroy();
            }
            HVRVixxyPersistentStore.FlushNow();
        }
    }

    public class HVRActuatorRegistrationToken
    {
        public int registeredAddressId;
        public HVRVariableStore.AddressUpdated registeredCallback;
        public IHVRVixxyActuator registeredActuator;

        public float initialValue;
    }

    internal class HVRVixxyToBeNetworked
    {
        public int addressId;
        public HVRVixxyNetworkingType networkingType;
        public float defaultValue;
        public float min;
        public float max;
    }
}
