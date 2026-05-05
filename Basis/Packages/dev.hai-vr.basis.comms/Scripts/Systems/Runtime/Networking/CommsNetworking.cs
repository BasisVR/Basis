using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using UnityEngine;

namespace HVR.Basis.Comms
{
    public class CommsNetworking
    {
        public static MutualizedFeatureInterpolator UsingMutualizedInterpolator(BasisAvatar avatar, List<MutualizedInterpolationRange> mutualized, InterpolatedDataChanged interpolatedDataChanged)
        {
            var comms = HVRCommsUtil.GetComms(avatar);
            return comms.NeedsMutualizedInterpolator(mutualized, interpolatedDataChanged);
        }

        public static FeatureEvent NewEventDriven(EventReceived eventReceived, ResyncRequested resyncRequested, ResyncEveryoneRequested resyncEveryoneRequested, IHVRTransmitter transmitter)
        {
            var handle = new FeatureEvent(eventReceived, resyncRequested, resyncEveryoneRequested, transmitter);
            return handle;
        }

        public delegate void InterpolatedDataChanged(float[] current);

        public delegate void EventReceived(ArraySegment<byte> subBuffer);

        public delegate void ResyncRequested(ushort[] whoAsked);

        public delegate void ResyncEveryoneRequested();
    }

    public class MutualizedInterpolationRange
    {
        public int addressId;
        public float lower;
        public float upper;
    }

    public class MutualizedInterpolationRangeStorage
    {
        public int index;
        public int addressId;
        public float lower;
        public float upper;

        public float AbsoluteToRange(float absolute)
        {
            return Mathf.InverseLerp(lower, upper, absolute);
        }

        public float RangeToAbsolute(float streamed01)
        {
            return Mathf.Lerp(lower, upper, streamed01);
        }
    }
}
