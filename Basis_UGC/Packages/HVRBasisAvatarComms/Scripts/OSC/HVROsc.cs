using System.Collections.Generic;
using System.Net;
using HVR.Basis.Comms.OSC.Lyuma;

namespace HVR.Basis.Comms.OSC
{
    public class HVROsc
    {
        private const int DefaultReceiverPort = 9000;
        private int _currentReceiverPort;
        
        private readonly int _oscPort;
        private readonly SimpleOSC _client;
        
        private readonly byte[] _byteBuffer = new byte[65535];
        private readonly List<SimpleOSC.OSCMessage> _queue = new();

        public HVROsc(int oscPort)
        {
            _oscPort = oscPort;
            _client = new SimpleOSC();
        }
        
        public void Start()
        {
            RedefineReceiver(DefaultReceiverPort);
            _client.OpenClient(_oscPort);
        }

        public void SetReceiverOscPort(int oscPort)
        {
            RedefineReceiver(oscPort);
        }

        private void RedefineReceiver(int receiverPort)
        {
            if (receiverPort == _currentReceiverPort) return;

            _currentReceiverPort = receiverPort;

            _client.SetUnconnectedEndpoint(new IPEndPoint(IPAddress.Loopback, receiverPort));
        }
        
        public void SendOsc(string oscItemKey, object x)
        {
            _client.SendOSCPacket(new SimpleOSC.OSCMessage
            {
                path = oscItemKey,
                arguments = new[] { x }
            }, _byteBuffer);
        }

        public void SendOscMultivalue(string oscItemKey, object[] objects)
        {
            _client.SendOSCPacket(new SimpleOSC.OSCMessage
            {
                path = oscItemKey,
                arguments = objects
            }, _byteBuffer);
        }

        public List<SimpleOSC.OSCMessage> PullMessages()
        {
            _queue.Clear();
            _client.GetIncomingOSC(_queue);
            return _queue;
        }

        public void Finish()
        {
            _client.StopClient();
        }
    }
}
