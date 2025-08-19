using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class BufferPool : IDisposable
{
    public struct Handle
    {
        public int SlabIndex;
        public int OffsetBytes;
        public int SizeBytes;
        public bool IsValid => SlabIndex >= 0 && SizeBytes > 0;
    }

    private class Slab
    {
        public readonly int Capacity;
        public readonly GraphicsBuffer Buffer;
        private readonly SortedDictionary<int, int> freeList = new();

        public Slab(int capacityBytes, GraphicsBuffer.Target target, GraphicsBuffer.UsageFlags usage)
        {
            Capacity = capacityBytes;
            Buffer = new GraphicsBuffer(target, (GraphicsBuffer.UsageFlags)(capacityBytes / 4), 4, (int)usage);
            freeList[0] = capacityBytes;
        }

        public bool TryAlloc(int sizeBytes, out Handle h)
        {
            foreach (var kv in freeList)
            {
                if (kv.Value >= sizeBytes)
                {
                    int offset = kv.Key;
                    int remain = kv.Value - sizeBytes;
                    freeList.Remove(offset);
                    if (remain > 0) freeList[offset + sizeBytes] = remain;

                    h = new Handle { SlabIndex = -1, OffsetBytes = offset, SizeBytes = sizeBytes };
                    return true;
                }
            }
            h = default;
            return false;
        }

        public void Free(Handle h)
        {
            freeList[h.OffsetBytes] = h.SizeBytes;
            Coalesce();
        }

        private void Coalesce()
        {
            if (freeList.Count <= 1) return;
            var keys = new List<int>(freeList.Keys);
            int prevOff = keys[0];
            int prevSize = freeList[prevOff];

            for (int i = 1; i < keys.Count; i++)
            {
                int off = keys[i];
                int size = freeList[off];
                if (prevOff + prevSize == off)
                {
                    freeList.Remove(prevOff);
                    freeList.Remove(off);
                    freeList[prevOff] = prevSize + size;
                    prevSize += size;
                }
                else
                {
                    prevOff = off;
                    prevSize = size;
                }
            }
        }
    }

    private readonly List<Slab> slabs = new();
    private readonly GraphicsBuffer.Target target;
    private readonly GraphicsBuffer.UsageFlags usage;

    public BufferPool(GraphicsBuffer.Target target, GraphicsBuffer.UsageFlags usage, params int[] slabSizesBytes)
    {
        this.target = target;
        this.usage = usage;
        foreach (var s in slabSizesBytes) slabs.Add(new Slab(s, target, usage));
    }

    public Handle Allocate(int sizeBytes, out GraphicsBuffer buffer)
    {
        int aligned = (sizeBytes + 255) & ~255;
        for (int i = 0; i < slabs.Count; i++)
        {
            if (slabs[i].TryAlloc(aligned, out var h))
            {
                h.SlabIndex = i;
                buffer = slabs[i].Buffer;
                return h;
            }
        }
        buffer = null;
        return default;
    }

    public void Free(Handle h)
    {
        if (!h.IsValid) return;
        slabs[h.SlabIndex].Free(h);
    }

    public GraphicsBuffer GetSlabBuffer(int slabIndex) => slabs[slabIndex].Buffer;

    public void Dispose()
    {
        foreach (var s in slabs) s.Buffer?.Dispose();
        slabs.Clear();
    }
}
