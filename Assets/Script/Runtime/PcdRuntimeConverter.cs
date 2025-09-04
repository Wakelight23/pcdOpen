using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

// hierarchy.bin 노드 엔트리(간단 사양)
// nodeId(int32), parentId(int32), level(int16), childMask(uint8), reserved(uint8),
// pointCount(int32), offset(int64), size(int64), bounds(min3 float32, max3 float32), spacing(float32)
struct HierEntry
{
    public int nodeId;
    public int parentId;
    public short level;
    public byte childMask;
    public byte reserved;
    public int pointCount;
    public long offset;
    public long size;
    public Vector3 bmin;
    public Vector3 bmax;
    public float spacing;
}

public static class PcdRuntimeConverter
{
    // pcd → datasetDir(metadata.json/hierarchy.bin/octree.bin)
    public static async Task ConvertAsync(string pcdPath, string datasetDir, bool useColors, int maxDepth, int minPointsPerNode, int maxPointsPerNode)
    {
        Directory.CreateDirectory(datasetDir);
        PcdEntry.Report(0.05f, "Read PCD header");
        using var fs = new FileStream(pcdPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess);
        long dataOffset;
        var header = PcdLoader.ReadHeaderOnly(fs, out dataOffset);
        // 인덱스 생성
        var idx = PcdIndexBuilder.Build(fs, header, dataOffset, new PcdIndexBuilder.BuildOptions
        {
            asciiLineIndexStride = 1,
            scanBufferBytes = 1 << 20,
            verboseLog = false
        });

        // 부분로더 래핑
        var subIndex = ToSubIndex(idx, header);
        var sub = new PcdSubloader(pcdPath, subIndex, useColors);
        int totalPts = subIndex.Points;

        // 루트 샘플/옥트리
        PcdEntry.Report(0.15f, "Sample & build octree");
        var sampleIds = await Task.Run(() => sub.BuildUniformSampleIds(Mathf.Min(200_000, totalPts)));
        var sample = await sub.LoadPointsAsync(sampleIds);
        var oct = new PcdOctree();
        oct.Configure(new PcdOctree.BuildParams
        {
            maxDepth = maxDepth,
            minPointsPerNode = minPointsPerNode,
            maxPointsPerNode = maxPointsPerNode
        });
        // 위치 캐시
        var posCache = new Dictionary<int, Vector3>(sample.pointCount);
        for (int i = 0; i < sample.pointCount; i++) posCache[sampleIds[i]] = sample.positions[i];

        Vector3 PosGetter(int pid)
        {
            if (posCache.TryGetValue(pid, out var v)) return v;
            var d = sub.LoadPointsAsync(new[] { pid }).GetAwaiter().GetResult();
            var p = d.pointCount > 0 ? d.positions[0] : Vector3.zero;
            posCache[pid] = p;
            return p;
        }
        var root = oct.BuildInitial(sampleIds, PosGetter);

        // DFS로 분할 & 노드 수집
        PcdEntry.Report(0.25f, "Subdivide");
        var nodes = new List<PcdOctree.Node>(4096);
        var stack = new Stack<PcdOctree.Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            nodes.Add(n);
            if (n.Level + 1 < maxDepth && n.PointIds != null && n.PointIds.Count >= minPointsPerNode)
            {
                var ch = oct.SubdivideOnDemand(n, PosGetter);
                if (ch != null)
                {
                    for (int i = 0; i < ch.Length; i++) if (ch[i] != null) stack.Push(ch[i]);
                }
            }
        }

        // octree.bin 작성(노드별 블록)
        string octreePath = PcdCache.OctreePath(datasetDir);
        string hierPath = PcdCache.HierarchyPath(datasetDir);
        string metaPath = PcdCache.MetadataPath(datasetDir);

        int targetAtLevel0 = 200_000;
        PcdEntry.Report(0.35f, "Box-filter sampling + pack");
        var entries = new List<HierEntry>(nodes.Count);

        long curOff = 0;
        long sumBlocks = 0;

