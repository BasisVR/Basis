using Basis.Scripts.BasisSdk;
using System;
using System.Collections;
using System.Collections.Generic;
using Basis.Scripts.Behaviour;
using Basis.Scripts.Networking.Behaviour;
using Basis.Network.Core;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Internal/HVR Avatar Comms")]
    [HelpURL("https://docs.hai-vr.dev/docs/basis/avatar-customization")]
    public class HVRAvatarComms : BasisNetworkAvatarBehaviour
    {
        private const int AvatarMessageProcessingCarrier0 = 0;
        private const int VariableNetworkingCarrier = 1;

        new public static bool VisibleInAvatarMenu = false;
        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [SerializeField] private bool isFromPrefab = false;

        public HVRVariableStore VariableStore { get; private set; }

        private readonly Nethack _nethack;

        private bool _isWearer;

        internal readonly List<MutualizedInterpolationRangeStorage> _ranges = new();
        private readonly List<HVRNeedsInterpolationCallback> _needsInterpolation = new();
        private readonly List<HVRToSubmitLater> _toStoreLater = new();

        private AvatarMessageProcessing avatarMessageProcessing;
        internal StreamedAvatarFeature _streamedLateInit;

        private AvatarMessageProcessing variableNetworkingProcessing;
        private HVRVariableNetworking _variableNetworking;

        public HVRAvatarComms()
        {
            _nethack = new Nethack(OnReadyBothAvatarAndNetwork);
        }

        private void Awake()
        {
            if (!isFromPrefab)
            {
                Destroy(this);
                return;
            }
            if (avatar == null)
            {
                avatar = HVRCommsUtil.GetAvatar(this);
            }
            if (avatar == null)
            {
                throw new InvalidOperationException("Broke assumption: Avatar cannot be found.");
            }

            avatar.OnAvatarReady += OnAvatarReady;
        }

        private void OnAvatarReady(bool isWearer)
        {
            _isWearer = isWearer;
            if (isWearer)
            {
                VariableStore = AcquisitionService.SceneInstance.VariableStore;
            }
            else
            {
                VariableStore = new HVRVariableStore();
            }


            var allInitializables = avatar.GetComponentsInChildren<IHVRInitializable>(true);
            foreach (var initializable in allInitializables)
            {
                initializable.OnHVRAvatarReady(isWearer);
            }

            _nethack.AfterAvatarReady();
        }

        public override void OnNetworkReady(bool isLocallyOwned)
        {
            _nethack.AfterNetworkReady(isLocallyOwned);
        }

        private void OnReadyBothAvatarAndNetwork(bool isWearer)
        {
            var carriers = avatar.GetComponentsInChildren<HVRNetworkingCarrier>(true);
            if (carriers.Length < 5)
            {
                throw new InvalidOperationException("Broke assumption: At least 5 Networking Carriers are required.");
            }

            for (var index = 0; index < carriers.Length; index++)
            {
                var carrier = carriers[index];
                carrier.index = index;
            }

            var holder = new GameObject("Generated__VariableNetworking")
            {
                transform = { parent = avatar.transform }
            };
            holder.SetActive(false);
            _variableNetworking = holder.AddComponent<HVRVariableNetworking>();
            _variableNetworking.isWearer = isWearer;
            _variableNetworking.comms = this;
            _variableNetworking.transmitter = carriers[VariableNetworkingCarrier];
            holder.SetActive(true);

            var allInitializables = avatar.GetComponentsInChildren<IHVRInitializable>(true);
            foreach (var initializable in allInitializables)
            {
                initializable.OnHVRReadyBothAvatarAndNetwork(isWearer);
            }

            DeclareMutualizedInterpolator(isWearer, carriers[AvatarMessageProcessingCarrier0]);

            variableNetworkingProcessing = AvatarMessageProcessing.ForFeature(carriers[VariableNetworkingCarrier], isWearer, avatar.LinkedPlayerID, _variableNetworking, true);
        }

        public void RequireVariable(HVRVariable variable)
        {
            if (_variableNetworking == null) throw new InvalidOperationException("Broke assumption: VariableNetworking is not yet initialized, it may have been called before OnHVRReadyBothAvatarAndNetwork was called?");

            _variableNetworking.RequireVariable(variable);
        }

        private void DeclareMutualizedInterpolator(bool isWearer, HVRNetworkingCarrier carrier)
        {
            var holder = new GameObject("Streamed-Mutualized")
            {
                transform = { parent = avatar.transform }
            };
            holder.SetActive(false);
            _streamedLateInit = holder.AddComponent<StreamedAvatarFeature>();
            _streamedLateInit.valueArraySize = (byte)_ranges.Count; // TODO: Sanitize count to be within bounds
            _streamedLateInit.transmitter = carrier;
            _streamedLateInit.isWearer = isWearer;
            _streamedLateInit.localIdentifier = 0;
            var pendingStores = _toStoreLater.ToArray();
            holder.SetActive(true);
            _streamedLateInit.InitializeNormalizedValues(BuildNeutralNormalizedValues());
            // StreamedAvatarFeature only gets the ability to store data AFTER Awake() runs, so order matters here.
            foreach (var toStoreLater in pendingStores)
            {
                var mutualizedIndex = toStoreLater.mutualizedIndex;
                _streamedLateInit.Store(mutualizedIndex, _ranges[mutualizedIndex].AbsoluteToRange(toStoreLater.absolute));
            }
            _toStoreLater.Clear();

            _streamedLateInit.OnInterpolatedDataChanged += mutualizedData =>
            {
                foreach (var callback in _needsInterpolation)
                {
                    for (var ours = 0; ours < callback.floats.Length; ours++)
                    {
                        var mutualizedIndex = callback.oursToMutualizedIndex[ours];
                        var streamed01 = mutualizedData[mutualizedIndex];
                        var absolute = _ranges[mutualizedIndex].RangeToAbsolute(streamed01);
                        callback.floats[ours] = absolute;
                    }

                    callback.callback(callback.floats);
                }
            };

            avatarMessageProcessing = AvatarMessageProcessing.ForFeature(carrier, isWearer, avatar.LinkedPlayerID, new HVRRedirectToStreamed(_streamedLateInit));

            StartCoroutine(SendInitialPacketNextFrame());
        }

        IEnumerator SendInitialPacketNextFrame()
        {
            // We want to send the initial packet when all BasisAvatarMonoBehaviours have been initialized.
            yield return null;
            avatarMessageProcessing.SendInitialPacket();
            variableNetworkingProcessing.SendInitialPacket();
        }

        public MutualizedFeatureInterpolator NeedsMutualizedInterpolator(List<MutualizedInterpolationRange> inputRanges, CommsNetworking.InterpolatedDataChanged interpolatedDataChanged)
        {
            List<int> oursToMutualizedIndex = new();
            foreach (var inputRange in inputRanges)
            {
                var address = inputRange.addressId;
                var mutualizedIndex = _ranges.FindIndex(range => range.addressId == address);
                if (mutualizedIndex == -1)
                {
                    mutualizedIndex = _ranges.Count;
                    _ranges.Add(new MutualizedInterpolationRangeStorage
                    {
                        index = mutualizedIndex,
                        addressId = address,
                        lower = inputRange.lower,
                        upper = inputRange.upper,
                    });
                }
                else
                {
                    var storedRange = _ranges[mutualizedIndex];
                    if (inputRange.lower < storedRange.lower)
                    {
                        storedRange.lower = inputRange.lower;
                    }

                    if (inputRange.upper > storedRange.upper)
                    {
                        storedRange.upper = inputRange.upper;
                    }
                }

                oursToMutualizedIndex.Add(mutualizedIndex);
            }

            _needsInterpolation.Add(new HVRNeedsInterpolationCallback
            {
                oursToMutualizedIndex = oursToMutualizedIndex,
                floats = new float[oursToMutualizedIndex.Count],
                callback = interpolatedDataChanged
            });

            return new MutualizedFeatureInterpolator(oursToMutualizedIndex, this);
        }

        public void SubmitAbsolute(int mutualizedIndex, float absolute)
        {
            if (_streamedLateInit != null)
            {
                _streamedLateInit.Store(mutualizedIndex, _ranges[mutualizedIndex].AbsoluteToRange(absolute));
            }
            else
            {
                _toStoreLater.Add(new HVRToSubmitLater
                {
                    mutualizedIndex = mutualizedIndex,
                    absolute = absolute
                });
            }
        }

        public void WhenNetworkMessageReceived(int carrierIndex, ushort remoteUser, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            switch (carrierIndex)
            {
                case AvatarMessageProcessingCarrier0:
                {
                    avatarMessageProcessing.OnNetworkMessageReceived(remoteUser, buffer, deliveryMethod);
                    break;
                }
                case VariableNetworkingCarrier:
                {
                    variableNetworkingProcessing.OnNetworkMessageReceived(remoteUser, buffer, deliveryMethod);
                    break;
                }
            }
        }

        public void WhenNetworkMessageServerReductionSystem(int carrierIndex, byte[] buffer)
        {
            switch (carrierIndex)
            {
                case AvatarMessageProcessingCarrier0:
                {
                    avatarMessageProcessing.OnNetworkMessageServerReductionSystem(buffer);
                    break;
                }
                case VariableNetworkingCarrier:
                {
                    variableNetworkingProcessing.OnNetworkMessageServerReductionSystem(buffer);
                    break;
                }
            }
        }

        private float[] BuildNeutralNormalizedValues()
        {
            var normalized = new float[_ranges.Count];
            for (int index = 0; index < _ranges.Count; index++)
            {
                var range = _ranges[index];
                normalized[index] = range.lower <= 0f && range.upper >= 0f
                    ? Mathf.Clamp01(range.AbsoluteToRange(0f))
                    : 0f;
            }

            return normalized;
        }

        private class HVRRedirectToStreamed : IFeatureReceiver
        {
            private readonly StreamedAvatarFeature streamed;

            public HVRRedirectToStreamed(StreamedAvatarFeature streamed)
            {
                this.streamed = streamed;
            }

            public void OnPacketReceived(byte localIdentifier, ArraySegment<byte> data)
            {
                streamed.OnPacketReceived(data);
            }

            public void OnResyncEveryoneRequested()
            {
                streamed.OnResyncEveryoneRequested();
            }

            public void OnResyncRequested(ushort[] whoAsked)
            {
                streamed.OnResyncRequested(whoAsked);
            }
        }

        private class HVRNeedsInterpolationCallback
        {
            public List<int> oursToMutualizedIndex;
            public float[] floats;
            public CommsNetworking.InterpolatedDataChanged callback;
        }

        private class HVRToSubmitLater
        {
            public int mutualizedIndex;
            public float absolute;
        }
    }
}
