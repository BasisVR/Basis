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

using JetBrains.Annotations;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Unity.Collections;

/// <summary>
/// (Internal) Allows you to enumerate an IEnumerable in a non allocating way, if possible.
/// </summary>
/// <typeparam name="T">The type of item contained by the collection.</typeparam>
/// <seealso cref="OVRExtensions.ToNonAlloc{T}"/>
internal readonly struct OVREnumerable<T> : IEnumerable<T>
{
    private readonly IEnumerable<T> _enumerable;

    /// <summary>This is an internal member.</summary>
    public OVREnumerable(IEnumerable<T> enumerable) => _enumerable = enumerable;

    /// <summary>This is an internal member.</summary>
    public Enumerator GetEnumerator() => new(_enumerable);

    /// <summary>This is an internal member.</summary>
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    /// <summary>This is an internal member.</summary>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // If the count can be determined without enumerating the collection, sets count and returns True.
    // Otherwise, returns False.
    /// <summary>This is an internal member.</summary>
    public bool TryGetCount(out int count)
    {
        var ncount = Count;
        count = ncount ?? 0;
        return ncount.HasValue;
    }

    // Returns the count if it can do so without enumerating the collection, otherwise null.
    /// <summary>This is an internal member.</summary>
    public int? Count => _enumerable switch
    {
        null => 0,
        ICollection c => c.Count,
        ICollection<T> c => c.Count,
        IReadOnlyCollection<T> c => c.Count,
        _ => null,
    };

    /// <summary>This is an internal member.</summary>
    [Obsolete("This method may enumerate the collection. Consider " + nameof(Count) + " or " +
              nameof(TryGetCount) + " instead.")]
    public int GetCount()
    {
        if (!TryGetCount(out var count))
        {
            count = 0;
            foreach (var item in _enumerable)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>This is an internal type.</summary>
    public struct Enumerator : IEnumerator<T>
    {
        private enum CollectionType
        {
            None,
            ReadOnlyList,
            List,
            Set,
            Queue,
            Enumerable,
        }

        private int _listIndex;
        private readonly CollectionType _type;
        private readonly int _listCount;
        private readonly IEnumerator<T> _enumerator;
        private readonly IReadOnlyList<T> _readOnlyList;
        private HashSet<T>.Enumerator _setEnumerator;
        private Queue<T>.Enumerator _queueEnumerator;
        private List<T>.Enumerator _listEnumerator;

        /// <summary>This is an internal member.</summary>
        public Enumerator(IEnumerable<T> enumerable)
        {
            _setEnumerator = default;
            _queueEnumerator = default;
            _listEnumerator = default;
            _enumerator = null;
            _readOnlyList = null;
            _listIndex = -1;
            _listCount = 0;

            switch (enumerable)
            {
                case null:
                    _type = CollectionType.None;
                    break;
                case List<T> list:
                    _listEnumerator = list.GetEnumerator();
                    _type = CollectionType.List;
                    break;
                case IReadOnlyList<T> readOnlyList:
                    _readOnlyList = readOnlyList;
                    _listCount = readOnlyList.Count;
                    _type = CollectionType.ReadOnlyList;
                    break;
                case HashSet<T> set:
                    _setEnumerator = set.GetEnumerator();
                    _type = CollectionType.Set;
                    break;
                case Queue<T> queue:
                    _queueEnumerator = queue.GetEnumerator();
                    _type = CollectionType.Queue;
                    break;
                default:
                    _enumerator = enumerable.GetEnumerator();
                    _type = CollectionType.Enumerable;
                    break;
            }
        }

        /// <summary>This is an internal member.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _type switch
        {
            CollectionType.None => false,
            CollectionType.List => _listEnumerator.MoveNext(),
            CollectionType.ReadOnlyList => MoveNextReadOnlyList(),
            CollectionType.Set => _setEnumerator.MoveNext(),
            CollectionType.Queue => _queueEnumerator.MoveNext(),
            CollectionType.Enumerable => _enumerator.MoveNext(),
            _ => throw new InvalidOperationException($"Unsupported collection type {_type}.")
        };

        private bool MoveNextReadOnlyList()
        {
            ValidateAndThrow();
            return ++_listIndex < _listCount;
        }

        /// <summary>This is an internal member.</summary>
        public void Reset()
        {
            switch (_type)
            {
                case CollectionType.ReadOnlyList:
                    ValidateAndThrow();
                    _listIndex = -1;
                    break;
                case CollectionType.Set:
                case CollectionType.Queue:
                case CollectionType.List:
                    break;
                case CollectionType.Enumerable:
                    _enumerator.Reset();
                    break;
            }
        }

        /// <summary>This is an internal member.</summary>
        public T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _type switch
            {
                CollectionType.List => _listEnumerator.Current,
                CollectionType.ReadOnlyList => _readOnlyList[_listIndex],
                CollectionType.Set => _setEnumerator.Current,
                CollectionType.Queue => _queueEnumerator.Current,
                CollectionType.Enumerable => _enumerator.Current,
                _ => throw new InvalidOperationException($"Unsupported collection type {_type}.")
            };
        }

        /// <summary>This is an internal member.</summary>
        object IEnumerator.Current => Current;

        /// <summary>This is an internal member.</summary>
        public void Dispose()
        {
            switch (_type)
            {
                case CollectionType.List:
                    _listEnumerator.Dispose();
                    break;
                case CollectionType.ReadOnlyList:
                    break;
                case CollectionType.Set:
                    _setEnumerator.Dispose();
                    break;
                case CollectionType.Queue:
                    _queueEnumerator.Dispose();
                    break;
                case CollectionType.Enumerable:
                    _enumerator.Dispose();
                    break;
            }
        }

        /// <summary>This is an internal member.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ValidateAndThrow()
        {
            if (_listCount != _readOnlyList.Count)
                throw new InvalidOperationException($"The list changed length during enumeration.");
        }
    }
}

static partial class OVRExtensions
{
    /// <summary>
    /// Allows the caller to enumerate an IEnumerable in a non-allocating way, if possible.
    /// </summary>
    /// <remarks>
    /// <example>
    /// If you have an IEnumerable, this will allocate IEnumerator:
    /// <code><![CDATA[
    /// void Foo(IEnumerable<T> collection) {
    ///   // Allocates an IEnumerator<T>
    ///   foreach (var item in collection) {
    ///     // do something with item
    ///   }
    /// }
    /// ]]></code>
    /// However, often the IEnumerable is at least an IReadOnlyList, e.g., a List or Array, its elements can be accessed
    /// using the index operator. This custom enumerable will do that:
    /// <code><![CDATA[
    /// void Foo(IEnumerable<T> collection) {
    ///   // Returns a non-allocating struct-based enumerator
    ///   foreach (var item in collection.ToNonAlloc()) {
    ///     // do something with item
    ///   }
    /// }
    /// ]]></code>
    /// </example>
    ///
    /// Note that some safeties cannot be guaranteed, such as mutations to a List during enumeration.
    /// </remarks>
    /// <param name="enumerable">The collection you wish to enumerate.</param>
    /// <typeparam name="T">The type of item in the collection.</typeparam>
    /// <returns>Returns a non-allocating enumerable.</returns>
    internal static OVREnumerable<T> ToNonAlloc<T>([NoEnumeration] this IEnumerable<T> enumerable) => new(enumerable);

    /// <summary>
    /// Copies a collection to a `NativeArray`.
    /// </summary>
    /// <remarks>
    /// This will copy <paramref name="enumerable"/> to a NativeArray in the most efficient way possible. Behavior of
    /// <paramref name="enumerable"/> in order of decreasing efficiency:
    /// - Fixed-size array: single native allocation + memcpy - no managed allocations
    /// - IReadOnlyList: single native allocation + iteration - no managed allocations
    /// - HashSet: single native allocation + iteration - no managed allocations
    /// - Queue: single native allocation + iteration - no managed allocations
    /// - IReadOnlyCollection: single native allocation - single managed IEnumerator allocation
    /// - ICollection: single native allocation - single managed IEnumerator allocation
    /// - Anything else: multiple native allocations (using a growth strategy) - single managed IEnumerator allocation
    /// </remarks>
    /// <param name="enumerable">The collection to copy to a NativeArray</param>
    /// <param name="allocator">The allocator to use for the returned NativeArray</param>
    /// <typeparam name="T">The type of the elements in the collection.</typeparam>
    /// <returns>Returns a new NativeArray allocated with <paramref name="allocator"/> filled with the elements of
    /// <paramref name="enumerable"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="enumerable"/> is `null`.</exception>
    internal static NativeArray<T> ToNativeArray<T>(this IEnumerable<T> enumerable, Allocator allocator)
        where T : struct
    {
        if (enumerable == null)
            throw new ArgumentNullException(nameof(enumerable));

        switch (enumerable)
        {
            // Easiest case, since NativeArray supports this
            case T[] fixedArray: return new NativeArray<T>(fixedArray, allocator);

            // Good, since we can iterate the list without allocating
            case IReadOnlyList<T> list:
            {
                var array = new NativeArray<T>(list.Count, allocator, NativeArrayOptions.UninitializedMemory);
                for (var i = 0; i < array.Length; i++)
                {
                    array[i] = list[i];
                }

                return array;
            }

            // HashSet can be iterated without allocation but doesn't conform to any interface that supports it, so
            // it's a special case.
            case HashSet<T> set:
            {
                var array = new NativeArray<T>(set.Count, allocator, NativeArrayOptions.UninitializedMemory);
                var index = 0;
                foreach (var item in set)
                {
                    array[index++] = item;
                }

                return array;
            }

            // Same as HashSet
            case Queue<T> queue:
            {
                var array = new NativeArray<T>(queue.Count, allocator, NativeArrayOptions.UninitializedMemory);
                var index = 0;
                foreach (var item in queue)
                {
                    array[index++] = item;
                }

                return array;
            }

            // Less good because we need to allocate to iterate, but we can know the size beforehand
            case IReadOnlyCollection<T> collection:
            {
                var array = new NativeArray<T>(collection.Count, allocator, NativeArrayOptions.UninitializedMemory);
                var index = 0;
                foreach (var item in collection)
                {
                    array[index++] = item;
                }

                return array;
            }

            // Same as above
            case ICollection<T> collection:
            {
                var array = new NativeArray<T>(collection.Count, allocator, NativeArrayOptions.UninitializedMemory);
                var index = 0;
                foreach (var item in collection)
                {
                    array[index++] = item;
                }

                return array;
            }

            // Fallback to worst case, but only enumerate the collection once
            default:
            {
                var count = 0;
                var capacity = 4;
                var array = new NativeArray<T>(capacity, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                foreach (var item in enumerable)
                {
                    if (count == capacity)
                    {
                        // Grow the array
                        capacity *= 2;
                        NativeArray<T> newArray;
                        using (array)
                        {
                            newArray = new NativeArray<T>(capacity, Allocator.Temp,
                                NativeArrayOptions.UninitializedMemory);
                            NativeArray<T>.Copy(array, newArray, array.Length);
                        }

                        array = newArray;
                    }

                    array[count++] = item;
                }

                using (array)
                {
                    var result = new NativeArray<T>(count, allocator, NativeArrayOptions.UninitializedMemory);
                    NativeArray<T>.Copy(array, result, count);
                    return result;
                }
            }
        }
    }
}

internal static class OVREnumerable
{
    /// <summary>This is an internal member.</summary>
    public static unsafe int CopyTo<T>(this OVREnumerable<T> enumerable, T* memory) where T : unmanaged
    {
        var count = 0;
        foreach (var item in enumerable)
        {
            memory[count++] = item;
        }

        return count;
    }
}
