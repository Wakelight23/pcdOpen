using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// PCD ������ ��ȯ ���� �״�� �ΰ�, ��Ÿ�� ���� ����/���ø�/�κ� �б⸦ �����ϰ� �ϴ� �ε���.
/// - ASCII: ��� ���� ���� ���� ������ ���̺�(���� �Ǵ� ���� ������) ����
/// - Binary: stride/�ʵ� �������� ����ϰ� ���ڵ� ������ ������ ����
/// - BinaryCompressed: ���� ������ ����/ũ�� �� SOA ���̾ƿ� ��Ÿ�� ����(���� ������ ������)
///
/// ��� �帧:
///   using var fs = new FileStream(path, ...);
///   long dataOffset;
///   var header = PcdLoader.ReadHeaderOnly(fs, out dataOffset);
///   var index = PcdIndexBuilder.Build(fs, header, dataOffset, new BuildOptions{ ... });
///
/// ����: FileStream.Position�� Build ���� �� ����˴ϴ�. �ܺο��� ���� �б⸦ �� ���� ���ϴ� ��ġ�� �缳���� ��.
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

        // Binary ����
        public int Stride;             // ����Ʈ ���ڵ� ũ��
        public int[] FieldOffsets;     // �� �ʵ��� ���ڵ� �� ����Ʈ ������

        // ASCII ����
        public bool HasFullLineOffsets; // true�� ��� ������ ���� ������ ����
        public List<long> LineOffsets;  // ���� ���� ���� ������(���� �Ǵ� ������)
        public int LineStride;          // ������ �ε����� ��, �� ���θ��� �ϳ� �����ߴ���(1�̸� ����)

        // BinaryCompressed ����
        public long CompStart;         // ���� ������ ���� ��ġ
        public uint CompSize;
        public uint UncompSize;
        public PcdLoader.SoaLayout Soa; // SOA ���̾ƿ�(�ʵ庰 ��� ����/ũ��/����)

        // ����
        public int IndexGranularity => LineStride <= 0 ? 1 : LineStride;
        public bool IsOrganized => Width > 1 && Height > 1;
    }

    public sealed class BuildOptions
    {
        // ASCII �ε��� �ɼ�:
        //   1: ��� ���� ���� ������ ����(�޸𸮡�, ���� ������ ����)
        //   N>1: N���θ��� 1�� ������ ����(�޸𸮡�, ���� ������ ���� ��ĵ �ʿ�)
        public int asciiLineIndexStride = 1;

        // ASCII �ε��� �� �ִ� ���� ��(�޸� ������ġ). 0�̸� ���� ����.
        public int asciiMaxIndexedLines = 0;

        // ���� �б� ���� ũ��(ASCII ��ĳ�׿� ���)
        public int scanBufferBytes = 1024 * 1024;

        // �α� ��� ����
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
        // ��� ���ĺ��� ���� ���� �������� ����
        // CR, LF, CRLF ��� ó��. �� "���� ������ ����"�� ���� ��ġ�� ���.
        fs.Position = idx.DataStart;

        var offsets = new List<long>(Mathf.Min(idx.Points, 1_000_000)); // conservative reserve
        long fileLen = fs.Length;
        int stride = Mathf.Max(1, opt.asciiLineIndexStride);
        idx.LineStride = stride;

        // ��ĵ ����
        byte[] buffer = new byte[Mathf.Clamp(opt.scanBufferBytes, 64 * 1024, 8 * 1024 * 1024)];
        long basePos = fs.Position; // ��ĵ ���� ���� ��ġ
        long cursor = basePos;

        // ���� ���� ���� ��ġ(ù ������ dataStart�� �� ���� ����)
        long currentLineStart = basePos;
        int lineIndex = 0; // 0-based(ù ������ ����)
        bool needFirst = true;

        using (var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true))
        {
            while (cursor < fileLen && offsets.Count < idx.Points)
            {
                fs.Position = cursor;
                int read = br.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                int i = 0;
                // ù ���� ������ ������ ���(������������ ���������� ���)
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
                        // CRLF ó��: \r ���� \n�̸� �Һ�
                        if (b == (byte)'\r')
                        {
                            if (i < read && buffer[i] == (byte)'\n') i++;
                            else if (i == read && (cursor + i) < fileLen)
                            {
                                // ��迡 ������ ���: ���� ûũ�� ù ����Ʈ�� \n�� �� ����
                                long savePos = fs.Position;
                                int next = fs.ReadByte();
                                if (next == (byte)'\n') { /* consumed */ }
                                else if (next >= 0) fs.Position = savePos; // �ǵ���
                            }
                        }

                        // ���� ���� ���� ��ġ = ��������� ���� ��ġ
                        currentLineStart = cursor + i;

                        // �ε��� ���ݿ� ���� ���
                        if ((lineIndex % stride) == 0)
                            offsets.Add(currentLineStart);

                        lineIndex++;
                    }
                }

                cursor += read;
            }
        }

        // ���� ����Ʈ ������ ���� �����Ǿ�����(���� ��/������ �� ���� ���� ��),
        // ������ ��ŭ�� ��ϵǾ �ε����μ� ������ ����. �������� ��ĵ ������ �ʿ��� �� ����.
        idx.LineOffsets = offsets;
        idx.HasFullLineOffsets = (stride == 1) && (offsets.Count >= idx.Points);
    }

    // ===== Binary =====

    static void BuildBinaryIndex(FileStream fs, PcdIndex idx, BuildOptions opt)
    {
        // stride/�ʵ� ������ ���
        int stride = 0;
        int[] fieldOffsets = new int[idx.FieldCount];
        for (int i = 0; i < idx.FieldCount; i++)
        {
            fieldOffsets[i] = stride;
            stride += idx.Size[i] * idx.Count[i];
        }
        idx.Stride = stride;
        idx.FieldOffsets = fieldOffsets;

        // ��ȿ�� �˻�: ���� ũ�� Ȯ��(�����ϰ�)
        long remain = fs.Length - idx.DataStart;
        long needed = (long)stride * idx.Points;
        if (remain < needed)
        {
            // ������ ����Ʈ �� �����(���)
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
        // ������ ���ۿ��� 8����Ʈ ��� �б�: [compressedSize(uint32)][uncompressedSize(uint32)]
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

        // SOA ���̾ƿ�
        var L = PcdLoader.BuildSoaLayout(new PcdLoader.Header
        {
            FIELDS = idx.Fields,
            SIZE = idx.Size,
            COUNT = idx.Count
        }, idx.Points);

        // ����: ���� SOA ��ũ��� uncompressedSize�� ��ġ�ؾ� ����
        if (L.totalBytes != idx.UncompSize)
        {
            Debug.LogWarning($"[PcdIndex] Uncompressed size mismatch. Layout={L.totalBytes}, header={idx.UncompSize}");
        }

        idx.Soa = L;

        // ����: ���� ������ �Ұ�. ���� Subloader���� ��Ʈ�� ���ڵ��� �� �� �긮�� ���͸�/���ø��ϵ��� ����.
        // ���⼭�� ��Ÿ�����͸� ����.
    }

    // ===== ���� �޼��� =====

    /// Binary ���: ����Ʈ i�� ���� ������ ��ȯ
    public static long GetBinaryPointOffset(PcdIndex idx, int i)
    {
        if (idx.Mode != PcdDataMode.Binary) throw new InvalidOperationException("Index mode is not Binary.");
        if (i < 0 || i >= idx.Points) throw new ArgumentOutOfRangeException(nameof(i));
        return idx.DataStart + (long)i * idx.Stride;
    }

    /// Binary ���: Ư�� ����Ʈ i�� Ư�� �ʵ� f�� ������
    public static long GetBinaryFieldOffset(PcdIndex idx, int i, int fieldIndex)
    {
        if (idx.Mode != PcdDataMode.Binary) throw new InvalidOperationException("Index mode is not Binary.");
        if (i < 0 || i >= idx.Points) throw new ArgumentOutOfRangeException(nameof(i));
        if (fieldIndex < 0 || fieldIndex >= idx.FieldCount) throw new ArgumentOutOfRangeException(nameof(fieldIndex));
        return idx.DataStart + (long)i * idx.Stride + idx.FieldOffsets[fieldIndex];
    }

    /// ASCII ���: ���� ��ȣ i�� �뷫���� ���� ������(������ �ε����� ���� ������ ��ȯ)
    /// ��Ȯ�� �������� �ʿ��ϸ� �� ��ġ���� '\n' �Ǵ� '\r\n' ������ ��/�� Ž���� �ʿ��� �� ����.
    public static long GetAsciiApproxLineOffset(PcdIndex idx, int i)
    {
        if (idx.Mode != PcdDataMode.ASCII) throw new InvalidOperationException("Index mode is not ASCII.");
        if (idx.LineOffsets == null || idx.LineOffsets.Count == 0) throw new InvalidOperationException("No ASCII offsets.");
        if (i < 0) i = 0;
        if (i >= idx.Points) i = idx.Points - 1;

        int stride = idx.IndexGranularity;
        if (idx.HasFullLineOffsets)
        {
            // ���� �ε���
            if (i < idx.LineOffsets.Count) return idx.LineOffsets[i];
            // ������ �� ������ ���� ���� ������ Count�� ��¦ ���ڶ� �� ����
            return idx.LineOffsets[idx.LineOffsets.Count - 1];
        }
        else
        {
            // ������ �ε���: i�� ���� ��Ŷ�� ��ǥ ������ ��ȯ
            int bucket = i / stride;
            if (bucket < idx.LineOffsets.Count) return idx.LineOffsets[bucket];
            return idx.LineOffsets[idx.LineOffsets.Count - 1];
        }
    }

    /// ASCII ���: [start,end) ���� ������ �б� ���� ��ĵ ���� ������(������ �ε��� ���)
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
