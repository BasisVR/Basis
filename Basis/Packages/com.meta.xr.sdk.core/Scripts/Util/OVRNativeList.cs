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
using JetBrains.Annotations;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

/// <summary>
/// Internal
/// </summary>
// This facilitates interop between C# and C when it is necessary to pass around contiguous blocks of unmanaged memory
// e.g., an array.
struct OVRNativeList<T> : IDisposable, IReadOnlyList<T> where T : unmanaged
{
    NativeArray<T> _array;

    readonly Allocator _allocator;

    public int Count { get; private set; }

    public int Capacity => _array.Length;

    public bool IsCreated => _array.IsCreated;

    public OVRNativeList(int? initialCapacity, Allocator allocator)
    {
        _array = initialCapacity.HasValue ? new NativeArray<T>(initialCapacity.Value, allocator) : default;
        _allocator = allocator;
        Count = 0;
    }

    public OVRNativeList(Allocator allocator)
    {
        _array = default;
        _allocator = allocator;
        Count = 0;
    }

    // NOTE: Add and AddRange invalidates these pointers
    public unsafe T* PtrToElementAt(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"{nameof(index)} must be greater than or equal to zero.");
        if (index >= Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"{nameof(index)} must be less than {nameof(Count)} ({Count}).");

        return Data + index;
    }

    // NOTE: Add and AddRange invalidate this pointer
    public unsafe T* Data => IsCreated ? (T*)_array.GetUnsafePtr() : null;

    // NOTE: Add and AddRange invalidate the NativeArray
    public NativeArray<T> AsNativeArray() => _array.GetSubArray(0, Count);

    // NOTE: Add and AddRange invalidate the Span
    public unsafe Span<T> AsSpan() => new(Data, Count);

    // NOTE: Add and AddRange invalidate the ReadOnlySpan
    public unsafe ReadOnlySpan<T> AsReadOnlySpan() => new(Data, Count);

    public NativeArray<T>.Enumerator GetEnumerator() => AsNativeArray().GetEnumerator();

    // NOTE: This invalidates any pointers returned by other methods
    public void Add(T item)
    {
        EnsureCapacity(Count + 1);
        _array[Count++] = item;
    }

    // NOTE: This invalidates any pointers returned by other methods
    public unsafe void AddRange(IEnumerable<T> collection)
    {
        var enumerable = collection.ToNonAlloc();
        if (enumerable.TryGetCount(out var count))
        {
            // If possible, try to reserve memory once, to avoid repeated reallocations during the Add phase
            EnsureCapacity(Count + count);

            // Fixed arrays use contiguous memory, so we can just use a memcpy
            if (collection is T[] fixedArray)
            {
                fixed (T* source = fixedArray)
                {
                    UnsafeUtility.MemCpy(Data + Count, source, sizeof(T) * fixedArray.Length);
                }

                Count += count;
                return;
            }
        }

        foreach (var item in enumerable)
        {
            Add(item);
        }
    }

    public void Clear() => Count = 0;

    public T this[int index]
    {
        get
        {
            if (index >= Count)
                throw new IndexOutOfRangeException($"{nameof(index)} ({index}) must be less than {nameof(Count)} ({Count}).");

            return _array[index];
        }

        set
        {
            if (index >= Count)
                throw new IndexOutOfRangeException($"{nameof(index)} ({index}) must be less than {nameof(Count)} ({Count}).");

            _array[index] = value;
        }
    }

    public void Dispose()
    {
        if (_array.IsCreated)
        {
            _array.Dispose();
            _array = default;
        }

        Count = 0;
    }

    public JobHandle Dispose(JobHandle dependency)
    {
        Count = 0;
        return _array.Dispose(dependency);
    }

    public static unsafe implicit operator T*(OVRNativeList<T> list) => list.Data;

    public static implicit operator Span<T>(OVRNativeList<T> list) => list.AsSpan();

    public static implicit operator ReadOnlySpan<T>(OVRNativeList<T> list) => list.AsReadOnlySpan();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    unsafe void EnsureCapacity(int capacity)
    {
        if (Capacity >= capacity) return;

        capacity = Math.Max(capacity, Math.Max(4, Capacity * 3 / 2));
        var array = new NativeArray<T>(capacity, _allocator);

        if (_array.IsCreated)
        {
            UnsafeUtility.MemCpy(
                destination: array.GetUnsafePtr(),
                source: _array.GetUnsafeReadOnlyPtr(),
                size: sizeof(T) * Count);

            _array.Dispose();
        }

        _array = array;
    }
}

internal static class OVRNativeList
{
    // See WithSuggestedCapacityFrom below
    public readonly struct CapacityHelper
    {
        readonly int? _count;

        public CapacityHelper(int? count) => _count = count;

        // Allocates an empty list with sufficient capacity if possible
        // The caller owns the resulting list and must dispose it.
        public OVRNativeList<T> AllocateEmpty<T>(Allocator allocator) where T : unmanaged => new(_count, allocator);
    }

    // Use this to allocate a native list without having to provide excess type information.
    // The resulting list will be empty, but will have reserved capacity if possible to do so without enumerating
    // the collection.
    // Ex:
    //   using var list = OVRNativeList
    //                      .WithSuggestedCapacityFrom(collection)
    //                      .AllocateEmpty<ulong>(Allocator.Temp);
    //
    //   foreach (var item in collection.ToNonAlloc()) {
    //     list.Add(item.Value);
    //  }
    public static CapacityHelper WithSuggestedCapacityFrom<T>([NoEnumeration] IEnumerable<T> collection)
        => new(collection.ToNonAlloc().Count);

    // Similar to WithSuggestedCapacityFrom above, but provides the non-allocating enumerable as an out parameter, which
    // can simplify the callsite to this:
    //
    //  using var list = OVRNativeList
    //                     .WithSuggestedCapacityFrom(collection, out var enumerable)
    //                     .AllocateEmpty<ulong>(Allocator.Temp);
    //
    // foreach (var item in enumerable) {
    //   list.Add(item.Value);
    // }
    public static CapacityHelper WithSuggestedCapacityFrom<T>([NoEnumeration] IEnumerable<T> collection,
        out OVREnumerable<T> nonAllocatingEnumerable)
    {
        nonAllocatingEnumerable = collection.ToNonAlloc();
        return new CapacityHelper(nonAllocatingEnumerable.Count);
    }

    // Creates a new OVRNativeList and copies the elements from collection into it.
    // The caller owns the memory and must dispose it.
    public static OVRNativeList<T> ToNativeList<T>(this IEnumerable<T> collection, Allocator allocator)
        where T : unmanaged
    {
        var list = new OVRNativeList<T>(allocator);
        list.AddRange(collection);
        return list;
    }
}
