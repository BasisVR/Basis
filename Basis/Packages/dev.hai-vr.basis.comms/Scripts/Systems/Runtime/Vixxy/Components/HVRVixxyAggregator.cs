using UnityEngine;

namespace HVR.Vixxy
{
    [AddComponentMenu("HVR.Basis/Comms/HVRVixxyAggregator")]
    public class HVRVixxyAggregator : MonoBehaviour
        // , IHVRVixxyAggregator
    {
#if HVR_AGGREGATOR_IS_AVAILABLE
        [SerializeField] private string addressA;
        [SerializeField] private string addressB;
        [SerializeField] private string outputAddress;
        public HVRVixxyOrchestrator orchestrator;

        private readonly HashSet<HVRVixxyAggregator> _transformerResult = new();
        private readonly HashSet<IHVRVixxyActuator> _actuatorResult = new();

        private int _addressIdA;
        private int _addressIdB;
        private int _outputAddressId;
        private float _activeResult = float.MinValue;
        private bool _hasNeverBeenAggregated = true;

        // FIXME: We need some way to initialize those values. It's probably the job of the orchestrator to do this.
        private float a = 0f;
        private float b = 0f;

        private void Awake()
        {
            _addressIdA = HVRAddress.AddressToId(addressA);
            _addressIdB = HVRAddress.AddressToId(addressB);
            _outputAddressId = HVRAddress.AddressToId(outputAddress);

            if (string.IsNullOrEmpty(addressA) || string.IsNullOrEmpty(addressB) || string.IsNullOrEmpty(outputAddress))
            {
                HVR_VixxyUtil.LogUnusual(this, $"{nameof(HVRVixxyAggregator)} actuator named \"{name}\" has some missing addresses. It will be disabled.");
                enabled = false;
            }
        }

        private void OnEnable()
        {
            orchestrator.RegisterAggregator(_addressIdA, this);
            orchestrator.RegisterAggregator(_addressIdB, this);
            // FIXME: Address registration should be inside the orchestrator
            // acquisitionService.RegisterAddresses(new []{ _addressIdA, _addressIdB }, OnAddressUpdated);
        }

        private void OnDisable()
        {
            orchestrator.UnregisterAggregator(_addressIdA, this);
            orchestrator.UnregisterAggregator(_addressIdB, this);
            // FIXME: Address registration should be inside the orchestrator
            // acquisitionService.UnregisterAddresses(new []{ _addressIdA, _addressIdB }, OnAddressUpdated);
        }

        private void OnAddressUpdated(int inputAddressId, float value)
        {
            if (inputAddressId == _addressIdA)
            {
                a = value;
            }
            else if (inputAddressId == _addressIdB)
            {
                b = value;
            }
            else return;

            orchestrator.PassAddressUpdated(inputAddressId);
        }

        public bool TryAggregate(out IEnumerable<IHVRVixxyAggregator> aggregators, out IEnumerable<IHVRVixxyActuator> actuators)
        {
            var result = a * b;

            aggregators = _transformerResult;
            actuators = _actuatorResult;

            if (_hasNeverBeenAggregated || !Mathf.Approximately(_activeResult, result))
            {
                // First aggregation is always considered a successful aggregation, for initialization purposes.
                _hasNeverBeenAggregated = false;
                _activeResult = result;

                orchestrator.ProvideValue(_outputAddressId, result);

                return true;
            }

            // Even if an input changes, if the output doesn't change, then it will not result in a change on the actuators.
            return false;
        }
#endif
    }
}
