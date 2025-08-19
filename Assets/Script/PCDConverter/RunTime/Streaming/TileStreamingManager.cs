using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Buffers;
using UnityEngine;
using Unity.Collections.LowLevel.Unsafe;
using System.Security.Cryptography;

public sealed class TileStreamingManager : MonoBehaviour
{
    public TextAsset TileIndexJson;
    public Camera TargetCamera;
    public long MaxResidentBytes = 1_024L << 20;

    public BufferPool BufferPool;
    public GraphicsBuffer SharedVertexBuffer;

    private Dictionary<int, TileHandle> resident = new();
    private Queue<int> requestQueue = new();
    private long residentBytes;
    private static ulong GlobalTick;

    [Serializable]
    private class TileIndex { public List<TileMeta> Tiles; }
    private TileIndex index;

        void Awake()
    {
        index = JsonUtility.FromJson<TileIndex>(TileIndexJson.text);
        BufferPool = new BufferPool(GraphicsBuffer.Target.Structured,
                                    GraphicsBuffer.UsageFlags.LockBufferForWrite,
                                    256 << 20, 512 << 20);
    }

    void Update()
    {
        ScheduleTiles();
        if (requestQueue.Count > 0) StartCoroutine(DrainRequests());
    }

    void ScheduleTiles()
    {
        Vector3 camPos = TargetCamera.transform.position;
        foreach (var t in index.Tiles)
        {
            if (!resident.ContainsKey(t.Id))
            {
                float dist = Vector3.Distance(camPos, t.AABB.center);
                if (dist < 300f) requestQueue.Enqueue(t.Id);
            }
        }
    }

    IEnumerator DrainRequests()
    {
        while (requestQueue.Count > 0)
        {
            var id = requestQueue.Dequeue();
            if (resident.ContainsKey(id)) continue;

            var meta = index.Tiles.Find(x => x.Id == id);
            var handle = new TileHandle { Meta = meta };
            yield return StartCoroutine(LoadTile(handle));

            if (handle.State == ResidencyState.Resident)
            {
                resident[id] = handle;
                residentBytes += handle.VertexHandle.SizeBytes;
                EnforceBudget();
            }
        }
    }

    void EnforceBudget()
    {
        if (residentBytes <= MaxResidentBytes) return;
        var oldest = default(TileHandle);
        foreach (var kv in resident)
        {
            if (oldest == null || kv.Value.LastUsedTick < oldest.LastUsedTick)
                oldest = kv.Value;
        }
        if (oldest != null) EvictTile(oldest);
    }

    void EvictTile(TileHandle h)
    {
        BufferPool.Free(h.VertexHandle);
        resident.Remove(h.Meta.Id);
        residentBytes -= h.VertexHandle.SizeBytes;
        h.State = ResidencyState.Unloaded;
    }

