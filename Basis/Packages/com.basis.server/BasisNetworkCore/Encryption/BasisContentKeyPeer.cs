using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using static Basis.Network.Core.BasisContentKeyProtocol;
namespace Basis.Network.Core
{
    public sealed class BasisContentKeyPeer
    {
        public delegate bool Sender(Action<NetDataWriter> write);
        private readonly struct Reply
        {
            public readonly ushort Holder;
            public readonly byte Reason;
            public readonly byte[] Commit, HolderPublic, Nonce, Sealed;
            public Reply(ushort holder, byte[] commit, byte[] holderPublic, byte[] nonce, byte[] sealedData) { Holder = holder; Reason = ReasonNone; Commit = commit; HolderPublic = holderPublic; Nonce = nonce; Sealed = sealedData; }
            public Reply(byte reason) { Holder = 0; Reason = reason; Commit = HolderPublic = Nonce = Sealed = null; }
        }
        public const int MaxConcurrentRequests = 32, MaxAttempts = 4, MaxHeld = 8192, MaxSessions = 8192, MaxUnsentWaits = 40;
        private readonly Sender send;
        private readonly Action<Action> offload;
        private readonly int attemptTimeoutMs;
        private readonly byte[] privateKey, publicKey;
        private readonly ConcurrentDictionary<string, uint[]> sessions = new ConcurrentDictionary<string, uint[]>();
        private readonly ConcurrentDictionary<string, byte[]> held = new ConcurrentDictionary<string, byte[]>();
        private readonly ConcurrentQueue<string> heldOrder = new ConcurrentQueue<string>();
        private readonly ConcurrentDictionary<string, Task<byte[]>> owned = new ConcurrentDictionary<string, Task<byte[]>>();
        private readonly ConcurrentDictionary<string, byte[]> ownedGrants = new ConcurrentDictionary<string, byte[]>();
        private readonly ConcurrentDictionary<string, (byte[] ContentId, byte[] Commit)> ownedCommits = new ConcurrentDictionary<string, (byte[], byte[])>();
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<Reply>> pending = new ConcurrentDictionary<uint, TaskCompletionSource<Reply>>();
        private readonly ConcurrentDictionary<string, Task<byte[]>> resolving = new ConcurrentDictionary<string, Task<byte[]>>();
        private readonly SemaphoreSlim slots = new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);
        private int nextRequestId, disposed;
        private long served, received, rejected;
        public long Served => Interlocked.Read(ref served);
        public long Received => Interlocked.Read(ref received);
        public long Rejected => Interlocked.Read(ref rejected);
        public byte[] PublicKey => publicKey;
        public BasisContentKeyPeer(Sender send, Action<Action> offload = null, int attemptTimeoutMs = (int)RequestTimeoutMs)
        {
            this.send = send;
            this.offload = offload ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
            this.attemptTimeoutMs = attemptTimeoutMs;
            NewKeyPair(out privateKey, out publicKey);
        }
        public Task<byte[]> Own(byte[] contentId, Func<Task<byte[]>> build)
        {
            if (!IsId(contentId) || build == null) return Task.FromResult<byte[]>(null);
            string key = Hex(contentId);
            if (owned.TryGetValue(key, out Task<byte[]> existing) && !Failed(existing)) return existing;
            Task<byte[]> task = OwnCore(contentId, build);
            owned[key] = task;
            return task;
        }
        public bool Owns(byte[] contentId) => IsId(contentId) && owned.TryGetValue(Hex(contentId), out Task<byte[]> task) && !Failed(task);
        public Task<byte[]> ResolveAsync(ushort origin, byte[] contentId)
        {
            if (!IsId(contentId) || Volatile.Read(ref disposed) != 0) return Task.FromResult<byte[]>(null);
            if (owned.TryGetValue(Hex(contentId), out Task<byte[]> mine) && !Failed(mine)) return mine;
            string key = string.Concat(origin.ToString(), ":", Hex(contentId));
            if (resolving.TryGetValue(key, out Task<byte[]> existing) && !Failed(existing)) return existing;
            if (resolving.Count >= MaxHeld) resolving.Clear();
            Task<byte[]> task = RequestAsync(origin, contentId);
            resolving[key] = task;
            return task;
        }
        public void Announce()
        {
            if (Volatile.Read(ref disposed) != 0) return;
            foreach ((byte[] contentId, byte[] commit) in ownedCommits.Values) SendOwn(contentId, commit);
            foreach (string key in held.Keys)
            {
                byte[] commit = HexBytes(key);
                send(writer =>
                {
                    writer.Put(OpHave);
                    writer.Put(commit, 0, IdBytes);
                });
            }
        }
        public void Handle(NetDataReader reader)
        {
            if (Volatile.Read(ref disposed) != 0 || !reader.TryGetByte(out byte op)) return;
            switch (op)
            {
                case OpRequest:
                    {
                        if (!reader.TryGetUShort(out ushort requester) || !reader.TryGetUInt(out uint requestId) || !TryReadBytes(reader, IdBytes, out byte[] commit) || !TryReadBytes(reader, PublicKeyBytes, out byte[] requesterPublic)) return;
                        offload(() => Serve(requester, requestId, commit, requesterPublic));
                        break;
                    }
                case OpGrant:
                    {
                        if (!reader.TryGetUShort(out ushort holder) || !reader.TryGetUInt(out uint requestId) || !TryReadBytes(reader, IdBytes, out byte[] commit) || !TryReadBytes(reader, PublicKeyBytes, out byte[] holderPublic) || !TryReadBytes(reader, NonceBytes, out byte[] nonce) || !reader.TryGetUShort(out ushort count) || count > MaxSealedBytes || !TryReadBytes(reader, count, out byte[] sealedData)) return;
                        if (pending.TryGetValue(requestId, out TaskCompletionSource<Reply> reply)) reply.TrySetResult(new Reply(holder, commit, holderPublic, nonce, sealedData));
                        break;
                    }
                case OpDeny:
                    {
                        if (!reader.TryGetUInt(out uint requestId) || !reader.TryGetByte(out byte reason)) return;
                        if (pending.TryGetValue(requestId, out TaskCompletionSource<Reply> reply)) reply.TrySetResult(new Reply(reason == ReasonNone ? ReasonUnavailable : reason));
                        break;
                    }
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            foreach (TaskCompletionSource<Reply> reply in pending.Values) reply.TrySetResult(new Reply(ReasonOwnerLeft));
            pending.Clear();
            resolving.Clear();
            held.Clear();
            ownedGrants.Clear();
            ownedCommits.Clear();
            owned.Clear();
            sessions.Clear();
            Array.Clear(privateKey, 0, privateKey.Length);
        }
        private static bool Failed(Task<byte[]> task) => task.IsCompleted && (task.IsFaulted || task.IsCanceled || task.Result == null);
        private async Task<byte[]> OwnCore(byte[] contentId, Func<Task<byte[]>> build)
        {
            byte[] grant;
            try
            {
                grant = await build().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
            if (!IsValidGrant(grant) || Volatile.Read(ref disposed) != 0) return null;
            byte[] commit = Commit(grant);
            ownedGrants[Hex(commit)] = grant;
            ownedCommits[Hex(contentId)] = (contentId, commit);
            SendOwn(contentId, commit);
            return grant;
        }
        private void SendOwn(byte[] contentId, byte[] commit)
        {
            send(writer =>
            {
                writer.Put(OpOwn);
                writer.Put(contentId, 0, IdBytes);
                writer.Put(commit, 0, IdBytes);
            });
        }
        private async Task<byte[]> RequestAsync(ushort origin, byte[] contentId)
        {
            int unsent = 0;
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(500 * attempt).ConfigureAwait(false);
                if (Volatile.Read(ref disposed) != 0) return null;
                await slots.WaitAsync().ConfigureAwait(false);
                uint requestId = unchecked((uint)Interlocked.Increment(ref nextRequestId));
                TaskCompletionSource<Reply> completion = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending[requestId] = completion;
                try
                {
                    bool sent = send(writer =>
                    {
                        writer.Put(OpRequest);
                        writer.Put(requestId);
                        writer.Put(origin);
                        writer.Put(contentId, 0, IdBytes);
                        writer.Put(publicKey, 0, PublicKeyBytes);
                    });
                    if (!sent)
                    {
                        if (++unsent > MaxUnsentWaits) return null;
                        attempt--;
                        await Task.Delay(250).ConfigureAwait(false);
                        continue;
                    }
                    Task finished = await Task.WhenAny(completion.Task, Task.Delay(attemptTimeoutMs)).ConfigureAwait(false);
                    if (finished != completion.Task) continue;
                    Reply reply = completion.Task.Result;
                    if (reply.Sealed == null)
                    {
                        if (reply.Reason == ReasonUnknown || reply.Reason == ReasonOwnerLeft) return null;
                        continue;
                    }
                    byte[] grant = Open(Session(false, reply.HolderPublic), requestId, reply.Commit, publicKey, reply.HolderPublic, reply.Nonce, reply.Sealed);
                    if (grant == null)
                    {
                        Interlocked.Increment(ref rejected);
                        ushort badHolder = reply.Holder;
                        byte[] badCommit = reply.Commit;
                        send(writer =>
                        {
                            writer.Put(OpBad);
                            writer.Put(badHolder);
                            writer.Put(badCommit, 0, IdBytes);
                        });
                        continue;
                    }
                    Hold(reply.Commit, grant);
                    Interlocked.Increment(ref received);
                    return grant;
                }
                finally
                {
                    pending.TryRemove(requestId, out _);
                    slots.Release();
                }
            }
            return null;
        }
        private void Hold(byte[] commit, byte[] grant)
        {
            string key = Hex(commit);
            if (held.TryAdd(key, grant)) heldOrder.Enqueue(key);
            send(writer =>
            {
                writer.Put(OpHave);
                writer.Put(commit, 0, IdBytes);
            });
            while (held.Count > MaxHeld && heldOrder.TryDequeue(out string oldest))
            {
                if (!held.TryRemove(oldest, out _)) continue;
                byte[] dropped = HexBytes(oldest);
                send(writer =>
                {
                    writer.Put(OpDrop);
                    writer.Put(dropped, 0, IdBytes);
                });
            }
        }
        private void Serve(ushort requester, uint requestId, byte[] commit, byte[] requesterPublic)
        {
            try
            {
                string key = Hex(commit);
                if (Volatile.Read(ref disposed) != 0) return;
                if (!ownedGrants.TryGetValue(key, out byte[] grant) && !held.TryGetValue(key, out grant))
                {
                    Deny(requester, requestId, ReasonUnknown);
                    return;
                }
                byte[] sealedData = Seal(Session(true, requesterPublic), requestId, commit, requesterPublic, publicKey, grant, out byte[] nonce);
                if (sealedData == null)
                {
                    Deny(requester, requestId, ReasonUnavailable);
                    return;
                }
                send(writer =>
                {
                    writer.Put(OpGrant);
                    writer.Put(requester);
                    writer.Put(requestId);
                    writer.Put(publicKey, 0, PublicKeyBytes);
                    writer.Put(nonce, 0, NonceBytes);
                    writer.Put((ushort)sealedData.Length);
                    writer.Put(sealedData, 0, sealedData.Length);
                });
                Interlocked.Increment(ref served);
            }
            catch (Exception)
            {
                Deny(requester, requestId, ReasonUnavailable);
            }
        }
        private void Deny(ushort requester, uint requestId, byte reason)
        {
            send(writer =>
            {
                writer.Put(OpDeny);
                writer.Put(requester);
                writer.Put(requestId);
                writer.Put(reason);
            });
        }
        private uint[] Session(bool asHolder, byte[] peerPublic)
        {
            if (!IsKey(peerPublic)) return null;
            string key = string.Concat(asHolder ? "h" : "r", Hex(peerPublic));
            if (sessions.TryGetValue(key, out uint[] session)) return session;
            session = asHolder ? SessionKey(privateKey, peerPublic, peerPublic, publicKey) : SessionKey(privateKey, peerPublic, publicKey, peerPublic);
            if (session == null) return null;
            if (sessions.Count >= MaxSessions) sessions.Clear();
            sessions[key] = session;
            return session;
        }
        private static byte[] HexBytes(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int index = 0; index < bytes.Length; index++) bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
            return bytes;
        }
    }
}
