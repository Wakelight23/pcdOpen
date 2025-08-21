using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// PCD 파일을 변환 없이 그대로 두고, 런타임 임의 접근/샘플링/부분 읽기를 가능하게 하는 인덱서.
/// - ASCII: 헤더 이후 라인 시작 오프셋 테이블(전량 또는 샘플 간격형) 생성
/// - Binary: stride/필드 오프셋을 계산하고 레코드 오프셋 공식을 제공
/// - BinaryCompressed: 압축 영역의 시작/크기 및 SOA 레이아웃 메타만 제공(랜덤 접근이 제한적)
///
/// 사용 흐름:
///   using var fs = new FileStream(path, ...);
///   long dataOffset;
///   var header = PcdLoader.ReadHeaderOnly(fs, out dataOffset);
///   var index = PcdIndexBuilder.Build(fs, header, dataOffset, new BuildOptions{ ... });
///
/// 주의: FileStream.Position은 Build 수행 중 변경됩니다. 외부에서 이후 읽기를 할 때는 원하는 위치로 재설정할 것.
public static class PcdIndexBuilder
{
    public enum PcdDataMode { ASCII, Binary, BinaryCompressed }

    [Serializable]
    public sealed class PcdIndex
    {
        public PcdDataMode Mode;
        public string[] Fields;
        public int[] Size;
        public char[] Type;
        public int[] Count;
        public int FieldCount;
        public int Width;
        public int Height;
        public int Points;
        public long DataStart;

        // Binary 전용
        public int Stride;             // 포인트 레코드 크기
        public int[] FieldOffsets;     // 각 필드의 레코드 내 바이트 오프셋

        // ASCII 전용
        public bool HasFullLineOffsets; // true면 모든 라인의 시작 오프셋 보유
        public List<long> LineOffsets;  // 라인 시작 파일 오프셋(전량 또는 간격형)
        public int LineStride;          // 간격형 인덱싱일 때, 몇 라인마다 하나 저장했는지(1이면 전량)

        // BinaryCompressed 전용
        public long CompStart;         // 압축 데이터 시작 위치
        public uint CompSize;
        public uint UncompSize;
        public PcdLoader.SoaLayout Soa; // SOA 레이아웃(필드별 블록 시작/크기/총합)

        // 편의
        public int IndexGranularity => LineStride <= 0 ? 1 : LineStride;
        public bool IsOrganized => Width > 1 && Height > 1;
    }

    public sealed class BuildOptions
    {
        // ASCII 인덱싱 옵션:
        //   1: 모든 라인 시작 오프셋 저장(메모리↑, 빠른 무작위 접근)
        //   N>1: N라인마다 1개 오프셋 저장(메모리↓, 범위 접근은 구간 스캔 필요)
        public int asciiLineIndexStride = 1;

        // ASCII 인덱싱 중 최대 라인 수(메모리 안전장치). 0이면 제한 없음.
        public int asciiMaxIndexedLines = 0;

        // 파일 읽기 버퍼 크기(ASCII 스캐닝에 사용)
        public int scanBufferBytes = 1024 * 1024;

        // 로그 출력 제어
        public bool verboseLog = false;
    }

    public static PcdIndex Build(FileStream fs, PcdLoader.Header header, long dataOffset, BuildOptions opt = null)
    {
        if (fs == null) throw new ArgumentNullException(nameof(fs));
        if (header == null) throw new ArgumentNullException(nameof(header));
        opt ??= new BuildOptions();

        var idx = new PcdIndex
        {
            Fields = header.FIELDS,
            Size = header.SIZE,
            Type = header.TYPE,
            Count = header.COUNT,
            FieldCount = header.FieldCount,
            Width = header.WIDTH,
            Height = header.HEIGHT,
            Points = (header.POINTS > 0 ? header.POINTS : header.WIDTH * Math.Max(1, header.HEIGHT)),
            DataStart = dataOffset
        };
        if (idx.Points <= 0) throw new Exception("Invalid PCD: POINTS or WIDTH*HEIGHT must be positive.");

        switch (header.DATA)
        {
            case "ascii":
                idx.Mode = PcdDataMode.ASCII;
                BuildAsciiIndex(fs, idx, opt);
                break;

            case "binary":
                idx.Mode = PcdDataMode.Binary;
                BuildBinaryIndex(fs, idx, opt);
                break;

            case "binary_compressed":
                idx.Mode = PcdDataMode.BinaryCompressed;
                BuildBinaryCompressedIndex(fs, idx, opt);
                break;

            default:
                throw new NotSupportedException("Unsupported PCD DATA mode: " + header.DATA);
        }

        if (opt.verboseLog)
        {
            Debug.Log($"[PcdIndex] Mode={idx.Mode}, Points={idx.Points}, Organized={idx.IsOrganized}, DataStart={idx.DataStart}");
            if (idx.Mode == PcdDataMode.Binary)
                Debug.Log($"[PcdIndex] stride={idx.Stride}");
            if (idx.Mode == PcdDataMode.ASCII)
                Debug.Log($"[PcdIndex] ASCII offsets: count={(idx.LineOffsets?.Count ?? 0)}, stride={idx.LineStride}, full={(idx.HasFullLineOffsets)}");
            if (idx.Mode == PcdDataMode.BinaryCompressed)
                Debug.Log($"[PcdIndex] compressed size={idx.CompSize}, uncompressed size={idx.UncompSize}, soaBytes={(idx.Soa.totalBytes)}");
        }

        return idx;
    }

