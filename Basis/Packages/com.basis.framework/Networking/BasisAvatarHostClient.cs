using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
namespace Basis.Scripts.Networking
{
    [AutoStaticsCleanup]
    public static partial class BasisAvatarHostClient
    {
        private sealed class PendingRequest
        {
            public uint RequestId;
            public byte[] Buffer;
            public int Received;
            public long LastActivityMs;
            public Action<float> Progress;
            public TaskCompletionSource<(byte[] Data, string Error)> Completion;
        }
        private sealed class HostedAvatar
        {
            public string LocalUrl;
            public BasisHostedBeeLayout Layout;
            public byte[] Id;
        }
        private const int OwnerReadBlockBytes = 1024 * 1024, MaxQueuedPackets = 256;
        private const long RequestIdleTimeoutMs = 60000, OfferTimeoutMs = 15000;
        private static readonly ConcurrentDictionary<uint, PendingRequest> requests = new ConcurrentDictionary<uint, PendingRequest>();
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<byte>> offerReplies = new ConcurrentDictionary<string, TaskCompletionSource<byte>>();
        [NoAutoStaticsCleanup] private static readonly object hostedSync = new object();
        private static readonly List<HostedAvatar> hosted = new List<HostedAvatar>();
        private static int nextRequestId;
        [NoAutoStaticsCleanup] private static readonly Stopwatch clock = Stopwatch.StartNew();
        public static void Initialize()
        {
            BasisServerHostedBee.Fetcher = FetchAsync;
            BasisClientMessageRegistry.RegisterClientPlugin(BasisAvatarHostProtocol.MessageName, Handle);
            BasisNetworkModeration.OnGlobalAvatarHostingLockedChanged -= OnHostingLockChanged;
            BasisNetworkModeration.OnGlobalAvatarHostingLockedChanged += OnHostingLockChanged;
        }
        public static bool ServerSupportsHosting => BasisClientMessageRegistry.IsPluginSupplied(BasisAvatarHostProtocol.MessageName);
        public static bool SharingAllowed => ServerSupportsHosting && !BasisNetworkModeration.AvatarHostingBlockedLocally;
        private static void OnHostingLockChanged(bool locked)
        {
            if (!locked || !BasisNetworkModeration.AvatarHostingBlockedLocally) return;
            bool hadShared;
            lock (hostedSync)
            {
                hadShared = hosted.Count > 0;
                hosted.Clear();
            }
            if (hadShared) BasisDeviceManagement.EnqueueOnMainThread(Basis.Scripts.Avatar.BasisLocalAvatarNetworkNotice.OnSharingRevoked);
        }
        public static bool IsHosted(string localUrl) => TryGetHostedUrl(localUrl, out _);
        public static bool TryGetHostedUrl(string localUrl, out string hostedUrl)
        {
            hostedUrl = null;
            if (string.IsNullOrEmpty(localUrl)) return false;
            lock (hostedSync)
            {
                foreach (HostedAvatar entry in hosted)
                {
                    if (entry.LocalUrl != localUrl) continue;
                    hostedUrl = BasisServerHostedBee.BuildUrl(entry.Layout.Id);
                    return true;
                }
            }
            return false;
        }
        public static byte[] OutboundAvatarBytes(BasisLoadableBundle bundle)
        {
            string localUrl = bundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation;
            if (!TryGetHostedUrl(localUrl, out string hostedUrl)) return BasisBundleConversionNetwork.ConvertBasisLoadableBundleToBytes(bundle);
            BasisAvatarNetworkLoad load = new BasisAvatarNetworkLoad { URL = hostedUrl, UnlockPassword = bundle.UnlockPassword, VersionTag = string.Empty };
            return load.EncodeToBytes();
        }
        public static async Task<(bool Shared, byte Reason, string Error)> ShareAsync(string localUrl, string password)
        {
            if (IsHosted(localUrl)) return (true, BasisAvatarHostProtocol.ReasonNone, null);
            if (!SharingAllowed) return (false, BasisAvatarHostProtocol.ReasonDisabled, null);
            if (!BasisIOManagement.TryResolveLocalBeePath(localUrl, out string localPath)) return (false, BasisAvatarHostProtocol.ReasonNone, "The avatar file could not be found.");
            (BasisHostedBeeLayout layout, string error) = await Task.Run(() => BasisHostedBeeLayout.BuildAsync(localPath, password));
            if (layout == null || !BasisAvatarHostProtocol.TryHexToId(layout.Id, out byte[] id)) return (false, BasisAvatarHostProtocol.ReasonNone, error ?? "The avatar file could not be read.");
            HostedAvatar oldest = null;
            lock (hostedSync)
            {
                if (hosted.Count >= BasisAvatarHostProtocol.MaxOffersPerOwner)
                {
                    oldest = hosted[0];
                    hosted.RemoveAt(0);
                }
            }
            if (oldest != null) SendWithdraw(oldest.Id);
            TaskCompletionSource<byte> reply = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
            offerReplies[layout.Id] = reply;
            bool sent = BasisClientMessageRegistry.Send(BasisAvatarHostProtocol.MessageName, writer =>
            {
                writer.Put(BasisAvatarHostProtocol.OpOffer);
                BasisAvatarHostProtocol.WriteId(writer, id);
                writer.Put(layout.VirtualLength);
            });
            if (!sent)
            {
                offerReplies.TryRemove(layout.Id, out _);
                return (false, BasisAvatarHostProtocol.ReasonDisabled, null);
            }
            Task finished = await Task.WhenAny(reply.Task, Task.Delay((int)OfferTimeoutMs));
            offerReplies.TryRemove(layout.Id, out _);
            if (finished != reply.Task) return (false, BasisAvatarHostProtocol.ReasonBusy, "The server did not answer.");
            byte reason = reply.Task.Result;
            if (reason != BasisAvatarHostProtocol.ReasonNone) return (false, reason, null);
            lock (hostedSync)
            {
                hosted.RemoveAll(entry => entry.LocalUrl == localUrl);
                hosted.Add(new HostedAvatar { LocalUrl = localUrl, Layout = layout, Id = id });
            }
            BasisDebug.Log($"Sharing local avatar through the server ({layout.SharedSectionCount} section(s) available, {layout.VirtualLength} bytes addressable).", BasisDebug.LogTag.Networking);
            return (true, BasisAvatarHostProtocol.ReasonNone, null);
        }
        public static void Reset()
        {
            lock (hostedSync) hosted.Clear();
            foreach (KeyValuePair<string, TaskCompletionSource<byte>> reply in offerReplies) reply.Value.TrySetResult(BasisAvatarHostProtocol.ReasonOwnerLeft);
            offerReplies.Clear();
            foreach (KeyValuePair<uint, PendingRequest> request in requests) request.Value.Completion.TrySetResult((null, "Disconnected from the server."));
            requests.Clear();
        }
        private static async Task<(byte[] Data, string Error)> FetchAsync(string id, long start, int length, Action<float> progress, CancellationToken cancellationToken)
        {
            if (!BasisAvatarHostProtocol.TryHexToId(id, out byte[] idBytes)) return (null, "Invalid server hosted avatar id.");
            if (!ServerSupportsHosting) return (null, "This server does not share local avatars.");
            PendingRequest request = new PendingRequest
            {
                RequestId = unchecked((uint)Interlocked.Increment(ref nextRequestId)),
                Buffer = new byte[length],
                LastActivityMs = clock.ElapsedMilliseconds,
                Progress = progress,
                Completion = new TaskCompletionSource<(byte[] Data, string Error)>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            requests[request.RequestId] = request;
            try
            {
                bool sent = BasisClientMessageRegistry.Send(BasisAvatarHostProtocol.MessageName, writer =>
                {
                    writer.Put(BasisAvatarHostProtocol.OpRequest);
                    writer.Put(request.RequestId);
                    BasisAvatarHostProtocol.WriteId(writer, idBytes);
                    writer.Put(start);
                    writer.Put(length);
                });
                if (!sent) return (null, "Not connected to a server that shares local avatars.");
                while (true)
                {
                    Task finished = await Task.WhenAny(request.Completion.Task, Task.Delay(1000));
                    if (finished == request.Completion.Task) return request.Completion.Task.Result;
                    if (cancellationToken.IsCancellationRequested) return (null, "Cancelled");
                    if (clock.ElapsedMilliseconds - Interlocked.Read(ref request.LastActivityMs) > RequestIdleTimeoutMs) return (null, "The server stopped sending this avatar.");
                }
            }
            finally
            {
                requests.TryRemove(request.RequestId, out _);
            }
        }
        private static void Handle(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                if (!reader.TryGetByte(out byte op)) return;
                switch (op)
                {
                    case BasisAvatarHostProtocol.OpOfferResult:
                        {
                            if (!BasisAvatarHostProtocol.TryReadId(reader, out byte[] id) || !reader.TryGetBool(out bool accepted) || !reader.TryGetByte(out byte reason)) return;
                            string key = BasisAvatarHostProtocol.IdToHex(id);
                            if (offerReplies.TryGetValue(key, out TaskCompletionSource<byte> reply)) reply.TrySetResult(accepted ? BasisAvatarHostProtocol.ReasonNone : (reason == BasisAvatarHostProtocol.ReasonNone ? BasisAvatarHostProtocol.ReasonDisabled : reason));
                            break;
                        }
                    case BasisAvatarHostProtocol.OpChunk:
                        {
                            if (!reader.TryGetUInt(out uint requestId) || !reader.TryGetInt(out int offset) || !reader.TryGetInt(out int count)) return;
                            if (!requests.TryGetValue(requestId, out PendingRequest request)) return;
                            if (count <= 0 || count > reader.AvailableBytes || offset != request.Received || count > request.Buffer.Length - request.Received)
                            {
                                request.Completion.TrySetResult((null, "The server sent a damaged piece of this avatar."));
                                return;
                            }
                            reader.GetBytes(request.Buffer, offset, count);
                            request.Received += count;
                            Interlocked.Exchange(ref request.LastActivityMs, clock.ElapsedMilliseconds);
                            request.Progress?.Invoke(request.Received * 100f / request.Buffer.Length);
                            if (request.Received == request.Buffer.Length) request.Completion.TrySetResult((request.Buffer, null));
                            break;
                        }
                    case BasisAvatarHostProtocol.OpFailed:
                        {
                            if (!reader.TryGetUInt(out uint requestId) || !reader.TryGetByte(out byte reason)) return;
                            if (requests.TryGetValue(requestId, out PendingRequest request)) request.Completion.TrySetResult((null, DescribeFailure(reason)));
                            break;
                        }
                    case BasisAvatarHostProtocol.OpFetch:
                        {
                            if (!reader.TryGetUInt(out uint fetchId) || !BasisAvatarHostProtocol.TryReadId(reader, out byte[] id) || !reader.TryGetLong(out long start) || !reader.TryGetInt(out int length)) return;
                            string hex = BasisAvatarHostProtocol.IdToHex(id);
                            _ = Task.Run(() => ServeFetchAsync(fetchId, hex, start, length));
                            break;
                        }
                }
            }
            finally
            {
                reader.Recycle();
            }
        }
        private static async Task ServeFetchAsync(uint fetchId, string id, long start, int length)
        {
            BasisHostedBeeLayout layout = null;
            lock (hostedSync)
            {
                foreach (HostedAvatar entry in hosted)
                {
                    if (entry.Layout.Id == id) layout = entry.Layout;
                }
            }
            if (layout == null || length <= 0 || start < 0 || start > layout.VirtualLength - length)
            {
                SendOwnerFailed(fetchId);
                return;
            }
            byte[] block = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Min(OwnerReadBlockBytes, length));
            try
            {
                for (int blockOffset = 0; blockOffset < length; blockOffset += OwnerReadBlockBytes)
                {
                    int blockLength = Math.Min(OwnerReadBlockBytes, length - blockOffset);
                    string error = await layout.ReadIntoAsync(start + blockOffset, block, blockLength).ConfigureAwait(false);
                    if (error != null)
                    {
                        BasisDebug.LogWarning($"Could not serve shared avatar piece: {error}", BasisDebug.LogTag.Networking);
                        SendOwnerFailed(fetchId);
                        return;
                    }
                    for (int chunkOffset = 0; chunkOffset < blockLength; chunkOffset += BasisAvatarHostProtocol.ChunkBytes)
                    {
                        NetPeer peer = BasisNetworkConnection.LocalPlayerPeer;
                        if (peer == null) return;
                        while (peer.GetPacketsCountInQueue(BasisNetworkCommons.PluginReliableChannel, DeliveryMethod.ReliableOrdered) > MaxQueuedPackets)
                        {
                            await Task.Delay(5).ConfigureAwait(false);
                            if (BasisNetworkConnection.LocalPlayerPeer != peer) return;
                        }
                        OwnerChunk chunk = new OwnerChunk { FetchId = fetchId, WireOffset = blockOffset + chunkOffset, Block = block, From = chunkOffset, Count = Math.Min(BasisAvatarHostProtocol.ChunkBytes, blockLength - chunkOffset) };
                        if (!BasisClientMessageRegistry.Send(BasisAvatarHostProtocol.MessageName, chunk, WriteOwnerChunk)) return;
                    }
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Serving a shared avatar piece failed: {ex.Message}", BasisDebug.LogTag.Networking);
                SendOwnerFailed(fetchId);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(block);
            }
        }
        private struct OwnerChunk
        {
            public uint FetchId;
            public int WireOffset, From, Count;
            public byte[] Block;
        }
        [NoAutoStaticsCleanup] private static readonly Action<NetDataWriter, OwnerChunk> WriteOwnerChunk = (writer, chunk) =>
        {
            writer.Put(BasisAvatarHostProtocol.OpOwnerChunk);
            writer.Put(chunk.FetchId);
            writer.Put(chunk.WireOffset);
            writer.Put(chunk.Count);
            writer.Put(chunk.Block, chunk.From, chunk.Count);
        };
        private static void SendOwnerFailed(uint fetchId)
        {
            BasisClientMessageRegistry.Send(BasisAvatarHostProtocol.MessageName, writer =>
            {
                writer.Put(BasisAvatarHostProtocol.OpOwnerFailed);
                writer.Put(fetchId);
            });
        }
        private static void SendWithdraw(byte[] id)
        {
            BasisClientMessageRegistry.Send(BasisAvatarHostProtocol.MessageName, writer =>
            {
                writer.Put(BasisAvatarHostProtocol.OpWithdraw);
                BasisAvatarHostProtocol.WriteId(writer, id);
            });
        }
        private static string DescribeFailure(byte reason)
        {
            switch (reason)
            {
                case BasisAvatarHostProtocol.ReasonUnknownId: return "The player is no longer sharing this avatar.";
                case BasisAvatarHostProtocol.ReasonOwnerLeft: return "The player stopped sharing this avatar.";
                case BasisAvatarHostProtocol.ReasonOwnerFailed: return "The player could not send this part of the avatar.";
                case BasisAvatarHostProtocol.ReasonTooLarge: return "This avatar is larger than the server allows.";
                case BasisAvatarHostProtocol.ReasonBusy: return "The server is busy sending avatars, try again shortly.";
                case BasisAvatarHostProtocol.ReasonBadRange: return "The server refused this part of the avatar.";
                default: return "The server could not send this avatar.";
            }
        }
    }
}
