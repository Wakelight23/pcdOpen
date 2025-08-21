using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public static class PcdLoader
{
    // 기존 옵션 유지
    public static bool UseStreamingForCompressed = true;

    // 변경점 1) Header/SoaLayout을 public으로 노출
    public class Header
    {
        public string VERSION;
        public string[] FIELDS;
        public int[] SIZE;
        public char[] TYPE;    // 'F','I','U'
        public int[] COUNT;
        public int WIDTH = 0;
        public int HEIGHT = 1;
        public int POINTS = 0;
        public string DATA;    // ascii, binary, binary_compressed
        public int FieldCount => FIELDS?.Length ?? 0;
    }

    public struct SoaLayout
    {
        public int fields;
        public int points;
        public int[] fieldByteSize; // size*count (per field)
        public int[] blockStart;    // SOA 내에서 각 필드 블록의 시작 바이트
        public int totalBytes;      // 전체 SOA 바이트 수
    }

    // 변경점 2) 외부에서 헤더/오프셋만 얻는 API 추가
    public static Header ReadHeaderOnly(string path, out long dataOffset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sr = new StreamReader(fs, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 128 * 1024, leaveOpen: true);
        return ParseHeaderStreaming(sr, out dataOffset);
    }

    // 변경점 3) FileStream이 이미 열려있는 경우 헤더만 파싱하는 API
    public static Header ReadHeaderOnly(FileStream fs, out long dataOffset)
    {
        using var sr = new StreamReader(fs, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 128 * 1024, leaveOpen: true);
        return ParseHeaderStreaming(sr, out dataOffset);
    }

    // 변경점 4) SOA 레이아웃 공개 유틸
    public static SoaLayout BuildSoaLayout(Header h, int points)
    {
        int fields = h.FieldCount;
        var L = new SoaLayout
        {
            fields = fields,
            points = points,
            fieldByteSize = new int[fields],
            blockStart = new int[fields],
        };
        int off = 0;
        for (int f = 0; f < fields; f++)
        {
            L.fieldByteSize[f] = h.SIZE[f] * h.COUNT[f];
            L.blockStart[f] = off;
            off += L.fieldByteSize[f] * points;
        }
        L.totalBytes = off;
        return L;
    }

    // 기존: 전체 파일을 읽어 PcdData 생성
    public static PcdData LoadFromFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sr = new StreamReader(fs, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 128 * 1024, leaveOpen: true);

        long dataOffset;
        var header = ParseHeaderStreaming(sr, out dataOffset);

        int ix = Array.IndexOf(header.FIELDS, "x");
        int iy = Array.IndexOf(header.FIELDS, "y");
        int iz = Array.IndexOf(header.FIELDS, "z");
        if (ix < 0 || iy < 0 || iz < 0)
            throw new Exception("x/y/z fields are required");

        int iRgb = Array.IndexOf(header.FIELDS, "rgb");
        int iRgba = Array.IndexOf(header.FIELDS, "rgba");
        int iIntensity = Array.IndexOf(header.FIELDS, "intensity");

        int points = header.POINTS > 0 ? header.POINTS : header.WIDTH * Math.Max(1, header.HEIGHT);
        if (points <= 0) throw new Exception("Invalid POINTS/WIDTH*HEIGHT");

        var data = new PcdData
        {
            pointCount = points,
            positions = new Vector3[points]
        };
        if (iRgb >= 0 || iRgba >= 0) data.colors = new Color32[points];
        if (iIntensity >= 0) data.intensity = new float[points];

        // 본문으로 이동
        fs.Position = dataOffset;

        switch (header.DATA)
        {
            case "ascii":
                ParseAsciiStream(sr, header, ix, iy, iz, iRgb, iRgba, iIntensity, data);
                break;

            case "binary":
                {
                    int fieldCount = header.FieldCount;
                    int stride = 0;
                    for (int i = 0; i < fieldCount; i++)
                        stride += header.SIZE[i] * header.COUNT[i];

                    long needed = (long)stride * points;
                    long remain = fs.Length - fs.Position;

                    if (remain < needed)
                    {
                        Debug.LogWarning($"[PCD] Binary body shorter than expected. remain={remain}, needed={needed}, stride={stride}, points={points}. " +
                                         "Header may be inaccurate (COUNT/SIZE/POINTS) or extra whitespace was miscounted.");
                        // 자동 보정 시도
                        int possiblePoints = (int)(remain / stride);
                        if (possiblePoints > 0 && possiblePoints < points)
                        {
                            data.pointCount = possiblePoints;
                            data.positions = new Vector3[possiblePoints];
                            if (data.colors != null) data.colors = new Color32[possiblePoints];
                            if (data.intensity != null) data.intensity = new float[possiblePoints];
                            points = possiblePoints;
                        }
                        else
                        {
                            throw new EndOfStreamException("Binary body size < expected.");
                        }
                    }

                    using (var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true))
                        ParseBinaryStream(br, header, ix, iy, iz, iRgb, iRgba, iIntensity, data, true);
                    break;
                }

            case "binary_compressed":
                if (UseStreamingForCompressed)
                    ParseBinaryCompressed_Streamed(fs, header, ix, iy, iz, iRgb, iRgba, iIntensity, data, 1 << 20);
                else
                    ParseBinaryCompressed_NoAOS_Legacy(fs, header, ix, iy, iz, iRgb, iRgba, iIntensity, data);
                break;

            default:
                throw new Exception("Unsupported DATA: " + header.DATA);
        }

        ComputeBounds(data);
        return data;
    }

    // 변경점 5) 부분 읽기 지원을 위한 헬퍼: 바이너리 레코드 크기/오프셋 계산 공개
    public static int ComputeBinaryStride(Header h)
    {
        int stride = 0;
        for (int i = 0; i < h.FieldCount; i++) stride += h.SIZE[i] * h.COUNT[i];
        return stride;
    }

    public static int[] ComputeBinaryFieldOffsets(Header h)
    {
        int[] offsets = new int[h.FieldCount];
        int stride = 0;
        for (int i = 0; i < h.FieldCount; i++)
        {
            offsets[i] = stride;
            stride += h.SIZE[i] * h.COUNT[i];
        }
        return offsets;
    }

    // ===== 내부(공용화된) 파서 =====

    static Header ParseHeaderStreaming(StreamReader sr, out long dataOffset)
    {
        var h = new Header();
        var fs = sr.BaseStream;
        var enc = Encoding.ASCII;

        fs.Position = 0;
        using var br = new BinaryReader(fs, enc, leaveOpen: true);

        using var ms = new MemoryStream();
        void ParseOneLine(byte[] buf, int len)
        {
            var line = enc.GetString(buf, 0, len).Trim();
            if (line.Length == 0) return;
            if (line.StartsWith("#")) return;

            if (line.StartsWith("VERSION"))
                h.VERSION = line.Substring("VERSION".Length).Trim();
            else if (line.StartsWith("FIELDS"))
                h.FIELDS = SplitTokens(line.Substring("FIELDS".Length));
            else if (line.StartsWith("SIZE"))
                h.SIZE = Array.ConvertAll(SplitTokens(line.Substring("SIZE".Length)), int.Parse);
            else if (line.StartsWith("TYPE"))
            {
                var tokens = SplitTokens(line.Substring("TYPE".Length));
                h.TYPE = Array.ConvertAll(tokens, t => t[0]);
            }
            else if (line.StartsWith("COUNT"))
                h.COUNT = Array.ConvertAll(SplitTokens(line.Substring("COUNT".Length)), int.Parse);
            else if (line.StartsWith("WIDTH"))
                h.WIDTH = int.Parse(line.Substring("WIDTH".Length));
            else if (line.StartsWith("HEIGHT"))
                h.HEIGHT = int.Parse(line.Substring("HEIGHT".Length));
            else if (line.StartsWith("POINTS"))
                h.POINTS = int.Parse(line.Substring("POINTS".Length));
            else if (line.StartsWith("DATA"))
                h.DATA = line.Substring("DATA".Length).Trim().ToLowerInvariant();
        }

        int b;
        bool seenData = false;
        long afterDataLinePos = 0;

        while ((b = br.Read()) != -1)
        {
            if (b == '\n' || b == '\r')
            {
                var buf = ms.ToArray();
                ParseOneLine(buf, buf.Length);
                ms.SetLength(0);

                if (b == '\r')
                {
                    long peek = fs.Position;
                    int n2 = br.PeekChar();
                    if (n2 == '\n') { br.Read(); }
                }

                if (!string.IsNullOrEmpty(h.DATA) && !seenData)
                {
                    afterDataLinePos = fs.Position;
                    seenData = true;
                    break;
                }
            }
            else
            {
                ms.WriteByte((byte)b);
            }
        }

        if (!seenData || string.IsNullOrEmpty(h.DATA))
            throw new Exception("PCD header not found or DATA line missing");

        if (h.COUNT == null && h.FIELDS != null)
        {
            h.COUNT = new int[h.FIELDS.Length];
            for (int i = 0; i < h.COUNT.Length; i++) h.COUNT[i] = 1;
        }

        dataOffset = afterDataLinePos;
        return h;
    }

    static string[] SplitTokens(string s)
        => s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    static void ParseAsciiStream(StreamReader sr, Header h,
                                 int ix, int iy, int iz, int iRgb, int iRgba, int iIntensity,
                                 PcdData outData)
    {
        var fmt = CultureInfo.InvariantCulture;
        int n = outData.pointCount;
        int idx = 0;

        string line;
        while (idx < n && (line = sr.ReadLine()) != null)
        {
            var tok = SplitTokens(line);
            if (tok.Length < h.FieldCount) continue;

            float fx = float.Parse(tok[ix], fmt);
            float fy = float.Parse(tok[iy], fmt);
            float fz = float.Parse(tok[iz], fmt);
            outData.positions[idx] = new Vector3(fx, fy, fz);

            if (outData.colors != null)
            {
                if (iRgb >= 0) outData.colors[idx] = DecodeRgbASCII(tok[iRgb], fmt);
                else if (iRgba >= 0) outData.colors[idx] = DecodeRgbaASCII(tok[iRgba], fmt);
            }
            if (outData.intensity != null && iIntensity >= 0)
                outData.intensity[idx] = float.Parse(tok[iIntensity], fmt);

            idx++;
        }
    }

    static Color32 DecodeRgbASCII(string token, IFormatProvider fmt)
    {
        if (float.TryParse(token, NumberStyles.Float, fmt, out float f))
        {
            uint u = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
            return new Color32(
                (byte)((u >> 16) & 0xFF),
                (byte)((u >> 8) & 0xFF),
                (byte)(u & 0xFF),
                255);
        }
        else
        {
            uint u = Convert.ToUInt32(token);
            return new Color32(
                (byte)((u >> 16) & 0xFF),
                (byte)((u >> 8) & 0xFF),
                (byte)(u & 0xFF),
                255);
        }
    }

    static Color32 DecodeRgbaASCII(string token, IFormatProvider fmt)
    {
        if (float.TryParse(token, NumberStyles.Float, fmt, out float f))
        {
            uint u = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
            return new Color32(
                (byte)((u >> 16) & 0xFF),
                (byte)((u >> 8) & 0xFF),
                (byte)(u & 0xFF),
                (byte)((u >> 24) & 0xFF));
        }
        else
        {
            uint u = Convert.ToUInt32(token);
            return new Color32(
                (byte)((u >> 16) & 0xFF),
                (byte)((u >> 8) & 0xFF),
                (byte)(u & 0xFF),
                (byte)((u >> 24) & 0xFF));
        }
    }

    static void ParseBinaryStream(BinaryReader br, Header h,
                                  int ix, int iy, int iz, int iRgb, int iRgba, int iIntensity,
                                  PcdData outData, bool littleEndian = true)
    {
        int fieldCount = h.FieldCount;

        int[] offsets = new int[fieldCount];
        int[] fieldBytes = new int[fieldCount];
        int stride = 0;
        for (int i = 0; i < fieldCount; i++)
        {
            offsets[i] = stride;
            int bytes = h.SIZE[i] * h.COUNT[i];
            fieldBytes[i] = bytes;
            stride += bytes;
        }

        int n = outData.pointCount;
        var tmp = new byte[stride];

        for (int p = 0; p < n; p++)
        {
            int read = br.Read(tmp, 0, stride);
            if (read == 0)
                throw new EndOfStreamException("Reached EOF before reading any bytes for point record.");
            if (read < stride)
            {
                throw new EndOfStreamException($"Unexpected EOF in binary body. read={read}, stride={stride}, at point={p}/{n}.");
            }

            float fx = ReadFloat(tmp, offsets[ix], littleEndian);
            float fy = ReadFloat(tmp, offsets[iy], littleEndian);
            float fz = ReadFloat(tmp, offsets[iz], littleEndian);
            outData.positions[p] = new Vector3(fx, fy, fz);

            if (outData.colors != null)
            {
                if (iRgb >= 0)
                {
                    uint u = ReadUInt32(tmp, offsets[iRgb], littleEndian);
                    outData.colors[p] = new Color32(
                        (byte)((u >> 16) & 0xFF),
                        (byte)((u >> 8) & 0xFF),
                        (byte)(u & 0xFF),
                        255);
                }
                else if (iRgba >= 0)
                {
                    uint u = ReadUInt32(tmp, offsets[iRgba], littleEndian);
                    outData.colors[p] = new Color32(
                        (byte)((u >> 16) & 0xFF),
                        (byte)((u >> 8) & 0xFF),
                        (byte)(u & 0xFF),
                        (byte)((u >> 24) & 0xFF));
                }
            }

            if (outData.intensity != null && iIntensity >= 0)
                outData.intensity[p] = ReadFloat(tmp, offsets[iIntensity], littleEndian);
        }
    }

    static float ReadFloat(byte[] buf, int off, bool le)
    {
        if (le) return BitConverter.ToSingle(buf, off);
        var tmp = new byte[4]; Buffer.BlockCopy(buf, off, tmp, 0, 4); Array.Reverse(tmp);
        return BitConverter.ToSingle(tmp, 0);
    }

    static uint ReadUInt32(byte[] buf, int off, bool le)
    {
        if (le) return BitConverter.ToUInt32(buf, off);
        var tmp = new byte[4]; Buffer.BlockCopy(buf, off, tmp, 0, 4); Array.Reverse(tmp);
        return BitConverter.ToUInt32(tmp, 0);
    }

    // ===== LZF 스트리밍 디코더 =====
    class LzfStreamDecoder
    {
        readonly Stream src;
        readonly long srcEnd;
        long ip;

        public LzfStreamDecoder(Stream src, long offset, long length)
        {
            this.src = src;
            this.ip = offset;
            this.srcEnd = offset + length;
        }

        int ReadByte()
        {
            int b = src.ReadByte();
            if (b < 0) return -1;
            ip++;
            return b;
        }

        // dstCap 바이트까지 출력 청크를 채움, 반환=이번에 쓴 바이트 수
        public int DecodeNextChunk(byte[] dst, int dstCap)
        {
            int op = 0;

            while (ip < srcEnd && op < dstCap)
            {
                int ctrl = ReadByte();
                if (ctrl < 0) break;

                if (ctrl < (1 << 5))
                {
                    int len = ctrl + 1;

                    int remainIn = (int)Math.Min(len, srcEnd - ip);
                    if (remainIn <= 0) break;

                    int remainOut = dstCap - op;
                    int take = Math.Min(remainIn, remainOut);
                    if (take <= 0) break;

                    int actually = src.Read(dst, op, take);
                    ip += actually;
                    op += actually;

                    if (actually < take) break;
                }
                else
                {
                    int len = (ctrl >> 5);

                    int b1 = ReadByte();
                    if (b1 < 0) break;

                    int refOffset = op - (((ctrl & 0x1F) << 8) + 1);
                    refOffset -= (b1 & 0xFF);

                    if (len == 7)
                    {
                        int ext = ReadByte();
                        if (ext < 0) break;
                        len += (ext & 0xFF);
                    }
                    len += 2;

                    if (refOffset < 0) return op;

                    int end = op + len;
                    if (end > dstCap) end = dstCap;

                    while (op < end)
                    {
                        dst[op++] = dst[refOffset++];
                    }
                }
            }

            return op;
        }

        public bool Finished => ip >= srcEnd;
    }

    // 변경: SoaLayout은 public으로 승격했으므로 여기서는 내부 유틸을 사용하지 않고 그대로 활용

    static unsafe void ConsumeSoaSliceIntoArrays(
        byte[] chunk, int chunkOff, int countBytes,
        SoaLayout L, int fieldIndex,
        ref int fieldSoaOffset,
        int ix, int iy, int iz, int iRgb, int iRgba, int iIntensity,
        PcdData outData)
    {
        int elemSize = L.fieldByteSize[fieldIndex];
        if (elemSize <= 0 || countBytes <= 0)
        {
            fieldSoaOffset += countBytes;
            return;
        }

        bool isX = fieldIndex == ix;
        bool isY = fieldIndex == iy;
        bool isZ = fieldIndex == iz;
        bool isRgb = (outData.colors != null && fieldIndex == iRgb);
        bool isRgba = (outData.colors != null && fieldIndex == iRgba);
        bool isI = (outData.intensity != null && fieldIndex == iIntensity);

        if (elemSize == 4 && (isX || isY || isZ || isRgb || isRgba || isI))
        {
            int startByteInField = fieldSoaOffset;
            int endByteInField = startByteInField + countBytes;

            int firstElem = startByteInField / 4;
            int firstElemByte = firstElem * 4;
            int leadSkip = startByteInField - firstElemByte;

            int fieldByteCursor = startByteInField;
            int localCursor = chunkOff;

            if (leadSkip > 0)
            {
                int skip = Math.Min(leadSkip, countBytes);
                fieldByteCursor += skip;
                localCursor += skip;
            }

            int alignedBytes = endByteInField - fieldByteCursor;
            int alignedElems = alignedBytes / 4;
            int startElem = firstElem + ((fieldByteCursor - firstElemByte) / 4);

            fixed (byte* raw = chunk)
            {
                byte* src = raw + localCursor;
                for (int e = 0; e < alignedElems; e++)
                {
                    int pIndex = startElem + e;
                    if (pIndex < 0 || pIndex >= L.points) break;

                    uint u = *(uint*)(src + e * 4);

                    if (isX || isY || isZ)
                    {
                        float f = BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
                        if (isX) { var v = outData.positions[pIndex]; v.x = f; outData.positions[pIndex] = v; }
                        else if (isY) { var v = outData.positions[pIndex]; v.y = f; outData.positions[pIndex] = v; }
                        else { var v = outData.positions[pIndex]; v.z = f; outData.positions[pIndex] = v; }
                    }
                    else if (isRgb)
                    {
                        byte r = (byte)((u >> 16) & 0xFF);
                        byte g = (byte)((u >> 8) & 0xFF);
                        byte b = (byte)(u & 0xFF);
                        outData.colors[pIndex] = new Color32(r, g, b, 255);
                    }
                    else if (isRgba)
                    {
                        byte r = (byte)((u >> 16) & 0xFF);
                        byte g = (byte)((u >> 8) & 0xFF);
                        byte b = (byte)(u & 0xFF);
                        byte a = (byte)((u >> 24) & 0xFF);
                        outData.colors[pIndex] = new Color32(r, g, b, a);
                    }
                    else if (isI)
                    {
                        float f = BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
                        outData.intensity[pIndex] = f;
                    }
                }
            }
        }

        fieldSoaOffset += countBytes;
    }

    static void ParseBinaryCompressed_Streamed(FileStream fs, Header h,
                                               int ix, int iy, int iz, int iRgb, int iRgba, int iIntensity,
                                               PcdData outData, int chunkBytes)
    {
        // 앞 8바이트: compressedSize, uncompressedSize
        Span<byte> hdr = stackalloc byte[8];
        int read = fs.Read(hdr);
        if (read != 8) throw new EndOfStreamException("binary_compressed header too short");

        uint compressedSize = BitConverter.ToUInt32(hdr.Slice(0, 4));
        uint uncompressedSize = BitConverter.ToUInt32(hdr.Slice(4, 4));

        long compStart = fs.Position;
        long compEnd = compStart + compressedSize;
        if (compEnd > fs.Length) throw new Exception("Compressed data out of range");

        int points = outData.pointCount;
        var L = BuildSoaLayout(h, points);
        if (L.totalBytes != uncompressedSize)
            throw new Exception("Uncompressed size mismatch with header/points");

        var dec = new LzfStreamDecoder(fs, compStart, compressedSize);

        int soaPos = 0;
        int fIndex = 0;
        int fEnd = L.blockStart[fIndex] + L.fieldByteSize[fIndex] * points;
        int fieldSoaOffset = 0;

        byte[] chunk = new byte[Mathf.Min(chunkBytes, L.totalBytes)];

        while (soaPos < L.totalBytes)
        {
            int wrote = dec.DecodeNextChunk(chunk, chunk.Length);
            if (wrote <= 0) break;

            int chunkOff = 0;
            while (chunkOff < wrote && soaPos < L.totalBytes)
            {
                int remainField = fEnd - soaPos;
                int take = Math.Min(remainField, wrote - chunkOff);

                ConsumeSoaSliceIntoArrays(
                    chunk, chunkOff, take,
                    L, fIndex,
                    ref fieldSoaOffset,
                    ix, iy, iz, iRgb, iRgba, iIntensity,
                    outData
                );

                chunkOff += take;
                soaPos += take;

                if (soaPos >= fEnd)
                {
                    fIndex++;
                    if (fIndex >= L.fields) break;
                    fEnd = L.blockStart[fIndex] + L.fieldByteSize[fIndex] * points;
                    fieldSoaOffset = 0;
                }
            }
        }

        if (soaPos != L.totalBytes)
            throw new Exception($"SOA decode incomplete: {soaPos} / {L.totalBytes}");

        fs.Position = compEnd;
    }

    static void ParseBinaryCompressed_NoAOS_Legacy(FileStream fs, Header h,
                                                   int ix, int iy, int iz, int iRgb, int iRgba, int iIntensity,
                                                   PcdData outData)
    {
        Span<byte> hdr = stackalloc byte[8];
        int read = fs.Read(hdr);
        if (read != 8) throw new EndOfStreamException("binary_compressed header too short");

        uint compressedSize = BitConverter.ToUInt32(hdr.Slice(0, 4));
        uint uncompressedSize = BitConverter.ToUInt32(hdr.Slice(4, 4));

        byte[] comp = new byte[compressedSize];
        int got = fs.Read(comp, 0, (int)compressedSize);
        if (got != (int)compressedSize) throw new EndOfStreamException();

        byte[] soa = new byte[uncompressedSize];
        int decoded = LzfDecode(comp, comp.Length, soa, (int)uncompressedSize);
        if (decoded != uncompressedSize) throw new Exception("LZF decode size mismatch");

        int points = outData.pointCount;
        var L = BuildSoaLayout(h, points);

        int offX = ix >= 0 ? L.blockStart[ix] : -1;
        int offY = iy >= 0 ? L.blockStart[iy] : -1;
        int offZ = iz >= 0 ? L.blockStart[iz] : -1;

        bool hasRgb = (outData.colors != null && iRgb >= 0);
        bool hasRgba = (outData.colors != null && iRgba >= 0);
        bool hasI = (outData.intensity != null && iIntensity >= 0);

        for (int p = 0; p < points; p++)
        {
            float fx = BitConverter.ToSingle(soa, offX + p * L.fieldByteSize[ix]);
            float fy = BitConverter.ToSingle(soa, offY + p * L.fieldByteSize[iy]);
            float fz = BitConverter.ToSingle(soa, offZ + p * L.fieldByteSize[iz]);
            outData.positions[p] = new Vector3(fx, fy, fz);

            if (hasRgb)
            {
                uint u = BitConverter.ToUInt32(soa, L.blockStart[iRgb] + p * L.fieldByteSize[iRgb]);
                outData.colors[p] = new Color32(
                    (byte)((u >> 16) & 0xFF),
                    (byte)((u >> 8) & 0xFF),
                    (byte)(u & 0xFF),
                    255);
            }
            else if (hasRgba)
            {
                uint u = BitConverter.ToUInt32(soa, L.blockStart[iRgba] + p * L.fieldByteSize[iRgba]);
                outData.colors[p] = new Color32(
                    (byte)((u >> 16) & 0xFF),
                    (byte)((u >> 8) & 0xFF),
                    (byte)(u & 0xFF),
                    (byte)((u >> 24) & 0xFF));
            }

            if (hasI)
                outData.intensity[p] = BitConverter.ToSingle(soa, L.blockStart[iIntensity] + p * L.fieldByteSize[iIntensity]);
        }
    }

    static int LzfDecode(byte[] src, int srcLen, byte[] dst, int dstLen)
    {
        int ip = 0;
        int ipEnd = srcLen;
        int op = 0;

        while (ip < ipEnd && op < dstLen)
        {
            int ctrl = src[ip++] & 0xFF;
            if (ctrl < (1 << 5))
            {
                int len = ctrl + 1;
                int remainIn = ipEnd - ip;
                if (len > remainIn) len = remainIn;
                int remainOut = dstLen - op;
                if (len > remainOut) len = remainOut;
                if (len <= 0) break;

                Buffer.BlockCopy(src, ip, dst, op, len);
                ip += len;
                op += len;
            }
            else
            {
                int len = (ctrl >> 5);
                if (ip >= ipEnd) break;

                int refOffset = op - (((ctrl & 0x1F) << 8) + 1);
                refOffset -= (src[ip++] & 0xFF);

                if (len == 7)
                {
                    if (ip >= ipEnd) break;
                    len += src[ip++] & 0xFF;
                }
                len += 2;

                if (refOffset < 0) return op;

                int end = op + len;
                if (end > dstLen) end = dstLen;

                while (op < end)
                {
                    dst[op++] = dst[refOffset++];
                }
            }
        }
        return op;
    }

    static void ComputeBounds(PcdData data)
    {
        if (data == null || data.positions == null || data.positions.Length == 0)
        {
            data.boundsMin = data.boundsMax = Vector3.zero;
            return;
        }
        Vector3 minV = data.positions[0];
        Vector3 maxV = data.positions[0];
        for (int i = 1; i < data.pointCount; i++)
        {
            var v = data.positions[i];
            if (v.x < minV.x) minV.x = v.x; if (v.y < minV.y) minV.y = v.y; if (v.z < minV.z) minV.z = v.z;
            if (v.x > maxV.x) maxV.x = v.x; if (v.y > maxV.y) maxV.y = v.y; if (v.z > maxV.z) maxV.z = v.z;
        }
        data.boundsMin = minV;
        data.boundsMax = maxV;
    }
}
