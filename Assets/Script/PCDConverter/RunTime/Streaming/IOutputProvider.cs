using System;

public interface IOutputProvider
{
    ReadOnlySpan<byte> ReadRange(int offset, int length);
    void Append(int outOffset, ArraySegment<byte> chunk);
}