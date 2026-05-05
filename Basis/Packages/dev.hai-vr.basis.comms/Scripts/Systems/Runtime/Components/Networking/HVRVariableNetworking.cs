using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Network.Core;
using HVR.Basis.Comms.HVRUtility;
using UnityEngine;

namespace HVR.Basis.Comms
{
    public class HVRVariableNetworking : MonoBehaviour, IFeatureReceiver
    {
        private const bool PrintDebug = false;

        public HVRAvatarComms comms;
        public bool isWearer;
        public IHVRTransmitter transmitter;

        internal IHVRVariableBehaviour _behaviour;

        private void Awake() => _behaviour = isWearer ? new HVRVariableBehaviour_Wearer(this) : new HVRVariableBehaviour_Remote(this);
        public void Update() => _behaviour.Update();
        private void OnDestroy() => _behaviour.OnDestroy();

        public void RequireVariable(HVRVariable variable) => _behaviour.RequireVariable(variable);

        public void OnPacketReceived(byte localIdentifier, ArraySegment<byte> data) => _behaviour.OnPacketReceived(localIdentifier, data);
        public void OnResyncEveryoneRequested() => _behaviour.OnResyncEveryoneRequested();
        public void OnResyncRequested(ushort[] whoAsked) => _behaviour.OnResyncRequested(whoAsked);

        private void WhenAddressUpdated(int addressId, float currentValue)
        {
            if (PrintDebug) HVRLogging.ProtocolDebug($"Updating address {HVRAddress.ResolveKnownAddressFromId(addressId)} to value {currentValue}.");
            comms.VariableStore.Submit(addressId, currentValue);
        }

        private const float TransmissionDeltaSeconds = 0.1f;

        internal interface IHVRVariableBehaviour : IFeatureReceiver
        {
            public void Update();
            void RequireVariable(HVRVariable variable);
            void OnDestroy();
        }

        internal class HVRVariableBehaviour_Wearer : IHVRVariableBehaviour
        {
            private readonly HVRVariableNetworking _state;
            private readonly AcquisitionService _acquisitionService;

            internal readonly Dictionary<int, HVRVariableHolder> _addressIdToHolder = new();
            private readonly List<int> _newVariablesAddressIds = new();
            private readonly HashSet<int> _addressIdsWithNewValue = new();
            private ushort _networkId = 0;

            private float _timeLeft;

            public HVRVariableBehaviour_Wearer(HVRVariableNetworking state)
            {
                _state = state;
                _acquisitionService = AcquisitionService.SceneInstance;
            }

            public void OnPacketReceived(byte localIdentifier, ArraySegment<byte> data) { } // Not applicable

            public void OnResyncEveryoneRequested()
            {
                var packet = BuildNewVariablesPacket(_addressIdToHolder.Keys.ToList());
                _state.transmitter.NetworkMessageSend(packet, DeliveryMethod.ReliableSequenced);
                if (PrintDebug) HVRLogging.ProtocolDebug("(OnResyncEveryoneRequested) Sending NewVariablesPacket.");
            }

            public void OnResyncRequested(ushort[] whoAsked)
            {
                var packet = BuildNewVariablesPacket(_addressIdToHolder.Keys.ToList());
                _state.transmitter.NetworkMessageSend(packet, DeliveryMethod.ReliableSequenced, whoAsked);
                if (PrintDebug) HVRLogging.ProtocolDebug("(OnResyncRequested) Sending NewVariablesPacket.");
            }

            public void RequireVariable(HVRVariable variable)
            {
                if (_addressIdToHolder.ContainsKey(variable.addressId)) return;

                if (variable.variableTypeCode == HVRVariableTypeCode.Float)
                {
                    if (variable.initialValue is not float) throw new InvalidOperationException("Initial value does not match variable type code.");
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported variable type code {variable.variableTypeCode}.");
                }

                _acquisitionService.RegisterAddresses(new []{ variable.addressId }, OnAddressUpdated);

                _addressIdToHolder.Add(variable.addressId, new HVRVariableHolder
                {
                    variable = variable,
                    networkId = ++_networkId,
                    currentValue = variable.initialValue,
                    lastTransmittedValue = variable.initialValue,
                    valueWithGreatestDeltaSinceLastTransmittedValue = variable.initialValue
                });

                _newVariablesAddressIds.Add(variable.addressId);
            }