    // ===== ASCII =====

    static void BuildAsciiIndex(FileStream fs, PcdIndex idx, BuildOptions opt)
    {
        // 헤더 이후부터 라인 시작 오프셋을 수집
        // CR, LF, CRLF 모두 처리. 각 "실제 데이터 라인"의 시작 위치를 기록.
        fs.Position = idx.DataStart;

        var offsets = new List<long>(Mathf.Min(idx.Points, 1_000_000)); // conservative reserve
        long fileLen = fs.Length;
        int stride = Mathf.Max(1, opt.asciiLineIndexStride);
        idx.LineStride = stride;

        // 스캔 버퍼
        byte[] buffer = new byte[Mathf.Clamp(opt.scanBufferBytes, 64 * 1024, 8 * 1024 * 1024)];
        long basePos = fs.Position; // 스캔 시작 절대 위치
        long cursor = basePos;

        // 현재 라인 시작 위치(첫 라인은 dataStart가 곧 라인 시작)
        long currentLineStart = basePos;
        int lineIndex = 0; // 0-based(첫 데이터 라인)
        bool needFirst = true;

        using (var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true))
        {
            while (cursor < fileLen && offsets.Count < idx.Points)
            {
                fs.Position = cursor;
                int read = br.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                int i = 0;
                // 첫 라인 시작은 무조건 기록(간격형에서도 기준점으로 사용)
                if (needFirst)
                {
                    offsets.Add(currentLineStart);
                    needFirst = false;
                    lineIndex = 1;
                }

                while (i < read && offsets.Count < idx.Points)
                {
                    byte b = buffer[i++];
                    if (b == (byte)'\n' || b == (byte)'\r')
                    {
                        // CRLF 처리: \r 직후 \n이면 소비
                        if (b == (byte)'\r')
                        {
                            if (i < read && buffer[i] == (byte)'\n') i++;
                            else if (i == read && (cursor + i) < fileLen)
                            {
                                // 경계에 걸쳤을 경우: 다음 청크의 첫 바이트가 \n일 수 있음
                                long savePos = fs.Position;
                                int next = fs.ReadByte();
                                if (next == (byte)'\n') { /* consumed */ }
                                else if (next >= 0) fs.Position = savePos; // 되돌림
                            }
                        }

                        // 다음 라인 시작 위치 = 현재까지의 절대 위치
                        currentLineStart = cursor + i;

                        // 인덱싱 간격에 맞춰 기록
                        if ((lineIndex % stride) == 0)
                            offsets.Add(currentLineStart);

                        lineIndex++;
                    }
                }

                cursor += read;
            }
        }

