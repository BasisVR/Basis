using System;
using HVR.Basis.Comms.OSC;

namespace HVR.Basis.Comms
{
    public static class BasisOscService
    {
        public static event Action<OscMessage> MessageReceived;

        internal static void Publish(OscMessage message)
        {
            MessageReceived?.Invoke(message);
        }

        public static void PublishValue(string address, OscData value)
        {
            PublishValues(address, value == null ? Array.Empty<OscData>() : new[] { value });
        }

        public static void PublishValues(string address, OscData[] values)
        {
            OSCAcquisitionServer.SceneInstance.PublishValues(address, values);
        }
    }
}
