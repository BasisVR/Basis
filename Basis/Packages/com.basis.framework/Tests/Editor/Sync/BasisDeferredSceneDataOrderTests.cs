using System.Collections.Generic;
using Basis.Network.Core;
using Basis.Scripts.Networking.Sync;
using NUnit.Framework;
using static SerializableBasis;

namespace Basis.Framework.Sync.Tests
{
    /// <summary>
    /// Scene data that arrives before its handler is registered is held and replayed. The replay must keep
    /// the arrival order: a chunked transfer whose body is delivered ahead of its header loses the whole
    /// transfer, which is how images vanished on a client that had just joined.
    /// </summary>
    public sealed class BasisDeferredSceneDataOrderTests
    {
        private const ushort MessageIndex = 41000;
        private const ushort Sender = 7;

        private readonly List<byte> _seen = new();

        [SetUp]
        public void SetUp()
        {
            _seen.Clear();
            BasisNetworkGenericMessages.UnregisterDirectHandler(MessageIndex);
            BasisNetworkGenericMessages.UnregisterHandler(MessageIndex);
            BasisNetworkGenericMessages.ReleaseConnectionRegistrations();
        }

        [TearDown]
        public void TearDown()
        {
            BasisNetworkGenericMessages.UnregisterDirectHandler(MessageIndex);
            BasisNetworkGenericMessages.UnregisterHandler(MessageIndex);
        }

        private void Record(ushort sender, byte[] payload, DeliveryMethod deliveryMethod)
        {
            _seen.Add(payload[0]);
        }

        private static void SendDirect(byte marker)
        {
            BasisNetworkGenericMessages.HandleDirectP2PSceneMessage(
                Sender,
                MessageIndex,
                new[] { marker },
                DeliveryMethod.ReliableOrdered
            );
        }

        [Test]
        public void DeferredDirectMessagesReplayInArrivalOrder()
        {
            SendDirect(1);
            SendDirect(2);
            SendDirect(3);
            Assert.That(_seen, Is.Empty, "nothing should have been delivered before the handler existed");

            BasisNetworkGenericMessages.RegisterDirectHandler(MessageIndex, Record);

            Assert.That(_seen, Is.EqualTo(new List<byte> { 1, 2, 3 }));
        }

        [Test]
        public void AHeaderFollowedByItsBodyStillArrivesHeaderFirst()
        {
            // The exact shape of the image-pickup failure: one opening message and then the sequence that
            // depends on it, all arriving before the handler was ready.
            SendDirect(0);
            for (byte chunk = 1; chunk <= 8; chunk++)
                SendDirect(chunk);

            BasisNetworkGenericMessages.RegisterDirectHandler(MessageIndex, Record);

            Assert.That(_seen.Count, Is.EqualTo(9));
            Assert.That(_seen[0], Is.EqualTo(0), "the header has to be replayed before anything that needs it");
            for (int index = 1; index < _seen.Count; index++)
                Assert.That(_seen[index], Is.EqualTo((byte)index));
        }

        [Test]
        public void MessagesDeliverOnceAndAreNotReplayedAgainOnALaterRegistration()
        {
            SendDirect(1);
            BasisNetworkGenericMessages.RegisterDirectHandler(MessageIndex, Record);
            Assert.That(_seen, Is.EqualTo(new List<byte> { 1 }));

            BasisNetworkGenericMessages.UnregisterDirectHandler(MessageIndex);
            BasisNetworkGenericMessages.RegisterDirectHandler(MessageIndex, Record);

            Assert.That(_seen, Is.EqualTo(new List<byte> { 1 }));
        }

        [Test]
        public void MessagesForOtherIndexesKeepWaitingAndKeepTheirOrder()
        {
            const ushort otherIndex = 41001;
            try
            {
                SendDirect(1);
                BasisNetworkGenericMessages.HandleDirectP2PSceneMessage(
                    Sender,
                    otherIndex,
                    new byte[] { 9 },
                    DeliveryMethod.ReliableOrdered
                );
                SendDirect(2);

                BasisNetworkGenericMessages.RegisterDirectHandler(MessageIndex, Record);
                Assert.That(_seen, Is.EqualTo(new List<byte> { 1, 2 }));

                BasisNetworkGenericMessages.RegisterDirectHandler(otherIndex, Record);
                Assert.That(_seen, Is.EqualTo(new List<byte> { 1, 2, 9 }));
            }
            finally
            {
                BasisNetworkGenericMessages.UnregisterDirectHandler(otherIndex);
            }
        }