        // 만약 포인트 수보다 적게 수집되었으면(파일 끝/마지막 줄 개행 없음 등),
        // 가능한 만큼만 기록되어도 인덱서로서 동작은 가능. 부족분은 스캔 보간이 필요할 수 있음.
        idx.LineOffsets = offsets;
        idx.HasFullLineOffsets = (stride == 1) && (offsets.Count >= idx.Points);
    }

    // ===== Binary =====

    static void BuildBinaryIndex(FileStream fs, PcdIndex idx, BuildOptions opt)
    {
        // stride/필드 오프셋 계산
        int stride = 0;
        int[] fieldOffsets = new int[idx.FieldCount];
        for (int i = 0; i < idx.FieldCount; i++)
        {
            fieldOffsets[i] = stride;
            stride += idx.Size[i] * idx.Count[i];
        }
        idx.Stride = stride;
        idx.FieldOffsets = fieldOffsets;

        // 유효성 검사: 파일 크기 확인(느슨하게)
        long remain = fs.Length - idx.DataStart;
        long needed = (long)stride * idx.Points;
        if (remain < needed)
        {
            // 가능한 포인트 수 재산정(경고)
            int possible = (int)(remain / stride);
            if (possible > 0 && possible < idx.Points)
            {
                Debug.LogWarning($"[PcdIndex] Binary body smaller than expected. POINTS corrected {idx.Points} -> {possible}");
                idx.Points = possible;
            }
            else
            {
                Debug.LogWarning($"[PcdIndex] Binary body size < expected. Random access may throw.");
            }
        }
    }

    // ===== Binary Compressed =====

    static void BuildBinaryCompressedIndex(FileStream fs, PcdIndex idx, BuildOptions opt)
    {
        // 데이터 시작에서 8바이트 헤더 읽기: [compressedSize(uint32)][uncompressedSize(uint32)]
        fs.Position = idx.DataStart;
        Span<byte> hdr = stackalloc byte[8];
        int read = fs.Read(hdr);
        if (read != 8) throw new EndOfStreamException("binary_compressed header too short");

        uint comp = BitConverter.ToUInt32(hdr.Slice(0, 4));
        uint uncomp = BitConverter.ToUInt32(hdr.Slice(4, 4));

        long compStart = fs.Position;
        long compEnd = compStart + comp;
        if (compEnd > fs.Length)
            throw new Exception("Compressed data out of range");

        idx.CompStart = compStart;
        idx.CompSize = comp;
        idx.UncompSize = uncomp;

        // SOA 레이아웃
        var L = PcdLoader.BuildSoaLayout(new PcdLoader.Header
        {
            FIELDS = idx.Fields,
            SIZE = idx.Size,
            COUNT = idx.Count
        }, idx.Points);

        // 검증: 예상 SOA 총크기와 uncompressedSize가 일치해야 정상
        if (L.totalBytes != idx.UncompSize)
        {
            Debug.LogWarning($"[PcdIndex] Uncompressed size mismatch. Layout={L.totalBytes}, header={idx.UncompSize}");
        }

        idx.Soa = L;

        // 참고: 랜덤 접근은 불가. 이후 Subloader에서 스트림 디코딩을 한 번 흘리며 필터링/샘플링하도록 설계.
        // 여기서는 메타데이터만 제공.
    }

    // ===== 편의 메서드 =====

    /// Binary 모드: 포인트 i의 파일 오프셋 반환
    public static long GetBinaryPointOffset(PcdIndex idx, int i)
    {
        if (idx.Mode != PcdDataMode.Binary) throw new InvalidOperationException("Index mode is not Binary.");
        if (i < 0 || i >= idx.Points) throw new ArgumentOutOfRangeException(nameof(i));
        return idx.DataStart + (long)i * idx.Stride;
    }

    /// Binary 모드: 특정 포인트 i의 특정 필드 f의 오프셋
    public static long GetBinaryFieldOffset(PcdIndex idx, int i, int fieldIndex)
    {
        if (idx.Mode != PcdDataMode.Binary) throw new InvalidOperationException("Index mode is not Binary.");
        if (i < 0 || i >= idx.Points) throw new ArgumentOutOfRangeException(nameof(i));
        if (fieldIndex < 0 || fieldIndex >= idx.FieldCount) throw new ArgumentOutOfRangeException(nameof(fieldIndex));
        return idx.DataStart + (long)i * idx.Stride + idx.FieldOffsets[fieldIndex];
    }

    /// ASCII 모드: 라인 번호 i의 대략적인 파일 오프셋(간격형 인덱스면 구간 시작점 반환)
    /// 정확한 시작점이 필요하면 이 위치부터 '\n' 또는 '\r\n' 경계까지 전/후 탐색이 필요할 수 있음.
    public static long GetAsciiApproxLineOffset(PcdIndex idx, int i)
    {
        if (idx.Mode != PcdDataMode.ASCII) throw new InvalidOperationException("Index mode is not ASCII.");
        if (idx.LineOffsets == null || idx.LineOffsets.Count == 0) throw new InvalidOperationException("No ASCII offsets.");
        if (i < 0) i = 0;
        if (i >= idx.Points) i = idx.Points - 1;

        int stride = idx.IndexGranularity;
        if (idx.HasFullLineOffsets)
        {
            // 완전 인덱스
            if (i < idx.LineOffsets.Count) return idx.LineOffsets[i];
            // 마지막 줄 개행이 없는 파일 등으로 Count가 살짝 모자랄 수 있음
            return idx.LineOffsets[idx.LineOffsets.Count - 1];
        }
        else
        {
            // 간격형 인덱스: i가 속한 버킷의 대표 오프셋 반환
            int bucket = i / stride;
            if (bucket < idx.LineOffsets.Count) return idx.LineOffsets[bucket];
            return idx.LineOffsets[idx.LineOffsets.Count - 1];
        }
    }

    /// ASCII 모드: [start,end) 라인 범위를 읽기 위한 스캔 시작 오프셋(간격형 인덱스 고려)
    public static long GetAsciiScanStartForRange(PcdIndex idx, int startLine)
    {
        if (idx.Mode != PcdDataMode.ASCII) throw new InvalidOperationException("Index mode is not ASCII.");
        int stride = idx.IndexGranularity;

        if (idx.HasFullLineOffsets)
        {
            if (startLine < 0) startLine = 0;
            if (startLine >= idx.LineOffsets.Count) startLine = idx.LineOffsets.Count - 1;
            return idx.LineOffsets[startLine];
        }
        else
        {
            if (startLine < 0) startLine = 0;
            int bucket = startLine / stride;
            if (bucket < idx.LineOffsets.Count) return idx.LineOffsets[bucket];
            return idx.LineOffsets[idx.LineOffsets.Count - 1];
        }
    }
}
