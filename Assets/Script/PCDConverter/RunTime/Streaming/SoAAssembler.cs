using System;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

public sealed class SoAAssembler
{
    private readonly SoALayout _layout;
    private readonly int _floatBytes = sizeof(float);

    // ���� ������(����Ʈ ����, �� SoA �ʵ忡�� ���������� Ȯ���� ����Ʈ ��)
    private int _xWrittenBytes, _yWrittenBytes, _zWrittenBytes;

    // �̹� GPU�� ��ŷ/���ε��� float3 ����
    private int _packedFloat3Count;

    // GPU ���
    private readonly GraphicsBuffer _dst;
    private readonly int _dstStartOffsetBytes; // �� Ÿ���� GPU ���� ���� ������(����Ʈ)
    private int _dstWriteCursorBytes; // GPU ���� �� ���� ���� ������(����Ʈ)
    private readonly int _dstTotalBytes;

    // ���ڴ� ��� ������ (��ü ��� ���۸� ���� �������� �ʰ� �κ� ��ȸ)
    private readonly IOutputProvider _outputProvider;

    // �� ���� GPU�� ������ �� ����� �ӽ� CPU ��ũ ����(�ʿ� �ּ� ũ�⸸ŭ ����)
    private byte[] _packScratch;
    private SoALayout soa;
    private GraphicsBuffer buf;
    private int offsetBytes;
    private int dstBytes;

    public SoAAssembler(SoALayout layout, GraphicsBuffer dst, int dstStartOffsetBytes, int dstTotalBytes, IOutputProvider outputProvider)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _dst = dst ?? throw new ArgumentNullException(nameof(dst));
        if (dstStartOffsetBytes < 0) throw new ArgumentOutOfRangeException(nameof(dstStartOffsetBytes));
        if (dstTotalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(dstTotalBytes));
        _dstStartOffsetBytes = dstStartOffsetBytes;
        _dstTotalBytes = dstTotalBytes;

        _dstWriteCursorBytes = dstStartOffsetBytes;
        _packedFloat3Count = 0;