        [Test]
        public void RelayedSceneDataReplaysInArrivalOrderToo()
        {
            var seen = new List<byte>();
            BasisNetworkGenericMessages.RegisterHandler(
                MessageIndex,
                (sender, payload, method) => seen.Add(payload[0])
            );
            BasisNetworkGenericMessages.UnregisterHandler(MessageIndex);

            for (byte marker = 1; marker <= 4; marker++)
            {
                BasisNetworkGenericMessages.HandleDirectP2PSceneMessage(
                    Sender,
                    MessageIndex,
                    new[] { marker },
                    DeliveryMethod.ReliableOrdered
                );
            }

            BasisNetworkGenericMessages.RegisterDirectHandler(MessageIndex, Record);

            Assert.That(_seen, Is.EqualTo(new List<byte> { 1, 2, 3, 4 }));
        }

        /// <summary>
        /// A message index is only meaningful inside the connection that assigned it, so anything still
        /// waiting for a handler when the connection ends has to be dropped. Left in the queue it is replayed
        /// into whichever subsystem claims that index on the next server - which is how a client that visited
        /// two servers ended up with the first server's image traffic arriving in the second one's session.
        /// </summary>
        [Test]
        public void ReleasingDropsSceneDataTheLastConnectionNeverDelivered()
        {
            SendDirect(1);
            SendDirect(2);

            BasisNetworkGenericMessages.ReleaseConnectionRegistrations();
            BasisNetworkGenericMessages.RegisterDirectHandler(MessageIndex, Record);

            Assert.That(_seen, Is.Empty, "the previous connection's undelivered payloads must not replay");
        }

        /// <summary>
        /// The same index belongs to a different object on the next server, so a registration that outlived
        /// its connection would hand one subsystem another's traffic. Everything re-registers on join.
        /// </summary>
        [Test]
        public void ReleasingDropsHandlerRegistrationsSoTheNextConnectionRebindsThem()
        {
            BasisNetworkGenericMessages.RegisterDirectHandler(MessageIndex, Record);
            BasisNetworkGenericMessages.ReleaseConnectionRegistrations();

            SendDirect(5);
            Assert.That(_seen, Is.Empty, "the registration must not survive the connection that issued the index");

            // Delivered only once something claims the index again, which proves it was held rather than run.
            BasisNetworkGenericMessages.RegisterDirectHandler(MessageIndex, Record);
            Assert.That(_seen, Is.EqualTo(new List<byte> { 5 }));
        }

        [Test]
        public void ReleasingDropsRelayedRegistrationsToo()
        {
            var seen = new List<byte>();
            BasisNetworkGenericMessages.RegisterHandler(MessageIndex, (sender, payload, method) => seen.Add(payload[0]));
            BasisNetworkGenericMessages.ReleaseConnectionRegistrations();

            BasisNetworkGenericMessages.DispatchServerSceneDataMessage(
                BuildRelayed(MessageIndex, new byte[] { 6 }),
                DeliveryMethod.ReliableOrdered,
                false
            );

            Assert.That(seen, Is.Empty, "the relay table has to be swept as well as the direct one");
        }

        /// <summary>
        /// Batch demux is session-wide policy rather than per connection, and SetEnabled short-circuits when
        /// the flag is already set, so a sweep that dropped it would leave batching permanently unreceivable.
        /// </summary>
        [Test]
        public void ReleasingReArmsBatchDemuxWhileBatchingIsOn()
        {
            BasisSyncBatchCollector.SetEnabled(true);
            try
            {
                BasisNetworkGenericMessages.ReleaseConnectionRegistrations();

                // Batches ride the relay table, which is the one the sweep empties. An empty batch is
                // consumed and produces nothing; had the demux been swept away this would instead be held,
                // and the recorder registered afterwards would receive the replay.
                BasisNetworkGenericMessages.DispatchServerSceneDataMessage(
                    BuildRelayed(BasisSyncBatchCollector.BatchMessageIndex, new byte[0]),
                    DeliveryMethod.ReliableOrdered,
                    false
                );

                var seen = new List<byte>();
                BasisNetworkGenericMessages.RegisterHandler(
                    BasisSyncBatchCollector.BatchMessageIndex,
                    (sender, payload, method) => seen.Add(0)
                );
                Assert.That(seen, Is.Empty, "the batch demux must still be registered after a sweep");
            }
            finally
            {
                BasisSyncBatchCollector.SetEnabled(false);
                BasisNetworkGenericMessages.UnregisterHandler(BasisSyncBatchCollector.BatchMessageIndex);
                BasisNetworkGenericMessages.ReleaseConnectionRegistrations();
            }
        }

        private static ServerSceneDataMessage BuildRelayed(ushort messageIndex, byte[] payload)
        {
            return new ServerSceneDataMessage
            {
                playerIdMessage = new PlayerIdMessage { playerID = Sender },
                sceneDataMessage = new RemoteSceneDataMessage
                {
                    messageIndex = messageIndex,
                    payload = payload,
                    payloadLength = payload.Length,
                },
            };
        }
    }
}
