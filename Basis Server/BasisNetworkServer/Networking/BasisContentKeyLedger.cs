using Basis.Network.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using static Basis.Network.Core.BasisContentKeyProtocol;
namespace Basis.Network.Server.Generic
{
    public interface IBasisContentKeySink
    {
        bool IsConnected(int peerId);
        bool ForwardRequest(int holderPeerId, int requesterPeerId, uint requestId, byte[] commit, byte[] requesterPublic);
        void ForwardGrant(int requesterPeerId, int holderPeerId, uint requestId, byte[] commit, byte[] holderPublic, byte[] nonce, byte[] data, int offset, int count);
        void Deny(int requesterPeerId, uint requestId, byte reason);
    }
    public sealed class BasisContentKeyLedger
    {
        private readonly struct Id : IEquatable<Id>
        {
            private readonly ulong a, b;
            public Id(byte[] bytes)
            {
                a = BitConverter.ToUInt64(bytes, 0);
                b = BitConverter.ToUInt64(bytes, 8);
            }
            public bool Equals(Id other) => a == other.a && b == other.b;
            public override bool Equals(object obj) => obj is Id other && Equals(other);
            public override int GetHashCode() => unchecked((int)a ^ (int)(a >> 32) * 31 ^ (int)b * 17 ^ (int)(b >> 32));
            public byte[] Bytes()
            {
                byte[] bytes = new byte[IdBytes];
                BitConverter.GetBytes(a).CopyTo(bytes, 0);
                BitConverter.GetBytes(b).CopyTo(bytes, 8);
                return bytes;
            }
        }
        private sealed class Ask
        {
            public int Requester, Holder = -1;
            public uint RequestId;
            public ushort Origin;
            public Id ContentId, Commit;
            public byte[] CommitBytes, Public;
            public long Started;
            public bool Dead;
            public List<int> Tried;
            public Action<byte[]> Done;
        }
        private sealed class Content
        {
            public Id Commit;
            public byte[] CommitBytes, ServerGrant;
            public readonly List<int> Holders = new List<int>();
            public readonly HashSet<int> HolderSet = new HashSet<int>();
            public readonly HashSet<int> Owners = new HashSet<int>();
            public readonly Queue<Ask> Waiting = new Queue<Ask>();
            public readonly List<(ushort Origin, Id ContentId)> OwnerKeys = new List<(ushort, Id)>();
            public HashSet<int> Rejected;
        }
        private sealed class Owner
        {
            public Id Commit;
            public bool Departed;
        }
        private sealed class PeerState
        {
            public int Inflight, Pending, RecentIndex;
            public bool Unreachable;
            public readonly HashSet<Id> Holding = new HashSet<Id>();
            public readonly List<(ushort Origin, Id ContentId)> Owns = new List<(ushort, Id)>();
            public readonly HashSet<(ushort Origin, Id ContentId)> Awaited = new HashSet<(ushort, Id)>();
            public readonly HashSet<Ask> Asked = new HashSet<Ask>(), Serving = new HashSet<Ask>();
            public readonly (Id Commit, int Holder)[] Recent = new (Id, int)[64];
        }
        private readonly IBasisContentKeySink sink;
        private readonly Func<long> clock;
        private readonly object sync = new object();
        private readonly Random random = new Random();
        private readonly byte[] serverPrivate, serverPublic;
        private readonly Dictionary<string, uint[]> serverSessions = new Dictionary<string, uint[]>();
        private readonly Dictionary<(int Requester, uint RequestId), Ask> live = new Dictionary<(int, uint), Ask>();
        private readonly Queue<Ask> timeline = new Queue<Ask>();
        private readonly Dictionary<(ushort Origin, Id ContentId), Owner> owners = new Dictionary<(ushort, Id), Owner>();
        private readonly Dictionary<(ushort Origin, Id ContentId), List<Ask>> awaiting = new Dictionary<(ushort, Id), List<Ask>>();
        private readonly Dictionary<Id, Content> contents = new Dictionary<Id, Content>();
        private readonly HashSet<Content> queued = new HashSet<Content>();
        private readonly Dictionary<int, PeerState> peers = new Dictionary<int, PeerState>();
        private readonly List<Content> scratch = new List<Content>();
        private uint serverRequestId;
        public BasisContentKeyLedger(IBasisContentKeySink sink, Func<long> clock = null)
        {
            this.sink = sink;
            if (clock == null)
            {
                Stopwatch watch = Stopwatch.StartNew();
                clock = () => watch.ElapsedMilliseconds;
            }
            this.clock = clock;
            NewKeyPair(out serverPrivate, out serverPublic);
        }
        public int LiveCount { get { lock (sync) return live.Count; } }
        public int ContentCount { get { lock (sync) return contents.Count; } }
        public int HolderCount(byte[] commit) { lock (sync) return IsId(commit) && contents.TryGetValue(new Id(commit), out Content content) ? content.Holders.Count : 0; }
        public int InflightFor(int peerId) { lock (sync) return peers.TryGetValue(peerId, out PeerState state) ? state.Inflight : 0; }
        public void Own(int peerId, byte[] contentId, byte[] commit)
        {
            if (!IsId(contentId) || !IsId(commit) || peerId < 0 || peerId >= ServerOrigin) return;
            lock (sync)
            {
                (ushort, Id) key = ((ushort)peerId, new Id(contentId));
                PeerState state = Peer(peerId);
                Id commitId = new Id(commit);
                if (owners.TryGetValue(key, out Owner owner))
                {
                    if (owner.Departed || owner.Commit.Equals(commitId)) return;
                    if (contents.TryGetValue(owner.Commit, out Content previous)) previous.Owners.Remove(peerId);
                    owner.Commit = commitId;
                }
                else
                {
                    if (state.Owns.Count >= MaxOwnedPerPeer) return;
                    owners[key] = new Owner { Commit = commitId };
                    state.Owns.Add(key);
                }
                Content content = GetContent(commitId, commit);
                content.OwnerKeys.Add(key);
                content.Owners.Add(peerId);
                content.Rejected?.Remove(peerId);
                AddHolder(content, peerId);
                Resolve(key, content);
                Pump(content);
            }
        }
        public void Have(int peerId, byte[] commit)
        {
            if (!IsId(commit)) return;
            lock (sync)
            {
                if (!contents.TryGetValue(new Id(commit), out Content content) || (content.Rejected != null && content.Rejected.Contains(peerId))) return;
                AddHolder(content, peerId);
                Pump(content);
            }
        }
        public void Drop(int peerId, byte[] commit)
        {
            if (!IsId(commit)) return;
            lock (sync)
            {
                if (contents.TryGetValue(new Id(commit), out Content content) && !content.Owners.Contains(peerId)) RemoveHolder(content, peerId);
            }
        }
        public void Bad(int requesterPeerId, int holderPeerId, byte[] commit)
        {
            if (!IsId(commit)) return;
            lock (sync)
            {
                Id commitId = new Id(commit);
                if (!peers.TryGetValue(requesterPeerId, out PeerState state) || Array.IndexOf(state.Recent, (commitId, holderPeerId)) < 0) return;
                if (!contents.TryGetValue(commitId, out Content content) || content.Owners.Contains(holderPeerId)) return;
                (content.Rejected ??= new HashSet<int>()).Add(holderPeerId);
                RemoveHolder(content, holderPeerId);
            }
        }
        public void Request(int requesterPeerId, uint requestId, ushort origin, byte[] contentId, byte[] requesterPublic)
        {
            if (!IsId(contentId) || !IsKey(requesterPublic)) return;
            lock (sync) Start(requesterPeerId, requestId, origin, new Id(contentId), requesterPublic, null);
        }
        public void Grant(int holderPeerId, int requesterPeerId, uint requestId, byte[] holderPublic, byte[] nonce, byte[] data, int offset, int count)
        {
            Action<byte[]> done = null;
            byte[] fetched = null;
            lock (sync)
            {
                if (!live.TryGetValue((requesterPeerId, requestId), out Ask request) || request.Holder != holderPeerId) return;
                if (request.Requester == ServerOrigin)
                {
                    byte[] sealedData = new byte[count];
                    Buffer.BlockCopy(data, offset, sealedData, 0, count);
                    byte[] grant = Open(ServerSession(false, holderPublic), requestId, request.CommitBytes, serverPublic, holderPublic, nonce, sealedData);
                    if (grant == null)
                    {
                        if (contents.TryGetValue(request.Commit, out Content content) && !content.Owners.Contains(holderPeerId))
                        {
                            (content.Rejected ??= new HashSet<int>()).Add(holderPeerId);
                            RemoveHolder(content, holderPeerId);
                        }
                        Retry(request, holderPeerId);
                        PumpFreed(holderPeerId);
                        return;
                    }
                    Finish(request);
                    HoldLocked(request.ContentId, grant);
                    done = request.Done;
                    fetched = grant;
                }
                else
                {
                    Finish(request);
                    if (peers.TryGetValue(requesterPeerId, out PeerState state))
                    {
                        state.Recent[state.RecentIndex] = (request.Commit, holderPeerId);
                        state.RecentIndex = (state.RecentIndex + 1) % state.Recent.Length;
                    }
                    sink.ForwardGrant(requesterPeerId, holderPeerId, requestId, request.CommitBytes, holderPublic, nonce, data, offset, count);
                }
                PumpFreed(holderPeerId);
            }
            if (done != null) ThreadPool.QueueUserWorkItem(_ => done(fetched));
        }
        public void Denied(int holderPeerId, int requesterPeerId, uint requestId, byte reason)
        {
            lock (sync)
            {
                if (!live.TryGetValue((requesterPeerId, requestId), out Ask request) || request.Holder != holderPeerId) return;
                if (reason == ReasonUnknown && contents.TryGetValue(request.Commit, out Content content) && !content.Owners.Contains(holderPeerId)) RemoveHolder(content, holderPeerId);
                Retry(request, holderPeerId);
                PumpFreed(holderPeerId);
            }
        }
        public byte[] Hold(byte[] contentId, byte[] grant)
        {
            if (!IsId(contentId) || !IsValidGrant(grant)) return null;
            lock (sync) return HoldLocked(new Id(contentId), grant);
        }
        public void Fetch(ushort origin, byte[] contentId, Action<byte[]> done)
        {
            if (!IsId(contentId) || done == null) return;
            lock (sync)
            {
                serverRequestId = unchecked(serverRequestId + 1);
                Start(ServerOrigin, serverRequestId, origin, new Id(contentId), serverPublic, done);
            }
        }
        public byte[] HeldByServer(byte[] contentId)
        {
            if (!IsId(contentId)) return null;
            lock (sync) return owners.TryGetValue((ServerOrigin, new Id(contentId)), out Owner owner) && contents.TryGetValue(owner.Commit, out Content content) ? content.ServerGrant : null;
        }
        public void RemovePeer(int peerId)
        {
            lock (sync)
            {
                if (!peers.TryGetValue(peerId, out PeerState state)) return;
                peers.Remove(peerId);
                foreach (Ask request in new List<Ask>(state.Asked))
                {
                    if (request.Holder >= 0 && peers.TryGetValue(request.Holder, out PeerState holder))
                    {
                        holder.Inflight--;
                        holder.Serving.Remove(request);
                    }
                    request.Holder = -1;
                    Finish(request);
                }
                foreach ((ushort, Id) key in state.Owns)
                {
                    if (owners.TryGetValue(key, out Owner owner)) owner.Departed = true;
                }
                foreach (Id commit in new List<Id>(state.Holding))
                {
                    if (contents.TryGetValue(commit, out Content content))
                    {
                        content.Owners.Remove(peerId);
                        RemoveHolder(content, peerId);
                    }
                }
                foreach (Ask request in new List<Ask>(state.Serving))
                {
                    if (!request.Dead) Retry(request, peerId);
                }
                foreach ((ushort, Id) key in state.Awaited)
                {
                    if (!awaiting.TryGetValue(key, out List<Ask> list)) continue;
                    awaiting.Remove(key);
                    foreach (Ask request in list) Fail(request, ReasonOwnerLeft);
                }
            }
        }
        public void Tick()
        {
            lock (sync)
            {
                long now = clock();
                while (timeline.Count > 0 && (timeline.Peek().Dead || now - timeline.Peek().Started > RequestTimeoutMs)) Fail(timeline.Dequeue(), ReasonUnavailable);
                scratch.Clear();
                scratch.AddRange(queued);
                foreach (Content content in scratch) Pump(content);
            }
        }
        private void Start(int requester, uint requestId, ushort origin, Id contentId, byte[] requesterPublic, Action<byte[]> done)
        {
            (int, uint) key = (requester, requestId);
            if (live.ContainsKey(key)) return;
            PeerState state = Peer(requester);
            if (state.Pending >= (requester == ServerOrigin ? MaxLiveRequests : MaxPendingPerRequester) || live.Count >= MaxLiveRequests)
            {
                if (done != null) ThreadPool.QueueUserWorkItem(_ => done(null));
                else sink.Deny(requester, requestId, ReasonBusy);
                return;
            }
            Ask request = new Ask { Requester = requester, RequestId = requestId, Origin = origin, ContentId = contentId, Public = requesterPublic, Started = clock(), Done = done };
            live[key] = request;
            timeline.Enqueue(request);
            state.Pending++;
            state.Asked.Add(request);
            (ushort, Id) ownerKey = (origin, contentId);
            if (owners.TryGetValue(ownerKey, out Owner owner) && contents.TryGetValue(owner.Commit, out Content content))
            {
                Route(request, content);
                return;
            }
            if (origin != ServerOrigin && (origin == requester || !sink.IsConnected(origin)))
            {
                Fail(request, ReasonOwnerLeft);
                return;
            }
            if (!awaiting.TryGetValue(ownerKey, out List<Ask> list)) awaiting[ownerKey] = list = new List<Ask>();
            list.Add(request);
            Peer(origin).Awaited.Add(ownerKey);
        }
        private void Resolve((ushort, Id) key, Content content)
        {
            if (!awaiting.TryGetValue(key, out List<Ask> list)) return;
            awaiting.Remove(key);
            if (peers.TryGetValue(key.Item1, out PeerState origin)) origin.Awaited.Remove(key);
            foreach (Ask request in list)
            {
                if (!request.Dead) Route(request, content);
            }
        }
        private void Route(Ask request, Content content)
        {
            request.Commit = content.Commit;
            request.CommitBytes = content.CommitBytes;
            if (content.ServerGrant != null && request.Requester != ServerOrigin)
            {
                ServeFromServer(request, content);
                return;
            }
            int holder = Pick(content, request);
            if (holder >= 0)
            {
                Dispatch(request, holder);
                return;
            }
            if (!CanEverServe(content, request))
            {
                Fail(request, request.Tried == null ? ReasonOwnerLeft : ReasonUnavailable);
                return;
            }
            if (content.Waiting.Count >= MaxQueuedPerContent)
            {
                Fail(request, ReasonBusy);
                return;
            }
            content.Waiting.Enqueue(request);
            queued.Add(content);
        }
        private void Pump(Content content)
        {
            while (content.Waiting.Count > 0)
            {
                Ask request = content.Waiting.Peek();
                if (request.Dead)
                {
                    content.Waiting.Dequeue();
                    continue;
                }
                if (content.ServerGrant != null && request.Requester != ServerOrigin)
                {
                    content.Waiting.Dequeue();
                    ServeFromServer(request, content);
                    continue;
                }
                int holder = Pick(content, request);
                if (holder < 0)
                {
                    if (CanEverServe(content, request)) break;
                    content.Waiting.Dequeue();
                    Fail(request, ReasonUnavailable);
                    continue;
                }
                content.Waiting.Dequeue();
                Dispatch(request, holder);
            }
            if (content.Waiting.Count == 0) queued.Remove(content);
        }
        private void PumpFreed(int holder)
        {
            if (queued.Count == 0) return;
            scratch.Clear();
            foreach (Content content in queued)
            {
                if (content.HolderSet.Contains(holder)) scratch.Add(content);
            }
            foreach (Content content in scratch) Pump(content);
        }
        private int Pick(Content content, Ask request)
        {
            List<int> holders = content.Holders;
            int count = holders.Count;
            if (count == 0) return -1;
            int start = random.Next(count), limit = Math.Min(count, 64), best = -1, bestLoad = int.MaxValue, found = 0;
            for (int step = 0; step < limit && found < 2; step++)
            {
                int holder = holders[(start + step) % count];
                if (!Eligible(holder, request) || !peers.TryGetValue(holder, out PeerState state) || state.Unreachable || state.Inflight >= MaxInflightPerHolder) continue;
                found++;
                if (state.Inflight < bestLoad)
                {
                    best = holder;
                    bestLoad = state.Inflight;
                }
            }
            return best;
        }
        private bool CanEverServe(Content content, Ask request)
        {
            if (content.ServerGrant != null) return request.Requester != ServerOrigin;
            foreach (int holder in content.Holders)
            {
                if (Eligible(holder, request)) return true;
            }
            return false;
        }
        private static bool Eligible(int holder, Ask request) => holder != request.Requester && (request.Tried == null || !request.Tried.Contains(holder));
        private void Dispatch(Ask request, int holder)
        {
            request.Holder = holder;
            PeerState state = Peer(holder);
            state.Inflight++;
            state.Serving.Add(request);
            if (sink.ForwardRequest(holder, request.Requester, request.RequestId, request.CommitBytes, request.Public)) return;
            Unassign(request);
            state.Unreachable = true;
            if (contents.TryGetValue(request.Commit, out Content content)) Route(request, content);
            else Fail(request, ReasonUnavailable);
        }
        public void Reachable(int peerId)
        {
            lock (sync)
            {
                if (!peers.TryGetValue(peerId, out PeerState state) || !state.Unreachable) return;
                state.Unreachable = false;
                PumpFreed(peerId);
            }
        }
        private void ServeFromServer(Ask request, Content content)
        {
            byte[] sealedData = Seal(ServerSession(true, request.Public), request.RequestId, content.CommitBytes, request.Public, serverPublic, content.ServerGrant, out byte[] nonce);
            if (sealedData == null)
            {
                Fail(request, ReasonUnavailable);
                return;
            }
            Finish(request);
            sink.ForwardGrant(request.Requester, ServerOrigin, request.RequestId, content.CommitBytes, serverPublic, nonce, sealedData, 0, sealedData.Length);
        }
        private void Retry(Ask request, int failedHolder)
        {
            Unassign(request);
            (request.Tried ??= new List<int>()).Add(failedHolder);
            if (request.Tried.Count >= MaxTries || !contents.TryGetValue(request.Commit, out Content content))
            {
                Fail(request, ReasonUnavailable);
                return;
            }
            Route(request, content);
        }
        private void Unassign(Ask request)
        {
            if (request.Holder >= 0 && peers.TryGetValue(request.Holder, out PeerState holder))
            {
                holder.Inflight--;
                holder.Serving.Remove(request);
            }
            request.Holder = -1;
        }
        private void Finish(Ask request)
        {
            if (request.Dead) return;
            Unassign(request);
            request.Dead = true;
            live.Remove((request.Requester, request.RequestId));
            if (peers.TryGetValue(request.Requester, out PeerState state))
            {
                state.Pending--;
                state.Asked.Remove(request);
            }
        }
        private void Fail(Ask request, byte reason)
        {
            if (request.Dead) return;
            Finish(request);
            Action<byte[]> done = request.Done;
            if (done != null) ThreadPool.QueueUserWorkItem(_ => done(null));
            else sink.Deny(request.Requester, request.RequestId, reason);
        }
        private byte[] HoldLocked(Id contentId, byte[] grant)
        {
            byte[] commit = Commit(grant);
            Id commitId = new Id(commit);
            (ushort, Id) key = (ServerOrigin, contentId);
            if (owners.TryGetValue(key, out Owner owner)) owner.Commit = commitId;
            else owners[key] = new Owner { Commit = commitId };
            Content content = GetContent(commitId, commit);
            content.ServerGrant = (byte[])grant.Clone();
            if (!content.OwnerKeys.Contains(key)) content.OwnerKeys.Add(key);
            Resolve(key, content);
            Pump(content);
            return commit;
        }
        private Content GetContent(Id commit, byte[] commitBytes)
        {
            if (!contents.TryGetValue(commit, out Content content)) contents[commit] = content = new Content { Commit = commit, CommitBytes = (byte[])commitBytes.Clone() };
            return content;
        }
        private void AddHolder(Content content, int peerId)
        {
            if (!content.HolderSet.Add(peerId)) return;
            content.Holders.Add(peerId);
            Peer(peerId).Holding.Add(content.Commit);
        }
        private void RemoveHolder(Content content, int peerId)
        {
            if (content.HolderSet.Remove(peerId))
            {
                int index = content.Holders.IndexOf(peerId);
                int last = content.Holders.Count - 1;
                content.Holders[index] = content.Holders[last];
                content.Holders.RemoveAt(last);
            }
            if (peers.TryGetValue(peerId, out PeerState state)) state.Holding.Remove(content.Commit);
            if (content.Holders.Count > 0 || content.ServerGrant != null) return;
            contents.Remove(content.Commit);
            queued.Remove(content);
            while (content.Waiting.Count > 0)
            {
                Fail(content.Waiting.Dequeue(), ReasonOwnerLeft);
            }
            foreach ((ushort, Id) key in content.OwnerKeys)
            {
                if (owners.TryGetValue(key, out Owner owner) && owner.Commit.Equals(content.Commit) && (owner.Departed || key.Item1 == ServerOrigin)) owners.Remove(key);
            }
        }
        private PeerState Peer(int peerId)
        {
            if (!peers.TryGetValue(peerId, out PeerState state)) peers[peerId] = state = new PeerState();
            return state;
        }
        private uint[] ServerSession(bool asHolder, byte[] peerPublic)
        {
            string key = string.Concat(asHolder ? "h" : "r", Hex(peerPublic));
            if (serverSessions.TryGetValue(key, out uint[] session)) return session;
            session = asHolder ? SessionKey(serverPrivate, peerPublic, peerPublic, serverPublic) : SessionKey(serverPrivate, peerPublic, serverPublic, peerPublic);
            if (session == null) return null;
            if (serverSessions.Count >= 16384) serverSessions.Clear();
            serverSessions[key] = session;
            return session;
        }
    }
}
