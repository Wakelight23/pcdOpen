using System;
using UnityEngine;

[Serializable]
public class TileMeta
{
    public int Id;
    public Bounds AABB;
    public string Path;
    public long FileBytes;
    public int[] LodOffsets;     // float3 ¥‹¿ß
    public int[] LodCounts;
    public int PointStrideBytes;
    public bool IsCompressed;
}