using System;
using System.Collections.Generic;

namespace Basis.EventDriver
{
    /// <summary>
    /// One per-frame main-thread callback, polled by <see cref="BasisEventDriver"/> from the
    /// single window in LateUpdate where every avatar transform is main-thread safe.
    ///
    /// That window sits between the network transmit (<c>AfterAvatarChanges</c>, which reads the
    /// local bones inline) and <c>JigglePhysics.DispatchSimulate</c>, which is where the jiggle
    /// transform jobs are scheduled. By the time it runs, IK and the local player sim have posed
    /// the avatar, authored motion has written, the constraint solve is fenced, and the remote
    /// bone jobs are joined — and nothing has been dispatched yet. Reading or writing a Transform
    /// here therefore cannot race a TransformAccessArray job or stall the main thread on one, and
    /// a write lands early enough that JigglePhysics samples it this frame rather than next.
    ///
    /// Implementations must be main-thread only and must not assume they are the only entry:
    /// order between entries is registration order and is not a contract.
    /// </summary>
    public interface IBasisFrameSync
    {
        /// <summary>
        /// Do one frame of work. Throwing is contained by the registry, but a throw is a bug —
        /// the entry is reported once and keeps being polled.
        /// </summary>
        void FrameSync();
    }

    /// <summary>
    /// The polling loop behind <see cref="IBasisFrameSync"/>.
    ///
    /// This exists instead of another <c>BasisEventDriver</c> static event for two reasons. It is
    /// a plain array walk rather than a multicast delegate, so the per-entry cost is an interface
    /// call and nothing else. And because it lives inside the driver's own assembly, the driver
    /// can <see cref="Clear"/> it on teardown — which is what makes a cross-instance cap safe to
    /// enforce here at all. A cap parked on a static that nothing reliably resets would leak its
    /// count across scene loads and eventually refuse registrations for every later prop in the
    /// session.
    /// </summary>
    public static class BasisFrameSyncRegistry
    {
        /// <summary>
        /// Hard ceiling on simultaneously registered entries. Sandboxed content reaches this
        /// registry through the sync shims, and native work behind a shim is not covered by
        /// Cilbox's interpreted-instruction budget — so the count is bounded here where it can
        /// be reset on teardown.
        /// </summary>
        public const int MaxEntries = 1024;

        private static IBasisFrameSync[] Entries = Array.Empty<IBasisFrameSync>();
        private static int EntryCount;

        // Entries are polled off a snapshot: an entry is allowed to unregister itself from inside
        // FrameSync (the shims do exactly that when their lifetime owner has been destroyed), and
        // walking the live array while it compacts underneath would skip the swapped-in neighbour.
        private static IBasisFrameSync[] Snapshot = Array.Empty<IBasisFrameSync>();

        // Failures are reported once per implementing type. Keyed on the type rather than a single
        // bool so two different broken shims both surface, and checked before building the message
        // so a persistently throwing entry does not allocate a string every frame.
        private static readonly HashSet<Type> ReportedFailures = new HashSet<Type>();

        private static bool CapacityWarned;

        /// <summary>Number of entries currently registered.</summary>
        public static int Count { get { return EntryCount; } }

        /// <summary>
        /// Start polling <paramref name="sync"/>. Idempotent — registering twice is a no-op rather
        /// than a double tick. Returns false if the entry is null or the registry is full.
        /// </summary>
        public static bool Register(IBasisFrameSync sync)
        {
            if (sync == null)
            {
                return false;
            }

            int count = EntryCount;
            IBasisFrameSync[] entries = Entries;
            for (int Index = 0; Index < count; Index++)
            {
                if (ReferenceEquals(entries[Index], sync))
                {
                    return true;
                }
            }

            if (count >= MaxEntries)
            {
                if (!CapacityWarned)
                {
                    CapacityWarned = true;
                    BasisDebug.LogError($"BasisFrameSyncRegistry is full at {MaxEntries} entries; refusing {sync.GetType().FullName}.", BasisDebug.LogTag.Shims);
                }
                return false;
            }

            if (count == entries.Length)
            {
                int capacity = entries.Length == 0 ? 8 : entries.Length * 2;
                if (capacity > MaxEntries)
                {
                    capacity = MaxEntries;
                }
                Array.Resize(ref entries, capacity);
                Entries = entries;
            }

            entries[count] = sync;
            EntryCount = count + 1;
            return true;
        }

        /// <summary>Stop polling <paramref name="sync"/>. Safe to call when not registered.</summary>
        public static void Unregister(IBasisFrameSync sync)
        {
            if (sync == null)
            {
                return;
            }

            int count = EntryCount;
            IBasisFrameSync[] entries = Entries;
            for (int Index = 0; Index < count; Index++)
            {
                if (!ReferenceEquals(entries[Index], sync))
                {
                    continue;
                }
                int last = count - 1;
                entries[Index] = entries[last];
                entries[last] = null;
                EntryCount = last;
                return;
            }
        }

        /// <summary>
        /// Drop every entry. Called when the driver tears down, so the cap and the failure
        /// reporting start clean on the next scene rather than carrying a leaked count forward.
        /// </summary>
        public static void Clear()
        {
            Array.Clear(Entries, 0, EntryCount);
            EntryCount = 0;
            Array.Clear(Snapshot, 0, Snapshot.Length);
            ReportedFailures.Clear();
            CapacityWarned = false;
        }

        /// <summary>
        /// Poll every entry once. Driver-only: called from exactly one point in
        /// <see cref="BasisEventDriver"/>. Each entry is invoked under its own catch, so one bad
        /// sandboxed shim cannot abort the rest of the driver chain for the frame.
        /// </summary>
        internal static void Simulate()
        {
            int count = EntryCount;
            if (count == 0)
            {
                return;
            }

            IBasisFrameSync[] snapshot = Snapshot;
            if (snapshot.Length < count)
            {
                snapshot = new IBasisFrameSync[Entries.Length];
                Snapshot = snapshot;
            }
            Array.Copy(Entries, snapshot, count);

            for (int Index = 0; Index < count; Index++)
            {
                IBasisFrameSync entry = snapshot[Index];
                snapshot[Index] = null;
                try
                {
                    entry.FrameSync();
                }
                catch (Exception ex)
                {
                    Type type = entry.GetType();
                    if (ReportedFailures.Add(type))
                    {
                        BasisDebug.LogError($"BasisFrameSyncRegistry entry {type.FullName}.FrameSync failed: {ex}", BasisDebug.LogTag.Shims);
                    }
                }
            }
        }
    }
}