        using (var fOct = new FileStream(octreePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
        {
            int done = 0;
            foreach (var n in nodes)
            {
                var ids = n.PointIds?.ToArray();
                if (ids == null || ids.Length == 0)
                {
                    entries.Add(new HierEntry
                    {
                        nodeId = n.NodeId,
                        parentId = FindParentId(nodes, n),
                        level = (short)n.Level,
                        childMask = ComputeChildMask(n),
                        reserved = 0,
                        pointCount = 0,
                        offset = curOff,
                        size = 0,
                        bmin = n.Bounds.min,
                        bmax = n.Bounds.max,
                        spacing = Mathf.Max(1e-6f, n.Spacing)
                    });
                    continue;
                }

                // 1) 부분 로드
                var d = await sub.LoadPointsAsync(ids);

                // 2) 박스 필터 다운샘플
                int target = Mathf.Max(512, targetAtLevel0 >> n.Level);
                var s = new PcdBoxFilter.Settings
                {
                    targetPoints = target,
                    minGrid = 4,
                    maxGrid = 64,
                    occupancyBias = 1.0f,
                    preferCenter = true,
                    averageColor = true
                };
                PcdBoxFilter.DownsampleBox(n.Bounds, d.positions, d.colors, in s, out var pos, out var col);

                // === 레벨별 하한 및 빈 결과 보정 ===
                int minL0 = 2048;         // 루트 하한
                int minL1 = 1024;         // 레벨1 하한(데이터에 맞게 512~2048 사이 조정)
                int minRequired = (n.Level == 0) ? minL0 : (n.Level == 1 ? minL1 : 0);

                // 2-1) 결과가 비었으면 후보 ids에서 균일 보강
                if (pos == null || pos.Length == 0)
                {
                    int want = Mathf.Max(1, minRequired);
                    var fb = new List<int>(want);
                    int step = Mathf.Max(1, ids.Length / want);
                    for (int k = 0; k < ids.Length && fb.Count < want; k += step)
                        fb.Add(ids[k]);

                    if (fb.Count > 0)
                    {
                        var fd = await sub.LoadPointsAsync(fb.ToArray());
                        pos = fd.positions;
                        col = fd.colors;
                    }
                }

                // 2-2) 여전히 하한 미달이면 추가로 채움(가능 범위 내)
                if (pos != null && pos.Length < minRequired && ids.Length > pos.Length)
                {
                    int need = Mathf.Min(minRequired - pos.Length, ids.Length - pos.Length);
                    var extra = new List<int>(need);
                    int step2 = Mathf.Max(1, ids.Length / (pos.Length + need));
                    for (int k = 0; k < ids.Length && extra.Count < need; k += step2)
                        extra.Add(ids[k]);

                    if (extra.Count > 0)
                    {
                        var exd = await sub.LoadPointsAsync(extra.ToArray());
                        // pos 확장
                        var pos2 = new Vector3[pos.Length + exd.pointCount];
                        Array.Copy(pos, pos2, pos.Length);
                        Array.Copy(exd.positions, 0, pos2, pos.Length, exd.pointCount);
                        pos = pos2;

                        // col 확장(둘 중 하나라도 있으면 생성)
                        if (col != null || exd.colors != null)
                        {
                            var col2 = new Color32[pos.Length];
                            if (col != null) Array.Copy(col, col2, col.Length);
                            if (exd.colors != null) Array.Copy(exd.colors, 0, col2, (col != null ? col.Length : 0), exd.pointCount);
                            col = col2;
                        }
                    }
                }

                // 3) 빈 결과면 엔트리만 기록
                if (pos == null || pos.Length == 0)
                {
                    entries.Add(new HierEntry
                    {
                        nodeId = n.NodeId,
                        parentId = FindParentId(nodes, n),
                        level = (short)n.Level,
                        childMask = ComputeChildMask(n),
                        reserved = 0,
                        pointCount = 0,
                        offset = curOff,
                        size = 0,
                        bmin = n.Bounds.min,
                        bmax = n.Bounds.max,
                        spacing = Mathf.Max(1e-6f, n.Spacing)
                    });
                    continue;
                }

                // 4) 블록 패킹(stride = 12 bytes + 4 if color)
                int stride = sizeof(float) * 3 + ((col != null) ? sizeof(uint) : 0);
                int blockBytes = stride * pos.Length;
                var block = new byte[blockBytes];

                int dst = 0;
                for (int i = 0; i < pos.Length; i++)
                {
                    var p = pos[i];
                    Buffer.BlockCopy(BitConverter.GetBytes(p.x), 0, block, dst + 0, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(p.y), 0, block, dst + 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(p.z), 0, block, dst + 8, 4);
                    dst += 12;

                    if (col != null)
                    {
                        var c = col[i];
                        uint packed = 0xFF000000u | ((uint)c.r << 16) | ((uint)c.g << 8) | c.b;
                        Buffer.BlockCopy(BitConverter.GetBytes(packed), 0, block, dst, 4);
                        dst += 4;
                    }
                }

                long myOffset = curOff;
                await fOct.WriteAsync(block, 0, block.Length);
                sumBlocks += blockBytes;

                entries.Add(new HierEntry
                {
                    nodeId = n.NodeId,
                    parentId = FindParentId(nodes, n),
                    level = (short)n.Level,
                    childMask = ComputeChildMask(n),
                    reserved = 0,
                    pointCount = pos.Length,
                    offset = myOffset,
                    size = blockBytes,
                    bmin = n.Bounds.min,
                    bmax = n.Bounds.max,
                    spacing = Mathf.Max(1e-6f, n.Spacing)
                });
                curOff += blockBytes;

                done++;
                if ((done & 31) == 0)
                {
                    float t = 0.4f + 0.3f * (done / (float)nodes.Count);
                    PcdEntry.Report(t, $"Packing nodes {done}/{nodes.Count}");
                }
            }
            await fOct.FlushAsync();
        }



        Bounds ComputeSampleBounds(Vector3[] pts, int count)
        {
            if (pts == null || count <= 0) return new Bounds(Vector3.zero, Vector3.zero);
            var minV = pts; var maxV = pts;
            for (int i = 1; i < count; i++)
            {
                var v = pts[i];
                if (v.x < minV[0].x) minV[0].x = v.x; if (v.y < minV[0].y) minV[0].y = v.y; if (v.z < minV[0].z) minV[0].z = v.z;
                if (v.x > maxV[0].x) maxV[0].x = v.x; if (v.y > maxV[0].y) maxV[0].y = v.y; if (v.z > maxV[0].z) maxV[0].z = v.z;
            }
            var b = new Bounds(); b.SetMinMax(minV[0], maxV[0]); return b;
        }

        int lvl0 = 0;
        for (int i = 0; i < entries.Count; i++) if (entries[i].level == 0) { lvl0++; break; }
        if (lvl0 == 0)
        {
            // 안전한 루트 AABB/spacing 소스: 옥트리 루트 또는 샘플 bounds
            Bounds rb = root != null ? root.Bounds : default;
            float rs = root != null ? Mathf.Max(1e-6f, root.Spacing) : 1f;
            if (rb.size.sqrMagnitude <= 0f)
            {
                // 샘플에서 재계산 (보조)
                var sb = ComputeSampleBounds(sample.positions, sample.pointCount);
                rb = (sb.size.sqrMagnitude > 0f) ? sb : new Bounds(Vector3.zero, Vector3.one);
                rs = (root != null) ? Mathf.Max(1e-6f, root.Spacing) : 1f;
            }
            entries.Add(new HierEntry
            {
                nodeId = 0,
                parentId = -1,
                level = 0,
                childMask = 0,
                reserved = 0,
                pointCount = 0,
                offset = 0,
                size = 0,
                bmin = rb.min,
                bmax = rb.max,
                spacing = rs
            });
            Debug.LogWarning("[Converter] No level-0 entry found. Injected a root stub entry.");
        }

        const int minRootSample = 2048;
        if (root.PointIds == null || root.PointIds.Count < minRootSample)
        {
            var fill = new List<int>(minRootSample);
            int step = Mathf.Max(1, sample.pointCount / Mathf.Max(1, minRootSample));
            for (int i = 0; i < sample.pointCount && fill.Count < minRootSample; i += step)
                fill.Add(sampleIds[i]);
            if (root.PointIds == null) root.PointIds = fill;
            else root.PointIds.AddRange(fill);
        }

        // hierarchy.bin 기록(간단: 순차 테이블)
        PcdEntry.Report(0.75f, "Write hierarchy.bin");
        using (var fH = new FileStream(hierPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 << 10, FileOptions.SequentialScan))
        using (var bw = new BinaryWriter(fH, Encoding.UTF8, leaveOpen: false))
        {
            bw.Write(entries.Count);
            foreach (var e in entries)
            {
                bw.Write(e.nodeId);
                bw.Write(e.parentId);
                bw.Write(e.level);
                bw.Write(e.childMask);
                bw.Write(e.reserved);
                bw.Write(e.pointCount);
                bw.Write(e.offset);
                bw.Write(e.size);
                bw.Write(e.bmin.x); bw.Write(e.bmin.y); bw.Write(e.bmin.z);
                bw.Write(e.bmax.x); bw.Write(e.bmax.y); bw.Write(e.bmax.z);
                bw.Write(e.spacing);
            }
            await fH.FlushAsync();
        }

        // metadata.json 기록
        PcdEntry.Report(0.85f, "Write metadata.json");
        var meta = new PcdMetadata
        {
            name = Path.GetFileNameWithoutExtension(pcdPath),
            points = totalPts,
            boundingBox = new float[] { root.Bounds.min.x, root.Bounds.min.y, root.Bounds.min.z,
                                    root.Bounds.max.x, root.Bounds.max.y, root.Bounds.max.z },
            spacing = Mathf.Max(1e-6f, root.Spacing),
            attributes = useColors ? new[] { "POSITION", "RGB" } : new[] { "POSITION" },
            scale = new float[] { 1, 1, 1 },
            offset = new float[] { 0, 0, 0 }
        };
        File.WriteAllText(metaPath, JsonUtility.ToJson(meta, true), Encoding.UTF8);
        PcdEntry.Report(1.0f, "Convert done");
        var fi = new FileInfo(octreePath);
        Debug.Log($"[PcdRuntimeConverter] octree.bin sumBlocks={sumBlocks}B, fileLen={fi.Length}B");

        // 같은 프레임 즉시 로드 방지: 다음 프레임으로 양보
        await PcdEntry.RunOnMainThreadAsync(() => { /* next frame tick */ });
    }

    static int FindParentId(List<PcdOctree.Node> all, PcdOctree.Node n)
    {
        // 샘플 구현: 부모를 리스트에서 level-1이면서 bounds 포함하는 것 중 가장 작은 것 찾기(비용 낮은 대략)
        // 런타임에 정확 부모 추적이 필요하면 SubdivideOnDemand에서 parent 참조를 따로 저장 권장
        if (n.Level == 0) return -1;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p.Level != n.Level - 1) continue;
            if (p.Bounds.Contains(n.Bounds.center)) return p.NodeId;
        }
        return -1;
    }

    static byte ComputeChildMask(PcdOctree.Node n)
    {
        if (n.Children == null) return 0;
        byte m = 0;
        for (int i = 0; i < 8; i++) if (n.Children[i] != null && (n.Children[i].PointIds?.Count ?? 0) > 0) m |= (byte)(1 << i);
        return m;
    }

    static PcdIndex ToSubIndex(PcdIndexBuilder.PcdIndex src, PcdLoader.Header header)
    {
        var dst = new PcdIndex
        {
            Mode = src.Mode == PcdIndexBuilder.PcdDataMode.ASCII ? PcdDataMode.ASCII :
                   src.Mode == PcdIndexBuilder.PcdDataMode.Binary ? PcdDataMode.Binary : PcdDataMode.BinaryCompressed,
            Points = src.Points,
            DataStart = src.DataStart,
            FieldCount = src.FieldCount,
            FIELDS = src.Fields,
            SIZE = src.Size,
            COUNT = src.Count
        };
        dst.Ix = Array.IndexOf(dst.FIELDS, "x");
        dst.Iy = Array.IndexOf(dst.FIELDS, "y");
        dst.Iz = Array.IndexOf(dst.FIELDS, "z");
        dst.IRgb = Array.IndexOf(dst.FIELDS, "rgb");
        dst.IRgba = Array.IndexOf(dst.FIELDS, "rgba");
        if (src.Mode == PcdIndexBuilder.PcdDataMode.ASCII)
        {
            dst.LineOffsets = src.LineOffsets;
        }
        else if (src.Mode == PcdIndexBuilder.PcdDataMode.Binary)
        {
            dst.Stride = src.Stride;
            dst.FieldOffsets = src.FieldOffsets;
            dst.FieldSizes = new int[src.FieldCount];
            for (int i = 0; i < src.FieldCount; i++) dst.FieldSizes[i] = src.Size[i] * src.Count[i];
        }
        else
        {
            dst.CompStart = src.CompStart;
            dst.CompSize = src.CompSize;
            dst.UncompSize = src.UncompSize;
            if (!Equals(src.Soa, default(PcdLoader.SoaLayout)))
            {
                dst.Layout = new PcdIndex.SoaLayout
                {
                    fields = src.Soa.fields,
                    points = src.Soa.points,
                    fieldByteSize = src.Soa.fieldByteSize,
                    blockStart = src.Soa.blockStart,
                    totalBytes = src.Soa.totalBytes
                };
            }
        }
        return dst;
    }
}