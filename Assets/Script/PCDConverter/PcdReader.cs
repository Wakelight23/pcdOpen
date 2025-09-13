using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

// PCD(점군 데이터) 읽기 -> 로컬 저장소에 캐시된 바이너리 파일에서 메타데이터 및 노드 정보 로드
public sealed class PcdReader
{
    readonly string _datasetDir;
    readonly string _hierPath;
    readonly string _octPath;
    readonly Dictionary<int, NodeInfo> _nodes = new();
    public struct NodeInfo
    {
        public int nodeId, parentId, level, pointCount;
        public byte childMask;
        public long offset, size;
        public Bounds bounds;
        public float spacing;
    }

    public PcdMetadata Metadata { get; private set; }

    public PcdReader(string datasetDir)
    {
        _datasetDir = datasetDir ?? throw new ArgumentNullException(nameof(datasetDir));
        _hierPath = PcdCache.HierarchyPath(datasetDir);
        _octPath = PcdCache.OctreePath(datasetDir);

        var metaPath = PcdCache.MetadataPath(datasetDir);
        var json = File.ReadAllText(metaPath, Encoding.UTF8);
        Metadata = JsonUtility.FromJson<PcdMetadata>(json);

        LoadHierarchy();
    }

    void LoadHierarchy()
    {
        using var fs = new FileStream(_hierPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 << 10, FileOptions.SequentialScan);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
        int count = br.ReadInt32();
        _nodes.Clear();
        for (int i = 0; i < count; i++)
        {
            int nodeId = br.ReadInt32();
            int parentId = br.ReadInt32();
            short level = br.ReadInt16();
            byte childMask = br.ReadByte();
            byte reserved = br.ReadByte();
            int pointCount = br.ReadInt32();
            long offset = br.ReadInt64();
            long size = br.ReadInt64();
            var bmin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            var bmax = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            float spacing = br.ReadSingle();
            _nodes[nodeId] = new NodeInfo
            {
                nodeId = nodeId,
                parentId = parentId,
                level = level,
                pointCount = pointCount,
                childMask = childMask,
                offset = offset,
                size = size,
                bounds = new Bounds((bmin + bmax) * 0.5f, bmax - bmin),
                spacing = spacing
            };
        }
    }

    public bool TryGetNode(int nodeId, out NodeInfo info) => _nodes.TryGetValue(nodeId, out info);

    public IEnumerable<NodeInfo> EnumerateLevel(int level)
    {
        foreach (var kv in _nodes) if (kv.Value.level == level) yield return kv.Value;
    }

    // 노드 데이터 읽기: 인터리브 [x y z (rgba?)]
    public async Task<(Vector3[] pos, Color32[] col)> LoadNodePointsAsync(int nodeId, bool wantColor)
    {
        if (!_nodes.TryGetValue(nodeId, out var n) || n.pointCount <= 0 || n.size <= 0) return (Array.Empty<Vector3>(), null);
        byte[] buf = new byte[n.size];
        using var fs = new FileStream(_octPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess);
        fs.Position = n.offset;
        int read = await fs.ReadAsync(buf, 0, buf.Length);

        int stride = sizeof(float) * 3 + (wantColor ? sizeof(uint) : 0);
        int count = n.pointCount;
        var pos = new Vector3[count];
        Color32[] col = wantColor ? new Color32[count] : null;
        int src = 0;
        for (int i = 0; i < count; i++)
        {
            float x = BitConverter.ToSingle(buf, src + 0);
            float y = BitConverter.ToSingle(buf, src + 4);
            float z = BitConverter.ToSingle(buf, src + 8);
            pos[i] = new Vector3(x, y, z);
            src += 12;
            if (wantColor)
            {
                uint u = BitConverter.ToUInt32(buf, src);
                byte r = (byte)((u >> 16) & 0xFF);
                byte g = (byte)((u >> 8) & 0xFF);
                byte b = (byte)(u & 0xFF);
                col[i] = new Color32(r, g, b, 255);
                src += 4;
            }
        }
        return (pos, col);
    }
}