        _outputProvider = outputProvider ?? throw new ArgumentNullException(nameof(outputProvider));
    }

    // decoder���� ȣ��: ��� ��Ʈ���� [outOffset, outOffset+data.Count) ������ ���� ������
    public void OnOutput(int outOffset, ArraySegment<byte> data)
    {
        var span = new ReadOnlySpan<byte>(data.Array, data.Offset, data.Count);

        // data ������ x/y/z SoA ��ϰ� ��ġ�� �κ��� ���� �ݿ�
        AccumulateField(_layout.XField, ref _xWrittenBytes, outOffset, span);
        AccumulateField(_layout.YField, ref _yWrittenBytes, outOffset, span);
        AccumulateField(_layout.ZField, ref _zWrittenBytes, outOffset, span);

        TryPackAndUploadSyncedTriplets();
    }

    // ���ڵ��� ������ �� ������ ���� ����ȭ ������ �������� ������ ���ε�
    public void Finish()
    {
        TryPackAndUploadSyncedTriplets();
        // ���� �����Ͱ� �־ x/y/z �� �ϳ��� �������� ���� ������ ����ȭ���� ���� �����̹Ƿ� ���⼭ ������.
    }

    private void TryPackAndUploadSyncedTriplets()
    {
        // x/y/z �� �ּ� ���൵�� �������� ����ȭ�� float3 ������ ���
        int xFloats = _xWrittenBytes / _floatBytes;
        int yFloats = _yWrittenBytes / _floatBytes;
        int zFloats = _zWrittenBytes / _floatBytes;
        int syncedCount = Math.Min(xFloats, Math.Min(yFloats, zFloats));

        int toPack = syncedCount - _packedFloat3Count;
        if (toPack <= 0) return;

        // �� ���� �ʹ� ���� �������� �ʵ��� ��ġ ó�� (��: 1M ����Ʈ ����)
        const int kBatch = 1_000_000;
        int remaining = toPack;

        while (remaining > 0)
        {
            int batchCount = Math.Min(remaining, kBatch);

            // �� �ʵ忡�� [packedFloat3Count .. packedFloat3Count+batchCount) ������ float���� �о�ͼ� float3�� ��ŷ
            int xStart = _layout.SoAStart[_layout.XField] + _packedFloat3Count * _floatBytes;
            int yStart = _layout.SoAStart[_layout.YField] + _packedFloat3Count * _floatBytes;
            int zStart = _layout.SoAStart[_layout.ZField] + _packedFloat3Count * _floatBytes;

            int fieldBytes = batchCount * _floatBytes;

            ReadOnlySpan<byte> xSpan = _outputProvider.ReadRange(xStart, fieldBytes);
            ReadOnlySpan<byte> ySpan = _outputProvider.ReadRange(yStart, fieldBytes);
            ReadOnlySpan<byte> zSpan = _outputProvider.ReadRange(zStart, fieldBytes);

            // GPU �������� �� ����Ʈ ��
            int packBytes = batchCount * 12; // float3 (x,y,z) = 12B

            EnsureScratch(packBytes);

            // CPU���� ���� float3 �迭�� ��ŷ (endianness: ��Ʋ ����� ����)
            // BitConverter.ToSingle�� Span ������尡 Ŀ��, unsafe�� ��� ���� ����
            unsafe
            {
                fixed (byte* px = xSpan)
                fixed (byte* py = ySpan)
                fixed (byte* pz = zSpan)
                fixed (byte* pd = _packScratch)
                {
                    var xPtr = px;
                    var yPtr = py;
                    var zPtr = pz;
                    var dst = pd;

                    for (int i = 0; i < batchCount; ++i)
                    {
                        // 4B�� �״�� ���� (float raw bytes)
                        *(uint*)(dst + 0) = *(uint*)xPtr;
                        *(uint*)(dst + 4) = *(uint*)yPtr;
                        *(uint*)(dst + 8) = *(uint*)zPtr;

                        xPtr += 4;
                        yPtr += 4;
                        zPtr += 4;
                        dst += 12;
                    }
                }
            }

            // GPU ���� ���� üũ
            int bytesWrittenFromTileStart = (_dstWriteCursorBytes - _dstStartOffsetBytes);
            if (bytesWrittenFromTileStart + packBytes > _dstTotalBytes)
                throw new InvalidOperationException("Destination GPU buffer tile range exceeded.");

            // GPU�� ���ε�
            _dst.SetData(_packScratch, 0, _dstWriteCursorBytes, packBytes);

            _dstWriteCursorBytes += packBytes;
            _packedFloat3Count += batchCount;
            remaining -= batchCount;
        }
    }

    private void EnsureScratch(int size)
    {
        if (_packScratch != null && _packScratch.Length >= size) return;
        int newSize = _packScratch?.Length ?? 0;
        if (newSize == 0) newSize = 1 << 20; // 1MB ����
        while (newSize < size) newSize <<= 1;
        _packScratch = new byte[newSize];
    }

    // data(���ڴ� ����� �� ����)�� �ش� �ʵ��� SoA ������ ��ġ�� �κ��� �������� "���� ����"�� ������Ʈ
    private void AccumulateField(int fieldIndex, ref int writtenBytes, int outOffset, ReadOnlySpan<byte> data)
    {
        if (fieldIndex < 0 || data.Length <= 0) return;

        int start = _layout.SoAStart[fieldIndex];
        int end = start + _layout.SoALength[fieldIndex];

        // ������ ���
        int segStart = Math.Max(outOffset, start);
        int segEnd = Math.Min(outOffset + data.Length, end);
        if (segEnd <= segStart) return;

        // ������� ���������� Ȯ���� ������ ���� ��(���� ���� ������)
        int absProgressEnd = start + writtenBytes;

        // �� �����Ͱ� ���� ���� ���ĸ� ���°�?
        if (segStart > absProgressEnd)
            return; // gap ���� �� ���� �ƴ�, ����

        // �������� �߰� ������ ����Ʈ ��
        int contiguous = Math.Min(segEnd, absProgressEnd + (segEnd - segStart)) - absProgressEnd;
        if (contiguous > 0)
        {
            writtenBytes += contiguous;
            if (writtenBytes > _layout.SoALength[fieldIndex])
                writtenBytes = _layout.SoALength[fieldIndex];
        }
    }
}
