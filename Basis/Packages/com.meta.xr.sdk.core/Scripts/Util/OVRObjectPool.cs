/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

/// <summary>
/// Helper class to handle generic class object pools and avoid allocations in the SDK that would lead to garbage collection.
/// </summary>
internal static class OVRObjectPool
{
    public interface IPoolObject
    {
        void OnGet();
        void OnReturn();
    }

    static class Storage<T> where T : class, new()
    {
        static readonly HashSet<T> s_hashSet = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Remove(T item)
        {
            return s_hashSet.Remove(item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Add(T item)
        {
            return s_hashSet.Add(item);
        }

        public static readonly Action Clear = () => s_hashSet.Clear();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T GetOrCreate()
        {
            using var enumerator = s_hashSet.GetEnumerator();
            if (enumerator.MoveNext())
            {
                var item = enumerator.Current;
                Remove(item);
                return item;
            }

            return new T();
        }
    }


    /// <summary>
    /// Gets an object of type T from it's respective pool. If none is available a new one is created.
    /// </summary>
    /// <returns>Object of type T</returns>
    public static T Get<T>() where T : class, new()
    {
        var item = Storage<T>.GetOrCreate();

        if (item is IList list) list.Clear();
        else if (item is IDictionary dict) dict.Clear();

        (item as IPoolObject)?.OnGet();

        return item;
    }

    public static List<T> List<T>() => Get<List<T>>();

    public static List<T> List<T>(IEnumerable<T> source)
    {
        var list = Get<List<T>>();
        foreach (var item in source.ToNonAlloc())
        {
            list.Add(item);
        }

        return list;
    }

    public static Dictionary<TKey, TValue> Dictionary<TKey, TValue>() => Get<Dictionary<TKey, TValue>>();

    public static HashSet<T> HashSet<T>()
    {
        var item = Get<HashSet<T>>();
        item.Clear();
        return item;
    }

    public static Stack<T> Stack<T>()
    {
        var item = Get<Stack<T>>();
        item.Clear();
        return item;
    }

    public static Queue<T> Queue<T>()
    {
        var item = Get<Queue<T>>();
        item.Clear();
        return item;
    }

    /// <summary>
    /// Returns an object of type T to it's respective pool. If the object is null or already present in the pool no changes are made.
    /// </summary>
    /// <remarks>
    /// After returning an object to the object pool using it is not allowed and leads to undefined behaviour, please <see cref="Get{T}"/> another object from the pool instead.
    /// </remarks>
    public static void Return<T>(T obj) where T : class, new()
    {
        switch (obj)
        {
            case null: return;
            case IList list:
                list.Clear();
                break;
            case IDictionary dict:
                dict.Clear();
                break;
            case IPoolObject returnable:
                returnable.OnReturn();
                break;
        }

        Storage<T>.Add(obj);
    }

    public static void Return<T>(HashSet<T> set)
    {
        set?.Clear();
        Return<HashSet<T>>(set);
    }

    public static void Return<T>(Stack<T> stack)
    {
        stack?.Clear();
        Return<Stack<T>>(stack);
    }

    public static void Return<T>(Queue<T> queue)
    {
        queue?.Clear();
        Return<Queue<T>>(queue);
    }

    public struct ListScope<T> : IDisposable
    {
        List<T> _list;
        public ListScope(out List<T> list) => _list = list = List<T>();
        public ListScope(IEnumerable<T> source, out List<T> list) => _list = list = List(source);
        public void Dispose() => Return(_list);
    }

    public struct TaskScope<T> : IDisposable
    {
        ListScope<OVRTask<T>> _tasks;
        ListScope<T> _results;

        public TaskScope(out List<OVRTask<T>> tasks, out List<T> results)
        {
            _tasks = new ListScope<OVRTask<T>>(out tasks);
            _results = new ListScope<T>(out results);
        }

        public void Dispose()
        {
            _tasks.Dispose();
            _results.Dispose();
        }
    }

    public readonly struct DictionaryScope<TKey, TValue> : IDisposable
    {
        readonly Dictionary<TKey, TValue> _dictionary;

        public DictionaryScope(out Dictionary<TKey, TValue> dictionary)
            => _dictionary = dictionary = Dictionary<TKey, TValue>();

        public void Dispose() => Return(_dictionary);
    }

    public readonly struct HashSetScope<T> : IDisposable
    {
        readonly HashSet<T> _set;
        public HashSetScope(out HashSet<T> set) => _set = set = HashSet<T>();
        public void Dispose() => Return(_set);
    }

    public readonly struct StackScope<T> : IDisposable
    {
        readonly Stack<T> _stack;
        public StackScope(out Stack<T> stack) => _stack = stack = Stack<T>();
        public void Dispose() => Return(_stack);
    }

    public readonly struct QueueScope<T> : IDisposable
    {
        readonly Queue<T> _queue;
        public QueueScope(out Queue<T> queue) => _queue = queue = Queue<T>();
        public void Dispose() => Return(_queue);
    }

    public readonly struct ItemScope<T> : IDisposable where T : class, new()
    {
        readonly T _item;
        public ItemScope(out T item) => _item = item = Get<T>();
        public void Dispose() => Return(_item);
    }

}
