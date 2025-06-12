using System.Collections.Generic;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Internal/Acquisition Service")]
    public class AcquisitionService : MonoBehaviour
    {
        public static AcquisitionService SceneInstance => CommsUtil.GetOrCreateSceneInstance(ref _sceneInstance);
        private static AcquisitionService _sceneInstance;

        public delegate void AddressUpdated(string address, float value);

        private readonly Dictionary<string, AcquisitionForAddress> _addressUpdated = new Dictionary<string, AcquisitionForAddress>();

        public void Submit(string address, float value)
        {
            if (_addressUpdated.TryGetValue(address, out var acquisitor))
            {
                acquisitor.Invoke(address, value);
            }
        }

        public void RegisterAddresses(string[] addressBase, AddressUpdated onAddressUpdated)
        {
            foreach (var address in addressBase)
            {
                _addressUpdated.TryAdd(address, new AcquisitionForAddress());

                var acquisitor = _addressUpdated[address];
                acquisitor.OnAddressUpdated -= onAddressUpdated;
                acquisitor.OnAddressUpdated += onAddressUpdated;
            }
        }

        public void UnregisterAddresses(string[] addressBase, AddressUpdated onAddressUpdated)
        {
            foreach (var address in addressBase)
            {
                if (_addressUpdated.TryGetValue(address, out var acquisitor))
                {
                    acquisitor.OnAddressUpdated -= onAddressUpdated;
                }
            }
        }
    }

    internal class AcquisitionForAddress
    {
        internal event AcquisitionService.AddressUpdated OnAddressUpdated;

        public void Invoke(string address, float value) => OnAddressUpdated?.Invoke(address, value);
    }
}