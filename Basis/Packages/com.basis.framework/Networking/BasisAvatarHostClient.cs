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
            public byte[] IdBytes;
            public long Start;
            public bool Direct;
            public ushort Owner;
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
            public bool Broadcast;
        }
        private const int OwnerReadBlockBytes = 1024 * 1024, MaxQueuedPackets = 256;
        private const long RequestIdleTimeoutMs = 60000, OfferTimeoutMs = 15000;
        private static readonly ConcurrentDictionary<uint, PendingRequest> requests = new ConcurrentDictionary<uint, PendingRequest>();
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<byte>> offerReplies = new ConcurrentDictionary<string, TaskCompletionSource<byte>>();
        [NoAutoStaticsCleanup] private static readonly object hostedSync = new object();
        private static readonly List<HostedAvatar> hosted = new List<HostedAvatar>();
        private static int nextRequestId;
        private static readonly ConcurrentDictionary<ushort, int> directServes = new ConcurrentDictionary<ushort, int>();
        [ThreadStatic] private static NetDataWriter directWriter;
        [NoAutoStaticsCleanup] private static readonly Stopwatch clock = Stopwatch.StartNew();
        public static void Initialize()
        {
            BasisServerHostedBee.Fetcher = FetchAsync;
            BasisClientMessageRegistry.RegisterClientPlugin(BasisAvatarHostProtocol.MessageName, Handle);
            BasisP2PManager.OnDirectPluginMessage -= HandleDirect;
            BasisP2PManager.OnDirectPluginMessage += HandleDirect;
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
                    if (entry.LocalUrl != localUrl || !entry.Broadcast) continue;
                    hostedUrl = BasisServerHostedBee.BuildUrl(entry.Layout.Id, entry.Layout.IsVrm);
                    return true;
                }
            }
            return false;
        }
        public static byte[] OutboundAvatarBytes(BasisLoadableBundle bundle, byte loadMode)
        {
            string localUrl = bundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation;
            BasisAvatarNetworkLoad load = TryGetHostedUrl(localUrl, out string hostedUrl) ? new BasisAvatarNetworkLoad { URL = hostedUrl, UnlockPassword = bundle.UnlockPassword, VersionTag = string.Empty } : BasisBundleConversionNetwork.ConvertToNetwork(bundle);
            if (loadMode == Basis.Scripts.BasisSdk.Players.BasisPlayer.LoadModeNetworkDownloadable) load.UnlockPassword = BasisContentKeyClient.Share(load.URL, load.VersionTag, bundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation, bundle?.UnlockPassword);
            return load.EncodeToBytes();
        }
        public static async Task<(bool Shared, byte Reason, string Error)> ShareAsync(string localUrl, string password)
        {
            if (IsHosted(localUrl)) return (true, BasisAvatarHostProtocol.ReasonNone, null);
            if (!BasisIOManagement.TryResolveLocalBeePath(localUrl, out string localPath)) return (false, BasisAvatarHostProtocol.ReasonNone, "The avatar file could not be found.");
            (bool shared, byte reason, string error, _) = await HostAsync(localUrl, localPath, password, true);
            return (shared, reason, error);
        }
        public static async Task<(bool Shared, byte Reason, string Error, BasisHostedBeeLayout Layout)> HostAsync(string avatarUrl, string filePath, string password, bool broadcast)
        {
            lock (hostedSync)
            {
                foreach (HostedAvatar entry in hosted)
                {
                    if (entry.LocalUrl != avatarUrl) continue;
                    entry.Broadcast |= broadcast;
                    return (true, BasisAvatarHostProtocol.ReasonNone, null, entry.Layout);
                }
            }
            if (!SharingAllowed) return (false, BasisAvatarHostProtocol.ReasonDisabled, null, null);
            (BasisHostedBeeLayout layout, string error) = await Task.Run(() => BasisHostedBeeLayout.BuildAsync(filePath, password));
            if (layout == null || !BasisAvatarHostProtocol.TryHexToId(layout.Id, out byte[] id)) return (false, BasisAvatarHostProtocol.ReasonNone, error ?? "The avatar file could not be read.", null);
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
                writer.Put(BasisAvatarHostProtocol.FlagDirect);
            });
            if (!sent)
            {
                offerReplies.TryRemove(layout.Id, out _);
                return (false, BasisAvatarHostProtocol.ReasonDisabled, null, null);
            }
            Task finished = await Task.WhenAny(reply.Task, Task.Delay((int)OfferTimeoutMs));
            offerReplies.TryRemove(layout.Id, out _);
            if (finished != reply.Task) return (false, BasisAvatarHostProtocol.ReasonBusy, "The server did not answer.", null);
            byte reason = reply.Task.Result;
            if (reason != BasisAvatarHostProtocol.ReasonNone) return (false, reason, null, null);
            lock (hostedSync)
            {
                hosted.RemoveAll(entry => entry.LocalUrl == avatarUrl);
                hosted.Add(new HostedAvatar { LocalUrl = avatarUrl, Layout = layout, Id = id, Broadcast = broadcast });
            }
            BasisDebug.Log($"Sharing avatar through the server ({layout.SharedSectionCount} section(s) available, {layout.VirtualLength} bytes addressable, {(broadcast ? "everyone" : "on request")}).", BasisDebug.LogTag.Networking);
            return (true, BasisAvatarHostProtocol.ReasonNone, null, layout);
        }
        public static bool AskShare(ushort ownerPlayerId, string avatarUrl)
        {
            if (!ServerSupportsHosting || BasisNetworkModeration.AvatarShareRequestsBlockedLocally || BasisNetworkConnection.NetworkClient == null || BasisNetworkConnection.NetworkClient.ProtocolVersion < BasisNetworkVersion.ShareAskVersion || string.IsNullOrEmpty(avatarUrl) || avatarUrl.Length > BasisAvatarHostProtocol.MaxAskUrlChars) return false;
            return BasisClientMessageRegistry.Send(BasisAvatarHostProtocol.MessageName, writer =>
            {
                writer.Put(BasisAvatarHostProtocol.OpAskShare);
                writer.Put(ownerPlayerId);
                writer.Put(avatarUrl);
            });
        }
        public static void AnswerShare(ushort requesterPlayerId, string avatarUrl, BasisHostedBeeLayout layout)
        {
            byte[] id = null;
            bool accepted = layout != null && BasisAvatarHostProtocol.TryHexToId(layout.Id, out id);
            BasisClientMessageRegistry.Send(BasisAvatarHostProtocol.MessageName, writer =>
            {
                writer.Put(BasisAvatarHostProtocol.OpShareAnswer);
                writer.Put(requesterPlayerId);
                writer.Put(avatarUrl);
                writer.Put(accepted);
                if (!accepted) return;
                BasisAvatarHostProtocol.WriteId(writer, id);
                writer.Put(layout.IsVrm);
            });
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
                IdBytes = idBytes,
                Start = start,
                Buffer = new byte[length],
                LastActivityMs = clock.ElapsedMilliseconds,
                Progress = progress,
                Completion = new TaskCompletionSource<(byte[] Data, string Error)>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            requests[request.RequestId] = request;
            try
            {
                if (!SendRelayRequest(request, BasisP2PManager.HasAnyConnectedSession())) return (null, "Not connected to a server that shares local avatars.");
                while (true)
                {
                    Task finished = await Task.WhenAny(request.Completion.Task, Task.Delay(1000));
                    if (finished == request.Completion.Task) return request.Completion.Task.Result;
                    if (cancellationToken.IsCancellationRequested) return (null, "Cancelled");
                    if (request.Direct && !BasisP2PManager.TryGetP2PPeer(request.Owner, out _)) FallBackToRelay(request);
                    if (clock.ElapsedMilliseconds - Interlocked.Read(ref request.LastActivityMs) > RequestIdleTimeoutMs) return (null, "The server stopped sending this avatar.");
                }
            }
            finally
            {
                requests.TryRemove(request.RequestId, out _);
            }
        }
        private static bool SendRelayRequest(PendingRequest request, bool allowDirect)
        {
            return BasisClientMessageRegistry.Send(BasisAvatarHostProtocol.MessageName, writer =>
            {
                writer.Put(BasisAvatarHostProtocol.OpRequest);
                writer.Put(request.RequestId);
                BasisAvatarHostProtocol.WriteId(writer, request.IdBytes);
                writer.Put(request.Start);
                writer.Put(request.Buffer.Length);
                if (allowDirect) writer.Put(BasisAvatarHostProtocol.FlagDirect);
            });
        }
        private static void FallBackToRelay(PendingRequest request)
        {
            lock (request)
            {
                if (!request.Direct) return;
                request.Direct = false;
                request.Received = 0;
            }
            if (!SendRelayRequest(request, false)) request.Completion.TrySetResult((null, "Not connected to a server that shares local avatars."));
        }
        private static void UseDirect(uint requestId, ushort owner)
        {
            if (!requests.TryGetValue(requestId, out PendingRequest request)) return;
            lock (request)
            {
                request.Direct = true;
                request.Owner = owner;
                request.Received = 0;
            }
            bool sent = SendDirect(owner, writer =>
            {
                writer.Put(BasisAvatarHostProtocol.OpDirectRequest);
                writer.Put(requestId);
                BasisAvatarHostProtocol.WriteId(writer, request.IdBytes);
                writer.Put(request.Start);
                writer.Put(request.Buffer.Length);
            });
            if (!sent) FallBackToRelay(request);
        }
        private static bool SendDirect(ushort player, Action<NetDataWriter> write)
        {
            if (!BasisClientMessageRegistry.TryGetPluginId(BasisAvatarHostProtocol.MessageName, out ushort pluginId)) return false;
            NetDataWriter writer = directWriter ??= new NetDataWriter(true, BasisAvatarHostProtocol.ChunkBytes + 32);
            writer.Reset();
            writer.Put(pluginId);
            write(writer);
            return BasisP2PManager.SendDirectTo(player, writer, BasisNetworkCommons.PluginSequencedChannel, DeliveryMethod.ReliableOrdered);
        }
        private static int DirectBacklog(ushort player)
        {
            if (!BasisP2PManager.TryGetP2PPeer(player, out NetPeer peer)) return -1;
            return peer.GetPacketsCountInQueue(BasisNetworkCommons.PluginSequencedChannel, DeliveryMethod.ReliableOrdered);
        }
        private static void AcceptChunk(PendingRequest request, NetPacketReader reader, int offset, int count, bool direct, ushort sender)
        {
            bool damaged = false, complete;
            lock (request)
            {
                if (request.Direct != direct || (direct && request.Owner != sender)) return;
                if (count <= 0 || count > reader.AvailableBytes || offset != request.Received || count > request.Buffer.Length - request.Received) damaged = true;
                else
                {
                    reader.GetBytes(request.Buffer, offset, count);
                    request.Received += count;
                }
                complete = request.Received == request.Buffer.Length;
            }
            if (damaged)
            {
                if (direct) FallBackToRelay(request);
                else request.Completion.TrySetResult((null, "The server sent a damaged piece of this avatar."));
                return;
            }
            Interlocked.Exchange(ref request.LastActivityMs, clock.ElapsedMilliseconds);
            request.Progress?.Invoke(request.Received * 100f / request.Buffer.Length);
            if (complete) request.Completion.TrySetResult((request.Buffer, null));
        }
        private static void HandleDirect(ushort sender, NetPacketReader reader, DeliveryMethod deliveryMethod)
        {
            if (!BasisClientMessageRegistry.TryGetPluginId(BasisAvatarHostProtocol.MessageName, out ushort pluginId)) return;
            int start = reader.Position;
            if (!reader.TryGetUShort(out ushort id) || id != pluginId || !reader.TryGetByte(out byte op))
            {
                reader.SetPosition(start);
                return;
            }
            switch (op)
            {
                case BasisAvatarHostProtocol.OpDirectRequest:
                    {
                        if (!reader.TryGetUInt(out uint requestId) || !BasisAvatarHostProtocol.TryReadId(reader, out byte[] idBytes) || !reader.TryGetLong(out long rangeStart) || !reader.TryGetInt(out int length)) return;
                        if (directServes.AddOrUpdate(sender, 1, (_, count) => count + 1) > BasisAvatarHostProtocol.MaxDirectServesPerRequester)
                        {
                            ReleaseDirectServe(sender);
                            SendDirectFailed(sender, requestId);
                            return;
                        }
                        string hex = BasisAvatarHostProtocol.IdToHex(idBytes);
                        _ = Task.Run(async () =>
                        {
                            try { await ServeAsync(requestId, hex, rangeStart, length, sender).ConfigureAwait(false); }
                            finally { ReleaseDirectServe(sender); }
                        });
                        break;
                    }
                case BasisAvatarHostProtocol.OpDirectChunk:
                    {
                        if (!reader.TryGetUInt(out uint requestId) || !reader.TryGetInt(out int offset) || !reader.TryGetInt(out int count)) return;
                        if (requests.TryGetValue(requestId, out PendingRequest request)) AcceptChunk(request, reader, offset, count, true, sender);
                        break;
                    }
                case BasisAvatarHostProtocol.OpDirectFailed:
                    {
                        if (!reader.TryGetUInt(out uint requestId)) return;
                        if (requests.TryGetValue(requestId, out PendingRequest request) && request.Direct && request.Owner == sender) FallBackToRelay(request);
                        break;
                    }
            }
        }
        private static void ReleaseDirectServe(ushort requester)
        {
            if (directServes.AddOrUpdate(requester, 0, (_, count) => count - 1) <= 0) directServes.TryRemove(requester, out _);
        }
        private static void SendDirectFailed(ushort requester, uint requestId)
        {
            SendDirect(requester, writer =>
            {
                writer.Put(BasisAvatarHostProtocol.OpDirectFailed);
                writer.Put(requestId);
            });
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
                            if (requests.TryGetValue(requestId, out PendingRequest request)) AcceptChunk(request, reader, offset, count, false, 0);
                            break;
                        }
                    case BasisAvatarHostProtocol.OpUseDirect:
                        {
                            if (reader.TryGetUInt(out uint requestId) && reader.TryGetUShort(out ushort owner)) UseDirect(requestId, owner);
                            break;
                        }
                    case BasisAvatarHostProtocol.OpFailed:
                        {
                            if (!reader.TryGetUInt(out uint requestId) || !reader.TryGetByte(out byte reason)) return;
                            if (requests.TryGetValue(requestId, out PendingRequest request) && !request.Direct) request.Completion.TrySetResult((null, DescribeFailure(reason)));
                            break;
                        }
                    case BasisAvatarHostProtocol.OpShareAsked:
                        {
                            if (!reader.TryGetUShort(out ushort requester) || !reader.TryGetString(out string url) || string.IsNullOrEmpty(url)) return;
                            BasisDeviceManagement.EnqueueOnMainThread(() => Basis.Scripts.Avatar.BasisAvatarShareRequests.OnAsked(requester, url));
                            break;
                        }
                    case BasisAvatarHostProtocol.OpShareAnswered:
                        {
                            if (!reader.TryGetUShort(out ushort owner) || !reader.TryGetString(out string url) || !reader.TryGetBool(out bool accepted)) return;
                            byte[] id = null;
                            bool vrm = false;
                            if (accepted && (!BasisAvatarHostProtocol.TryReadId(reader, out id) || !reader.TryGetBool(out vrm))) return;
                            string hostedUrl = accepted ? BasisServerHostedBee.BuildUrl(BasisAvatarHostProtocol.IdToHex(id), vrm) : null;
                            BasisDeviceManagement.EnqueueOnMainThread(() => Basis.Scripts.Avatar.BasisAvatarShareRequests.OnAnswered(owner, url, hostedUrl));
                            break;
                        }
                    case BasisAvatarHostProtocol.OpFetch:
                        {
                            if (!reader.TryGetUInt(out uint fetchId) || !BasisAvatarHostProtocol.TryReadId(reader, out byte[] id) || !reader.TryGetLong(out long start) || !reader.TryGetInt(out int length)) return;
                            string hex = BasisAvatarHostProtocol.IdToHex(id);
                            _ = Task.Run(() => ServeAsync(fetchId, hex, start, length, -1));
                            break;
                        }
                }
            }
            finally
            {
                reader.Recycle();
            }
        }
        private static async Task ServeAsync(uint fetchId, string id, long start, int length, int directTo)
        {
            bool direct = directTo >= 0;
            ushort requester = (ushort)Math.Max(0, directTo);
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
                if (direct) SendDirectFailed(requester, fetchId);
                else SendOwnerFailed(fetchId);
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
                        if (direct) SendDirectFailed(requester, fetchId);
                        else SendOwnerFailed(fetchId);
                        return;
                    }
                    for (int chunkOffset = 0; chunkOffset < blockLength; chunkOffset += BasisAvatarHostProtocol.ChunkBytes)
                    {
                        OwnerChunk chunk = new OwnerChunk { FetchId = fetchId, WireOffset = blockOffset + chunkOffset, Block = block, From = chunkOffset, Count = Math.Min(BasisAvatarHostProtocol.ChunkBytes, blockLength - chunkOffset) };
                        if (direct)
                        {
                            int backlog;
                            while ((backlog = DirectBacklog(requester)) > MaxQueuedPackets) await Task.Delay(5).ConfigureAwait(false);
                            if (backlog < 0 || !SendDirect(requester, writer => WriteDirectChunk(writer, chunk))) return;
                            continue;
                        }
                        NetPeer peer = BasisNetworkConnection.LocalPlayerPeer;
                        if (peer == null) return;
                        while (peer.GetPacketsCountInQueue(BasisNetworkCommons.PluginReliableChannel, DeliveryMethod.ReliableOrdered) > MaxQueuedPackets)
                        {
                            await Task.Delay(5).ConfigureAwait(false);
                            if (BasisNetworkConnection.LocalPlayerPeer != peer) return;
                        }
                        if (!BasisClientMessageRegistry.Send(BasisAvatarHostProtocol.MessageName, chunk, WriteOwnerChunk)) return;
                    }
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Serving a shared avatar piece failed: {ex.Message}", BasisDebug.LogTag.Networking);
                if (direct) SendDirectFailed(requester, fetchId);
                else SendOwnerFailed(fetchId);
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
        private static void WriteDirectChunk(NetDataWriter writer, OwnerChunk chunk)
        {
            writer.Put(BasisAvatarHostProtocol.OpDirectChunk);
            writer.Put(chunk.FetchId);
            writer.Put(chunk.WireOffset);
            writer.Put(chunk.Count);
            writer.Put(chunk.Block, chunk.From, chunk.Count);
        }
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
