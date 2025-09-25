using System;
using System.Collections.Generic;
using LiteNetLib;
using UnityEngine;

namespace HVR.Basis.Comms
{
    public class FeatureEvent : IFeatureReceiver
    {
        private DeliveryMethod DeliveryMethod = DeliveryMethod.Sequenced;

        private readonly CommsNetworking.EventReceived _eventReceived;
        private readonly CommsNetworking.ResyncRequested _resyncRequested;
        private readonly CommsNetworking.ResyncEveryoneRequested _resyncEveryoneRequested;
        private readonly IHVRTransmitter _transmitter;

        public FeatureEvent(CommsNetworking.EventReceived eventReceived, CommsNetworking.ResyncRequested resyncRequested, CommsNetworking.ResyncEveryoneRequested resyncEveryoneRequested, IHVRTransmitter transmitter)
        {
            _eventReceived = eventReceived;
            _resyncRequested = resyncRequested;
            _resyncEveryoneRequested = resyncEveryoneRequested;
            _transmitter = transmitter;
        }

        public void OnPacketReceived(byte localIdentifier, ArraySegment<byte> data)
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
            buffer[0] = (byte)0; // Formerly bytes. This class needs to be shelved, really.

            currentState.CopyTo(buffer, 1);


            if (whoAskedNullable == null || whoAskedNullable.Length == 0)
            {
                _transmitter.ServerReductionSystemMessageSend(buffer);
            }
            else
            {
                _transmitter.NetworkMessageSend(buffer, DeliveryMethod, whoAskedNullable);
            }
        }
    }

    public class MutualizedFeatureInterpolator
    {
        private readonly List<int> indices;
        private readonly List<MutualizedInterpolationRange> mutualized;
        private readonly HVRAvatarComms comms;

        public MutualizedFeatureInterpolator(List<int> indices, List<MutualizedInterpolationRange> mutualized, HVRAvatarComms comms)
        {
            this.indices = indices;
            this.mutualized = mutualized;
            this.comms = comms;
        }

        public void SubmitAbsolute(int key, float absolute)
        {
            comms.SubmitAbsolute(indices[key], absolute);
        }
    }

    public class RequestedFeature
    {
        public string identifier;
        public float lower;
        public float upper;
    }
}
