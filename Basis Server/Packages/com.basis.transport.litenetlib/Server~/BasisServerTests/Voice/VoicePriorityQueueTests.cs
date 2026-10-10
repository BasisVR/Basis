using LiteNetLib;
using System.Diagnostics;
using System.Linq;
using Xunit;
// Both namespaces define NetManager/EventBasedNetListener — the transport shell and the
// implementation. These tests drive the real LiteNetLib types, so only the channel constants are
// pulled across.
using BasisNetworkCommons = Basis.Network.Core.BasisNetworkCommons;
using BasisPopulationScale = Basis.Network.Core.BasisPopulationScale;

namespace BasisServerTests;

// -----------------------------------------------------------------------------
// Voice is queued apart from bulk avatar state.
//
// The regression these pin: voice and avatar updates shared one per-peer
// unreliable queue whose overflow policy drops OLDEST FIRST. That policy is
// correct for state -- the newer position supersedes the one behind it -- and
// wrong for audio, where every packet is a distinct slice of sound. Because bulk
// traffic outnumbers voice by orders of magnitude, an avatar backlog trimmed
// voice at the bulk stream's drop rate, which at overload removed roughly every
// second voice packet on the instance.
//
// So the property under test is not "voice is faster". It is that a saturated
// bulk queue costs voice NOTHING.
// -----------------------------------------------------------------------------

public class VoicePriorityQueueTests
{
    private const string ConnectKey = "voice-priority";

    /// <summary>Small enough that a burst is guaranteed to overrun it many times over.</summary>
    private const int BulkBound = 64;

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    private static NetManager Manager(EventBasedNetListener listener, int bulkBound) =>
        new NetManager(listener)
        {
            AutoRecycle = true,
            UnsyncedEvents = true,
            ChannelsCount = BasisNetworkCommons.TotalChannels,
            UpdateTime = 5,
            MaxUnreliableQueuePerPeer = bulkBound,
            MaxPriorityUnreliableQueuePerPeer = 256,
            PriorityUnreliableChannels = BasisNetworkCommons.BuildPriorityUnreliableChannelMap(),
        };

