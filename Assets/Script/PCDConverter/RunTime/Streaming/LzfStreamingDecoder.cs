using System;

public sealed class LzfStreamingDecoder
{
    // (outputOffset, newDataChunk)
    public Action<int, ArraySegment<byte>> OnOutput;

    private byte[] _out; // growing buffer
    private int _produced; // total produced bytes
    private readonly int _capacityHint;

    public LzfStreamingDecoder(int capacityHint)
    {
        _capacityHint = Math.Max(capacityHint, 1 << 20);
        _out = new byte[_capacityHint];
        _produced = 0;
    }

    private void EnsureOutCapacity(int needEnd)
    {
        if (needEnd <= _out.Length) return;
        int newSize = _out.Length;
        while (newSize < needEnd) newSize <<= 1;
        Array.Resize(ref _out, newSize);
    }

    // Simple LZF (liblzf-compatible) block streaming decode.
    // This expects that the input spans can be concatenated logically across calls.
    // If producing extremely large outputs, consider chunked OnOutput dispatch.
    public void Consume(ReadOnlySpan<byte> input)
    {
        int ip = 0; // input cursor
        while (ip < input.Length)
        {
            // Need at least 1 control byte
            byte ctrl = input[ip++];
            if (ctrl < 32)
            {
                // Literal run: length = ctrl + 1
                int litLen = ctrl + 1;
                if (ip + litLen > input.Length)
                {
                    // Not enough bytes in this chunk; defer to next Consume call
                    ip--; // step back so ctrl is re-read next time
                    break;
                }

                EnsureOutCapacity(_produced + litLen);
                input.Slice(ip, litLen).CopyTo(new Span<byte>(_out, _produced, litLen));
                int prev = _produced;
                _produced += litLen;
                OnOutput?.Invoke(prev, new ArraySegment<byte>(_out, prev, litLen));
                ip += litLen;
            }
            else
            {
                // Back-reference (match)
                // ctrl >= 32, determines length, and next byte gives low 8 bits of offset
                // length = (ctrl >> 5) + 2 (or extended if ctrl == 32..35 depending on variant)
                // offset = ((ctrl & 0x1F) << 8) + nextByte + 1
                if (ip >= input.Length)
                {
                    ip--; // re-read ctrl next call
                    break;
                }
                byte b2 = input[ip++];

                int length = (ctrl >> 5) + 2;
                int offset = ((ctrl & 0x1F) << 8) + b2 + 1;

                // Some encoders use ctrl == 32 to mean extended length (length += next input byte)
                if (length == 2)
                {
                    if (ip >= input.Length)
                    {
                        // Need one more byte for extended length
                        ip -= 2; // back to ctrl so we retry later
                        break;
                    }
                    length = input[ip++] + 9; // 2 + (7 + nextByte)? Common liblzf: len = next + 9
                }

                int srcPos = _produced - offset;
                if (srcPos < 0)
                {
                    throw new InvalidOperationException("Invalid LZF back-reference (negative source).");
                }

                EnsureOutCapacity(_produced + length);
                // Copy overlapping sequence
                var dst = new Span<byte>(_out, _produced, length);
                var src = new ReadOnlySpan<byte>(_out, srcPos, length);
                // Overlap-safe copy (Span handles overlap)
                src.CopyTo(dst);

                int prev = _produced;
                _produced += length;
                OnOutput?.Invoke(prev, new ArraySegment<byte>(_out, prev, length));
            }
        }
    }

    public void Finish()
    {
        // No-op for basic LZF: nothing buffered besides output which is already flushed via OnOutput.
    }

    public int TotalProduced => _produced;
}
