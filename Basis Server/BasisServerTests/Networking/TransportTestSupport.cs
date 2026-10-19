using Basis.Network.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace BasisServerTests;

internal static class TransportTestSupport
{
    public static int FreeTcpPort()
    {
        TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static int FreeTcpAndUdpPort()
    {
        Random random = new Random();
        for (int attempt = 0; attempt < 200; attempt++)
        {
            int port = random.Next(20000, 60000);
            try
            {
                TcpListener tcp = new TcpListener(IPAddress.Any, port);
                tcp.Start();
                tcp.Stop();
                using UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                return port;
            }
            catch (SocketException)
            {
            }
        }
        throw new InvalidOperationException("No free port found");
    }

    public static async Task<T> Within<T>(Task<T> task, int milliseconds = 5000)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(milliseconds));
        Assert.True(ReferenceEquals(finished, task), $"Timed out after {milliseconds} ms");
        return await task;
    }

    public static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Condition not met within {milliseconds} ms");
            await Task.Delay(10);
        }
    }

    public static NetDataWriter Writer(params byte[] bytes)
    {
        NetDataWriter writer = new NetDataWriter(true, Math.Max(8, bytes.Length));
        writer.Put(bytes);
        return writer;
    }

    public static async Task<string> RawHttpAsync(int port, string request)
    {
        using TcpClient client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        byte[] buffer = new byte[4096];
        int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.ASCII.GetString(buffer, 0, read);
    }
}

internal sealed class RecordedEvents
{
    public readonly ConcurrentQueue<(NetPeer Peer, byte[] Data, byte Channel, DeliveryMethod Method)> Received = new();
    public readonly ConcurrentQueue<NetPeer> Connected = new();
    public readonly ConcurrentQueue<(NetPeer Peer, DisconnectReason Reason, byte[] Data)> Disconnected = new();
    public readonly ConcurrentQueue<(IPEndPoint EndPoint, byte[] Data)> Unconnected = new();
    public Func<ConnectionRequest, NetPeer?>? OnRequest;
    public Action<IPEndPoint, byte[]>? OnUnconnected;

    public RecordedEvents(EventBasedNetListener listener)
    {
        listener.ConnectionRequestEvent += request =>
        {
            if (OnRequest != null) OnRequest(request);
            else request.Accept();
        };
        listener.PeerConnectedEvent += peer => Connected.Enqueue(peer);
        listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            Received.Enqueue((peer, reader.GetRemainingBytes(), channel, method));
            reader.Recycle();
        };
        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            byte[] data = info.AdditionalData == null ? Array.Empty<byte>() : info.AdditionalData.GetRemainingBytes();
            Disconnected.Enqueue((peer, info.Reason, data));
        };
        listener.NetworkReceiveUnconnectedEvent += (endPoint, reader) =>
        {
            byte[] data = reader.GetRemainingBytes();
            reader.Recycle(true);
            Unconnected.Enqueue((endPoint, data));
            OnUnconnected?.Invoke(endPoint, data);
        };
    }

    public async Task<(NetPeer Peer, byte[] Data, byte Channel, DeliveryMethod Method)> NextReceived(int milliseconds = 5000)
    {
        (NetPeer, byte[], byte, DeliveryMethod) item = default;
        await TransportTestSupport.Until(() => Received.TryDequeue(out item), milliseconds);
        return item;
    }

    public async Task<NetPeer> NextConnected(int milliseconds = 5000)
    {
        NetPeer? peer = null;
        await TransportTestSupport.Until(() => Connected.TryDequeue(out peer), milliseconds);
        return peer!;
    }

    public async Task<(NetPeer Peer, DisconnectReason Reason, byte[] Data)> NextDisconnected(int milliseconds = 5000)
    {
        (NetPeer, DisconnectReason, byte[]) item = default;
        await TransportTestSupport.Until(() => Disconnected.TryDequeue(out item), milliseconds);
        return item;
    }
}
