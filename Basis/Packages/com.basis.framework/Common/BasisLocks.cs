using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Basis.Scripts.Common
{
    /// <summary>
    /// Provides a global storage of lockable contexts. Each lockable context stores a list of lock requests which can be managed as needed.
    /// Each lockable context must be referenced separately by storing the result of <code>BasisLocks.LockContext myLock = BasisLocks.GetContext("MyLockId");</code>
    /// This returns a List-like wrapper to the provided lockable context.
    /// This context is globally unique, and if other unrelated logic attempts to retrieve the same context, they will get an equivalent wrapper.
    /// All lock contexts are thread-safe.
    /// Once you have a lock context, you can do whatever you want to do with it like you would any other List.
    /// To add a lock, use <code>myLock.Add(nameof(MyClass))</code>
    /// To remove a lock, use <code>myLock.Remove(nameof(MyClass))</code>
    /// To check if a lock is occupied <code>if (myLock)</code>
    /// You can add a lock multiple times. For each lock added, that same number of locks must be removed before it is considered unlocked.
    /// </summary>
    public static class BasisLocks
    {
        public const string LookRotation = "LookRotation";
        public const string Movement = "Movement";
        public const string Crouching = "Crouching";

        // Static ConcurrentDictionary to store locks
        private static readonly ConcurrentDictionary<string, List<string>> Locks = new ConcurrentDictionary<string, List<string>>(
            new Dictionary<string, List<string>>
            {
                { LookRotation, new List<string>() },
                { Movement, new List<string>() },
                { Crouching, new List<string>() }
            });

        // Static dictionary of lock objects for per-category thread safety on nested lists
        private static readonly ConcurrentDictionary<string, object> ListLocks = new ConcurrentDictionary<string, object>(
            new Dictionary<string, object>
            {
                { LookRotation, new object() },
                { Movement, new object() },
                { Crouching, new object() }
            });

        public static void DebugDump(string context = null)
        {
            if (!string.IsNullOrWhiteSpace(context))
            {
                UnityEngine.Debug.Log(new LockContext(context).ToString());
            }
            else
            {
                var sb = new StringBuilder();
                foreach (var key in Locks.Keys.ToList())
                    sb.AppendLine(new LockContext(key).ToString());
                UnityEngine.Debug.Log(sb.ToString());
            }
        }

        public static LockContext GetContext(string context)
        {
            if (string.IsNullOrWhiteSpace(context)) throw new System.ArgumentNullException(nameof(context), "Context name is required.");
            if (!Locks.TryGetValue(context, out _))
            {
                Locks.GetOrAdd(context, _ => new List<string>());
                ListLocks.GetOrAdd(context, _ => new object());
            }

            return new LockContext(context);
        }

        public static LockContext CopyContext(LockContext context)
        {
            return new LockContext(context.Context);
        }

        public class LockContext : IList<string>
        {
            public readonly string Context;

            internal LockContext(string context)
            {
                if (string.IsNullOrWhiteSpace(context)) throw new System.ArgumentNullException(nameof(context), "Context name is required.");
                Context = context;
            }

            public void Add(string key)
            {
                if (string.IsNullOrWhiteSpace(key)) throw new System.ArgumentNullException(nameof(key), "Locking a context without a key is disallowed.");

                Locks.GetOrAdd(Context, _ => new List<string>());
                ListLocks.GetOrAdd(Context, _ => new object());
                lock (ListLocks[Context])
                    Locks[Context].Add(key);
            }

            public void Clear()
            {
                if (!Locks.TryGetValue(Context, out var lockList)) return;
                lock (ListLocks[Context])
                    lockList.Clear();
            }

            public bool Contains(string key)
            {
                if (string.IsNullOrWhiteSpace(key)) return false;
                if (!Locks.TryGetValue(Context, out var lockList)) return false;
                lock (ListLocks[Context])
                    return lockList.Contains(key);
            }

            public void CopyTo(string[] array, int arrayIndex)
            {
                ToArray().CopyTo(array, arrayIndex);
            }

            public bool Remove(string key)
            {
                if (string.IsNullOrWhiteSpace(key)) return false;
                if (!Locks.TryGetValue(Context, out var lockList)) return false;
                lock (ListLocks[Context])
                    return lockList.Remove(key);
            }

            public int Count
            {
                get
                {
                    if (!Locks.TryGetValue(Context, out var lockList)) return 0;
                    lock (ListLocks[Context])
                        return lockList.Count;
                }
            }

            public bool IsReadOnly => false;

            public List<string> ToList()
            {
                if (!Locks.TryGetValue(Context, out var lockList))
                    return new List<string>(0);

                lock (ListLocks[Context])
                    return new List<string>(lockList);
            }

            public string[] ToArray()
            {
                if (!Locks.TryGetValue(Context, out var lockList))
                    return new string[0];

                lock (ListLocks[Context])
                    return lockList.ToArray();
            }

            public IEnumerator<string> GetEnumerator()
            {
                if (string.IsNullOrEmpty(Context))
                    yield break;

                string[] snapshot = ToArray();
                foreach (var item in snapshot)
                    yield return item;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public int IndexOf(string key)
            {
                if (!Locks.TryGetValue(Context, out var lockList)) return -1;
                lock (ListLocks[Context])
                    return lockList.IndexOf(key);
            }

            public void Insert(int index, string key)
            {
                if (!Locks.TryGetValue(Context, out var lockList)) return;
                lock (ListLocks[Context])
                    lockList.Insert(index, key);
            }

            public void RemoveAt(int index)
            {
                if (!Locks.TryGetValue(Context, out var lockList)) return;
                lock (ListLocks[Context])
                    lockList.RemoveAt(index);
            }

            public string this[int index]
            {
                get
                {
                    if (!Locks.TryGetValue(Context, out var lockList)) return null;
                    lock (ListLocks[Context])
                        return lockList[index];
                }
                set
                {
                    if (!Locks.TryGetValue(Context, out var lockList)) return;
                    lock (ListLocks[Context])
                        lockList[index] = value;
                }
            }

            public bool Equals(LockContext obj)
            {
                return obj.Context == Context;
            }

            public override bool Equals(object obj)
            {
                if (obj is null) return false;
                if (ReferenceEquals(this, obj)) return true;
                return obj.GetType() == GetType() && Equals((LockContext)obj);
            }

            public override int GetHashCode()
            {
                return Context?.GetHashCode() ?? 0;
            }

            public override string ToString() => $"{Context}[{string.Join(", ", ToArray())}]";

            public static bool operator ==(LockContext a, LockContext b) => a?.Context == b?.Context;
            public static bool operator !=(LockContext a, LockContext b) => !(a == b);

            public static implicit operator bool(LockContext a) => a != null && a.Count > 0;
        }
    }
}