            public void OnDestroy()
            {
                _acquisitionService.UnregisterAddresses(_addressIdToHolder.Keys.ToArray(), OnAddressUpdated);
            }

            private void OnAddressUpdated(int addressId, float value)
            {
                if (_addressIdToHolder.TryGetValue(addressId, out var holder))
                {
                    if (holder.variable.variableTypeCode == HVRVariableTypeCode.Float && !Mathf.Approximately((float)holder.currentValue, value))
                    {
                        _addressIdsWithNewValue.Add(addressId);

                        holder.currentValue = value;
                        if (Mathf.Abs((float)holder.lastTransmittedValue - value) > Mathf.Abs((float)holder.lastTransmittedValue - (float)holder.valueWithGreatestDeltaSinceLastTransmittedValue))
                        {
                            holder.valueWithGreatestDeltaSinceLastTransmittedValue = value;
                        }
                    }
                }
            }

            public void Update()
            {
                if (_newVariablesAddressIds.Count > 0)
                {
                    var packet = BuildNewVariablesPacket(_newVariablesAddressIds);
                    _state.transmitter.NetworkMessageSend(packet, DeliveryMethod.ReliableSequenced);
                    if (PrintDebug) HVRLogging.ProtocolDebug("(Update) Sending NewVariablesPacket.");

                    _newVariablesAddressIds.Clear();
                }

                _timeLeft += Time.deltaTime;
                if (_timeLeft > TransmissionDeltaSeconds)
                {
                    DoTick();
                    _timeLeft = 0;
                }
            }

            private void DoTick()
            {
                if (_addressIdsWithNewValue.Count == 0) return;

                var addressIdsThatNeedToBeResentLater = new HashSet<int>();

                var addressIdsToValueToTransmit = new Dictionary<int, object>();
                foreach (var addressId in _addressIdsWithNewValue)
                {
                    var holder = _addressIdToHolder[addressId];
                    var currentValue = holder.currentValue;

                    // Reminder: We network the value with the greatest delta, which is not necessarily the current value.
                    // Networking the value with the greatest delta helps networking short-lived events such as the eyes blinking.
                    var valueToBeTransmitted = holder.valueWithGreatestDeltaSinceLastTransmittedValue;
                    addressIdsToValueToTransmit.Add(addressId, valueToBeTransmitted);

                    holder.lastTransmittedValue = valueToBeTransmitted;
                    holder.valueWithGreatestDeltaSinceLastTransmittedValue = currentValue;

                    if (holder.variable.variableTypeCode == HVRVariableTypeCode.Float && !Mathf.Approximately((float)currentValue, (float)valueToBeTransmitted))
                    {
                        // If the value with the greatest delta is not the current value, then we need to transmit the current value
                        // (which is now stored inside valueWithGreatestDeltaSinceLastTransmittedValue) next frame.
                        addressIdsThatNeedToBeResentLater.Add(addressId);
                    }
                }

                if (addressIdsToValueToTransmit.Count > 0)
                {
                    var packet = BuildUpdatedVariablesPacket(addressIdsToValueToTransmit);
                    _state.transmitter.NetworkMessageSend(packet, DeliveryMethod.ReliableSequenced);
                    if (PrintDebug) HVRLogging.ProtocolDebug($"(Update) Sending UpdatedVariablesPacket (at T={Time.time:0.00}).");
                }

                _addressIdsWithNewValue.Clear();
                _addressIdsWithNewValue.UnionWith(addressIdsThatNeedToBeResentLater);
            }

