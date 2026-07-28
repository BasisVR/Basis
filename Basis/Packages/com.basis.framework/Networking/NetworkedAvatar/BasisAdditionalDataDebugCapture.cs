using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    /// <summary>
    /// Live per-messageIndex capture of AdditionalAvatarData (face tracking et al.) feeding the
    /// Basis/Debug/Additional Data editor window. Capture only runs while <see cref="Capture"/>
    /// is set (the window turns it on while open); otherwise each hook is one volatile bool check.
    /// </summary>
    public static class BasisAdditionalDataDebugCapture
    {
        public static volatile bool Capture;
        public const int PayloadPreviewBytes = 48;

        public sealed class Slot
        {
            public long Count;
            public long ChangedCount;   // payloads that differed from the previous one — a rate of 0 while Count climbs means the source is emitting a frozen buffer
            public int LastSize;
            public double LastTime;
            public readonly byte[] Preview = new byte[PayloadPreviewBytes];
            public int PreviewSize;
            public byte[] PrevPayload;
            public int PrevSize;
        }

        public sealed class PlayerCapture
        {
            public long FramesWithSection;
            public long DroppedLinkedIndex;
            public long EntriesDispatched;
            public long SkippedEmpty;
            public long SkippedIndex;
            public double LastFrameTime;
            public readonly Slot[] Slots = new Slot[256];
            public readonly Slot[] SlotsCh15 = new Slot[256];
        }

        // ── HVR wearer pipeline (incremented from dev.hai-vr.basis.comms while Capture is on) ──
        // Store.Submit → wearer OnAddressUpdated (registered) → accepted (value changed) → DoTick sees them.
        public static long HvrStoreSubmits;            // HVRVariableStore.Submit calls that had a listener entry
        public static long HvrStoreSubmitsNoListener;  // Submit calls for addresses nothing registered
        public static long HvrWearerAddressUpdates;    // wearer networking OnAddressUpdated invocations
        public static long HvrWearerNewValues;         // of those, accepted as a changed value (queued for send)
        public static long HvrWearerTicks;             // wearer DoTick invocations
        public static long HvrWearerTicksWithValues;   // DoTick entries where _addressIdsWithNewValue was non-empty
        public static long HvrActivitySamples;         // FaceTrackingActivityRelay.NotifySourceSample calls

        public static readonly Slot[] Sent = new Slot[256];
        public static readonly Slot[] SentCh15 = new Slot[256];
        public static readonly ConcurrentDictionary<ushort, PlayerCapture> Players = new ConcurrentDictionary<ushort, PlayerCapture>();

        public static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        public static void Clear()
        {
            Array.Clear(Sent, 0, Sent.Length);
            Array.Clear(SentCh15, 0, SentCh15.Length);
            Players.Clear();
            HvrStoreSubmits = 0;
            HvrStoreSubmitsNoListener = 0;
            HvrWearerAddressUpdates = 0;
            HvrWearerNewValues = 0;
            HvrWearerTicks = 0;
            HvrWearerTicksWithValues = 0;
            HvrActivitySamples = 0;
        }

        public static void RecordSent(byte messageIndex, byte[] payload)
        {
            if (!Capture) return;
            Record(Sent, messageIndex, payload);
        }

        public static void RecordSentAvatarChannel(byte messageIndex, byte[] payload)
        {
            if (!Capture) return;
            Record(SentCh15, messageIndex, payload);
        }

        public static void RecordReceivedAvatarChannel(ushort playerId, byte messageIndex, byte[] payload)
        {
            if (!Capture) return;
            Record(Players.GetOrAdd(playerId, _ => new PlayerCapture()).SlotsCh15, messageIndex, payload);
        }

        public static void RecordReceiverFrame(ushort playerId)
        {
            if (!Capture) return;
            PlayerCapture pc = Players.GetOrAdd(playerId, _ => new PlayerCapture());
            Interlocked.Increment(ref pc.FramesWithSection);
            pc.LastFrameTime = Now;
        }

        public static void RecordReceiverGateDrop(ushort playerId)
        {
            if (!Capture) return;
            Interlocked.Increment(ref Players.GetOrAdd(playerId, _ => new PlayerCapture()).DroppedLinkedIndex);
        }

        public static void RecordReceiverSkippedEmpty(ushort playerId)
        {
            if (!Capture) return;
            Interlocked.Increment(ref Players.GetOrAdd(playerId, _ => new PlayerCapture()).SkippedEmpty);
        }

        public static void RecordReceiverSkippedIndex(ushort playerId)
        {
            if (!Capture) return;
            Interlocked.Increment(ref Players.GetOrAdd(playerId, _ => new PlayerCapture()).SkippedIndex);
        }

        public static void RecordReceived(ushort playerId, byte messageIndex, byte[] payload)
        {
            if (!Capture) return;
            PlayerCapture pc = Players.GetOrAdd(playerId, _ => new PlayerCapture());
            Interlocked.Increment(ref pc.EntriesDispatched);
            Record(pc.Slots, messageIndex, payload);
        }

        static void Record(Slot[] slots, byte messageIndex, byte[] payload)
        {
            Slot slot = slots[messageIndex];
            if (slot == null)
            {
                slot = new Slot();
                slot = Interlocked.CompareExchange(ref slots[messageIndex], slot, null) ?? slot;
            }

            int len = payload?.Length ?? 0;
            bool changed = len != slot.PrevSize;
            if (!changed && payload != null && slot.PrevPayload != null)
            {
                for (int Index = 0; Index < len; Index++)
                {
                    if (payload[Index] != slot.PrevPayload[Index]) { changed = true; break; }
                }
            }
            if (changed) Interlocked.Increment(ref slot.ChangedCount);

            if (payload != null)
            {
                if (slot.PrevPayload == null || slot.PrevPayload.Length < len)
                {
                    slot.PrevPayload = new byte[Math.Max(len, 64)];
                }
                Array.Copy(payload, slot.PrevPayload, len);

                slot.PreviewSize = Math.Min(len, PayloadPreviewBytes);
                Array.Copy(payload, slot.Preview, slot.PreviewSize);
            }
            else
            {
                slot.PreviewSize = 0;
            }
            slot.PrevSize = len;
            slot.LastSize = len;
            slot.LastTime = Now;
            Interlocked.Increment(ref slot.Count);
        }
    }
}
