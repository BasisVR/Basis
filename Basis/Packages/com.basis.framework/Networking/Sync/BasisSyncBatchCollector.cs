using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;

namespace Basis.Scripts.Networking.Sync
{
    /// <summary>
    /// EXPERIMENTAL, off by default. Coalesces a client's owned scene-data sends into fewer packets to amortize the
    /// per-packet header + transport overhead. Only broadcast (no explicit recipients) sends are batched. Objects that
    /// send direct get their own batches, which go peer-to-peer to linked players and through the server to the rest;
    /// P2P-only objects and explicit recipients fall back to an individual send.
    ///
    /// Enabling is a SESSION-WIDE policy: it changes the wire framing for batched objects, so every client must agree,
    /// and <see cref="BatchMessageIndex"/> must never be assigned to a real networked object (reserve it in the id
    /// allocator) — otherwise it collides with that object's receive handler. Verify a real batched round-trip before
    /// shipping it on.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisSyncBatchCollector
    {
        /// <summary>Reserved scene-data messageIndex that carries a batch. Must not collide with any assigned NetworkID.</summary>
        public const ushort BatchMessageIndex = ushort.MaxValue;

        // Keep one batch packet inside a single datagram. Derived from the shared budget rather than
        // picked against a typical MTU: batches flush Unreliable, which cannot be fragmented, and a
        // peer runs at its initial MTU until discovery finishes — a cap tuned to the discovered value
        // throws for the first seconds of every session. Batches always broadcast (TryEnqueue rejects
        // explicit recipients), so the framing term is the no-recipients case.
        internal static readonly int MaxBatchPayload =
            BasisNetworkCommons.MaxUnfragmentedPayload - BasisNetworkGenericMessages.SceneDataFramingBytes(null);

        public static bool Enabled { get; private set; }

        private static byte[] _unreliableBuf;
        private static byte[] _reliableBuf;
        private static byte[] _directUnreliableBuf;
        private static byte[] _directReliableBuf;
        private static BasisSyncBatchWriter _unreliable;
        private static BasisSyncBatchWriter _reliable;
        private static BasisSyncBatchWriter _directUnreliable;
        private static BasisSyncBatchWriter _directReliable;
        private static byte[] _sendScratch;

        /// <summary>Turn batching on/off for this client (session-wide policy). Registers/unregisters the receive demux.</summary>
        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return;
            Enabled = enabled;
            if (enabled)
            {
                _unreliableBuf = new byte[MaxBatchPayload];
                _reliableBuf = new byte[MaxBatchPayload];
                _directUnreliableBuf = new byte[MaxBatchPayload];
                _directReliableBuf = new byte[MaxBatchPayload];
                _unreliable = new BasisSyncBatchWriter(_unreliableBuf);
                _reliable = new BasisSyncBatchWriter(_reliableBuf);
                _directUnreliable = new BasisSyncBatchWriter(_directUnreliableBuf);
                _directReliable = new BasisSyncBatchWriter(_directReliableBuf);
                BasisNetworkGenericMessages.RegisterBatchHandler();
            }
            else
            {
                BasisNetworkGenericMessages.UnregisterBatchHandler();
            }
        }

        /// <summary>True if the payload was accepted into a batch — the caller must then NOT send it individually.</summary>
        public static bool TryEnqueue(ushort networkId, byte[] payload, int length, DeliveryMethod delivery, ushort[] recipients, bool useDirectP2P, bool directOnly = false)
        {
            if (!Enabled || recipients != null || directOnly) return false;
            bool reliable = IsReliable(delivery);
            if (useDirectP2P)
            {
                return reliable
                    ? Append(ref _directReliable, networkId, payload, length, true, true)
                    : Append(ref _directUnreliable, networkId, payload, length, false, true);
            }
            return reliable
                ? Append(ref _reliable, networkId, payload, length, true, false)
                : Append(ref _unreliable, networkId, payload, length, false, false);
        }

        /// <summary>Sends any pending batches. Call once after a transmit pass (no-op when disabled / empty).</summary>
        public static void Flush()
        {
            if (!Enabled) return;
            FlushOne(false, false);
            FlushOne(true, false);
            FlushOne(false, true);
            FlushOne(true, true);
        }

        private static bool Append(ref BasisSyncBatchWriter writer, ushort networkId, byte[] payload, int length, bool reliable, bool direct)
        {
            if (writer.TryAppend(networkId, payload, length)) return true;
            if (writer.Count > 0)
            {
                FlushOne(reliable, direct);
                if (writer.TryAppend(networkId, payload, length)) return true;
            }
            return false; // a single payload bigger than a batch -> let the caller send it solo
        }

        private static void FlushOne(bool reliable, bool direct)
        {
            int length = direct ? (reliable ? _directReliable.Length : _directUnreliable.Length) : (reliable ? _reliable.Length : _unreliable.Length);
            if (length == 0) return;

            byte[] src = direct ? (reliable ? _directReliableBuf : _directUnreliableBuf) : (reliable ? _reliableBuf : _unreliableBuf);
            if (_sendScratch == null || _sendScratch.Length != length) _sendScratch = new byte[length];
            System.Array.Copy(src, 0, _sendScratch, 0, length);

            DeliveryMethod dm = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
            if (direct) BasisNetworkGenericMessages.OnNetworkMessageSendDirect(BatchMessageIndex, _sendScratch, dm, null);
            else BasisNetworkGenericMessages.OnNetworkMessageSend(BatchMessageIndex, _sendScratch, dm, null);

            if (direct)
            {
                if (reliable) _directReliable.Reset();
                else _directUnreliable.Reset();
            }
            else if (reliable) _reliable.Reset();
            else _unreliable.Reset();
        }

        private static bool IsReliable(DeliveryMethod d) =>
            d == DeliveryMethod.ReliableOrdered || d == DeliveryMethod.ReliableUnordered || d == DeliveryMethod.ReliableSequenced;
    }
}
