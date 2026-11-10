using Basis.Network.Core;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using static Basis.Network.Core.BasisContentKeyProtocol;
using static SerializableBasis;
namespace Basis.Network.Server.Generic
{
    public static class BasisContentKeyRelay
    {
        public abstract class WireSink : IBasisContentKeySink
        {
            public abstract bool IsConnected(int peerId);
            protected abstract bool Send(int peerId, Action<NetDataWriter> write);
            public bool ForwardRequest(int holderPeerId, int requesterPeerId, uint requestId, byte[] commit, byte[] requesterPublic)
            {
                return Send(holderPeerId, writer =>
                {
                    writer.Put(OpRequest);
                    writer.Put((ushort)requesterPeerId);
                    writer.Put(requestId);
                    writer.Put(commit, 0, IdBytes);
                    writer.Put(requesterPublic, 0, PublicKeyBytes);
                });
            }
            public void ForwardGrant(int requesterPeerId, int holderPeerId, uint requestId, byte[] commit, byte[] holderPublic, byte[] nonce, byte[] data, int offset, int count)
            {
                Send(requesterPeerId, writer =>
                {
                    writer.Put(OpGrant);
                    writer.Put((ushort)holderPeerId);
                    writer.Put(requestId);
                    writer.Put(commit, 0, IdBytes);
                    writer.Put(holderPublic, 0, PublicKeyBytes);
                    writer.Put(nonce, 0, NonceBytes);
                    writer.Put((ushort)count);
                    writer.Put(data, offset, count);
                });
            }
            public void Deny(int requesterPeerId, uint requestId, byte reason)
            {
                Send(requesterPeerId, writer =>
                {
                    writer.Put(OpDeny);
                    writer.Put(requestId);
                    writer.Put(reason);
                });
            }
        }
        private sealed class PeerSink : WireSink
        {
            public override bool IsConnected(int peerId) => NetworkServer.AuthenticatedPeers.ContainsKey(peerId);
            protected override bool Send(int peerId, Action<NetDataWriter> write) => NetworkServer.AuthenticatedPeers.TryGetValue(peerId, out NetPeer peer) && BasisServerMessageRegistry.SendToPeer(peer, MessageName, write);
        }
        public static readonly BasisContentKeyLedger Ledger = new BasisContentKeyLedger(new PeerSink());
        private static readonly ConcurrentDictionary<string, string> custody = new ConcurrentDictionary<string, string>();
        private static readonly ConcurrentDictionary<string, byte> preparing = new ConcurrentDictionary<string, byte>();
        private static Timer ticker;
        public static void Register()
        {
            BasisServerMessageRegistry.RegisterServerPlugin(MessageName, DeliveryMethod.ReliableOrdered, Handle);
            BasisServerMessageRegistry.PeerSubscribed -= OnSubscribed;
            BasisServerMessageRegistry.PeerSubscribed += OnSubscribed;
            ticker ??= new Timer(_ => Ledger.Tick(), null, 250, 250);
        }
        public static void RemovePeer(int peerId) => Ledger.RemovePeer(peerId);
        private static void OnSubscribed(NetPeer peer) => Ledger.Reachable(peer.Id);
        public static void StampFromPeer(ref LocalLoadResource resource, int peerId)
        {
            if (IsMarker(resource.UnlockPassword)) resource.UnlockPassword = WithOrigin(resource.UnlockPassword, (ushort)peerId);
        }
        public static void TakeCustody(ref LocalLoadResource resource)
        {
            string password = resource.UnlockPassword;
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(resource.CombinedURL)) return;
            if (IsMarker(password))
            {
                KeepKeys(resource);
                return;
            }
            if (!string.IsNullOrEmpty(resource.LoadedNetID)) custody[resource.LoadedNetID] = password;
            resource.UnlockPassword = ServeFromServer(resource.CombinedURL, password);
        }
        private static void KeepKeys(LocalLoadResource resource)
        {
            if (!TryParseMarker(resource.UnlockPassword, out ushort origin, out byte[] contentId) || origin == ServerOrigin) return;
            contentId ??= ContentId(resource.CombinedURL, string.Empty);
            if (Ledger.HeldByServer(contentId) != null) return;
            string url = resource.CombinedURL;
            Ledger.Fetch(origin, contentId, grant =>
            {
                if (grant == null) BNL.LogWarning($"Could not keep the keys for {url}: players joining after its loader leaves will not be able to open it.");
            });
        }
        public static string ServeFromServer(string url, string password)
        {
            if (string.IsNullOrEmpty(password) || IsMarker(password) || string.IsNullOrEmpty(url)) return password;
            byte[] contentId = ContentId(url, string.Empty);
            string marker = Marker(ServerOrigin, contentId);
            if (Ledger.HeldByServer(contentId) != null) return marker;
            byte[] grant = GrantFromToken(password);
            if (grant != null)
            {
                Ledger.Hold(contentId, grant);
                return marker;
            }
            string key = string.Concat(url, "|", password);
            if (!preparing.TryAdd(key, 0)) return marker;
            _ = BasisServerBeeKeys.DeriveAsync(url, password).ContinueWith(task =>
            {
                preparing.TryRemove(key, out _);
                (byte[] derived, string error) = task.Result;
                if (derived != null) Ledger.Hold(contentId, derived);
                else BNL.LogWarning($"Could not prepare the keys for {url}: {error}");
            }, TaskContinuationOptions.ExecuteSynchronously);
            return marker;
        }
        public static string SecretFor(LocalLoadResource resource) => custody.TryGetValue(resource.LoadedNetID ?? string.Empty, out string secret) ? secret : resource.UnlockPassword;
        public static bool NeedsKeysToSave(LocalLoadResource resource) => IsMarker(SecretFor(resource));
        public static void FetchKeysToSave(LocalLoadResource resource, Action<bool> done)
        {
            if (!TryParseMarker(resource.UnlockPassword, out ushort origin, out byte[] contentId))
            {
                done(false);
                return;
            }
            contentId ??= ContentId(resource.CombinedURL, string.Empty);
            byte[] held = Ledger.HeldByServer(contentId);
            if (held != null)
            {
                custody[resource.LoadedNetID] = Token(held);
                done(true);
                return;
            }
            Ledger.Fetch(origin, contentId, grant =>
            {
                if (grant != null) custody[resource.LoadedNetID] = Token(grant);
                done(grant != null);
            });
        }
        public static void Forget(string loadedNetId)
        {
            if (!string.IsNullOrEmpty(loadedNetId)) custody.TryRemove(loadedNetId, out _);
        }
        public static void Reset() => custody.Clear();
        private static void Handle(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                Dispatch(Ledger, peer.Id, reader);
            }
            finally
            {
                reader.Recycle();
            }
        }
        public static void Dispatch(BasisContentKeyLedger ledger, int peerId, NetDataReader reader)
        {
            if (!reader.TryGetByte(out byte op)) return;
            switch (op)
            {
                case OpOwn:
                    if (TryReadBytes(reader, IdBytes, out byte[] ownedId) && TryReadBytes(reader, IdBytes, out byte[] ownedCommit)) ledger.Own(peerId, ownedId, ownedCommit);
                    break;
                case OpHave:
                    if (TryReadBytes(reader, IdBytes, out byte[] heldCommit)) ledger.Have(peerId, heldCommit);
                    break;
                case OpDrop:
                    if (TryReadBytes(reader, IdBytes, out byte[] droppedCommit)) ledger.Drop(peerId, droppedCommit);
                    break;
                case OpRequest:
                    if (reader.TryGetUInt(out uint requestId) && reader.TryGetUShort(out ushort origin) && TryReadBytes(reader, IdBytes, out byte[] contentId) && TryReadBytes(reader, PublicKeyBytes, out byte[] requesterPublic)) ledger.Request(peerId, requestId, origin, contentId, requesterPublic);
                    break;
                case OpGrant:
                    {
                        if (!reader.TryGetUShort(out ushort requester) || !reader.TryGetUInt(out uint grantedId) || !TryReadBytes(reader, PublicKeyBytes, out byte[] holderPublic) || !TryReadBytes(reader, NonceBytes, out byte[] nonce) || !reader.TryGetUShort(out ushort count)) return;
                        if (count <= TagBytes || count > MaxSealedBytes || count > reader.AvailableBytes)
                        {
                            ledger.Denied(peerId, requester, grantedId, ReasonUnavailable);
                            return;
                        }
                        ledger.Grant(peerId, requester, grantedId, holderPublic, nonce, reader.RawData, reader.Position, count);
                        break;
                    }
                case OpDeny:
                    if (reader.TryGetUShort(out ushort denied) && reader.TryGetUInt(out uint deniedId) && reader.TryGetByte(out byte reason)) ledger.Denied(peerId, denied, deniedId, reason);
                    break;
                case OpBad:
                    if (reader.TryGetUShort(out ushort holder) && TryReadBytes(reader, IdBytes, out byte[] badCommit)) ledger.Bad(peerId, holder, badCommit);
                    break;
            }
        }
    }
}
