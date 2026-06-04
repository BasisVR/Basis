using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Basis.Scripts.Common
{
    /// <summary>
    /// Global lock registry.
    /// Each context stores unique lock owner names only.
    /// Thread-safe.
    /// </summary>
    public static class BasisLocks
    {
        public const string LookRotation = "LookRotation";
        public const string Movement = "Movement";
        public const string Crouching = "Crouching";

        private static readonly ConcurrentDictionary<string, ContextState> States =
            new ConcurrentDictionary<string, ContextState>();

        private sealed class ContextState
        {
            public readonly object Sync = new object();
            public readonly HashSet<string> Owners = new HashSet<string>();
            // Mirrors Owners.Count; written under Sync, read lock-free via Volatile.
            public int LiveCount;
        }

        public static LockContext GetContext(string context)
        {
            if (string.IsNullOrWhiteSpace(context))
                throw new ArgumentNullException(nameof(context));

            States.GetOrAdd(context, _ => new ContextState());
            return new LockContext(context);
        }

        public static LockContext CopyContext(LockContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            return new LockContext(context.Context);
        }

        public static void DebugDump(string context = null)
        {
            if (!string.IsNullOrWhiteSpace(context))
            {
                UnityEngine.Debug.Log(new LockContext(context).ToString());
                return;
            }

            var sb = new StringBuilder();
            foreach (var key in States.Keys)
                sb.AppendLine(new LockContext(key).ToString());

            UnityEngine.Debug.Log(sb.ToString());
        }

        public sealed class LockContext : IEnumerable<string>
        {
            public readonly string Context;

            internal LockContext(string context)
            {
                Context = context ?? throw new ArgumentNullException(nameof(context));
            }

            private ContextState GetState()
            {
                return States.GetOrAdd(Context, _ => new ContextState());
            }

            public void Add(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                    throw new ArgumentNullException(nameof(key));

                var state = GetState();

                lock (state.Sync)
                {
                    if (state.Owners.Add(key))
                        Volatile.Write(ref state.LiveCount, state.Owners.Count);
                }
            }

            public bool Remove(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    BasisDebug.LogError("Failed to Remove Lock no key provided!");
                    return false;
                }

                if (!States.TryGetValue(Context, out var state))
                {
                    BasisDebug.Log($"no lock exists for {Context}");
                    return false;
                }

                lock (state.Sync)
                {
                    BasisDebug.Log($"removing lock for {key}");
                    bool removed = state.Owners.Remove(key);
                    if (removed)
                        Volatile.Write(ref state.LiveCount, state.Owners.Count);
                    return removed;
                }
            }

            public void Clear()
            {
                if (!States.TryGetValue(Context, out var state))
                {
                    return;
                }

                lock (state.Sync)
                {
                    state.Owners.Clear();
                    Volatile.Write(ref state.LiveCount, 0);
                }
            }

            public bool Contains(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return false;
                }

                if (!States.TryGetValue(Context, out var state))
                {
                    return false;
                }

                lock (state.Sync)
                {
                    return state.Owners.Contains(key);
                }
            }

            public bool ContainsOnly(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return false;
                }

                if (!States.TryGetValue(Context, out var state))
                {
                    return false;
                }

                lock (state.Sync)
                {
                    return state.Owners.Count == 1 && state.Owners.Contains(key);
                }
            }

            public int Count
            {
                get
                {
                    if (!States.TryGetValue(Context, out var state))
                    {
                        return 0;
                    }

                    return Volatile.Read(ref state.LiveCount);
                }
            }

            public List<string> ToList()
            {
                if (!States.TryGetValue(Context, out var state))
                {
                    return new List<string>();
                }

                lock (state.Sync)
                {
                    return state.Owners.ToList();
                }
            }

            public override string ToString()
            {
                if (!States.TryGetValue(Context, out var state))
                {
                    return $"{Context}[]";
                }

                lock (state.Sync)
                {
                    return state.Owners.Count == 0 ? $"{Context}[]" : $"{Context}[{string.Join(", ", state.Owners)}]";
                }
            }

            public IEnumerator<string> GetEnumerator()
            {
                return ToList().GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public override bool Equals(object obj)
            {
                return obj is LockContext other &&
                       other.Context == Context;
            }

            public override int GetHashCode()
            {
                return Context.GetHashCode();
            }

            public static bool operator ==(LockContext a, LockContext b) => a?.Context == b?.Context;

            public static bool operator !=(LockContext a, LockContext b) => !(a == b);

            public static implicit operator bool(LockContext context) => context != null && context.Count > 0;
        }
    }
}
