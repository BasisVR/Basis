using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Transmitters;
using LiteNetLib;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Feature Networking")]
    public class FeatureNetworking : MonoBehaviour
    {
        public const byte NegotiationPacket = 255;
        public const byte ReservedPacket = 254;

        public const byte ReservedPacket_RemoteRequestsInitializationMessage = 0;

        public delegate void InterpolatedDataChanged(float[] current);
        public delegate void EventReceived(ArraySegment<byte> subBuffer);
        public delegate void ResyncRequested(ushort[] whoAsked);
        public delegate void ResyncEveryoneRequested();

        [SerializeField] private FeatureNetPairing[] netPairings = new FeatureNetPairing[0]; // Unsafe: May contain malformed GUIDs, or null components, or non-networkable components.
        [HideInInspector] [SerializeField] private BasisAvatar avatar;

        private Dictionary<Guid, ICommsNetworkable> _guidToNetworkable;
        private Guid[] _orderedGuids;
        private byte[] _negotiationPacket;

        private IFeatureReceiver[] _featureHandles; // May contain null values if the corresponding Feature fails to initialize. Iterate defensively
        private GameObject _holder;
        private bool _isWearer;
        private byte[] _remoteRequestsInitializationPacket;
        private BasisNetworkTransmitter _netTransmitter;

        private void Awake()
        {
            if (avatar == null) avatar = CommsUtil.GetAvatar(this);
            if (avatar.GetComponentInChildren<HVRAvatarComms>(true) == null)
            {
                avatar.gameObject.AddComponent<HVRAvatarComms>();
            }

            var rand = new System.Random();
            var safeNetPairings = netPairings
                .Where(pairing => Guid.TryParse(pairing.guid, out _))
                .Where(pairing => pairing.component != null)
                .Select(pairing =>
                {
                    if (pairing.component is ICommsNetworkable) return pairing;

                    // Be lenient if the user has dragged the Transform into this.
                    var lookingForNetworkable = pairing.component.GetComponents<Component>();
                    foreach (var candidate in lookingForNetworkable)
                    {
                        if (candidate is ICommsNetworkable)
                        {
                            return new FeatureNetPairing
                            {
                                guid = pairing.guid,
                                component = candidate
                            };
                        }
                    }

                    return pairing; // Will not go through the following .Where predicate
                })
                .Where(pairing => pairing.component is ICommsNetworkable)
                // Shuffling the array makes sure we catch implementation mistakes early.
                // The order of the list of pairings should not matter between clients because of the Negotiation packet.
                .OrderBy(_ => rand.Next())
                .ToArray();

            _guidToNetworkable = safeNetPairings.ToDictionary(pairing => new Guid(pairing.guid), pairing => (ICommsNetworkable)pairing.component);
            _orderedGuids = safeNetPairings.Select(pairing => new Guid(pairing.guid)).ToArray();
            _negotiationPacket = new [] { NegotiationPacket }
                .Concat(safeNetPairings.SelectMany(pairing => new Guid(pairing.guid).ToByteArray()))
                .ToArray();
            _remoteRequestsInitializationPacket = new[] { ReservedPacket, ReservedPacket_RemoteRequestsInitializationMessage };

            _featureHandles = new IFeatureReceiver[safeNetPairings.Length];
        }

        public void OnPacketReceived(int guidIndex, ArraySegment<byte> arraySegment)
        {
            var handleOptional = _featureHandles[guidIndex];
            if (handleOptional != null)
            {
                handleOptional.OnPacketReceived(arraySegment);
            }
        }

        public Guid[] GetOrderedGuids()
        {
            return _orderedGuids;
        }

        public FeatureInterpolator NewInterpolator(int guidIndex, int count, InterpolatedDataChanged interpolatedDataChanged)
        {
            _holder = new GameObject($"Streamed-{guidIndex}")
            {
                transform = { parent = transform }
            };
            _holder.SetActive(false);
            var streamed = _holder.AddComponent<StreamedAvatarFeature>();
            streamed.avatar = avatar;
            streamed.valueArraySize = (byte)count; // TODO: Sanitize count to be within bounds
            streamed.transmitter = _netTransmitter;
            _holder.SetActive(true);

            var handle = new FeatureInterpolator(this, guidIndex, streamed, interpolatedDataChanged);
            streamed.OnInterpolatedDataChanged += handle.OnInterpolatedDataChanged;
            streamed.SetEncodingInfo(_isWearer, (byte)guidIndex); // TODO: Make sure upstream that guidIndex is within limits
            _featureHandles[guidIndex] = handle;
            return handle;
        }

        public FeatureEvent NewEventDriven(int guidIndex, EventReceived eventReceived, ResyncRequested resyncRequested, ResyncEveryoneRequested resyncEveryoneRequested)
        {
            var handle = new FeatureEvent(this, guidIndex, eventReceived, resyncRequested, resyncEveryoneRequested, avatar);
            _featureHandles[guidIndex] = handle;
            return handle;
        }

        internal void Unregister(int guidIndex)
        {
            _featureHandles[guidIndex] = null;
            Destroy(_holder);
        }

        public byte[] GetNegotiationPacket()
        {
            return _negotiationPacket;
        }

        public byte[] GetRemoteRequestsInitializationPacket()
        {
            return _remoteRequestsInitializationPacket;
        }

        public void AssignGuids(bool isWearer)
        {
            _isWearer = isWearer;

            if (BasisNetworkManagement.PlayerToNetworkedPlayer(BasisLocalPlayer.Instance, out var netPlayer))
            {
                if (netPlayer.NetworkSend is BasisNetworkTransmitter transmitter)
                {
                    _netTransmitter = transmitter;
                }
                else
                    throw new InvalidOperationException("BasisNetworkSendBase for the local player is not a BasisNetworkTransmitter.");
            }
            else
                throw new InvalidOperationException("Could not find networked player for local player during OnAvatarNetworkReady.");

            for (var index = 0; index < _orderedGuids.Length; index++)
            {
                var guid = _orderedGuids[index];
                var networkable = _guidToNetworkable[guid];

                networkable.OnGuidAssigned(index, guid);
            }
        }

        public bool TryAddPairingIfNotExists(Component networkable)
        {
            if (netPairings.All(pairing => pairing.component != networkable))
            {
                netPairings = netPairings.Concat(new[]
                {
                    new FeatureNetPairing
                    {
                        guid = Guid.NewGuid().ToString(),
                        component = networkable
                    }
                }).ToArray();
                return true;
            }

            return false;
        }

        public void TryResyncEveryone()
        {
            foreach (var featureReceiver in _featureHandles)
            {
                if (featureReceiver != null)
                {
                    featureReceiver.OnResyncEveryoneRequested();
                }
            }
        }

        public void TryResyncSome(ushort[] whoAsked)
        {
            foreach (var featureReceiver in _featureHandles)
            {
                if (featureReceiver != null)
                {
                    featureReceiver.OnResyncRequested(whoAsked);
                }
            }
        }
    }

    public class FeatureEvent : IFeatureReceiver
    {
        private DeliveryMethod DeliveryMethod = DeliveryMethod.Sequenced;

        private readonly FeatureNetworking _featureNetworking;
        private readonly int _guidIndex;
        private readonly FeatureNetworking.EventReceived _eventReceived;
        private readonly FeatureNetworking.ResyncRequested _resyncRequested;
        private readonly FeatureNetworking.ResyncEveryoneRequested _resyncEveryoneRequested;
        private readonly BasisAvatar _avatar;

        public FeatureEvent(FeatureNetworking featureNetworking, int guidIndex, FeatureNetworking.EventReceived eventReceived, FeatureNetworking.ResyncRequested resyncRequested, FeatureNetworking.ResyncEveryoneRequested resyncEveryoneRequested, BasisAvatar avatar)
        {
            _featureNetworking = featureNetworking;
            _guidIndex = guidIndex;
            _eventReceived = eventReceived;
            _resyncRequested = resyncRequested;
            _resyncEveryoneRequested = resyncEveryoneRequested;
            _avatar = avatar;
        }

        public void Unregister()
        {
            _featureNetworking.Unregister(_guidIndex);
        }

        public void OnPacketReceived(ArraySegment<byte> data)
        {
            _eventReceived.Invoke(data);
        }

        public void OnResyncEveryoneRequested()
        {
            _resyncEveryoneRequested.Invoke();
        }

        public void OnResyncRequested(ushort[] whoAsked)
        {
            _resyncRequested.Invoke(whoAsked);
        }

        public void Submit(ArraySegment<byte> currentState)
        {
            SubmitInternal(currentState, null);
        }

        public void Submit(ArraySegment<byte> currentState, ushort[] whoAsked)
        {
            if (whoAsked == null) throw new ArgumentException("whoAsked cannot be null");
            if (whoAsked.Length == 0) throw new ArgumentException("whoAsked cannot be empty");

            SubmitInternal(currentState, whoAsked);
        }

        private void SubmitInternal(ArraySegment<byte> currentState, ushort[] whoAskedNullable)
        {
            var buffer = new byte[1 + currentState.Count];
            buffer[0] = (byte)_guidIndex;

            currentState.CopyTo(buffer, 1);

            _avatar.NetworkMessageSend(HVRAvatarComms.OurMessageIndex, buffer, DeliveryMethod, whoAskedNullable);
        }
    }

    public class FeatureInterpolator : IFeatureReceiver
    {
        private readonly FeatureNetworking _featureNetworking;
        private readonly int _guidIndex;
        private readonly StreamedAvatarFeature _streamed;
        private readonly FeatureNetworking.InterpolatedDataChanged _interpolatedDataChanged;

        internal FeatureInterpolator(FeatureNetworking featureNetworking, int guidIndex, StreamedAvatarFeature streamed, FeatureNetworking.InterpolatedDataChanged interpolatedDataChanged)
        {
            _featureNetworking = featureNetworking;
            _guidIndex = guidIndex;
            _streamed = streamed;
            _interpolatedDataChanged = interpolatedDataChanged;
        }

        public void Store(int value, float streamed01)
        {
            _streamed.Store(value, streamed01);
        }

        public void Unregister()
        {
            _featureNetworking.Unregister(_guidIndex);
        }

        public void OnPacketReceived(ArraySegment<byte> data)
        {
            _streamed.OnPacketReceived(data);
        }

        public void OnResyncEveryoneRequested()
        {
            _streamed.OnResyncEveryoneRequested();
        }

        public void OnResyncRequested(ushort[] whoAsked)
        {
            _streamed.OnResyncRequested(whoAsked);
        }

        public void OnInterpolatedDataChanged(float[] current)
        {
            _interpolatedDataChanged.Invoke(current);
        }

        public void SwitchToHighSpeedTransmission()
        {
            _streamed.SwitchToHighSpeedTransmission();
        }

        public void SwitchToRegularSpeedTransmission()
        {
            _streamed.SwitchToRegularSpeedTransmission();
        }
    }

    [Serializable]
    public class FeatureNetPairing
    {
        public Component component;
        public string guid;
    }

    public class RequestedFeature
    {
        public string identifier;
        public float lower;
        public float upper;
    }
}
