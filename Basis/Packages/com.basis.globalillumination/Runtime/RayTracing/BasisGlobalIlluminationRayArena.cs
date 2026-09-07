using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A growable uint arena mirrored to a structured buffer. Ray hits need the triangle indices and the vertex
/// normals of whatever they land on, and there is no way to bind one buffer per mesh, so every mesh's data
/// lives in one shared arena and each instance carries the offset it was written at.
/// </summary>
public sealed class BasisGlobalIlluminationRayArena : IDisposable
{
    public readonly struct Block
    {
        public readonly int Offset, Count;
        public Block(int offset, int count) { Offset = offset; Count = count; }
        public bool IsValid => Count > 0;
        public static readonly Block None = new Block(0, 0);
    }

    private readonly string bufferName;
    private readonly List<Block> free = new List<Block>();
    private uint[] data = Array.Empty<uint>();
    private GraphicsBuffer buffer;
    private int highWater;
    private int dirtyStart = int.MaxValue, dirtyEnd = -1;
    private bool resized;

    public BasisGlobalIlluminationRayArena(string name) { bufferName = name; }

    public GraphicsBuffer Buffer => buffer;
    public uint[] Data => data;
    public int Capacity => data.Length;
    public int Used => highWater;
    public int FreeBlocks => free.Count;

    public Block Allocate(int count)
    {
        if (count <= 0) { return Block.None; }

        for (int index = 0; index < free.Count; index++)
        {
            Block candidate = free[index];
            if (candidate.Count < count) { continue; }
            if (candidate.Count == count) { free.RemoveAt(index); }
            else { free[index] = new Block(candidate.Offset + count, candidate.Count - count); }
            return new Block(candidate.Offset, count);
        }

        Reserve(highWater + count);
        Block allocated = new Block(highWater, count);
        highWater += count;
        return allocated;
    }

    public void Release(Block block)
    {
        if (!block.IsValid) { return; }
        if (block.Offset + block.Count == highWater)
        {
            highWater = block.Offset;
            TrimFreeListToHighWater();
            return;
        }
        Insert(block);
    }

    /// <summary>Marks a written range so the next Upload only pushes what changed.</summary>
    public void MarkDirty(in Block block)
    {
        if (!block.IsValid) { return; }
        dirtyStart = Mathf.Min(dirtyStart, block.Offset);
        dirtyEnd = Mathf.Max(dirtyEnd, block.Offset + block.Count);
    }

    /// <summary>
    /// Pushes what changed. The buffer is never left null even for an empty arena, because the trace kernel
    /// declares it unconditionally and an unbound structured buffer is not safe to leave on a shader.
    /// </summary>
    public void Upload()
    {
        Reserve(1);
        if (buffer == null || buffer.count < data.Length)
        {
            buffer?.Dispose();
            buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, data.Length, sizeof(uint)) { name = bufferName };
            resized = true;
        }

        if (resized)
        {
            buffer.SetData(data, 0, 0, highWater > 0 ? highWater : data.Length);
            resized = false;
        }
        else if (dirtyEnd > dirtyStart)
        {
            int count = Mathf.Min(dirtyEnd, data.Length) - dirtyStart;
            if (count > 0) { buffer.SetData(data, dirtyStart, dirtyStart, count); }
        }

        dirtyStart = int.MaxValue;
        dirtyEnd = -1;
    }

    private void Reserve(int required)
    {
        if (data.Length >= required) { return; }
        int capacity = Mathf.Max(1024, data.Length);
        while (capacity < required) { capacity *= 2; }
        Array.Resize(ref data, capacity);
        resized = true;
    }

    private void Insert(Block block)
    {
        int index = 0;
        while (index < free.Count && free[index].Offset < block.Offset) { index++; }
        free.Insert(index, block);

        if (index + 1 < free.Count && free[index].Offset + free[index].Count == free[index + 1].Offset)
        {
            free[index] = new Block(free[index].Offset, free[index].Count + free[index + 1].Count);
            free.RemoveAt(index + 1);
        }
        if (index > 0 && free[index - 1].Offset + free[index - 1].Count == free[index].Offset)
        {
            free[index - 1] = new Block(free[index - 1].Offset, free[index - 1].Count + free[index].Count);
            free.RemoveAt(index);
        }
    }

    private void TrimFreeListToHighWater()
    {
        bool trimmed = true;
        while (trimmed && free.Count > 0)
        {
            trimmed = false;
            Block last = free[free.Count - 1];
            if (last.Offset + last.Count == highWater)
            {
                highWater = last.Offset;
                free.RemoveAt(free.Count - 1);
                trimmed = true;
            }
        }
    }

    public void Clear()
    {
        free.Clear();
        highWater = 0;
        dirtyStart = int.MaxValue;
        dirtyEnd = -1;
    }

    public void Dispose()
    {
        buffer?.Dispose();
        buffer = null;
        data = Array.Empty<uint>();
        free.Clear();
        highWater = 0;
    }
}