            private byte[] BuildUpdatedVariablesPacket(Dictionary<int, object> addressIdsToValueToTransmit)
            {
                var zeroesNetworkIds = new List<ushort>();
                var onesNetworkIds = new List<ushort>();
                var otherAddressIds = new List<int>();

                foreach (var (addressId, value) in addressIdsToValueToTransmit)
                {
                    var networkId = _addressIdToHolder[addressId].networkId;
                    if (value is float f)
                    {
                        if (Mathf.Approximately(f, 0f))
                        {
                            zeroesNetworkIds.Add(networkId);
                        }
                        else if (Mathf.Approximately(f, 1f))
                        {
                            onesNetworkIds.Add(networkId);
                        }
                        else
                        {
                            otherAddressIds.Add(addressId);
                        }
                    }
                    else
                    {
                        otherAddressIds.Add(addressId);
                    }
                }

                if (otherAddressIds.Count > 0)
                {
                    if (PrintDebug) HVRLogging.ProtocolDebug("(BuildUpdatedVariablesPacket) Building a UpdatedVariables_Mixed packet.");
                    return new HVRVariableNetworkingPacket_UpdatedVariables_Mixed
                    {
                        numberOfZeroes = (ushort)zeroesNetworkIds.Count,
                        networkIds = zeroesNetworkIds.Concat(onesNetworkIds).ToList(),
                        other = otherAddressIds.Select(addressId => new HVRVariableNetworkingPacket_UpdatedVariables_Mixed.HVRVariableNetworkingPacket_UpdatedValue
                        {
                            networkId = _addressIdToHolder[addressId].networkId,
                            value = addressIdsToValueToTransmit[addressId]
                        }).ToList()
                    }.Serialize();
                }

                if (zeroesNetworkIds.Count > 0 && onesNetworkIds.Count > 0)
                {
                    if (PrintDebug) HVRLogging.ProtocolDebug("(BuildUpdatedVariablesPacket) Building a UpdatedVariables_ZeroesAndOnes packet.");
                    return new HVRVariableNetworkingPacket_UpdatedVariables_ZeroesAndOnes
                    {
                        numberOfZeroes = (ushort)zeroesNetworkIds.Count,
                        networkIds = zeroesNetworkIds.Concat(onesNetworkIds).ToList()
                    }.Serialize();
                }

                if (PrintDebug) HVRLogging.ProtocolDebug($"(BuildUpdatedVariablesPacket) Building a {(zeroesNetworkIds.Count > 0 ? "UpdatedVariables_Zeroes" : "UpdatedVariables_Ones")} packet.");
                return new HVRVariableNetworkingPacket_UpdatedVariables_ZeroesOrOnes
                {
                    packetType = zeroesNetworkIds.Count > 0 ? AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Zeroes : AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Ones,
                    networkIds = zeroesNetworkIds.Count > 0 ? zeroesNetworkIds : onesNetworkIds
                }.Serialize();
            }

            private byte[] BuildNewVariablesPacket(List<int> newVariablesAddressIds)
            {
                var allHolders = newVariablesAddressIds
                    .Select(addressId => _addressIdToHolder[addressId])
                    .ToList();

                var other = new List<HVRVariableNetworkingPacket_NewVariables.HVRVariableNetworkingPacket_NewVariable>();
                var zeroes = new List<HVRVariableNetworkingPacket_NewVariables.HVRVariableNetworkingPacket_NewQuickVariable>();
                var ones = new List<HVRVariableNetworkingPacket_NewVariables.HVRVariableNetworkingPacket_NewQuickVariable>();

                foreach (var holder in allHolders)
                {
                    var isFloat = holder.variable.variableTypeCode == HVRVariableTypeCode.Float;
                    if (isFloat
                        && (Mathf.Approximately((float)holder.currentValue, 0f)
                            || Mathf.Approximately((float)holder.currentValue, 1f)))
                    {
                        var quickVar = new HVRVariableNetworkingPacket_NewVariables.HVRVariableNetworkingPacket_NewQuickVariable
                        {
                            address = HVRAddress.ResolveKnownAddressFromId(holder.variable.addressId),
                            networkId = holder.networkId,
                        };
                        (Mathf.Approximately((float)holder.currentValue, 1f) ? ones : zeroes)
                            .Add(quickVar);
                    }
                    else
                    {
                        other.Add(new HVRVariableNetworkingPacket_NewVariables.HVRVariableNetworkingPacket_NewVariable
                        {
                            address = HVRAddress.ResolveKnownAddressFromId(holder.variable.addressId),
                            networkId = holder.networkId,
                            variableTypeCode = (byte)holder.variable.variableTypeCode,
                            initialValue = holder.currentValue
                        });
                    }
                }

                return new HVRVariableNetworkingPacket_NewVariables
                {
                    newGeneralVariables = other,
                    floatZero = zeroes,
                    floatOne = ones,
                }.Serialize();
            }