    private static byte[] Payload(int seed, int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)(seed * 31 + i * 7);
        return data;
    }

    /// <summary>
    /// The core regression: voice must still ARRIVE while the bulk queue is shedding hard.
    ///
    /// Arrival is the assertion rather than the drop counters, and that distinction matters — with
    /// the old shared queue the priority counter would read a perfect zero simply because no
    /// priority queue was ever used, so counters alone would pass against the very bug this exists
    /// to catch. The bulk counter is still checked, but only as a PRECONDITION: if the queue never
    /// overflowed, the run says nothing about what overflow costs voice.
    /// </summary>
    [Fact]
    public void SaturatedBulkQueue_DoesNotShedVoice()
    {
        var senderListener = new EventBasedNetListener();
        var receiverListener = new EventBasedNetListener();
        var sender = Manager(senderListener, BulkBound);
        var receiver = Manager(receiverListener, BulkBound);

        const int rounds = 10;
        const int voicePerRound = 3;
        const int voiceSent = rounds * voicePerRound;
        int voiceReceived = 0;
        byte[] voice = Payload(2, 96);

        receiverListener.ConnectionRequestEvent += r => r.AcceptIfKey(ConnectKey);
        receiverListener.NetworkReceiveEvent += (p, reader, ch, m) =>
        {
            if (ch == BasisNetworkCommons.VoiceChannel)
                Interlocked.Increment(ref voiceReceived);
            reader.Recycle();
        };
        senderListener.NetworkReceiveEvent += (p, reader, ch, m) => reader.Recycle();

        try
        {
            Assert.True(receiver.Start(0));
            Assert.True(sender.Start(0));
            sender.Connect("127.0.0.1", receiver.LocalPort, ConnectKey);
            Assert.True(WaitFor(() => sender.ConnectedPeersCount == 1), "peer never connected");

            NetPeer peer = sender.FirstPeer;
            Assert.NotNull(peer);

            byte[] bulk = Payload(1, 220);
            byte avatarChannel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, false);

            // Interleaved, and in paced rounds. Interleaved because the old shared queue only
            // destroyed voice that was already sitting in it when an overflow ran, so voice has to
            // be present throughout. Paced because the point is to overrun the SENDER's queue bound
            // (300 per round against a bound of 64 does that many times over) without also
            // overrunning the loopback receive buffer, which would confuse ordinary UDP loss with
            // the shedding under test.
            for (int round = 0; round < rounds; round++)
            {
                for (int i = 0; i < 300; i++)
                {
                    peer.SendUnreliableRawMerge(bulk, 0, bulk.Length, avatarChannel);
                    if (i % (300 / voicePerRound) == 0)
                        peer.SendUnreliableRawMerge(voice, 0, voice.Length, BasisNetworkCommons.VoiceChannel);
                }
                Thread.Sleep(5);
            }

            Assert.True(sender.UnreliableDropped > 0,
                "bulk queue never overflowed, so this run proves nothing about what overflow costs voice");

            // Tolerance covers incidental loopback loss only. The bug removed roughly every second
            // voice packet, so a broken build lands near 50% and fails this by a wide margin.
            Assert.True(WaitFor(() => Volatile.Read(ref voiceReceived) >= voiceSent * 9 / 10),
                $"only {Volatile.Read(ref voiceReceived)}/{voiceSent} voice packets survived a saturated bulk queue " +
                $"({sender.UnreliableDropped} bulk drops) — voice is being shed with bulk traffic again");
            Assert.Equal(0, sender.PriorityUnreliableDropped);
        }
        finally
        {
            sender.Stop();
            receiver.Stop();
        }
    }

    /// <summary>
    /// The split must not have cost delivery: voice still arrives, on its own channel, byte-intact.
    /// Run with a bulk bound high enough that nothing sheds, so any loss here is the priority path's
    /// own fault rather than overload.
    /// </summary>
    [Fact]
    public void VoiceOnPriorityQueue_ArrivesIntact()
    {
        var senderListener = new EventBasedNetListener();
        var receiverListener = new EventBasedNetListener();
        var sender = Manager(senderListener, 8192);
        var receiver = Manager(receiverListener, 8192);

        const int voiceSent = 30;
        int voiceReceived = 0;
        int corrupt = 0;
        byte[] voice = Payload(7, 110);

        receiverListener.ConnectionRequestEvent += r => r.AcceptIfKey(ConnectKey);
        receiverListener.NetworkReceiveEvent += (p, reader, ch, m) =>
        {
            if (ch == BasisNetworkCommons.VoiceChannel)
            {
                byte[] got = reader.GetRemainingBytes();
                if (got.Length != voice.Length || !got.AsSpan().SequenceEqual(voice))
                    Interlocked.Increment(ref corrupt);
                else
                    Interlocked.Increment(ref voiceReceived);
            }
            reader.Recycle();
        };
        senderListener.NetworkReceiveEvent += (p, reader, ch, m) => reader.Recycle();

        try
        {
            Assert.True(receiver.Start(0));
            Assert.True(sender.Start(0));
            sender.Connect("127.0.0.1", receiver.LocalPort, ConnectKey);
            Assert.True(WaitFor(() => sender.ConnectedPeersCount == 1), "peer never connected");

            NetPeer peer = sender.FirstPeer;
            Assert.NotNull(peer);

            byte[] bulk = Payload(9, 200);
            for (int i = 0; i < voiceSent; i++)
            {
                peer.SendUnreliableRawMerge(voice, 0, voice.Length, BasisNetworkCommons.VoiceChannel);
                for (int b = 0; b < 4; b++)
                    peer.SendUnreliableRawMerge(bulk, 0, bulk.Length,
                        BasisNetworkCommons.GetPlayerAvatarChannelForQuality(2, false));
                Thread.Sleep(5);
            }

            Assert.True(WaitFor(() => Volatile.Read(ref voiceReceived) == voiceSent),
                $"only {Volatile.Read(ref voiceReceived)}/{voiceSent} voice packets arrived");
            Assert.Equal(0, Volatile.Read(ref corrupt));
            Assert.Equal(0, sender.PriorityUnreliableDropped);
        }
        finally
        {
            sender.Stop();
            receiver.Stop();
        }
    }

    /// <summary>
    /// Guards the classification itself. Voice DATA channels are priority; avatar state and the
    /// voice control channels are not — a recipient list is low-rate and its newest message really
    /// does supersede the last, so it belongs in the bulk queue.
    ///
    /// The failure this catches is a new voice channel being added without being listed here, which
    /// would silently put that traffic back behind the avatar backlog.
    /// </summary>
    [Fact]
    public void PriorityChannelMap_CoversExactlyTheVoiceDataChannels()
    {
        bool[] map = BasisNetworkCommons.BuildPriorityUnreliableChannelMap();

        Assert.Equal(BasisNetworkCommons.TotalChannels, map.Length);

        Assert.True(map[BasisNetworkCommons.VoiceChannel]);
        Assert.True(map[BasisNetworkCommons.AnnounceVoiceChannel]);
        Assert.True(map[BasisNetworkCommons.VoiceLargeChannel]);

        Assert.False(map[BasisNetworkCommons.AudioRecipientsChannel]);
        Assert.False(map[BasisNetworkCommons.AudioRecipientsLargeChannel]);
        Assert.False(map[BasisNetworkCommons.AudioRecipientsInvertedChannel]);
        Assert.False(map[BasisNetworkCommons.AudioRecipientsBitfieldChannel]);
        Assert.False(map[BasisNetworkCommons.DeltaAvatarChannel]);
        Assert.False(map[BasisNetworkCommons.AuthIdentityChannel]);

        for (int quality = 0; quality < 4; quality++)
        {
            Assert.False(map[BasisNetworkCommons.GetPlayerAvatarChannelForQuality(quality, false)]);
            Assert.False(map[BasisNetworkCommons.GetPlayerAvatarChannelForQuality(quality, true)]);
        }

        // Exactly three, so an accidental blanket "everything is priority" fails here rather than
        // quietly reinstating one queue under two names.
        Assert.Equal(3, map.Count(x => x));
    }

    /// <summary>
    /// The sizing lesson, pinned so it cannot be undone by someone reasoning about voice as a single
    /// conversation again.
    ///
    /// This queue shipped once at a flat 256 on exactly that reasoning and measured 32.8% voice
    /// delivery at 1000 clients, against 93.6% with a population-scaled bound. The queue is a fan-IN:
    /// a receiver in a crowd is fed by every audible talker every frame period, so 256 is single-digit
    /// milliseconds of arrivals.
    ///
    /// It is also asserted DEEPER than the bulk bound, which reads backwards on purpose — bulk depth
    /// buys avatar frames the next frame supersedes, voice depth buys audio with no replacement.
    /// </summary>
    [Fact]
    public void PriorityQueueBound_ScalesWithPopulation_AndOutranksTheBulkBound()
    {
        const long Gb = 1024L * 1024L * 1024L;
        try
        {
            BasisPopulationScale.OverrideAvailableMemoryForTests(64 * Gb);

            int voiceAt1000 = BasisPopulationScale.PriorityQueuePerPeer(0, 1000);
            int bulkAt1000 = BasisPopulationScale.UnreliableQueuePerPeer(0, 1000);

            Assert.True(voiceAt1000 >= 4096,
                $"voice bound {voiceAt1000} at 1000 players is in the range that measured 32.8% delivery");
            // At least as deep as bulk, never shallower. Shallower is precisely the bug: it makes
            // voice the first thing an overloaded server destroys, which is the opposite of what a
            // listener would choose. Parity is the current design; a future tilt toward voice is
            // fine, a tilt away from it is not.
            Assert.True(voiceAt1000 >= bulkAt1000,
                $"voice bound {voiceAt1000} must not be shallower than the bulk bound {bulkAt1000} — bulk is the traffic that should shed first");

            // Deeper per peer as the crowd thins, never unbounded.
            Assert.True(BasisPopulationScale.PriorityQueuePerPeer(0, 100) >= voiceAt1000);
            Assert.InRange(BasisPopulationScale.PriorityQueuePerPeer(0, 8000),
                BasisPopulationScale.MinPriorityQueuePerPeer,
                BasisPopulationScale.MaxPriorityQueuePerPeer);

            // A pinned value still wins, so an operator can reproduce a measurement.
            Assert.Equal(777, BasisPopulationScale.PriorityQueuePerPeer(777, 1000));

            // Even a small box keeps enough depth to carry a crowd's fan-in.
            BasisPopulationScale.OverrideAvailableMemoryForTests(8 * Gb);
            Assert.True(BasisPopulationScale.PriorityQueuePerPeer(0, 2000) >= BasisPopulationScale.MinPriorityQueuePerPeer);
        }
        finally
        {
            BasisPopulationScale.OverrideAvailableMemoryForTests(0);
        }
    }
}
