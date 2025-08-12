/*// PcdLoader.cs (발췌/추가)
using System;
using System.IO;
using System.Text;
using System.Globalization;
using UnityEngine;

public class PcdData
{
    public Vector3[] positions;
    public Color32[] colors;
    public float[] intensity;
    public int pointCount;
}

public static class PcdLoader
{
    class Header
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

    public static PcdData LoadFromFile(string path)
    {
        byte[] all = File.ReadAllBytes(path);

        int headerEnd = FindHeaderEnd(all);
        if (headerEnd <= 0)
            throw new Exception("PCD header not found");

        string headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var header = ParseHeader(headerText);

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

        switch (header.DATA)
        {
            case "ascii":
                ParseAscii(all, headerEnd, header, ix, iy, iz, iRgb, iRgba, iIntensity, data);
                break;

            case "binary":
                ParseBinary(all, headerEnd, header, ix, iy, iz, iRgb, iRgba, iIntensity, data, true);
                break;

            case "binary_compressed":
                ParseBinaryCompressed(all, headerEnd, header, ix, iy, iz, iRgb, iRgba, iIntensity, data);
                break;

            default:
                throw new Exception("Unsupported DATA: " + header.DATA);
        }
        return data;
    }

    static int FindHeaderEnd(byte[] bytes)
    {
        string ascii = Encoding.ASCII.GetString(bytes);
        int idx = ascii.IndexOf("\nDATA ");
        if (idx < 0) idx = ascii.IndexOf("\r\nDATA ");
        if (idx < 0) return -1;
        int lineEnd = ascii.IndexOf('\n', idx + 1);
        if (lineEnd < 0) lineEnd = ascii.Length;
        return Encoding.ASCII.GetByteCount(ascii.Substring(0, lineEnd + 1));
    }

    static Header ParseHeader(string header)
    {
        var h = new Header();
        var lines = header.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#")) continue;

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

        if (h.COUNT == null && h.FIELDS != null)
        {
            h.COUNT = new int[h.FIELDS.Length];
            for (int i = 0; i < h.COUNT.Length; i++) h.COUNT[i] = 1;
        }
        return h;
    }

    static string[] SplitTokens(string s)
    {
        return s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    }

    // --- ascii/binary 기존 구현은 동일 (생략) ---

    static void ParseAscii(byte[] all, int dataOffset, Header h,
                           int ix, int iy, int iz, int iRgb, int iRgba, int iIntensity,
                           PcdData outData)
    {
        string body = Encoding.ASCII.GetString(all, dataOffset, all.Length - dataOffset);
        var lines = body.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var fmt = CultureInfo.InvariantCulture;

        int n = outData.pointCount;
        int idx = 0;

        for (int li = 0; li < lines.Length && idx < n; li++)
        {
            var tok = SplitTokens(lines[li]);
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
        if (float.TryParse(token, System.Globalization.NumberStyles.Float, fmt, out float f))
        {
            uint u = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
            return new Color32((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF), 255);
        }
        else
        {
            uint u = Convert.ToUInt32(token);
            return new Color32((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF), 255);
        }
    }

    static Color32 DecodeRgbaASCII(string token, IFormatProvider fmt)
    {
        if (float.TryParse(token, System.Globalization.NumberStyles.Float, fmt, out float f))
        {
            uint u = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
            return new Color32((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF), (byte)((u >> 24) & 0xFF));
        }
        else
        {
            uint u = Convert.ToUInt32(token);
            return new Color32((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF), (byte)((u >> 24) & 0xFF));
        }
    }

    static void ParseBinary(byte[] all, int dataOffset, Header h,
                            int ix, int iy, int iz, int iRgb, int iRgba, int iIntensity,
                            PcdData outData, bool littleEndian = true)
    {
        int fieldCount = h.FieldCount;

        int[] offsets = new int[fieldCount];
        int stride = 0;
        for (int i = 0; i < fieldCount; i++)
        {
            offsets[i] = stride;
            stride += h.SIZE[i] * h.COUNT[i];
        }

        int n = outData.pointCount;

        for (int p = 0; p < n; p++)
        {
            int baseOff = dataOffset + p * stride;

            float fx = ReadFloat(all, baseOff + offsets[ix], littleEndian);
            float fy = ReadFloat(all, baseOff + offsets[iy], littleEndian);
            float fz = ReadFloat(all, baseOff + offsets[iz], littleEndian);
            outData.positions[p] = new Vector3(fx, fy, fz);

            if (outData.colors != null)
            {
                if (iRgb >= 0)
                {
                    uint u = ReadUInt32(all, baseOff + offsets[iRgb], littleEndian);
                    outData.colors[p] = new Color32((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF), 255);
                }
                else if (iRgba >= 0)
                {
                    uint u = ReadUInt32(all, baseOff + offsets[iRgba], littleEndian);
                    outData.colors[p] = new Color32((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF), (byte)((u >> 24) & 0xFF));
                }
            }

            if (outData.intensity != null && iIntensity >= 0)
                outData.intensity[p] = ReadFloat(all, baseOff + offsets[iIntensity], littleEndian);
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

    // ---------------- binary_compressed 지원 ----------------

    static void ParseBinaryCompressed(byte[] all, int dataOffset, Header h,
                                      int ix, int iy, int iz, int iRgb, int iRgba, int iIntensity,
                                      PcdData outData)
    {
        // 1) 헤더 직후: compressed_size(uint32 LE), uncompressed_size(uint32 LE)
        if (dataOffset + 8 > all.Length)
            throw new Exception("Invalid binary_compressed header");

        uint compressedSize = BitConverter.ToUInt32(all, dataOffset + 0);
        uint uncompressedSize = BitConverter.ToUInt32(all, dataOffset + 4);

        int compOff = dataOffset + 8;
        if (compOff + compressedSize > all.Length)
            throw new Exception("Compressed data out of range");

        // 2) LZF 디코드
        byte[] comp = new byte[compressedSize];
        Buffer.BlockCopy(all, compOff, comp, 0, (int)compressedSize);

        byte[] soa = new byte[uncompressedSize];
        int decoded = LzfDecode(comp, (int)compressedSize, soa, (int)uncompressedSize);
        if (decoded != uncompressedSize)
            throw new Exception($"LZF decode size mismatch ({decoded} != {uncompressedSize})");

        // 3) SOA → AOS 복원
        int fieldCount = h.FieldCount;

        int[] fieldElemSize = new int[fieldCount];
        int[] fieldElemCount = new int[fieldCount];
        int[] fieldByteSize = new int[fieldCount];
        int pointStride = 0;
        for (int i = 0; i < fieldCount; i++)
        {
            fieldElemSize[i] = h.SIZE[i];
            fieldElemCount[i] = h.COUNT[i];
            fieldByteSize[i] = fieldElemSize[i] * fieldElemCount[i];
            pointStride += fieldByteSize[i];
        }

        int points = outData.pointCount;
        int expectedUncompressed = 0;
        for (int i = 0; i < fieldCount; i++)
            expectedUncompressed += fieldByteSize[i] * points;
        if (expectedUncompressed != uncompressedSize)
            throw new Exception("Uncompressed size mismatch with header/points");

        byte[] aos = new byte[expectedUncompressed];
        RepackSOAtoAOS(soa, aos, points, fieldByteSize);

        // 4) 기존 ParseBinary 로직 재사용
        // 다만 ParseBinary는 ‘all’ 바이트 배열과 dataOffset(시작 오프셋) 기반으로 읽으므로,
        // 여기서는 aos를 임시 패킹한 “가상 파일”로 간주하고 dataOffset=0으로 호출
        ParseBinary(aos, 0, h, ix, iy, iz, iRgb, iRgba, iIntensity, outData, true);
    }

    static void RepackSOAtoAOS(byte[] soa, byte[] aos, int points, int[] fieldByteSize)
    {
        // SOA 레이아웃:
        // [Field0 bytes for all points][Field1 bytes for all points]...[FieldN bytes for all points]
        // 각 Field i 블록 크기 = fieldByteSize[i] * points

        int fieldCount = fieldByteSize.Length;

        // SOA에서 각 필드 블록 시작 오프셋
        int[] fieldBlockStart = new int[fieldCount];
        {
            int offset = 0;
            for (int i = 0; i < fieldCount; i++)
            {
                fieldBlockStart[i] = offset;
                offset += fieldByteSize[i] * points;
            }
        }

        // AOS 포인트당 스트라이드
        int pointStride = 0;
        for (int i = 0; i < fieldCount; i++) pointStride += fieldByteSize[i];

        // repack
        // for each point p:
        //   for each field f:
        //     copy fieldByteSize[f] bytes from soa[fieldBlockStart[f] + p*fieldByteSize[f]] to aos[p*pointStride + runningOffset]
        for (int p = 0; p < points; p++)
        {
            int dstBase = p * pointStride;
            int run = 0;
            for (int f = 0; f < fieldCount; f++)
            {
                int sizeF = fieldByteSize[f];
                int src = fieldBlockStart[f] + p * sizeF;
                Buffer.BlockCopy(soa, src, aos, dstBase + run, sizeF);
                run += sizeF;
            }
        }
    }

    // ---------------- LZF 디코더 ----------------
    // Marc Lehmann LZF 알고리즘의 표준 디코더를 C#으로 포팅한 단순 구현
    // 입력: src[0..srcLen), 출력: dst[0..dstLen)
    // 반환: 실제 디코딩된 바이트 수
    static int LzfDecode(byte[] src, int srcLen, byte[] dst, int dstLen)
    {
        int ip = 0; // input pointer
        int op = 0; // output pointer

        while (ip < srcLen)
        {
            int ctrl = src[ip++] & 0xFF;
            if (ctrl < (1 << 5))
            {
                // literal run: ctrl + 1 bytes
                int len = ctrl + 1;
                if (op + len > dstLen) len = dstLen - op;
                if (ip + len > srcLen) len = srcLen - ip;
                if (len <= 0) break;

                Buffer.BlockCopy(src, ip, dst, op, len);
                ip += len;
                op += len;
            }
            else
            {
                // back reference
                int len = (ctrl >> 5);
                int refOffset = (op - (((ctrl & 0x1F) << 8) + 1));
                if (ip >= srcLen) break;
                refOffset -= (src[ip++] & 0xFF);

                if (len == 7)
                {
                    if (ip >= srcLen) break;
                    len += src[ip++] & 0xFF;
                }
                len += 2;

                if (refOffset < 0) return op; // invalid reference guard

                // copy
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
}
*/