using System;
using System.Collections.Generic;

namespace HVR.Basis.Comms
{
    public class HVRVariableStore
    {
        public delegate void AddressUpdated(int addressId, float value);

        internal readonly Dictionary<int, ListenerState> _addressIdToListenerState = new();

        public void Submit(int addressId, float value)
        {
            if (addressId == 0) throw new IndexOutOfRangeException("Address cannot be zero, this may indicate an initialization issue.");

            if (_addressIdToListenerState.TryGetValue(addressId, out var listenerState))
            {
                listenerState.value = value;
                listenerState.Invoke(addressId, value);
            }
        }

        public void SubmitOrDefineDefaultValue(int addressId, float value)
        {
            if (addressId == 0) throw new IndexOutOfRangeException("Address cannot be zero, this may indicate an initialization issue.");

            if (_addressIdToListenerState.TryGetValue(addressId, out var listenerState))
            {
                listenerState.value = value;
                listenerState.Invoke(addressId, value);
            }
            else
            {
                _addressIdToListenerState.Add(addressId, new ListenerState
                {
                    value = value
                });
            }
        }

        public void RegisterAddresses(int[] addressIds, AddressUpdated onAddressUpdated)
        {
            foreach (var addressId in addressIds)
            {
                _addressIdToListenerState.TryAdd(addressId, new ListenerState());

                var listenerState = _addressIdToListenerState[addressId];
                listenerState.OnAddressUpdated -= onAddressUpdated;
                listenerState.OnAddressUpdated += onAddressUpdated;
            }
        }

        public void UnregisterAddresses(int[] addressIds, AddressUpdated onAddressUpdated)
        {
            foreach (var addressId in addressIds)
            {
                if (_addressIdToListenerState.TryGetValue(addressId, out var listenerState))
                {
                    listenerState.OnAddressUpdated -= onAddressUpdated;
                }
            }
        }

        public float GetValue(int addressId)
        {
            if (_addressIdToListenerState.TryGetValue(addressId, out var listenerState))
            {
                return listenerState.value;
            }

            return 0f;
        }

        internal class ListenerState
        {
            internal event AddressUpdated OnAddressUpdated;
            internal float value;

            public void Invoke(int address, float value) => OnAddressUpdated?.Invoke(address, value);
            internal int GetListenersCount() => OnAddressUpdated?.GetInvocationList().Length ?? 0;
        }
    }
}