            internal class HVRVariableHolder
            {
                public HVRVariable variable;
                public ushort networkId;
                public object currentValue;
                public object lastTransmittedValue;
                public object valueWithGreatestDeltaSinceLastTransmittedValue;
            }
        }

        internal class HVRVariableBehaviour_Remote : IHVRVariableBehaviour
        {
            private readonly HVRVariableNetworking _state;
            internal readonly Dictionary<int, HVRVariableHolder> _addressIdToHolder = new();
            private readonly Dictionary<ushort, int> _networkIdToAddressId = new();

            public HVRVariableBehaviour_Remote(HVRVariableNetworking state)
            {
                _state = state;
            }

            public void Update() { }

            public void OnPacketReceived(byte localIdentifier, ArraySegment<byte> data)
            {
                if (data.Count < 1) { HVRLogging.ProtocolError("Data buffer is empty."); return; }

                var packetType = data[0];
                switch (packetType)
                {
                    case AvatarMessageProcessing.NewNet_WearerSubmitsNewVariables:
                    {
                        var packet = HVRVariableNetworkingPacket_NewVariables.Deserialize(data);
                        if (packet == null) { HVRLogging.ProtocolError("Failed to deserialize NewVariables packet."); return; }

                        if (PrintDebug) HVRLogging.ProtocolDebug("(OnPacketReceived) Receiving NewVariables packet.");
                        WhenNewVariablesReceived(packet);
                        break;
                    }
                    case AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Zeroes:
                    {
                        var packet = HVRVariableNetworkingPacket_UpdatedVariables_ZeroesOrOnes.Deserialize(data, packetType);
                        if (packet == null) { HVRLogging.ProtocolError("Failed to deserialize UpdatedVariables_Zeroes packet."); return; }

                        if (PrintDebug) HVRLogging.ProtocolDebug("(OnPacketReceived) Receiving UpdatedVariables_Zeroes packet.");
                        foreach (var networkId in packet.networkIds)
                        {
                            if (_networkIdToAddressId.TryGetValue(networkId, out var addressId))
                            {
                                _addressIdToHolder[addressId].currentValue = 0f;
                                _state.WhenAddressUpdated(addressId, 0f);
                            }
                        }

                        break;
                    }
                    case AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Ones:
                    {
                        var packet = HVRVariableNetworkingPacket_UpdatedVariables_ZeroesOrOnes.Deserialize(data, packetType);
                        if (packet == null) { HVRLogging.ProtocolError("Failed to deserialize UpdatedVariables_Ones packet."); return; }

                        if (PrintDebug) HVRLogging.ProtocolDebug("(OnPacketReceived) Receiving UpdatedVariables_Ones packet.");
                        foreach (var networkId in packet.networkIds)
                        {
                            if (_networkIdToAddressId.TryGetValue(networkId, out var addressId))
                            {
                                _addressIdToHolder[addressId].currentValue = 1f;
                                _state.WhenAddressUpdated(addressId, 1f);
                            }
                        }

                        break;
                    }
                    case AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_ZeroesAndOnes:
                    {
                        var packet = HVRVariableNetworkingPacket_UpdatedVariables_ZeroesAndOnes.Deserialize(data);
                        if (packet == null) { HVRLogging.ProtocolError("Failed to deserialize UpdatedVariables_ZeroesAndOnes packet."); return; }

                        if (PrintDebug) HVRLogging.ProtocolDebug("(OnPacketReceived) Receiving UpdatedVariables_ZeroesAndOnes packet.");
                        for (var index = 0; index < packet.networkIds.Count; index++)
                        {
                            if (_networkIdToAddressId.TryGetValue(packet.networkIds[index], out var addressId))
                            {
                                var isZero = index < packet.numberOfZeroes;
                                var value = isZero ? 0f : 1f;
                                _addressIdToHolder[addressId].currentValue = value;
                                _state.WhenAddressUpdated(addressId, value);
                            }
                        }

                        break;
                    }
                    case AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Mixed:
                    {
                        var packet = HVRVariableNetworkingPacket_UpdatedVariables_Mixed.Deserialize(data);
                        if (packet == null) { HVRLogging.ProtocolError("Failed to deserialize UpdatedVariables_Mixed packet."); return; }

                        if (PrintDebug) HVRLogging.ProtocolDebug("(OnPacketReceived) Receiving UpdatedVariables_Mixed packet.");
                        for (var index = 0; index < packet.networkIds.Count; index++)
                        {
                            if (_networkIdToAddressId.TryGetValue(packet.networkIds[index], out var addressId))
                            {
                                var isZero = index < packet.numberOfZeroes;
                                var value = isZero ? 0f : 1f;
                                _addressIdToHolder[addressId].currentValue = value;
                                _state.WhenAddressUpdated(addressId, value);
                            }
                        }
                        foreach (var other in packet.other)
                        {
                            if (_networkIdToAddressId.TryGetValue(other.networkId, out var addressId))
                            {
                                if (_addressIdToHolder[addressId].variable.variableTypeCode == HVRVariableTypeCode.Float && other.value is float f)
                                {
                                    _addressIdToHolder[addressId].currentValue = f;
                                    _state.WhenAddressUpdated(addressId, f);
                                }
                            }
                        }

                        break;
                    }
                    default:
                        HVRLogging.ProtocolError($"Unknown packet type {packetType}.");
                        break;
                }
            }