    IEnumerator LoadTile(TileHandle handle)
    {
        string path = Path.Combine(Application.streamingAssetsPath, handle.Meta.Path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var sr = new StreamReader(fs, System.Text.Encoding.ASCII, false, 1 << 20, leaveOpen: true);
        var header = PcdUtil.ParseHeader(sr);
        long headerEnd = fs.Position;
        int N = header.Width * header.Height;
        if (N <= 0) yield break;

        int dstBytes = N * 12; // float3
        var hdl = BufferPool.Allocate(dstBytes, out var buf);
        if (!hdl.IsValid) yield break;

        handle.VertexHandle = hdl;
        handle.SharedVertexBuffer = buf;
        handle.Float3Count = N;

        fs.Position = headerEnd;

        if (header.Data.Equals("binary", StringComparison.OrdinalIgnoreCase))
        {
            // binary: ����Ʈ AoS �� x,y,z�� �̾� float3�� GPU�� ���� ���
            int stride = header.PointStep;
            byte[] tmp = new byte[stride * 1024];
            int writtenPts = 0;
            int writeOffset = hdl.OffsetBytes;

            while (writtenPts < N)
            {
                int batchPts = Math.Min(1024, N - writtenPts);
                int toRead = batchPts * stride;

                var readTask = fs.ReadAsync(tmp, 0, toRead);
                while (!readTask.IsCompleted) yield return null;
                int read = readTask.Result;
                if (read <= 0) break;

                // unsafe ���� �и�: �ڷ�ƾ������ ȣ�⸸
                UploadFloat3BatchUnsafe(buf,
                                        writeOffset,
                                        tmp,
                                        readBytes: read,
                                        stride: stride,
                                        offX: header.OffsetX,
                                        offY: header.OffsetY,
                                        offZ: header.OffsetZ);

                writeOffset += batchPts * 12;
                writtenPts += batchPts;

                // ������ �纸
                yield return null;
            }
        }
        // 4) LoadTile() ���� binary_compressed �б� ��ü ��ü
        else if (header.Data.Equals("binary_compressed", StringComparison.OrdinalIgnoreCase))
        {
            // 1) comp/uncomp ũ�� �б�
            byte[] head8 = new byte[8];
            var t0 = fs.ReadAsync(head8, 0, 8);
            while (!t0.IsCompleted) yield return null;
            if (t0.Result != 8) yield break;

            int compSize = BitConverter.ToInt32(head8, 0);
            int uncompSize = BitConverter.ToInt32(head8, 4);

            // 2) SoA ���̾ƿ� �غ�
            var soa = PcdSoA.BuildSoALayout(header);
            if (soa.XField < 0 || soa.YField < 0 || soa.ZField < 0)
            {
                Debug.LogError("PCD binary_compressed: x/y/z �ʵ带 ã�� ���߽��ϴ�.");
                yield break;
            }

            // 3) ���ڴ� + ��� ���ι��̴� + �����
            var provider = new SlidingWindowOutputProvider(initialCapacity: Math.Min(uncompSize, 16 << 20),
                                                           maxWindowBytes: 64 << 20);
            var decoder = new LzfStreamingDecoder(capacityHint: Math.Max(uncompSize, 1 << 20));

            // SoAAssembler�� OutputProvider(IOutputProvider)�� �ʿ��ϹǷ� ����� ����
            var assembler = new SoAAssembler(soa, buf, hdl.OffsetBytes, dstBytes,
                   new OutputProviderAdapter(provider));

            // ���ڴ� �ݹ�: provider�� assembler�� ����
            decoder.OnOutput = (offset, seg) =>
            {
                provider.Append(offset, seg);
                assembler.OnOutput(offset, seg);
            };

            // 4) ���� ������ ��Ʈ���� ���ڵ�
            int remaining = compSize;
            int chunk = 1 << 20;
            byte[] compBuf = new byte[chunk];

            while (remaining > 0)
            {
                int toRead = Math.Min(compBuf.Length, remaining);
                var rt = fs.ReadAsync(compBuf, 0, toRead);
                while (!rt.IsCompleted) yield return null;
                int read = rt.Result;
                if (read <= 0) break;

                decoder.Consume(new ReadOnlySpan<byte>(compBuf, 0, read));
                remaining -= read;

                // ������ �纸
                yield return null;
            }

            decoder.Finish();
            assembler.Finish();
        }
        else
        {
            Debug.LogWarning("Compressed PCD�� ���� �̱���");
        }

        handle.State = ResidencyState.Resident;
        handle.LastUsedTick = ++GlobalTick;
    }

    // ==========================
    // unsafe ���۵� (�ڷ�ƾ������ ȣ��)
    // ==========================

    // binary��: AoS ���ڵ忡�� x,y,z�� �̾� float3�� GPU�� �ϰ� ���
    private unsafe void UploadFloat3BatchUnsafe(GraphicsBuffer buffer,
                                                int dstOffsetBytes,
                                                byte[] src,
                                                int readBytes,
                                                int stride,
                                                int offX, int offY, int offZ)
    {
        int batchPts = readBytes / stride;
        var na = buffer.LockBufferForWrite<byte>(dstOffsetBytes, batchPts * 12);
        byte* pDst = (byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(na);

        fixed (byte* pSrc = src)
        {
            for (int i = 0; i < batchPts; i++)
            {
                byte* rec = pSrc + i * stride;
                float x = *(float*)(rec + offX);
                float y = *(float*)(rec + offY);
                float z = *(float*)(rec + offZ);

                *(float*)(pDst + i * 12 + 0) = x;
                *(float*)(pDst + i * 12 + 4) = y;
                *(float*)(pDst + i * 12 + 8) = z;
            }
        }
        buffer.UnlockBufferAfterWrite<byte>(batchPts * 12);
    }

    // compressed��: SoA(uncompressed)���� x[], y[], z[]�� ��� float3�� ��ŷ �� GPU�� �ϰ� ���
    private unsafe void UploadSoAFloat3Unsafe(GraphicsBuffer buffer,
                                              int startOffsetBytes,
                                              byte[] soaUncompressed,
                                              int count,
                                              int xStart, int yStart, int zStart)
    {
        var na = buffer.LockBufferForWrite<byte>(startOffsetBytes, count * 12);
        byte* pDst = (byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(na);

        fixed (byte* pSrc = soaUncompressed)
        {
            byte* px = pSrc + xStart;
            byte* py = pSrc + yStart;
            byte* pz = pSrc + zStart;

            for (int i = 0; i < count; i++)
            {
                *(float*)(pDst + i * 12 + 0) = *(float*)(px + i * 4);
                *(float*)(pDst + i * 12 + 4) = *(float*)(py + i * 4);
                *(float*)(pDst + i * 12 + 8) = *(float*)(pz + i * 4);
            }
        }
        buffer.UnlockBufferAfterWrite<byte>(count * 12);
    }

    #region Binary-compressed Tools
    private sealed class SlidingWindowOutputProvider
    {
        // ������ ���� ������(�� ��� ����), ���� ����
        private int _baseOffset;
        private byte[] _buf;
        private int _len; // ���� ���� ����

        // �ִ� ������ ũ�� ����(�޸� ��ȣ). �ʿ信 �°� ���� ����.
        private readonly int _maxWindowBytes;

        public SlidingWindowOutputProvider(int initialCapacity = 4 << 20, int maxWindowBytes = 64 << 20)
        {
            _buf = new byte[Math.Max(1024, initialCapacity)];
            _baseOffset = 0;
            _len = 0;
            _maxWindowBytes = Math.Max(1 << 20, maxWindowBytes);
        }

        public void Append(int outOffset, ArraySegment<byte> chunk)
        {
            // ����: Append�� outOffset�� �񰨼� ������ ȣ���(��Ʈ����)
            // �ʿ��� ��� ���� 0���� ä���� �ʰ�, ReadRange������ ���� ���� �䱸.
            int rel = outOffset - _baseOffset;
            int needEnd = rel + chunk.Count;
            EnsureCapacity(needEnd);
            Buffer.BlockCopy(chunk.Array!, chunk.Offset, _buf, rel, chunk.Count);
            _len = Math.Max(_len, needEnd);

            // �޸� ��ȣ: ���̽��� ������ �����̵� (�Һ� ���� ���� �����͸� ������)
            TrimHeadIfNeeded();
        }

        public ReadOnlySpan<byte> ReadRange(int offset, int length)
        {
            int rel = offset - _baseOffset;
            if (rel < 0 || rel + length > _len)
                throw new InvalidOperationException("Requested range is not in sliding window.");
            return new ReadOnlySpan<byte>(_buf, rel, length);
        }

        private void EnsureCapacity(int need)
        {
            if (need <= _buf.Length) return;
            int newSize = _buf.Length;
            while (newSize < need) newSize <<= 1;
            Array.Resize(ref _buf, newSize);
        }

        private void TrimHeadIfNeeded()
        {
            // �ʹ� Ŀ���� �պκ��� �߶� �����̵�
            if (_len > _maxWindowBytes)
            {
                int drop = _len - (_maxWindowBytes / 2); // ���� ���� ����� �ڸ�
                if (drop > 0)
                {
                    Buffer.BlockCopy(_buf, drop, _buf, 0, _len - drop);
                    _baseOffset += drop;
                    _len -= drop;
                }
            }
        }
    }

    // SoAAssembler�� ����ϴ� IOutputProvider ����� �߰�
    private sealed class OutputProviderAdapter : IOutputProvider
    {
        private readonly SlidingWindowOutputProvider _inner;
        public OutputProviderAdapter(SlidingWindowOutputProvider inner) => _inner = inner;

        public ReadOnlySpan<byte> ReadRange(int offset, int length) => _inner.ReadRange(offset, length);
        public void Append(int outOffset, ArraySegment<byte> chunk) => _inner.Append(outOffset, chunk);
    }

    #endregion
}
