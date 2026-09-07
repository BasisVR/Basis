using Basis.Scripts.BasisCharacterController;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Basis.Scripts.Drivers
{

    public struct BasisLocomotionValues
    {
        public BasisLocomotionField Fields;
        public float JumpHeight;
        public float WalkSpeed;
        public float RunSpeed;
        public float Gravity;
        public BasisLocalCharacterDriver.Mode Mode;

        public bool Has(BasisLocomotionField field) => (Fields & field) != 0;
    }

    public struct BasisLocomotionBaseline
    {
        public float JumpHeight;
        public float WalkSpeed;
        public float RunSpeed;
        public float MinimumSpeed;
        public float Gravity;
        public BasisLocalCharacterDriver.Mode Mode;
    }

    public struct BasisLocomotionEffective
    {
        public float JumpHeight;
        public float MinimumSpeed;
        public float WalkSpeed;
        public float RunSpeed;
        public float Gravity;
        public BasisLocalCharacterDriver.Mode Mode;
    }

    /// <summary>
    /// Keyed, prioritized locomotion overrides layered over the driver's authored values.
    /// Each source registers under its own key and may set any subset of the fields; the
    /// highest-priority entry to set a field wins it, and removing a key restores whatever
    /// the entry beneath it asked for, or the authored baseline when none remain.
    /// </summary>
    public static class BasisLocomotionOverrides
    {
        public const string AdminKey = "BasisAdmin";
        public const int AdminPriority = int.MaxValue;

        /// <summary>
        /// The instance-wide policy the server pushes on join and on change. Reserved like
        /// <see cref="AdminKey"/> so world content can neither clear nor outrank it, but ranked one
        /// step below it, so a moderator singling out one player still wins over the house rules.
        /// </summary>
        public const string ServerPolicyKey = "BasisServerPolicy";
        public const int ServerPolicyPriority = int.MaxValue - 1;

        public const int DefaultPriority = 0;

        private sealed class Entry
        {
            public string Key;
            public int Priority;
            public long Sequence;
            public BasisLocomotionValues Values;
        }

        private static readonly object Sync = new object();
        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly Comparison<Entry> Order = CompareEntries;
        private static long _sequence;
        private static int _version;

        public static int Version => Volatile.Read(ref _version);

        public static bool IsReservedKey(string key) =>
            string.Equals(key, AdminKey, StringComparison.Ordinal) ||
            string.Equals(key, ServerPolicyKey, StringComparison.Ordinal);

        public static void Set(string key, BasisLocomotionValues values) => Set(key, DefaultPriority, values);

        public static void Set(string key, int priority, BasisLocomotionValues values)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                BasisDebug.LogError("Locomotion override rejected: no key provided.");
                return;
            }

            if (values.Fields == BasisLocomotionField.None)
            {
                return;
            }

            lock (Sync)
            {
                Entry entry = Find(key);
                if (entry == null)
                {
                    entry = new Entry { Key = key, Sequence = ++_sequence };
                    Entries.Add(entry);
                }

                entry.Priority = priority;
                Merge(ref entry.Values, values);
                Bump();
            }
        }

        public static bool ClearField(string key, BasisLocomotionField fields)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (Sync)
            {
                Entry entry = Find(key);
                if (entry == null || (entry.Values.Fields & fields) == 0)
                {
                    return false;
                }

                entry.Values.Fields &= ~fields;
                if (entry.Values.Fields == BasisLocomotionField.None)
                {
                    Entries.Remove(entry);
                }

                Bump();
                return true;
            }
        }

        public static bool Remove(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (Sync)
            {
                Entry entry = Find(key);
                if (entry == null)
                {
                    return false;
                }

                Entries.Remove(entry);
                Bump();
                return true;
            }
        }

        public static void RemoveAll(bool includeReserved)
        {
            lock (Sync)
            {
                if (Entries.Count == 0)
                {
                    return;
                }

                if (includeReserved)
                {
                    Entries.Clear();
                }
                else
                {
                    Entries.RemoveAll(entry => !IsReservedKey(entry.Key));
                }

                if (Entries.Count == 0)
                {
                    _sequence = 0;
                }

                Bump();
            }
        }

        public static bool Contains(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (Sync)
            {
                return Find(key) != null;
            }
        }

        public static int Count
        {
            get
            {
                lock (Sync)
                {
                    return Entries.Count;
                }
            }
        }

        public static BasisLocomotionValues Resolve()
        {
            BasisLocomotionValues result = default;

            lock (Sync)
            {
                if (Entries.Count == 0)
                {
                    return result;
                }

                if (Entries.Count > 1)
                {
                    Entries.Sort(Order);
                }

                for (int Index = 0; Index < Entries.Count; Index++)
                {
                    Merge(ref result, Entries[Index].Values);
                }
            }

            return result;
        }

        /// <summary>
        /// Speeds feed <c>math.unlerp(min, max, speed)</c>, which divides by zero when the two ends
        /// meet. Keeping the band at least this wide stops a frozen or fixed-speed override producing
        /// a NaN that would reach <c>CharacterController.Move</c> and corrupt the root transform.
        /// </summary>
        public const float MinimumSpeedSpan = 0.01f;

        /// <summary>
        /// Folds resolved override values onto a baseline and sanitizes the result into something the
        /// driver can use directly: every field falls back to its baseline when unclaimed or non-finite,
        /// gravity stays non-positive so the jump's <c>sqrt(-2gh)</c> cannot go imaginary, and the speed
        /// band is ordered min &lt;= walk &lt;= run with a non-zero span.
        /// </summary>
        public static BasisLocomotionEffective Flatten(BasisLocomotionValues values, BasisLocomotionBaseline baseline)
        {
            float jump = values.Has(BasisLocomotionField.JumpHeight) ? values.JumpHeight : baseline.JumpHeight;
            float walk = values.Has(BasisLocomotionField.WalkSpeed) ? values.WalkSpeed : baseline.WalkSpeed;
            float run = values.Has(BasisLocomotionField.RunSpeed) ? values.RunSpeed : baseline.RunSpeed;
            float gravity = values.Has(BasisLocomotionField.Gravity) ? values.Gravity : baseline.Gravity;

            BasisLocomotionEffective effective;
            effective.Mode = values.Has(BasisLocomotionField.Mode) ? values.Mode : baseline.Mode;
            effective.JumpHeight = Math.Max(Sanitize(jump, baseline.JumpHeight), 0f);
            effective.Gravity = Math.Min(Sanitize(gravity, baseline.Gravity), 0f);

            walk = Math.Max(Sanitize(walk, baseline.WalkSpeed), 0f);
            run = Math.Max(Sanitize(run, baseline.RunSpeed), walk);

            float minimum = Math.Max(Math.Min(Sanitize(baseline.MinimumSpeed, 0f), walk), 0f);
            if (run - minimum < MinimumSpeedSpan)
            {
                run = minimum + MinimumSpeedSpan;
            }

            effective.MinimumSpeed = minimum;
            effective.WalkSpeed = walk;
            effective.RunSpeed = run;
            return effective;
        }

        private static float Sanitize(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
        }

        public static List<string> ToList()
        {
            lock (Sync)
            {
                List<string> keys = new List<string>(Entries.Count);
                for (int Index = 0; Index < Entries.Count; Index++)
                {
                    keys.Add(Entries[Index].Key);
                }
                return keys;
            }
        }

        private static Entry Find(string key)
        {
            for (int Index = 0; Index < Entries.Count; Index++)
            {
                if (string.Equals(Entries[Index].Key, key, StringComparison.Ordinal))
                {
                    return Entries[Index];
                }
            }
            return null;
        }

        private static int CompareEntries(Entry left, Entry right)
        {
            int byPriority = left.Priority.CompareTo(right.Priority);
            return byPriority != 0 ? byPriority : left.Sequence.CompareTo(right.Sequence);
        }

        private static void Merge(ref BasisLocomotionValues target, BasisLocomotionValues source)
        {
            if (source.Has(BasisLocomotionField.JumpHeight))
            {
                target.JumpHeight = source.JumpHeight;
            }
            if (source.Has(BasisLocomotionField.WalkSpeed))
            {
                target.WalkSpeed = source.WalkSpeed;
            }
            if (source.Has(BasisLocomotionField.RunSpeed))
            {
                target.RunSpeed = source.RunSpeed;
            }
            if (source.Has(BasisLocomotionField.Gravity))
            {
                target.Gravity = source.Gravity;
            }
            if (source.Has(BasisLocomotionField.Mode))
            {
                target.Mode = source.Mode;
            }
            target.Fields |= source.Fields;
        }

        private static void Bump() => Volatile.Write(ref _version, _version + 1);
    }
}
