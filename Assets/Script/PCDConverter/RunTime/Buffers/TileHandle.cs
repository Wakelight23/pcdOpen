using System;
using UnityEngine;

public enum ResidencyState { Unloaded, Streaming, Resident }

public sealed class TileHandle
{
    public TileMeta Meta;
    public ResidencyState State;
    public BufferPool.Handle VertexHandle;
    public GraphicsBuffer SharedVertexBuffer;
    public int Float3Count;
    public ulong LastUsedTick;
}