            public void OnResyncEveryoneRequested() { } // Not applicable
            public void OnResyncRequested(ushort[] whoAsked) { } // Not applicable

            private void WhenNewVariablesReceived(HVRVariableNetworkingPacket_NewVariables packet)
            {
                var newlyAddedAddresses = new List<int>();

                foreach (var variable in packet.newGeneralVariables)
                {
                    var addressId = HVRAddress.AddressToId(variable.address);
                    _addressIdToHolder[addressId] = new HVRVariableHolder
                    {
                        variable = new HVRVariable
                        {
                            addressId = addressId,
                            variableTypeCode = (HVRVariableTypeCode)variable.variableTypeCode,
                            initialValue = (float)variable.initialValue
                        },
                        networkId = variable.networkId,
                        currentValue = (float)variable.initialValue
                    };

                    _networkIdToAddressId[variable.networkId] = addressId;
                    newlyAddedAddresses.Add(addressId);
                }

                foreach (var variable in packet.floatZero)
                {
                    var addressId = HVRAddress.AddressToId(variable.address);
                    _addressIdToHolder[addressId] = new HVRVariableHolder
                    {
                        variable = new HVRVariable
                        {
                            addressId = addressId,
                            variableTypeCode = HVRVariableTypeCode.Float,
                            initialValue = 0f
                        },
                        networkId = variable.networkId,
                        currentValue = 0f
                    };

                    _networkIdToAddressId[variable.networkId] = addressId;
                    newlyAddedAddresses.Add(addressId);
                }

                foreach (var variable in packet.floatOne)
                {
                    var addressId = HVRAddress.AddressToId(variable.address);
                    _addressIdToHolder[addressId] = new HVRVariableHolder
                    {
                        variable = new HVRVariable
                        {
                            addressId = addressId,
                            variableTypeCode = HVRVariableTypeCode.Float,
                            initialValue = 1f
                        },
                        networkId = variable.networkId,
                        currentValue = 1f
                    };

                    _networkIdToAddressId[variable.networkId] = addressId;
                    newlyAddedAddresses.Add(addressId);
                }

                foreach (var newlyAddedAddress in newlyAddedAddresses)
                {
                    _state.WhenAddressUpdated(newlyAddedAddress, (float)_addressIdToHolder[newlyAddedAddress].currentValue);
                }
            }

            public void RequireVariable(HVRVariable variable)
            {
                // Do nothing.
            }

            public void OnDestroy()
            {
            }

            internal class HVRVariableHolder
            {
                public HVRVariable variable;
                public ushort networkId;
                public object currentValue;
            }
        }
    }

    public enum HVRVariableTypeCode
    {
        Float = 1,
    }

    public class HVRVariable
    {
        public int addressId;
        public object initialValue; // This is not necessarily the default value, it is the value that was current when the variable was created on a specific remote; every user might have a different initialValue.
        public HVRVariableTypeCode variableTypeCode;

        // If float:
        public float min;
        public float max;
    }
}
