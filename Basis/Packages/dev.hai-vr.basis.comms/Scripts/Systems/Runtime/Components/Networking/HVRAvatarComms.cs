using Basis.Scripts.BasisSdk;
using System;
using System.Collections;
using System.Collections.Generic;
using Basis.Scripts.Behaviour;
using LiteNetLib;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Internal/HVR Avatar Comms")]
    public class HVRAvatarComms : BasisAvatarMonoBehaviour
    {
        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [SerializeField] private bool isFromPrefab = false;

        private readonly Nethack _nethack;

        private bool _isWearer;

        private readonly List<string> _keys = new();
        private readonly List<MutualizedInterpolationRange> _ranges = new();
        private readonly List<HVRNeedsInterpolationCallback> _needsInterpolation = new();
        private readonly List<HVRToSubmitLater> _toStoreLater = new();
        private AvatarMessageProcessing avatarMessageProcessing;
        private StreamedAvatarFeature _streamedLateInit;

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
            _isWearer = true;

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

            var allInitializables = avatar.GetComponentsInChildren<IHVRInitializable>(true);
            foreach (var initializable in allInitializables)
            {
                initializable.OnHVRReadyBothAvatarAndNetwork(isWearer);
            }

            DeclareMutualizedInterpolator(isWearer, carriers[0]);
        }

        private void DeclareMutualizedInterpolator(bool isWearer, HVRNetworkingCarrier carrier)
        {
            var holder = new GameObject("Streamed-Mutualized")
            {
                transform = { parent = avatar.transform }
            };
            holder.SetActive(false);
            _streamedLateInit = holder.AddComponent<StreamedAvatarFeature>();
            _streamedLateInit.valueArraySize = (byte)_keys.Count; // TODO: Sanitize count to be within bounds
            _streamedLateInit.transmitter = carrier;
            _streamedLateInit.isWearer = isWearer;
            _streamedLateInit.localIdentifier = 0;
            _toStoreLater.Clear();
            holder.SetActive(true);
            // StreamedAvatarFeature only gets the ability to store data AFTER Awake() runs, so order matters here.
            foreach (var toStoreLater in _toStoreLater)
            {
                var index = toStoreLater.index;
                _streamedLateInit.Store(index, _ranges[index].AbsoluteToRange(toStoreLater.absolute));
            }

            _streamedLateInit.OnInterpolatedDataChanged += data =>
            {
                foreach (var callback in _needsInterpolation)
                {
                    for (var index = 0; index < callback.floats.Length; index++)
                    {
                        var indexInData = callback.indicesInData[index];
                        var streamed01 = data[indexInData];
                        var absolute = _ranges[indexInData].RangeToAbsolute(streamed01);
                        callback.floats[index] = absolute;
                    }

                    callback.callback(data);
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
        }

        public MutualizedFeatureInterpolator NeedsMutualizedInterpolator(List<MutualizedInterpolationRange> inputRanges, CommsNetworking.InterpolatedDataChanged interpolatedDataChanged)
        {
            List<int> indices = new();
            foreach (var inputRange in inputRanges)
            {
                var key = inputRange.key;
                if (!_keys.Contains(key))
                {
                    _keys.Add(key);
                    _ranges.Add(new MutualizedInterpolationRange
                    {
                        key = key,
                        lower = inputRange.lower,
                        upper = inputRange.upper,
                    });
                }

                var index = _keys.IndexOf(key);
                indices.Add(index);

                var storedRange = _ranges[index];
                if (inputRange.lower < storedRange.lower)
                {
                    storedRange.lower = inputRange.lower;
                }
                if (inputRange.upper > storedRange.upper)
                {
                    storedRange.upper = inputRange.upper;
                }
            }

            _needsInterpolation.Add(new HVRNeedsInterpolationCallback
            {
                indicesInData = indices,
                floats = new float[indices.Count],
                callback = interpolatedDataChanged
            });

            return new MutualizedFeatureInterpolator(indices, inputRanges, this);
        }

        public void SubmitAbsolute(int index, float absolute)
        {
            if (_streamedLateInit != null)
            {
                _streamedLateInit.Store(index, _ranges[index].AbsoluteToRange(absolute));
            }
            else
            {
                _toStoreLater.Add(new HVRToSubmitLater
                {
                    index = index,
                    absolute = absolute
                });
            }
        }

        public void WhenNetworkMessageReceived(int carrierIndex, ushort remoteUser, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (carrierIndex == 0)
            {
                avatarMessageProcessing.OnNetworkMessageReceived(remoteUser, buffer, deliveryMethod);
            }
        }

        public void WhenNetworkMessageServerReductionSystem(int carrierIndex, byte[] buffer)
        {
            if (carrierIndex == 0)
            {
                avatarMessageProcessing.OnNetworkMessageServerReductionSystem(buffer);
            }
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
            public List<int> indicesInData;
            public float[] floats;
            public CommsNetworking.InterpolatedDataChanged callback;
        }

        private class HVRToSubmitLater
        {
            public int index;
            public float absolute;
        }
    }
}
