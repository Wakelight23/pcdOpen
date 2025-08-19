using System;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

public sealed class SoAAssembler
{
    private readonly SoALayout _layout;
    private readonly int _floatBytes = sizeof(float);

    // 진행 포인터(바이트 기준, 각 SoA 필드에서 연속적으로 확보된 바이트 수)
    private int _xWrittenBytes, _yWrittenBytes, _zWrittenBytes;

    // 이미 GPU로 패킹/업로드한 float3 개수
    private int _packedFloat3Count;

    // GPU 대상
    private readonly GraphicsBuffer _dst;
    private readonly int _dstStartOffsetBytes; // 이 타일의 GPU 버퍼 시작 오프셋(바이트)
    private int _dstWriteCursorBytes; // GPU 버퍼 내 현재 쓰기 오프셋(바이트)
    private readonly int _dstTotalBytes;

    // 디코더 출력 접근자 (전체 출력 버퍼를 직접 보관하지 않고도 부분 조회)
    private readonly IOutputProvider _outputProvider;

    // 한 번에 GPU로 복사할 때 사용할 임시 CPU 워크 버퍼(필요 최소 크기만큼 재사용)
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

    // decoder에서 호출: 출력 스트림의 [outOffset, outOffset+data.Count) 구간이 새로 생성됨
    public void OnOutput(int outOffset, ArraySegment<byte> data)
    {
        var span = new ReadOnlySpan<byte>(data.Array, data.Offset, data.Count);

        // data 조각이 x/y/z SoA 블록과 겹치는 부분을 각각 반영
        AccumulateField(_layout.XField, ref _xWrittenBytes, outOffset, span);
        AccumulateField(_layout.YField, ref _yWrittenBytes, outOffset, span);
        AccumulateField(_layout.ZField, ref _zWrittenBytes, outOffset, span);

        TryPackAndUploadSyncedTriplets();
    }

    // 디코딩이 끝났을 때 마지막 남은 동기화 가능한 영역까지 마무리 업로드
    public void Finish()
    {
        TryPackAndUploadSyncedTriplets();
        // 남은 데이터가 있어도 x/y/z 중 하나라도 도달하지 못한 영역은 동기화되지 않은 상태이므로 여기서 마무리.
    }

    private void TryPackAndUploadSyncedTriplets()
    {
        // x/y/z 중 최소 진행도를 기준으로 동기화된 float3 개수를 계산
        int xFloats = _xWrittenBytes / _floatBytes;
        int yFloats = _yWrittenBytes / _floatBytes;
        int zFloats = _zWrittenBytes / _floatBytes;
        int syncedCount = Math.Min(xFloats, Math.Min(yFloats, zFloats));

        int toPack = syncedCount - _packedFloat3Count;
        if (toPack <= 0) return;

        // 한 번에 너무 많이 복사하지 않도록 배치 처리 (예: 1M 포인트 단위)
        const int kBatch = 1_000_000;
        int remaining = toPack;

        while (remaining > 0)
        {
            int batchCount = Math.Min(remaining, kBatch);

            // 각 필드에서 [packedFloat3Count .. packedFloat3Count+batchCount) 구간의 float들을 읽어와서 float3로 패킹
            int xStart = _layout.SoAStart[_layout.XField] + _packedFloat3Count * _floatBytes;
            int yStart = _layout.SoAStart[_layout.YField] + _packedFloat3Count * _floatBytes;
            int zStart = _layout.SoAStart[_layout.ZField] + _packedFloat3Count * _floatBytes;

            int fieldBytes = batchCount * _floatBytes;

            ReadOnlySpan<byte> xSpan = _outputProvider.ReadRange(xStart, fieldBytes);
            ReadOnlySpan<byte> ySpan = _outputProvider.ReadRange(yStart, fieldBytes);
            ReadOnlySpan<byte> zSpan = _outputProvider.ReadRange(zStart, fieldBytes);

            // GPU 목적지에 쓸 바이트 수
            int packBytes = batchCount * 12; // float3 (x,y,z) = 12B

            EnsureScratch(packBytes);

            // CPU에서 연속 float3 배열로 패킹 (endianness: 리틀 엔디안 가정)
            // BitConverter.ToSingle는 Span 오버헤드가 커서, unsafe로 고속 복사 권장
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
                        // 4B씩 그대로 복사 (float raw bytes)
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

            // GPU 버퍼 범위 체크
            int bytesWrittenFromTileStart = (_dstWriteCursorBytes - _dstStartOffsetBytes);
            if (bytesWrittenFromTileStart + packBytes > _dstTotalBytes)
                throw new InvalidOperationException("Destination GPU buffer tile range exceeded.");

            // GPU로 업로드
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
        if (newSize == 0) newSize = 1 << 20; // 1MB 시작
        while (newSize < size) newSize <<= 1;
        _packScratch = new byte[newSize];
    }

    // data(디코더 출력의 새 조각)가 해당 필드의 SoA 구간과 겹치는 부분을 기준으로 "연속 진행"을 업데이트
    private void AccumulateField(int fieldIndex, ref int writtenBytes, int outOffset, ReadOnlySpan<byte> data)
    {
        if (fieldIndex < 0 || data.Length <= 0) return;

        int start = _layout.SoAStart[fieldIndex];
        int end = start + _layout.SoALength[fieldIndex];

        // 교집합 계산
        int segStart = Math.Max(outOffset, start);
        int segEnd = Math.Min(outOffset + data.Length, end);
        if (segEnd <= segStart) return;

        // 현재까지 연속적으로 확보된 영역의 절대 끝(파일 기준 오프셋)
        int absProgressEnd = start + writtenBytes;

        // 새 데이터가 연속 진행 이후를 덮는가?
        if (segStart > absProgressEnd)
            return; // gap 존재 → 연속 아님, 보류

        // 연속으로 추가 가능한 바이트 수
        int contiguous = Math.Min(segEnd, absProgressEnd + (segEnd - segStart)) - absProgressEnd;
        if (contiguous > 0)
        {
            writtenBytes += contiguous;
            if (writtenBytes > _layout.SoALength[fieldIndex])
                writtenBytes = _layout.SoALength[fieldIndex];
        }
    }
}
