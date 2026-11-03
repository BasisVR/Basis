using LiteNetLib;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;
using BasisNetworkCommons = Basis.Network.Core.BasisNetworkCommons;
using UnreliableRawSend = Basis.Network.Core.UnreliableRawSend;

namespace BasisServerTests;

// -----------------------------------------------------------------------------
// SendUnreliableRawMergeBatch packs a run of unreliable sends into a few queued
// packets instead of one per message. What arrives must be exactly what the same
// sends made one by one through SendUnreliableRawMerge would deliver, including
// the per-receiver patch byte, and the queue bound must still count messages.
// -----------------------------------------------------------------------------

public class UnreliableBatchSendTests
{
    private const string ConnectKey = "unreliable-batch";

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

    private static string Key(byte channel, byte[] data, int offset, int length) =>
        channel + ":" + Convert.ToHexString(data, offset, length);

    [Fact]
    public void Batch_DeliversExactlyWhatSingleSendsWould()
    {
        var senderListener = new EventBasedNetListener();
        var receiverListener = new EventBasedNetListener();
        var sender = Manager(senderListener, 0);
        var receiver = Manager(receiverListener, 0);
        var received = new ConcurrentBag<string>();

        receiverListener.ConnectionRequestEvent += r => r.AcceptIfKey(ConnectKey);
        receiverListener.NetworkReceiveEvent += (p, reader, ch, m) =>
        {
            byte[] bytes = reader.GetRemainingBytes();
            received.Add(Key(ch, bytes, 0, bytes.Length));
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

            // Mixed sizes (several batches' worth), several avatar channels, a priority voice entry
            // in the middle of the run and a patch byte on most entries.
            var sends = new UnreliableRawSend[60];
            var expected = new List<string>();
            for (int i = 0; i < sends.Length; i++)
            {
                byte channel = i == 30
                    ? BasisNetworkCommons.VoiceChannel
                    : BasisNetworkCommons.GetPlayerAvatarChannelForQuality(i % 4, false);
                byte[] src = Payload(i + 1, 20 + (i * 37) % 300);
                int patchOffset = i % 5 == 0 ? -1 : 1 + i % 7;
                byte patchValue = (byte)(200 + i);
                sends[i] = new UnreliableRawSend
                {
                    Data = src, Offset = 0, Length = src.Length, Channel = channel,
                    PatchOffset = patchOffset, PatchValue = patchValue,
                };
                byte[] wire = (byte[])src.Clone();
                if (patchOffset >= 0 && patchOffset < wire.Length) wire[patchOffset] = patchValue;
                expected.Add(Key(channel, wire, 0, wire.Length));
            }

            peer.SendUnreliableRawMergeBatch(sends, sends.Length);

            Assert.True(WaitFor(() => received.Count >= expected.Count),
                $"received {received.Count}/{expected.Count}");
            Thread.Sleep(50);
            Assert.Equal(expected.OrderBy(s => s), received.OrderBy(s => s));
        }
        finally
        {
            sender.Stop();
            receiver.Stop();
        }
    }

    [Fact]
    public void Batch_QueueBoundCountsMessagesNotBatches()
    {
        var senderListener = new EventBasedNetListener();
        var receiverListener = new EventBasedNetListener();
        const int bound = 32;
        var sender = Manager(senderListener, bound);
        var receiver = Manager(receiverListener, bound);
        receiverListener.ConnectionRequestEvent += r => r.AcceptIfKey(ConnectKey);
        receiverListener.NetworkReceiveEvent += (p, reader, ch, m) => reader.Recycle();
        senderListener.NetworkReceiveEvent += (p, reader, ch, m) => reader.Recycle();

        try
        {
            Assert.True(receiver.Start(0));
            Assert.True(sender.Start(0));
            sender.Connect("127.0.0.1", receiver.LocalPort, ConnectKey);
            Assert.True(WaitFor(() => sender.ConnectedPeersCount == 1), "peer never connected");
            NetPeer peer = sender.FirstPeer;

            byte avatar = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, false);
            byte[] src = Payload(9, 60);
            var sends = new UnreliableRawSend[400];
            for (int i = 0; i < sends.Length; i++)
                sends[i] = new UnreliableRawSend { Data = src, Length = src.Length, Channel = avatar, PatchOffset = -1 };

            // 400 messages against a bound of 32 in one call: well over the bound in messages,
            // though only a couple of dozen batches.
            peer.SendUnreliableRawMergeBatch(sends, sends.Length);

            Assert.True(sender.UnreliableDropped > 0, "a run ten times the bound was never trimmed");
            Assert.True(sender.UnreliableDropped <= sends.Length);
        }
        finally
        {
            sender.Stop();
            receiver.Stop();
        }
    }
}
