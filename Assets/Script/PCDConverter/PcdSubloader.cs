using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public enum PcdDataMode { ASCII, Binary, BinaryCompressed }

public sealed class PcdIndex
{
    public PcdDataMode Mode;
    public int Points;
    public long DataStart;

    // For ASCII
    public List<long> LineOffsets; // optional: full or sampled

    // For Binary
    public int Stride;
    public int[] FieldOffsets; // per field
    public int[] FieldSizes;   // per field

    // Field indices
    public int Ix = -1, Iy = -1, Iz = -1;
    public int IRgb = -1, IRgba = -1, IIntensity = -1;

    // For Binary Compressed (SOA)
    public long CompStart;
    public uint CompSize;
    public uint UncompSize;

    public int FieldCount;
    public int[] SIZE;
    public int[] COUNT;
    public string[] FIELDS;

    // Layout for SOA
    public class SoaLayout
    {
        public int fields;
        public int points;
        public int[] fieldByteSize;
        public int[] blockStart;
        public int totalBytes;
    }
    public SoaLayout Layout;
}

public sealed class PcdSubloader
{
    readonly string _path;
    readonly PcdIndex _index;
    readonly bool _useColors;

    public PcdSubloader(string path, PcdIndex index, bool useColors)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _useColors = useColors;
    }

    // 유틸: 파일 열기(공유/랜덤 액세스)
    FileStream OpenStream()
    {
        return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess);
    }

    // 루트 LOD나 샘플링용: 균일 간격으로 샘플 인덱스 선택
    public int[] BuildUniformSampleIds(int targetCount)
    {
        int n = _index.Points;
        if (n <= 0) return Array.Empty<int>();
        if (targetCount <= 0) targetCount = Math.Min(200_000, n);
        if (targetCount >= n)
        {
            var all = new int[n];
            for (int i = 0; i < n; i++) all[i] = i;
            return all;
        }
        var ids = new int[targetCount];
        double step = (double)n / targetCount;
        for (int i = 0; i < targetCount; i++)
        {
            ids[i] = (int)Math.Min(n - 1, Math.Round(i * step));
        }
        return ids;
    }

    // 영역 기반 샘플링(대략): AABB 필터 후 균일 샘플
    public async Task<int[]> BuildBoxFilteredSampleIdsAsync(Bounds worldBounds, Func<Vector3, bool> inBox, int targetCount)
    {
        // 단순하게 전체/스트림을 지나가며 샘플(대용량 시 주의: compressed는 통과 필요)
        switch (_index.Mode)
        {
            case PcdDataMode.Binary:
                return await Task.Run(() => SampleBoxBinary(inBox, targetCount));
            case PcdDataMode.ASCII:
                return await Task.Run(() => SampleBoxAscii(inBox, targetCount));
            case PcdDataMode.BinaryCompressed:
                return await Task.Run(() => SampleBoxCompressed(inBox, targetCount));
            default:
                return Array.Empty<int>();
        }
    }

    int[] SampleBoxBinary(Func<Vector3, bool> inBox, int targetCount)
    {
        var hits = new List<int>(Math.Min(targetCount * 2, 1000000));
        using var fs = OpenStream();
        fs.Position = _index.DataStart;
        int stride = _index.Stride;

        byte[] tmp = new byte[stride];

        // 필수 필드 오프셋
        int offX = _index.FieldOffsets[_index.Ix];
        int offY = _index.FieldOffsets[_index.Iy];
        int offZ = _index.FieldOffsets[_index.Iz];

        for (int i = 0; i < _index.Points; i++)
        {
            int read = fs.Read(tmp, 0, stride);
            if (read < stride) break;

            float x = BitConverter.ToSingle(tmp, offX);
            float y = BitConverter.ToSingle(tmp, offY);
            float z = BitConverter.ToSingle(tmp, offZ);

            if (inBox(new Vector3(x, y, z)))
                hits.Add(i);
        }

        if (hits.Count <= targetCount) return hits.ToArray();

        // 균일 다운샘플
        var result = new int[targetCount];
        double step = (double)hits.Count / targetCount;
        for (int i = 0; i < targetCount; i++)
            result[i] = hits[(int)Math.Min(hits.Count - 1, Math.Round(i * step))];
        return result;
    }

    int[] SampleBoxAscii(Func<Vector3, bool> inBox, int targetCount)
    {
        var hits = new List<int>(Math.Min(targetCount * 2, 500000));
        using var fs = OpenStream();
        using var sr = new StreamReader(fs, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 20, leaveOpen: true);

        // 헤더를 다시 스킵하여 본문 시작으로
        long body = _index.DataStart;
        fs.Position = body;

        // 필드 인덱스
        int ix = _index.Ix, iy = _index.Iy, iz = _index.Iz;
        int fieldCount = _index.FieldCount;

        var fmt = CultureInfo.InvariantCulture;
        string line;
        int pid = 0;
        while ((line = sr.ReadLine()) != null)
        {
            var tok = SplitTokens(line);
            if (tok.Length < fieldCount)
            {
                pid++;
                continue;
            }
            float x = float.Parse(tok[ix], fmt);
            float y = float.Parse(tok[iy], fmt);
            float z = float.Parse(tok[iz], fmt);
            if (inBox(new Vector3(x, y, z)))
                hits.Add(pid);
            pid++;
        }

        if (hits.Count <= targetCount) return hits.ToArray();
        var result = new int[targetCount];
        double step = (double)hits.Count / targetCount;
        for (int i = 0; i < targetCount; i++)
            result[i] = hits[(int)Math.Min(hits.Count - 1, Math.Round(i * step))];
        return result;
    }

    int[] SampleBoxCompressed(Func<Vector3, bool> inBox, int targetCount)
    {
        // 압축은 SOA 스트림을 통과하며 x/y/z를 읽어 박스 필터 후 인덱스 수집
        var hits = new List<int>(Math.Min(targetCount * 2, 500000));
        using var fs = OpenStream();

        // 앞 8바이트: compressedSize, uncompressedSize
        fs.Position = _index.DataStart;
        Span<byte> hdr = stackalloc byte[8];
        int read = fs.Read(hdr);
        if (read != 8) return Array.Empty<int>();
        uint comp = BitConverter.ToUInt32(hdr.Slice(0, 4));
        uint uncomp = BitConverter.ToUInt32(hdr.Slice(4, 4));

        long compStart = fs.Position;
        long compEnd = compStart + comp;

        var L = _index.Layout;
        if (L == null) return Array.Empty<int>();

        // 간단한 LZF 디코더 (PcdLoader와 동일 알고리즘)
        var dec = new LzfStreamDecoder(fs, compStart, comp);

        int soaPos = 0;
        int fIndex = 0;
        int fEnd = L.blockStart[fIndex] + L.fieldByteSize[fIndex] * L.points;
        int fieldSoaOffset = 0;

        byte[] chunk = new byte[Mathf.Min(1 << 20, L.totalBytes)];

        // x/y/z의 SOA 블록에서만 값 추출
        int ix = _index.Ix, iy = _index.Iy, iz = _index.Iz;
        int ex = L.fieldByteSize[ix], ey = L.fieldByteSize[iy], ez = L.fieldByteSize[iz];
        if (ex != 4 || ey != 4 || ez != 4) return Array.Empty<int>();

        // 임시 배열로 x,y,z를 점진 채우기(메모리 비용은 있지만 3*N*4바이트)
        float[] X = new float[L.points];
        float[] Y = new float[L.points];
        float[] Z = new float[L.points];

        while (soaPos < L.totalBytes)
        {
            int wrote = dec.DecodeNextChunk(chunk, chunk.Length);
            if (wrote <= 0) break;

            int chunkOff = 0;
            while (chunkOff < wrote && soaPos < L.totalBytes)
            {
                int remainField = fEnd - soaPos;
                int take = Math.Min(remainField, wrote - chunkOff);

                // consume into X/Y/Z only for those fields
                if (fIndex == ix || fIndex == iy || fIndex == iz)
                {
                    // 복사 범위
                    int startByteInField = fieldSoaOffset;
                    int endByteInField = startByteInField + take;

                    int firstElem = startByteInField / 4;
                    int firstElemByte = firstElem * 4;
                    int leadSkip = startByteInField - firstElemByte;

                    int fieldByteCursor = startByteInField;
                    int localCursor = chunkOff;

                    if (leadSkip > 0)
                    {
                        int skip = Math.Min(leadSkip, take);
                        fieldByteCursor += skip;
                        localCursor += skip;
                    }

                    int alignedBytes = endByteInField - fieldByteCursor;
                    int alignedElems = alignedBytes / 4;
                    int startElem = firstElem + ((fieldByteCursor - firstElemByte) / 4);

                    // 복사
                    for (int e = 0; e < alignedElems; e++)
                    {
                        int pIndex = startElem + e;
                        if (pIndex < 0 || pIndex >= L.points) break;

                        uint u = BitConverter.ToUInt32(chunk, localCursor + e * 4);
                        float f = BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
                        if (fIndex == ix) X[pIndex] = f;
                        else if (fIndex == iy) Y[pIndex] = f;
                        else Z[pIndex] = f;
                    }
                }

                chunkOff += take;
                soaPos += take;
                fieldSoaOffset += take;

                if (soaPos >= fEnd)
                {
                    fIndex++;
                    if (fIndex >= L.fields) break;
                    fEnd = L.blockStart[fIndex] + L.fieldByteSize[fIndex] * L.points;
                    fieldSoaOffset = 0;
                }
            }
        }

        for (int p = 0; p < L.points; p++)
        {
            if (inBox(new Vector3(X[p], Y[p], Z[p])))
                hits.Add(p);
        }
        if (hits.Count <= targetCount) return hits.ToArray();
        var result = new int[targetCount];
        double step = (double)hits.Count / targetCount;
        for (int i = 0; i < targetCount; i++)
            result[i] = hits[(int)Math.Min(hits.Count - 1, Math.Round(i * step))];
        return result;
    }

    // 특정 인덱스 집합만 로드하여 GPU 업로드용 배열 생성
    public async Task<PcdData> LoadPointsAsync(int[] ids)
    {
        if (ids == null || ids.Length == 0) return new PcdData { pointCount = 0, positions = Array.Empty<Vector3>() };

        switch (_index.Mode)
        {
            case PcdDataMode.Binary:
                return await Task.Run(() => LoadBinary(ids));
            case PcdDataMode.ASCII:
                return await Task.Run(() => LoadAscii(ids));
            case PcdDataMode.BinaryCompressed:
                return await Task.Run(() => LoadCompressed(ids));
            default:
                return new PcdData { pointCount = 0, positions = Array.Empty<Vector3>() };
        }
    }

    PcdData LoadBinary(int[] ids)
    {
        var pos = new Vector3[ids.Length];
        Color32[] col = _useColors && (_index.IRgb >= 0 || _index.IRgba >= 0) ? new Color32[ids.Length] : null;

        using var fs = OpenStream();
        using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);

        int stride = _index.Stride;
        int offX = _index.FieldOffsets[_index.Ix];
        int offY = _index.FieldOffsets[_index.Iy];
        int offZ = _index.FieldOffsets[_index.Iz];

        int offRgb = _index.IRgb >= 0 ? _index.FieldOffsets[_index.IRgb] : -1;
        int offRgba = _index.IRgba >= 0 ? _index.FieldOffsets[_index.IRgba] : -1;

        byte[] tmp = new byte[stride];

        for (int i = 0; i < ids.Length; i++)
        {
            long recOff = _index.DataStart + (long)ids[i] * stride;
            fs.Position = recOff;
            int read = br.Read(tmp, 0, stride);
            if (read < stride) throw new EndOfStreamException($"binary record short read at {ids[i]}");

            float x = BitConverter.ToSingle(tmp, offX);
            float y = BitConverter.ToSingle(tmp, offY);
            float z = BitConverter.ToSingle(tmp, offZ);
            pos[i] = new Vector3(x, y, z);

            if (col != null)
            {
                if (offRgb >= 0)
                {
                    uint u = BitConverter.ToUInt32(tmp, offRgb);
                    col[i] = new Color32((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF), 255);
                }
                else if (offRgba >= 0)
                {
                    uint u = BitConverter.ToUInt32(tmp, offRgba);
                    col[i] = new Color32((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF), (byte)((u >> 24) & 0xFF));
                }
            }
        }

        var data = new PcdData
        {
            pointCount = ids.Length,
            positions = pos,
            colors = col
        };
        ComputeBounds(data);
        return data;
    }

    PcdData LoadAscii(int[] ids)
    {
        // ids가 정렬되어 있으면 효율적. 정렬 보장 없으면 로컬 복사에서 정렬 후 결과 재정렬
        int n = ids.Length;
        var origOrder = new int[n];
        for (int i = 0; i < n; i++) origOrder[i] = i;

        Array.Sort(ids, origOrder);

        var posSorted = new Vector3[n];
        Color32[] colSorted = _useColors && (_index.IRgb >= 0 || _index.IRgba >= 0) ? new Color32[n] : null;

        using var fs = OpenStream();
        using var sr = new StreamReader(fs, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 20, leaveOpen: true);

        fs.Position = _index.DataStart;

        int ix = _index.Ix, iy = _index.Iy, iz = _index.Iz;
        int fieldCount = _index.FieldCount;
        int iRgb = _index.IRgb, iRgba = _index.IRgba;

        var fmt = CultureInfo.InvariantCulture;

        string line;
        int pid = 0;
        int targetPos = 0; // ids[targetPos]와 일치할 때까지 스캔
        while (targetPos < n && (line = sr.ReadLine()) != null)
        {
            if (pid == ids[targetPos])
            {
                var tok = SplitTokens(line);
                if (tok.Length >= fieldCount)
                {
                    float x = float.Parse(tok[ix], fmt);
                    float y = float.Parse(tok[iy], fmt);
                    float z = float.Parse(tok[iz], fmt);
                    posSorted[targetPos] = new Vector3(x, y, z);

                    if (colSorted != null)
                    {
                        if (iRgb >= 0)
                        {
                            colSorted[targetPos] = DecodeRgbASCII(tok[iRgb], fmt);
                        }
                        else if (iRgba >= 0)
                        {
                            colSorted[targetPos] = DecodeRgbaASCII(tok[iRgba], fmt);
                        }
                    }
                }
                targetPos++;
            }
            pid++;
        }

        // 원래 순서대로 재배열
        var pos = new Vector3[n];
        Color32[] col = colSorted != null ? new Color32[n] : null;
        for (int i = 0; i < n; i++)
        {
            pos[origOrder[i]] = posSorted[i];
            if (col != null) col[origOrder[i]] = colSorted[i];
        }

        var data = new PcdData
        {
            pointCount = n,
            positions = pos,
            colors = col
        };
        ComputeBounds(data);
        return data;
    }

    PcdData LoadCompressed(int[] ids)
    {
        // SOA를 통과해야 하므로, 전체 x/y/z 블록을 한 번 채우고 그중 ids만 추출하는 방식
        var pos = new Vector3[ids.Length];
        Color32[] col = _useColors && (_index.IRgb >= 0 || _index.IRgba >= 0) ? new Color32[ids.Length] : null;

        using var fs = OpenStream();

        fs.Position = _index.DataStart;
        Span<byte> hdr = stackalloc byte[8];
        int read = fs.Read(hdr);
        if (read != 8) throw new EndOfStreamException("binary_compressed header too short");

        uint comp = BitConverter.ToUInt32(hdr.Slice(0, 4));
        uint uncomp = BitConverter.ToUInt32(hdr.Slice(4, 4));
        long compStart = fs.Position;

        var L = _index.Layout ?? BuildSoaLayout(_index);
        int N = L.points;

        // 필요한 필드만 메모리 확보
        float[] X = new float[N];
        float[] Y = new float[N];
        float[] Z = new float[N];
        uint[] RGB = null, RGBA = null;
        if (col != null)
        {
            if (_index.IRgb >= 0) RGB = new uint[N];
            else if (_index.IRgba >= 0) RGBA = new uint[N];
        }

        var dec = new LzfStreamDecoder(fs, compStart, comp);

        int soaPos = 0;
        int fIndex = 0;
        int fEnd = L.blockStart[fIndex] + L.fieldByteSize[fIndex] * L.points;
        int fieldSoaOffset = 0;
        byte[] chunk = new byte[Mathf.Min(1 << 20, L.totalBytes)];

        while (soaPos < L.totalBytes)
        {
            int wrote = dec.DecodeNextChunk(chunk, chunk.Length);
            if (wrote <= 0) break;

            int chunkOff = 0;
            while (chunkOff < wrote && soaPos < L.totalBytes)
            {
                int remainField = fEnd - soaPos;
                int take = Math.Min(remainField, wrote - chunkOff);

                // x/y/z/rgb/rgba만 처리
                if (ProcessFieldSlice(fIndex, chunk, chunkOff, take, L, ref fieldSoaOffset, X, Y, Z, RGB, RGBA))
                {
                    // processed
                }

                chunkOff += take;
                soaPos += take;
                fieldSoaOffset += take;

                if (soaPos >= fEnd)
                {
                    fIndex++;
                    if (fIndex >= L.fields) break;
                    fEnd = L.blockStart[fIndex] + L.fieldByteSize[fIndex] * L.points;
                    fieldSoaOffset = 0;
                }
            }
        }

        // ids만 추출
        for (int i = 0; i < ids.Length; i++)
        {
            int p = ids[i];
            pos[i] = new Vector3(X[p], Y[p], Z[p]);
            if (col != null)
            {
                if (RGB != null)
                {
                    uint u = RGB[p];
                    col[i] = new Color32((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF), 255);
                }
                else if (RGBA != null)
                {
                    uint u = RGBA[p];
                    col[i] = new Color32((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF), (byte)((u >> 24) & 0xFF));
                }
            }
        }

        var data = new PcdData
        {
            pointCount = ids.Length,
            positions = pos,
            colors = col
        };
        ComputeBounds(data);
        return data;
    }

    bool ProcessFieldSlice(int fIndex, byte[] chunk, int chunkOff, int countBytes,
                           PcdIndex.SoaLayout L, ref int fieldSoaOffset,
                           float[] X, float[] Y, float[] Z, uint[] RGB, uint[] RGBA)
    {
        int elemSize = L.fieldByteSize[fIndex];
        if (elemSize != 4 || countBytes <= 0) { return false; }

        bool isX = (fIndex == _index.Ix);
        bool isY = (fIndex == _index.Iy);
        bool isZ = (fIndex == _index.Iz);
        bool isRgb = (_index.IRgb >= 0 && fIndex == _index.IRgb);
        bool isRgba = (_index.IRgba >= 0 && fIndex == _index.IRgba);

        if (!isX && !isY && !isZ && !isRgb && !isRgba) return false;

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

        if (alignedElems <= 0) return false;

        if (isX || isY || isZ)
        {
            for (int e = 0; e < alignedElems; e++)
            {
                int pIndex = startElem + e;
                if (pIndex < 0 || pIndex >= L.points) break;
                uint u = BitConverter.ToUInt32(chunk, localCursor + e * 4);
                float f = BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
                if (isX) X[pIndex] = f; else if (isY) Y[pIndex] = f; else Z[pIndex] = f;
            }
        }
        else if (isRgb && RGB != null)
        {
            for (int e = 0; e < alignedElems; e++)
            {
                int pIndex = startElem + e;
                if (pIndex < 0 || pIndex >= L.points) break;
                uint u = BitConverter.ToUInt32(chunk, localCursor + e * 4);
                RGB[pIndex] = u;
            }
        }
        else if (isRgba && RGBA != null)
        {
            for (int e = 0; e < alignedElems; e++)
            {
                int pIndex = startElem + e;
                if (pIndex < 0 || pIndex >= L.points) break;
                uint u = BitConverter.ToUInt32(chunk, localCursor + e * 4);
                RGBA[pIndex] = u;
            }
        }
        return true;
    }

    // 헤더/레이아웃이 없는 경우 SOA 레이아웃 생성
    PcdIndex.SoaLayout BuildSoaLayout(PcdIndex idx)
    {
        int fields = idx.FieldCount;
        var L = new PcdIndex.SoaLayout
        {
            fields = fields,
            points = idx.Points,
            fieldByteSize = new int[fields],
            blockStart = new int[fields],
        };
        int off = 0;
        for (int f = 0; f < fields; f++)
        {
            int bytes = idx.SIZE[f] * idx.COUNT[f];
            L.fieldByteSize[f] = bytes;
            L.blockStart[f] = off;
            off += bytes * idx.Points;
        }
        L.totalBytes = off;
        return L;
    }

    // 공통 유틸
    static string[] SplitTokens(string s)
        => s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    static Color32 DecodeRgbASCII(string token, IFormatProvider fmt)
    {
        if (float.TryParse(token, System.Globalization.NumberStyles.Float, fmt, out float f))
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
        if (float.TryParse(token, System.Globalization.NumberStyles.Float, fmt, out float f))
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

    // 간단 LZF 스트림 디코더 (PcdLoader와 동일 알고리즘)
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
}
