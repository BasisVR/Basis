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
        // These are limits put in place to try avoiding clients from sending an arbitrary number of variables and addresses containing an arbitrary length.
        private const int MaximumNumberOfNetworkedVariables = 8192;
        private const int MaximumNumberOfCharactersInAnAddress = 512;

        private const bool PrintDebug = false;
        private const bool PrintUnknownHighFrequencyEveryFrame = false;

        // 1/60 makes for a maximum encoded delta time of 4.25 seconds.
        private const float DeltaLocalIntToSeconds = 1 / 60f;
        // We use 254, not 255 (leaving 1 value out), because 254 divided by 2 is a round number, 127.
        // This makes the value of 0 in range [-1:1] encodable as 127.
        private const float EncodingRange = 254f;
        private const float FullRange = 255f;

        public HVRAvatarComms comms;
        public bool isWearer;
        public IHVRTransmitter transmitter;

        internal IHVRVariableBehaviour _behaviour;

        private void Awake() => _behaviour = isWearer ? new HVRVariableBehaviour_Wearer(this) : new HVRVariableBehaviour_Remote(this);
        private void OnEnable() => HVRCommsUpdateDriver.Register(this);
        private void OnDisable() => HVRCommsUpdateDriver.Unregister(this);
        internal void SimulateTick(float deltaTime) => _behaviour.Update(deltaTime);
        private void OnDestroy() => _behaviour.OnDestroy();

        public void RequireVariable(HVRVariable variable) => _behaviour.RequireVariable(variable);

        public void OnPacketReceived(byte localIdentifier, ArraySegment<byte> data) => _behaviour.OnPacketReceived(localIdentifier, data);
        public void OnResyncEveryoneRequested() => _behaviour.OnResyncEveryoneRequested();
        public void OnResyncRequested(ushort[] whoAsked) => _behaviour.OnResyncRequested(whoAsked);

        public float transmissionDeltaSeconds = 0.05f;
        private const float UpgradeAddressesDeltaSeconds = 5f;

        private static byte EncodeFloat(float value, HVRVariable variable)
        {
            var lerp01 = Mathf.InverseLerp(variable.min, variable.max, value);
            if (Mathf.Approximately(-variable.min, variable.max))
            {
                return (byte)(lerp01 * EncodingRange);
            }
            else
            {
                return (byte)(lerp01 * FullRange);
            }
        }

        private static float DecodeFloat(byte encodedByte, HVRVariableHighFrequency highFrequency)
        {
            return Mathf.Lerp(highFrequency.min, highFrequency.max, encodedByte * highFrequency.inverseRange);
        }

        internal interface IHVRVariableBehaviour : IFeatureReceiver
        {
            public void Update(float deltaTime);
            void RequireVariable(HVRVariable variable);
            void OnDestroy();
        }

        internal class HVRVariableBehaviour_Wearer : IHVRVariableBehaviour
        {
            private const DeliveryMethod MainDeliveryMethod = DeliveryMethod.ReliableSequenced;

            private readonly HVRVariableNetworking _state;
            private readonly AcquisitionService _acquisitionService;

            internal readonly Dictionary<int, HVRVariableHolder> _addressIdToHolder = new();
            private readonly List<int> _newVariablesAddressIds = new();
            private readonly HashSet<int> _addressIdsWithNewValue = new();
            private readonly List<int> _highFrequencyAddressIds = new();
            private readonly HashSet<int> _highFrequencyAddressIdsHashSet = new();
            private readonly List<int> _temp_addressIdsThatNeedUpgrade = new();
            private ushort _lastAddedNetworkId = 0;

            private float _timeLeftUpdateValues;
            private float _timeLeftUpgradeAddresses;
            private byte[] _highFrequencyBytes;
            private bool _needsUshortAddresses;

            public HVRVariableBehaviour_Wearer(HVRVariableNetworking state)
            {
                _state = state;
                _acquisitionService = AcquisitionService.SceneInstance;
            }

            public void OnPacketReceived(byte localIdentifier, ArraySegment<byte> data) { } // Not applicable
            public void OnResyncEveryoneRequested() => ResyncRequestedAny(null);
            public void OnResyncRequested(ushort[] whoAsked) => ResyncRequestedAny(whoAsked);

            private void ResyncRequestedAny(ushort[] whoAskedNullable)
            {
                if (_needsUshortAddresses) _state.transmitter.NetworkMessageSend(new[] { AvatarMessageProcessing.NewNet_WearerNeedsUshortForAddresses }, MainDeliveryMethod, whoAskedNullable);
                SubmitNewVariablesPacket(_addressIdToHolder.Keys.ToList(), "OnResyncRequested", whoAskedNullable);
                SubmitUpgradedVariables(_highFrequencyAddressIds, whoAskedNullable);
            }

            private void SubmitUpgradedVariables(List<int> highFrequencyAddressIds, ushort[] whoAskedNullable)
            {
                if (highFrequencyAddressIds.Count > 0)
                {
                    var upgradePacket = BuildUpgradePacket(highFrequencyAddressIds);
                    _state.transmitter.NetworkMessageSend(upgradePacket, MainDeliveryMethod, whoAskedNullable);
                    if (PrintDebug) HVRLogging.ProtocolDebug($"(Resync) Sending UpgradeFloatToHighFrequencyPacket (at T={Time.time:0.00}).");
                }
            }

            public void RequireVariable(HVRVariable variable)
            {
                if (_addressIdToHolder.TryGetValue(variable.addressId, out var existing))
                {
                    if (PrintDebug) HVRLogging.Debug($"Variable {HVRAddress.ResolveKnownAddressFromId(variable.addressId)} was already created, so we're extending it. This can happen if Vixxy and Face Tracking are sharing variables.");

                    // This happens when FaceTracking and Vixxy are listening to the same address (called once by Orchestrator, and once again by BlendshapeActuation/misc).
                    var holder = existing.variable;
                    if (variable.min < holder.min) holder.min = variable.min;
                    if (variable.max > holder.max) holder.max = variable.max;
                    if (variable.needsInterpolation) holder.needsInterpolation = variable.needsInterpolation;

                    return;
                }

                var addressName = HVRAddress.ResolveKnownAddressFromId(variable.addressId);
                if (addressName.Length > MaximumNumberOfCharactersInAnAddress)
                {
                    HVRLogging.LimitReached($"The name of the variable {addressName} is too long. Maximum is {MaximumNumberOfCharactersInAnAddress} characters. The variable will be ignored.");
                    return;
                }

                if (variable.variableTypeCode == HVRVariableTypeCode.Float)
                {
                    if (variable.initialValue is not float) throw new InvalidOperationException("Initial value does not match variable type code.");
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported variable type code {variable.variableTypeCode}.");
                }

                _acquisitionService.RegisterAddresses(new []{ variable.addressId }, OnAddressUpdated);

                _lastAddedNetworkId++;
                var currentValue = _state.comms.VariableStore.GetValue(variable.addressId);
                _addressIdToHolder.Add(variable.addressId, new HVRVariableHolder
                {
                    variable = variable,
                    networkId = _lastAddedNetworkId,
                    currentValue = currentValue,
                    lastTransmittedValue = currentValue,
                    valueWithGreatestDeltaSinceLastTransmittedValue = currentValue
                });

                _newVariablesAddressIds.Add(variable.addressId);

                if (_lastAddedNetworkId >= 256)
                {
                    HVRLogging.Debug("Addresses will be encoded as ushort instead of bytes.");
                    _needsUshortAddresses = true;
                }
                if (_addressIdToHolder.Count > MaximumNumberOfNetworkedVariables)
                {
                    HVRLogging.LimitReached($"There are too many networked variables. Currently registered {_addressIdToHolder.Count} variables. The receiver may ignore any variables beyond this point.");
                }
            }

            public void OnDestroy()
            {
                _acquisitionService.UnregisterAddresses(_addressIdToHolder.Keys.ToArray(), OnAddressUpdated);
            }

            private void OnAddressUpdated(int addressId, float value)
            {
                if (global::Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDebugCapture.Capture)
                {
                    System.Threading.Interlocked.Increment(ref global::Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDebugCapture.HvrWearerAddressUpdates);
                }
                if (_addressIdToHolder.TryGetValue(addressId, out var holder))
                {
                    if (holder.variable.variableTypeCode == HVRVariableTypeCode.Float && !Mathf.Approximately((float)holder.currentValue, value))
                    {
                        if (global::Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDebugCapture.Capture)
                        {
                            System.Threading.Interlocked.Increment(ref global::Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDebugCapture.HvrWearerNewValues);
                        }
                        _addressIdsWithNewValue.Add(addressId);
                        holder.numberOfUpdates += 1;
                        if (holder.numberOfUpdates == 100)
                        {
                            _temp_addressIdsThatNeedUpgrade.Add(addressId);
                        }

                        holder.currentValue = value;
                        if (Mathf.Abs((float)holder.lastTransmittedValue - value) > Mathf.Abs((float)holder.lastTransmittedValue - (float)holder.valueWithGreatestDeltaSinceLastTransmittedValue))
                        {
                            holder.valueWithGreatestDeltaSinceLastTransmittedValue = value;
                        }
                    }
                }
            }

            public void Update(float deltaTime)
            {
                if (_newVariablesAddressIds.Count > 0)
                {
                    SubmitNewVariablesPacket(_newVariablesAddressIds, "Update", null);
                    _newVariablesAddressIds.Clear();
                }

                // This runs every 5 seconds in general.
                _timeLeftUpgradeAddresses += deltaTime;
                if (_timeLeftUpgradeAddresses > UpgradeAddressesDeltaSeconds)
                {
                    UpgradeOrDowngradeAddressesIfNecessary();
                }

                // This runs every 0.1 seconds in general.
                _timeLeftUpdateValues += deltaTime;
                if (_timeLeftUpdateValues > _state.transmissionDeltaSeconds)
                {
                    DoTick(_timeLeftUpdateValues);
                    _timeLeftUpdateValues = 0;
                }
            }

            private void SubmitNewVariablesPacket(List<int> addressIds, string hook, ushort[] whoAskedNullable)
            {
                if (addressIds.Count > 0)
                {
                    var groupSize = 10;
                    for (var i = 0; i < addressIds.Count; i += groupSize)
                    {
                        var group = addressIds.Skip(i).Take(groupSize).ToList();
                        var packet = BuildNewVariablesPacket(group);
                        _state.transmitter.NetworkMessageSend(packet, MainDeliveryMethod, whoAskedNullable);
                        if (PrintDebug)
                        {
                            HVRLogging.ProtocolDebug($"({hook}) Sending NewVariablesPacket (group {i / groupSize + 1}).");
                        }
                    }
                }
                else
                {
                    var packet = BuildNewVariablesPacket(addressIds);
                    _state.transmitter.NetworkMessageSend(packet, MainDeliveryMethod, whoAskedNullable);
                    if (PrintDebug)
                    {
                        HVRLogging.ProtocolDebug($"({hook}) Sending NewVariablesPacket (empty).");
                    }
                }
            }

            private readonly HashSet<int> L_addressIdsThatNeedToBeResentLater = new(); // is field due to PR guidelines
            private readonly Dictionary<int, object> L_addressIdsToValueToTransmit = new(); // is field due to PR guidelines
            private void DoTick(float deltaTimeSinceLastTick)
            {
                if (global::Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDebugCapture.Capture)
                {
                    System.Threading.Interlocked.Increment(ref global::Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDebugCapture.HvrWearerTicks);
                    if (_addressIdsWithNewValue.Count > 0)
                    {
                        System.Threading.Interlocked.Increment(ref global::Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDebugCapture.HvrWearerTicksWithValues);
                    }
                }
                if (_addressIdsWithNewValue.Count == 0) return;

                L_addressIdsThatNeedToBeResentLater.Clear();
                L_addressIdsToValueToTransmit.Clear();

                foreach (var addressId in _addressIdsWithNewValue)
                {
                    var holder = _addressIdToHolder[addressId];
                    var currentValue = holder.currentValue;

                    // Reminder: We network the value with the greatest delta, which is not necessarily the current value.
                    // Networking the value with the greatest delta helps networking short-lived events such as the eyes blinking.
                    var valueToBeTransmitted = holder.valueWithGreatestDeltaSinceLastTransmittedValue;
                    L_addressIdsToValueToTransmit.Add(addressId, valueToBeTransmitted);

                    holder.lastTransmittedValue = valueToBeTransmitted;
                    holder.valueWithGreatestDeltaSinceLastTransmittedValue = currentValue;

                    if (holder.variable.variableTypeCode == HVRVariableTypeCode.Float && !Mathf.Approximately((float)currentValue, (float)valueToBeTransmitted))
                    {
                        // If the value with the greatest delta is not the current value, then we need to transmit the current value
                        // (which is now stored inside valueWithGreatestDeltaSinceLastTransmittedValue) next frame.
                        L_addressIdsThatNeedToBeResentLater.Add(addressId);
                    }
                }

                if (L_addressIdsToValueToTransmit.Count > 0)
                {
                    var packet = BuildUpdatedVariablesPacketOrNull(L_addressIdsToValueToTransmit, deltaTimeSinceLastTick);
                    if (packet != null)
                    {
                        _state.transmitter.NetworkMessageSend(packet, MainDeliveryMethod);
                        if (PrintDebug) HVRLogging.ProtocolDebug($"(Update) Sending UpdatedVariablesPacket (at T={Time.time:0.00}).");
                    }

                    if (_highFrequencyAddressIdsHashSet.Count > 0 && _highFrequencyAddressIdsHashSet.Overlaps(L_addressIdsToValueToTransmit.Keys))
                    {
                        _highFrequencyBytes[1] = (byte)(deltaTimeSinceLastTick / DeltaLocalIntToSeconds);

                        for (var index = 0; index < _highFrequencyAddressIds.Count; index++)
                        {
                            var addressId = _highFrequencyAddressIds[index];
                            var holder = _addressIdToHolder[addressId];
                            if (holder.variable.variableTypeCode != HVRVariableTypeCode.Float) continue;

                            if (L_addressIdsToValueToTransmit.TryGetValue(addressId, out var value))
                            {
                                _highFrequencyBytes[index + 2] = EncodeFloat((float)value, holder.variable);
                            }
                            else
                            {
                                _highFrequencyBytes[index + 2] = EncodeFloat((float)holder.currentValue, holder.variable);
                            }
                        }

                        _state.transmitter.ServerReductionSystemMessageSend(_highFrequencyBytes);
                    }
                }

                _addressIdsWithNewValue.Clear();
                _addressIdsWithNewValue.UnionWith(L_addressIdsThatNeedToBeResentLater);
            }

            private readonly List<int> L_addressIdsToUpgradeInOrder = new(); // is field due to PR guidelines
            private void UpgradeOrDowngradeAddressesIfNecessary()
            {
                L_addressIdsToUpgradeInOrder.Clear();

                foreach (var addressId in _temp_addressIdsThatNeedUpgrade)
                {
                    if (!_highFrequencyAddressIdsHashSet.Contains(addressId))
                    {
                        HVRLogging.Debug($"Upgrading address {HVRAddress.ResolveKnownAddressFromId(addressId)} to high frequency.");
                        L_addressIdsToUpgradeInOrder.Add(addressId);
                    }
                }
                _temp_addressIdsThatNeedUpgrade.Clear();

                if (L_addressIdsToUpgradeInOrder.Count > 0)
                {
                    _highFrequencyAddressIds.AddRange(L_addressIdsToUpgradeInOrder);
                    _highFrequencyAddressIdsHashSet.UnionWith(L_addressIdsToUpgradeInOrder);
                    _highFrequencyBytes = new byte[2 + _highFrequencyAddressIds.Count];
                    _highFrequencyBytes[0] = AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedHighFrequencyVariables;

                    var upgradePacket = BuildUpgradePacket(L_addressIdsToUpgradeInOrder);
                    _state.transmitter.NetworkMessageSend(upgradePacket, MainDeliveryMethod);
                    if (PrintDebug) HVRLogging.ProtocolDebug($"(Update) Sending UpgradeFloatToHighFrequencyPacket (at T={Time.time:0.00}).");
                }
            }

            private byte[] BuildUpgradePacket(List<int> addressIdsToUpgrade)
            {
                if (PrintDebug) HVRLogging.ProtocolDebug("(BuildUpgradePacket) Building an UpgradeFloatToHighFrequency packet.");

                return new HVRPacket_UpgradeFloatToHighFrequency
                {
                    items = addressIdsToUpgrade.Select(addressId =>
                    {
                        var holder = _addressIdToHolder[addressId];
                        return new HVRPacket_UpgradeFloatToHighFrequency.Inner_Item
                        {
                            networkId = holder.networkId,
                            min = holder.variable.min,
                            max = holder.variable.max,
                        };
                    }).ToList()
                }.Serialize(_needsUshortAddresses);
            }

            private readonly List<ushort> L_zeroesNetworkIds = new(); // is field due to PR guidelines
            private readonly List<ushort> L_onesNetworkIds = new(); // is field due to PR guidelines
            private readonly List<int> L_otherAddressIds = new(); // is field due to PR guidelines
            private readonly List<ushort> L_tempListSerializedImmediately = new List<ushort>(); // is field due to PR guidelines
            private readonly List<HVRPacket_UpdatedVariables_Mixed.Inner_UpdatedValue> L_tempOtherSerializedImmediately = new(); // is field due to PR guidelines
            private byte[] BuildUpdatedVariablesPacketOrNull(Dictionary<int, object> addressIdsToValueToTransmit, float deltaTimeSinceLastTick)
            {
                var deltaLocalIntToSeconds = (int)(deltaTimeSinceLastTick / DeltaLocalIntToSeconds);
                if (deltaLocalIntToSeconds > byte.MaxValue) deltaLocalIntToSeconds = byte.MaxValue;

                var timingSteps = (byte)deltaLocalIntToSeconds;

                // In our system, we currently only handle floats (this may change in the future to support strings and Color).
                // We do not handle booleans. Instead, this is what happens:
                // The float values of 0.0 and 1.0 are considered to be special. Instead of networking the value of 0.0 and 1.0,
                // we transmit a list of networkIds for those zeroes and ones and using four different packet types.
                // Only values that change are transmitted, so we do not deal with bitfields or anything like that.
                L_zeroesNetworkIds.Clear();
                L_onesNetworkIds.Clear();
                L_otherAddressIds.Clear();

                foreach (var (addressId, value) in addressIdsToValueToTransmit)
                {
                    if (_highFrequencyAddressIdsHashSet.Contains(addressId)) continue;

                    var networkId = _addressIdToHolder[addressId].networkId;
                    if (value is float f)
                    {
                        if (Mathf.Approximately(f, 0f))
                        {
                            L_zeroesNetworkIds.Add(networkId);
                        }
                        else if (Mathf.Approximately(f, 1f))
                        {
                            L_onesNetworkIds.Add(networkId);
                        }
                        else
                        {
                            L_otherAddressIds.Add(addressId);
                        }
                    }
                    else
                    {
                        L_otherAddressIds.Add(addressId);
                    }
                }

                if (L_zeroesNetworkIds.Count == 0 && L_onesNetworkIds.Count == 0 && L_otherAddressIds.Count == 0)
                {
                    return null;
                }

                if (L_otherAddressIds.Count > 0)
                {
                    if (PrintDebug) HVRLogging.ProtocolDebug("(BuildUpdatedVariablesPacket) Building a UpdatedVariables_Mixed packet.");

                    L_tempListSerializedImmediately.Clear();
                    L_tempListSerializedImmediately.AddRange(L_zeroesNetworkIds);
                    L_tempListSerializedImmediately.AddRange(L_onesNetworkIds);
                    L_tempOtherSerializedImmediately.Clear();
                    for (var i = 0; i < L_otherAddressIds.Count; i++)
                    {
                        var addressId = L_otherAddressIds[i];
                        L_tempOtherSerializedImmediately.Add(new HVRPacket_UpdatedVariables_Mixed.Inner_UpdatedValue
                        {
                            networkId = _addressIdToHolder[addressId].networkId,
                            value = addressIdsToValueToTransmit[addressId]
                        });
                    }
                    return new HVRPacket_UpdatedVariables_Mixed
                    {
                        timingSteps = timingSteps,
                        numberOfZeroes = (ushort)L_zeroesNetworkIds.Count,
                        networkIds = L_tempListSerializedImmediately,
                        other = L_tempOtherSerializedImmediately
                    }.Serialize(_needsUshortAddresses);
                }

                if (L_zeroesNetworkIds.Count > 0 && L_onesNetworkIds.Count > 0)
                {
                    L_tempListSerializedImmediately.Clear();
                    L_tempListSerializedImmediately.AddRange(L_zeroesNetworkIds);
                    L_tempListSerializedImmediately.AddRange(L_onesNetworkIds);

                    if (PrintDebug) HVRLogging.ProtocolDebug("(BuildUpdatedVariablesPacket) Building a UpdatedVariables_ZeroesAndOnes packet.");
                    return new HVRPacket_UpdatedVariables_ZeroesAndOnes
                    {
                        timingSteps = timingSteps,
                        numberOfZeroes = (ushort)L_zeroesNetworkIds.Count,
                        networkIds = L_tempListSerializedImmediately
                    }.Serialize(_needsUshortAddresses);
                }

                if (PrintDebug) HVRLogging.ProtocolDebug($"(BuildUpdatedVariablesPacket) Building a {(L_zeroesNetworkIds.Count > 0 ? "UpdatedVariables_Zeroes" : "UpdatedVariables_Ones")} packet.");
                L_tempListSerializedImmediately.Clear();
                L_tempListSerializedImmediately.AddRange(L_zeroesNetworkIds.Count > 0 ? L_zeroesNetworkIds : L_onesNetworkIds);
                return new HVRPacket_UpdatedVariables_ZeroesOrOnes
                {
                    timingSteps = timingSteps,
                    packetType = L_zeroesNetworkIds.Count > 0 ? AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Zeroes : AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Ones,
                    networkIds = L_tempListSerializedImmediately
                }.Serialize(_needsUshortAddresses);
            }

            private byte[] BuildNewVariablesPacket(List<int> newVariablesAddressIds)
            {
                var other = new List<HVRPacket_NewVariables.Inner_NewVariable>();
                var zeroes = new List<HVRPacket_NewVariables.Inner_NewQuickVariable>();
                var ones = new List<HVRPacket_NewVariables.Inner_NewQuickVariable>();

                for (var i = 0; i < newVariablesAddressIds.Count; i++)
                {
                    var holder = _addressIdToHolder[newVariablesAddressIds[i]];
                    var isFloat = holder.variable.variableTypeCode == HVRVariableTypeCode.Float;
                    if (isFloat
                        && (Mathf.Approximately((float)holder.currentValue, 0f)
                            || Mathf.Approximately((float)holder.currentValue, 1f)))
                    {
                        var quickVar = new HVRPacket_NewVariables.Inner_NewQuickVariable
                        {
                            address = HVRAddress.ResolveKnownAddressFromId(holder.variable.addressId),
                            networkId = holder.networkId,
                            needsInterpolation = holder.variable.needsInterpolation,
                        };
                        (Mathf.Approximately((float)holder.currentValue, 1f) ? ones : zeroes)
                            .Add(quickVar);
                    }
                    else
                    {
                        other.Add(new HVRPacket_NewVariables.Inner_NewVariable
                        {
                            address = HVRAddress.ResolveKnownAddressFromId(holder.variable.addressId),
                            networkId = holder.networkId,
                            needsInterpolation = holder.variable.needsInterpolation,
                            variableTypeCode = (byte)holder.variable.variableTypeCode,
                            initialValue = holder.currentValue
                        });
                    }
                }

                return new HVRPacket_NewVariables
                {
                    newGeneralVariables = other,
                    floatZero = zeroes,
                    floatOne = ones,
                }.Serialize(_needsUshortAddresses);
            }

            internal class HVRVariableHolder
            {
                public HVRVariable variable;
                public ushort networkId;
                public object currentValue;
                public object lastTransmittedValue;
                public object valueWithGreatestDeltaSinceLastTransmittedValue;
                public int numberOfUpdates;
            }
        }

        internal class HVRVariableBehaviour_Remote : IHVRVariableBehaviour
        {
            private const bool UseInterpolationTape = true;

            private readonly HVRVariableNetworking _state;
            internal readonly Dictionary<int, HVRVariableHolder> _addressIdToHolder = new();
            private readonly Dictionary<ushort, int> _networkIdToAddressId = new();
            private readonly List<HVRVariableHighFrequency> _upgradedToHighFrequencyInOrder = new();
            private readonly HashSet<ushort> _upgradedHighFrequencyNetworkIds = new();

            // Unknown high-frequency network IDs already reported, so a protocol-mismatched remote
            // is logged once per ID instead of every packet (which would spam the log at the send rate).
            private readonly HashSet<ushort> _reportedUnknownHighFrequencyNetworkIds = new();

            private readonly HVRInterpolator _lowFrequencyInterpolator = new(true);
            private readonly HVRInterpolator _highFrequencyInterpolator = new(true);

            private HVRInterpolationSnapshot _pendingLowFrequencySnapshot;
            private bool _needsUshortAddresses;

            public HVRVariableBehaviour_Remote(HVRVariableNetworking state)
            {
                _state = state;
            }

            private void AfterDataReceived(float deltaTime)
            {
                if (_pendingLowFrequencySnapshot != null)
                {
                    _pendingLowFrequencySnapshot.deltaTime = deltaTime;
                    _lowFrequencyInterpolator.Add(_pendingLowFrequencySnapshot);
                    _pendingLowFrequencySnapshot = null;
                }
            }

            private void WhenDataReceived(int addressId, float currentValue)
            {
                if (PrintDebug) HVRLogging.ProtocolDebug($"Received data for address {HVRAddress.ResolveKnownAddressFromId(addressId)} with value {currentValue}.");

                // TODO: IF APPLICABLE (address doesn't have a "no delay" flag + controls need to be able to define if it uses network interpolation), then:
                // - Put this data into the proper interpolation tape for that address, for that tick (we need to add the time delta inside the packet),
                // - then on the remote, play back the tape every frame on Update.
                // - QUESTION: Should the variable store be responsible for the interpolation tape?
                // --------------- NO. Interpolation is handled by the networking module.
                // :
                // -> When value is received, append to the tape with the delay.
                // -> The Variable Networking keeps tracks of the addresses that have a non-empty interpolation tape.
                // -> Every frame, Variable Networking advance the non-empty tapes by (delaySinceLastFrame)
                // -> the Variable Networking then emits Submit events with the new interpolated value to the Value Store.
                // :
                // If it's exposed as a slider in a MENU ITEM, then it MUST be interpolated
                // --> We need to mark the control itself as interpolated. Sliders must suggest to mark the control as interpolated.
                // Toggles SHOULD NOT BE interpolated.
                // Multiple choices SHOULD NOT BE interpolated.
                // --> Toggles and multiple choices should suggest to mark the control as non-interpolated.

                if (UseInterpolationTape)
                {
                    // Each packet needs its own snapshot; AfterDataReceived hands it to the interpolator and resets this to null.
                    _pendingLowFrequencySnapshot ??= _lowFrequencyInterpolator.RentSnapshot();
                    if (_addressIdToHolder[addressId].variable.needsInterpolation)
                    {
                        _pendingLowFrequencySnapshot.addressIdsToValues[addressId] = currentValue;
                    }
                    else
                    {
                        _state.comms.VariableStore.Submit(addressId, currentValue);
                    }
                }
                else
                {
                    _state.comms.VariableStore.Submit(addressId, currentValue);
                }
            }

            private void WhenDataReceived_BypassInterpolationTape(int addressId, float currentValue)
            {
                _submitGates.Remove(addressId);
                _state.comms.VariableStore.Submit(addressId, currentValue);
            }

            private readonly Dictionary<int, float> L_result = new(); // is field due to PR guidelines
            public void Update(float deltaTime)
            {
                if (UseInterpolationTape)
                {
                    if (_lowFrequencyInterpolator.HasPendingWork)
                    {
                        SubmitToVariableStore(_lowFrequencyInterpolator.Advance(deltaTime, L_result));
                    }
                    if (_highFrequencyInterpolator.HasPendingWork)
                    {
                        SubmitToVariableStore(_highFrequencyInterpolator.Advance(deltaTime, L_result));
                    }
                }
            }

            private struct SubmitGate
            {
                public float LastValue;
                public float Epsilon;
            }

            private readonly Dictionary<int, SubmitGate> _submitGates = new();

            private void SubmitToVariableStore(Dictionary<int, float> advance)
            {
                foreach (var (addressId, value) in advance)
                {
                    if (_submitGates.TryGetValue(addressId, out var gate))
                    {
                        if (Mathf.Abs(value - gate.LastValue) <= gate.Epsilon) continue;
                        gate.LastValue = value;
                        _submitGates[addressId] = gate;
                    }
                    else
                    {
                        var epsilon = 0f;
                        if (_addressIdToHolder.TryGetValue(addressId, out var holder) && holder.variable != null)
                        {
                            epsilon = Mathf.Abs(holder.variable.max - holder.variable.min) * (0.5f / FullRange);
                        }
                        _submitGates[addressId] = new SubmitGate { LastValue = value, Epsilon = epsilon };
                    }
                    _state.comms.VariableStore.Submit(addressId, value);
                }
            }

            public void OnPacketReceived(byte localIdentifier, ArraySegment<byte> data)
            {
                if (data.Count < 1) { HVRLogging.ProtocolError("Data buffer is empty."); return; }

                var packetType = data[0];
                switch (packetType)
                {
                    case AvatarMessageProcessing.NewNet_WearerNeedsUshortForAddresses:
                    {
                        if (data.Count != 1)
                        {
                            HVRLogging.ProtocolError("Packet for WearerNeedsUshortForAddresses exceeds expected size");
                            return;
                        }

                        if (PrintDebug) HVRLogging.ProtocolDebug("(OnPacketReceived) Receiving NeedsUshortForAddresses packet.");
                        _needsUshortAddresses = true;

                        break;
                    }
                    case AvatarMessageProcessing.NewNet_WearerSubmitsNewVariables:
                    {
                        if (_networkIdToAddressId.Count > MaximumNumberOfNetworkedVariables)
                        {
                            HVRLogging.ProtocolError($"We have received a NewVariables packet, but we already have too many networked variables ({_networkIdToAddressId.Count} > {MaximumNumberOfNetworkedVariables}) received so far. We will ignore any new ones because we can't store all of them.");
                            return;
                        }

                        if (!HVRPacket_NewVariables.TryDeserialize(_needsUshortAddresses, data, out var packet))
                        {
                            HVRLogging.ProtocolError("Failed to deserialize NewVariables packet.");
                            return;
                        }

                        if (PrintDebug) HVRLogging.ProtocolDebug("(OnPacketReceived) Receiving NewVariables packet.");
                        WhenNewVariablesReceived(packet);
                        break;
                    }
                    case AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Zeroes:
                    {
                        if (!HVRPacket_UpdatedVariables_ZeroesOrOnes.TryDeserialize(_needsUshortAddresses, data, packetType, out var packet))
                        {
                            HVRLogging.ProtocolError("Failed to deserialize UpdatedVariables_Zeroes packet.");
                            return;
                        }

                        if (PrintDebug) HVRLogging.ProtocolDebug("(OnPacketReceived) Receiving UpdatedVariables_Zeroes packet.");
                        foreach (var networkId in packet.networkIds)
                        {
                            if (_networkIdToAddressId.TryGetValue(networkId, out var addressId))
                            {
                                _addressIdToHolder[addressId].currentValue = 0f;
                                WhenDataReceived(addressId, 0f);
                            }
                        }
                        AfterDataReceived(packet.timingSteps * DeltaLocalIntToSeconds);

                        break;
                    }
                    case AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Ones:
                    {
                        if (!HVRPacket_UpdatedVariables_ZeroesOrOnes.TryDeserialize(_needsUshortAddresses, data, packetType, out var packet))
                        {
                            HVRLogging.ProtocolError("Failed to deserialize UpdatedVariables_Ones packet.");
                            return;
                        }

                        if (PrintDebug) HVRLogging.ProtocolDebug("(OnPacketReceived) Receiving UpdatedVariables_Ones packet.");
                        foreach (var networkId in packet.networkIds)
                        {
                            if (_networkIdToAddressId.TryGetValue(networkId, out var addressId))
                            {
                                _addressIdToHolder[addressId].currentValue = 1f;
                                WhenDataReceived(addressId, 1f);
                            }
                        }
                        AfterDataReceived(packet.timingSteps * DeltaLocalIntToSeconds);

                        break;
                    }
                    case AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_ZeroesAndOnes:
                    {
                        if (!HVRPacket_UpdatedVariables_ZeroesAndOnes.TryDeserialize(_needsUshortAddresses, data, out var packet))
                        {
                            HVRLogging.ProtocolError("Failed to deserialize UpdatedVariables_ZeroesAndOnes packet.");
                            return;
                        }

                        if (PrintDebug) HVRLogging.ProtocolDebug("(OnPacketReceived) Receiving UpdatedVariables_ZeroesAndOnes packet.");
                        for (var index = 0; index < packet.networkIds.Count; index++)
                        {
                            if (_networkIdToAddressId.TryGetValue(packet.networkIds[index], out var addressId))
                            {
                                var isZero = index < packet.numberOfZeroes;
                                var value = isZero ? 0f : 1f;
                                _addressIdToHolder[addressId].currentValue = value;
                                WhenDataReceived(addressId, value);
                            }
                        }
                        AfterDataReceived(packet.timingSteps * DeltaLocalIntToSeconds);

                        break;
                    }
                    case AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Mixed:
                    {
                        if (!HVRPacket_UpdatedVariables_Mixed.TryDeserialize(_needsUshortAddresses, data, out var packet))
                        {
                            HVRLogging.ProtocolError("Failed to deserialize UpdatedVariables_Mixed packet.");
                            return;
                        }

                        if (PrintDebug) HVRLogging.ProtocolDebug("(OnPacketReceived) Receiving UpdatedVariables_Mixed packet.");
                        for (var index = 0; index < packet.networkIds.Count; index++)
                        {
                            if (_networkIdToAddressId.TryGetValue(packet.networkIds[index], out var addressId))
                            {
                                var isZero = index < packet.numberOfZeroes;
                                var value = isZero ? 0f : 1f;
                                _addressIdToHolder[addressId].currentValue = value;
                                WhenDataReceived(addressId, value);
                            }
                        }
                        foreach (var other in packet.other)
                        {
                            if (_networkIdToAddressId.TryGetValue(other.networkId, out var addressId))
                            {
                                if (_addressIdToHolder[addressId].variable.variableTypeCode == HVRVariableTypeCode.Float && other.value is float f)
                                {
                                    _addressIdToHolder[addressId].currentValue = f;
                                    WhenDataReceived(addressId, f);
                                }
                            }
                        }
                        AfterDataReceived(packet.timingSteps * DeltaLocalIntToSeconds);

                        break;
                    }
                    case AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedHighFrequencyVariables:
                    {
                        if (data.Count < 2)
                        {
                            HVRLogging.ProtocolError("Packet for UpdatedHighFrequencyVariables is below expected size.");
                            return;
                        }

                        // This runs at the send rate for every remote, so it is parsed straight off the segment with no packet object.
                        var buffer = data.Array;
                        var valuesOffset = data.Offset + 2;
                        var snapshot = _highFrequencyInterpolator.RentSnapshot();
                        snapshot.deltaTime = buffer[data.Offset + 1] * DeltaLocalIntToSeconds;

                        var valueCount = data.Count - 2;
                        if (valueCount > _upgradedToHighFrequencyInOrder.Count) valueCount = _upgradedToHighFrequencyInOrder.Count;
                        for (var index = 0; index < valueCount; index++)
                        {
                            var highFrequency = _upgradedToHighFrequencyInOrder[index];
                            if (!highFrequency.addressResolved)
                            {
                                if (_networkIdToAddressId.TryGetValue(highFrequency.networkId, out var addressId))
                                {
                                    highFrequency.addressId = addressId;
                                    highFrequency.addressResolved = true;
                                }
                                else
                                {
                                    if (PrintUnknownHighFrequencyEveryFrame || _reportedUnknownHighFrequencyNetworkIds.Add(highFrequency.networkId))
                                    {
                                        HVRLogging.ProtocolWarning($"Network ID {highFrequency.networkId} is not known. High frequency value will be ignored.");
                                    }
                                    continue;
                                }
                            }
                            snapshot.addressIdsToValues[highFrequency.addressId] = DecodeFloat(buffer[valuesOffset + index], highFrequency);
                        }
                        _highFrequencyInterpolator.Add(snapshot);

                        break;
                    }
                    case AvatarMessageProcessing.NewNet_WearerUpgradesFloatToHighFrequency:
                    {
                        if (!HVRPacket_UpgradeFloatToHighFrequency.TryDeserialize(_needsUshortAddresses, data, out var packet))
                        {
                            HVRLogging.ProtocolError("Failed to deserialize UpgradeFloatToHighFrequency packet.");
                            return;
                        }

                        if (PrintDebug) HVRLogging.ProtocolDebug($"(Update) Received UpgradeFloatToHighFrequencyPacket (at T={Time.time:0.00}).");
                        var newlyAdded = packet.items.Select(item => new HVRVariableHighFrequency
                        {
                            networkId = item.networkId,
                            min = item.min,
                            max = item.max,
                            inverseRange = 1f / (Mathf.Approximately(-item.min, item.max) ? EncodingRange : FullRange),
                        }).ToList();
                        foreach (var highFrequency in newlyAdded)
                        {
                            if (!_networkIdToAddressId.TryGetValue(highFrequency.networkId, out var addressId))
                            {
                                if (PrintUnknownHighFrequencyEveryFrame || _reportedUnknownHighFrequencyNetworkIds.Add(highFrequency.networkId))
                                {
                                    HVRLogging.ProtocolWarning($"Network ID {highFrequency.networkId} is not known. Reading from the server reduction will be mangled.");
                                }
                                continue;
                            }

                            _addressIdToHolder[addressId].variable.min = highFrequency.min;
                            _addressIdToHolder[addressId].variable.max = highFrequency.max;
                            _submitGates.Remove(addressId);
                        }
                        foreach (var highFrequency in newlyAdded)
                        {
                            if (_upgradedHighFrequencyNetworkIds.Add(highFrequency.networkId))
                            {
                                _upgradedToHighFrequencyInOrder.Add(highFrequency);
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

            private void WhenNewVariablesReceived(HVRPacket_NewVariables packet)
            {
                var newlyAddedAddresses = new List<int>();

                foreach (var variable in packet.newGeneralVariables)
                {
                    if (variable.address.Length > MaximumNumberOfCharactersInAnAddress)
                    {
                        HVRLogging.ProtocolError("A variable we received has a name which is too long. We will ignore it.");
                        continue;
                    }

                    var addressId = HVRAddress.AddressToId(variable.address);
                    _addressIdToHolder[addressId] = new HVRVariableHolder
                    {
                        variable = new HVRVariable
                        {
                            addressId = addressId,
                            variableTypeCode = (HVRVariableTypeCode)variable.variableTypeCode,
                            initialValue = (float)variable.initialValue,
                            needsInterpolation = variable.needsInterpolation,
                        },
                        networkId = variable.networkId,
                        currentValue = (float)variable.initialValue
                    };

                    _networkIdToAddressId[variable.networkId] = addressId;
                    newlyAddedAddresses.Add(addressId);
                }

                foreach (var variable in packet.floatZero)
                {
                    if (variable.address.Length > MaximumNumberOfCharactersInAnAddress)
                    {
                        HVRLogging.ProtocolError("A variable we received has a name which is too long. We will ignore it.");
                        continue;
                    }

                    var addressId = HVRAddress.AddressToId(variable.address);
                    _addressIdToHolder[addressId] = new HVRVariableHolder
                    {
                        variable = new HVRVariable
                        {
                            addressId = addressId,
                            variableTypeCode = HVRVariableTypeCode.Float,
                            initialValue = 0f,
                            needsInterpolation = variable.needsInterpolation,
                        },
                        networkId = variable.networkId,
                        currentValue = 0f
                    };

                    _networkIdToAddressId[variable.networkId] = addressId;
                    newlyAddedAddresses.Add(addressId);
                }

                foreach (var variable in packet.floatOne)
                {
                    if (variable.address.Length > MaximumNumberOfCharactersInAnAddress)
                    {
                        HVRLogging.ProtocolError("A variable we received has a name which is too long. We will ignore it.");
                        continue;
                    }

                    var addressId = HVRAddress.AddressToId(variable.address);
                    _addressIdToHolder[addressId] = new HVRVariableHolder
                    {
                        variable = new HVRVariable
                        {
                            addressId = addressId,
                            variableTypeCode = HVRVariableTypeCode.Float,
                            initialValue = 1f,
                            needsInterpolation = variable.needsInterpolation,
                        },
                        networkId = variable.networkId,
                        currentValue = 1f
                    };

                    _networkIdToAddressId[variable.networkId] = addressId;
                    newlyAddedAddresses.Add(addressId);
                }

                foreach (var newlyAddedAddress in newlyAddedAddresses)
                {
                    WhenDataReceived_BypassInterpolationTape(newlyAddedAddress, (float)_addressIdToHolder[newlyAddedAddress].currentValue);
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

    internal class HVRVariableHighFrequency
    {
        public ushort networkId;
        public float min;
        public float max;
        public float inverseRange;
        public int addressId;
        public bool addressResolved;
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

        public bool needsInterpolation;

        // If float:
        public float min;
        public float max;
    }
}
