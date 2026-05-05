using System.Collections.Generic;
using System.Linq;
using HVR.Basis.Comms;
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
        private readonly HashSet<IHVRVixxyActuator> _actuatorsToUpdateThisTick = new();
        private bool _anythingNeedsUpdating;

        private readonly Dictionary<int, HashSet<IHVRVixxyAggregator>> _addressIdToAggregators = new();
        private readonly Dictionary<int, HashSet<IHVRVixxyActuator>> _addressIdToActuators = new();
        private readonly Dictionary<GameObject, MaterialPropertyBlock> _objectToMaterialPropertyBlock = new();
        private readonly Dictionary<GameObject, Renderer> _objectToRenderer_mayContainNullObjects = new();
        private readonly HashSet<GameObject> _stagedBlocks = new(); // FIXME: We should really just be binding tuples into _objectToMaterialPropertyBlock

        private readonly HashSet<IHVRVixxyAggregator> _workAggregators = new();

        private readonly List<HVRVixxyToBeNetworked> _toBeNetworked = new();
        private bool _alreadyCalledReadyBothAvatarAndNetwork;

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

            var aggregators = AggregatorsOf(addressId);
            var actuators = ActuatorsOf(addressId);

            // In AcquisitionService, acquisition events are raised as soon as the data arrives.
            // We don't want to process that new data when it arrives, instead we want to process
            // only after all data has arrived for that frame, all at once.

            // FIXME: AcquisitionService "OnAddressUpdated" fires when ANY data is received on that line.
            // The value may have not changed. We need to track it so that we don't send unnecessarily update actuators,
            // like that of face tracking.
            // OR, modify AcquisitionService to have OnAddressValueChanged.
            _aggregatorsToUpdateThisTick.UnionWith(aggregators);
            _actuatorsToUpdateThisTick.UnionWith(actuators);
            _anythingNeedsUpdating = true;
        }

        private IEnumerable<IHVRVixxyAggregator> AggregatorsOf(int addressId)
        {
            if (_addressIdToAggregators.TryGetValue(addressId, out var results)) return results;
            return Enumerable.Empty<IHVRVixxyAggregator>();
        }

        private IEnumerable<IHVRVixxyActuator> ActuatorsOf(int addressId)
        {
            if (_addressIdToActuators.TryGetValue(addressId, out var results)) return results;
            return Enumerable.Empty<IHVRVixxyActuator>();
        }

        private void Update()
        {
            if (!_anythingNeedsUpdating) return;

            // Randomness in the number of iteration cycles is an attempt to ensure we don't get implementation-specific
            // behaviour that expects a specific number of cycles to happen.
            var randomIterations = UnityEngine.Random.Range(5, 10);
            while (randomIterations > 0 && _aggregatorsToUpdateThisTick.Count > 0)
            {
                randomIterations--;
                // Starting a new cycle.
                _workAggregators.Clear();
                _workAggregators.UnionWith(_aggregatorsToUpdateThisTick);
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

            // Deck remaining aggregations for next frame. We already gave it a bunch of chances.
            _anythingNeedsUpdating = _aggregatorsToUpdateThisTick.Count > 0;

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
            }

            VariableStore.UnregisterAddresses(new []{ actuatorRegistrationToken.registeredAddressId }, actuatorRegistrationToken.registeredCallback);
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
            if (_addressIdToAggregators.TryGetValue(addressId, out var existingActuator)) existingActuator.Remove(aggregator);
        }

        /// Inform the orchestrator that the object will need a material property block assigned to it.
        /// If this object does not have a Renderer component, it is not considered to be an error.
        public void RequireMaterialPropertyBlock(GameObject bakedObject)
        {
            if (!_objectToMaterialPropertyBlock.ContainsKey(bakedObject))
            {
                _objectToMaterialPropertyBlock.Add(bakedObject, new MaterialPropertyBlock());
                _objectToRenderer_mayContainNullObjects.Add(bakedObject, bakedObject.GetComponent<Renderer>());
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
                _objectToRenderer_mayContainNullObjects.Add(bakedObject, bakedObject.GetComponent<Renderer>());
            }

            return _objectToMaterialPropertyBlock[bakedObject];
        }

        /// Inform the orchestrator that the material property block needs to be applied on the object.
        public void StagePropertyBlock(GameObject bakedObject)
        {
            _stagedBlocks.Add(bakedObject);
        }

        public void RequireNetworked(int addressId, HVRVixxyNetworkingType networkingType, float defaultValue, float min, float max)
        {
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
            if (_alreadyCalledReadyBothAvatarAndNetwork) return;
            _alreadyCalledReadyBothAvatarAndNetwork = true;

            var comms = HVRCommsUtil.GetComms(this);
            foreach (var toBeNetworked in _toBeNetworked)
            {
                comms.RequireVariable(new HVRVariable
                {
                    addressId = toBeNetworked.addressId,
                    initialValue = VariableStore.GetValue(toBeNetworked.addressId),
                    variableTypeCode = HVRVariableTypeCode.Float,
                    min = toBeNetworked.min,
                    max = toBeNetworked.max
                });
            }
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
