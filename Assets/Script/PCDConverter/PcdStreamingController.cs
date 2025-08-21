using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

[DefaultExecutionOrder(-10)]
public sealed class PcdStreamingController : MonoBehaviour
{
    [Header("Data")]
    public string pcdPath;
    public bool useColors = true;
    public bool normalizeToOrigin = true;

    [Header("Octree/LOD")]
    [Tooltip("��Ʈ�� �ִ� ����")]
    public int octreeMaxDepth = 8;
    [Tooltip("��� 1�� �ּ� ����Ʈ ��(�����̸� ���� ����)")]
    public int minPointsPerNode = 4096;
    [Tooltip("��� 1�� �ִ� ����Ʈ ��(�ʰ��� �������� ���ø�/����)")]
    public int maxPointsPerNode = 200_000;
    [Tooltip("��Ʈ LOD �ʱ� ���� ��(���� ��ü���� ���� ����)")]
    public int rootSampleCount = 200_000;

    [Header("Scheduling")]
    public Camera targetCamera;
    public Transform worldTransform; // ����Ʈ GameObject�� Transform
    public int pointBudget = 5_000_000;
    public float screenErrorTarget = 2.0f;
    public int maxLoadsPerFrame = 2;
    public int maxUnloadsPerFrame = 4;

    [Header("Rendering")]
    public PcdGpuRenderer gpuRenderer;
    public Material defaultPointMaterial;

    [Header("Runtime State (read-only)")]
    public int activeNodes;
    public int activePoints;
    public int inflightLoads;

    public static event Action<float, string> OnProgress;
    static void Report(float t, string label = null)
    {
        OnProgress?.Invoke(Mathf.Clamp01(t), label);
    }

    // ���� ����
    PcdLoader.Header _header;
    long _dataOffset;
    PcdIndexBuilder.PcdIndex _index;
    PcdSubloader _subloader;

    PcdOctree _octree;
    PcdStreamScheduler _scheduler;

    // pointId -> position ĳ��(��Ʈ�� ���ҿ� ���Ǵ� lightweight ������ ������)
    // ������ �޸� ����� ���ϱ� ���� ��ü�� ä���� �ʰ� �ʿ��� ��ŭ�� ����.
    readonly Dictionary<int, Vector3> _posCache = new Dictionary<int, Vector3>(256 * 1024);

    // ��� �ε� ���� ����
    readonly HashSet<int> _requestedNodes = new HashSet<int>();

    // ���� ������ Ȯ�ο�
    static bool IsMainThread()
    {
        try { var _ = Time.frameCount; return true; }
        catch (UnityException) { return false; }
    }

    [ContextMenu("Initialize (from pcdPath)")]
    public async void InitializeFromInspector()
    {
        if (string.IsNullOrEmpty(pcdPath))
        {
            Debug.LogError("[PcdStreamingController] pcdPath is empty.");
            return;
        }
        await InitializeAsync(pcdPath);
    }

    public async Task InitializeAsync(string path)
    {
        pcdPath = path;

        // 1) GPU ������ �غ�
        Report(0.15f, "Reading header"); // �Ǵ� PcdEntry.Report ����
        if (gpuRenderer == null)
        {
            gpuRenderer = GetComponent<PcdGpuRenderer>();
            if (gpuRenderer == null) gpuRenderer = gameObject.AddComponent<PcdGpuRenderer>();
        }
        if (gpuRenderer.pointMaterial == null && defaultPointMaterial != null)
            gpuRenderer.pointMaterial = defaultPointMaterial;
        gpuRenderer.useColors = useColors;

        // 2) PCD ��� & �ε��� ����
        Report(0.3f, "Building index");
        await Task.Run(() =>
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read, 1 << 20, System.IO.FileOptions.RandomAccess);
            _header = PcdLoader.ReadHeaderOnly(fs, out _dataOffset);

            var opt = new PcdIndexBuilder.BuildOptions
            {
                asciiLineIndexStride = 1,  // �����ϸ� ��ü ���� �ε���(��뷮 ASCII�� 8/16 ������ �÷��� ��)
                scanBufferBytes = 1 << 20,
                verboseLog = false
            };
            _index = PcdIndexBuilder.Build(fs, _header, _dataOffset, opt);
        });

        // 3) PcdIndex -> PcdSubloader ����
        // PcdSubloader�� ����ϴ� PcdIndex Ÿ�԰� PcdIndexBuilder.PcdIndex Ÿ���� �ٸ��Ƿ�, �ʿ��� �ʵ�� ��ȯ
        var subIndex = ToSubloaderIndex(_index, _header);
        _subloader = new PcdSubloader(path, subIndex, useColors);

        // 4) ��Ʈ ���� ���� �� ������ �����н�(���ø�)
        Report(0.45f, "Sampling root points");
        int[] rootSampleIds = await Task.Run(() => _subloader.BuildUniformSampleIds(Mathf.Min(rootSampleCount, subIndex.Points)));
        Report(0.5f, $"Root sample count: {rootSampleIds.Length}");
        // ��Ʈ AABB ����� ���� ������ ��ǥ�� ������ �ε�
        var rootSampleData = await _subloader.LoadPointsAsync(rootSampleIds);

        // 5) ����ȭ(�ɼ�)
        Report(0.55f, "Normalizing root points");
        Vector3 center = (rootSampleData.boundsMin + rootSampleData.boundsMax) * 0.5f;
        if (normalizeToOrigin)
        {
            for (int i = 0; i < rootSampleData.pointCount; i++)
            {
                rootSampleData.positions[i] -= center;
            }
        }

        // 6) ��Ʈ�� ����
        Report(0.6f, "Building octree");
        _octree = new PcdOctree();
        _octree.Configure(new PcdOctree.BuildParams
        {
            maxDepth = octreeMaxDepth,
            minPointsPerNode = minPointsPerNode,
            maxPointsPerNode = maxPointsPerNode
        });

        // ���� ����Ʈ ������: posCache�� �켱 ��ȸ, ������ on-demand �ε�(��Ʈ ���� �ÿ� �̹� rootSampleData ����)
        // ���⼭�� ��Ʈ ���� �� �ʿ��� ����Ʈ�� �����ϵ��� rootSampleData�� ĳ�ÿ� �־�д�.
        CachePositions(rootSampleIds, rootSampleData.positions);

        // positionsGetter: pointId -> Vector3
        Vector3 Getter(int pid)
        {
            if (_posCache.TryGetValue(pid, out var v)) return v;
            // �ʿ��� �������� on-demand �ε�(�ּ�ȭ). ���⼱ ������ ���� ���������� Vector3.zero ��ȯ���� ���� ���������� ����.
            // ������ ���� ���� IO�� ������ ������ ������ �� ������ ���� �ۿ��� �񵿱� ���ε� ����.
            var data = _subloader.LoadPointsAsync(new[] { pid }).GetAwaiter().GetResult();
            var p = data.pointCount > 0 ? data.positions[0] : Vector3.zero;
            if (normalizeToOrigin) p -= center;
            _posCache[pid] = p;
            return p;
        }

        var root = _octree.BuildInitial(rootSampleIds, Getter);

        // 7) ��Ʈ ��� GPU ���ε�
        // ����: ���� �����忡���� Unity API ȣ�� ����
        if (!IsMainThread())
        {
            await RunOnMainThreadAsync(() =>
            {
                gpuRenderer.ClearAllNodes();
                gpuRenderer.AddOrUpdateNode(root.NodeId, rootSampleData.positions, rootSampleData.colors, root.Bounds);
            });
        }
        else
        {
            gpuRenderer.ClearAllNodes();
            gpuRenderer.AddOrUpdateNode(root.NodeId, rootSampleData.positions, rootSampleData.colors, root.Bounds);
        }
        Report(0.75f, "Uploading root node to GPU");
        root.IsLoaded = true;

        // 8) �����ٷ� ����
        Report(0.8f, "Initializing scheduler");
        _scheduler = new PcdStreamScheduler
        {
            Camera = (targetCamera != null ? targetCamera : Camera.main),
            WorldTransform = (worldTransform != null ? worldTransform : transform),
            PointBudget = pointBudget,
            ScreenErrorTarget = screenErrorTarget,
            MaxLoadsPerFrame = maxLoadsPerFrame,
            MaxUnloadsPerFrame = maxUnloadsPerFrame,
            RequestLoad = OnRequestLoad,
            RequestUnload = OnRequestUnload,
            OnActiveNodesChanged = OnActiveNodesChanged
        };

        // 9) ��Ʈ ����
        _scheduler.Clear();
        _scheduler.AddRoot(Adapt(root, null));
        Report(1.0f, "Done");

        Debug.Log("[PcdStreamingController] Initialized.");
    }

    void Update()
    {
        if (_scheduler == null) return;
        if (_scheduler.Camera == null) _scheduler.Camera = Camera.main;

        _scheduler.PointBudget = pointBudget;
        _scheduler.ScreenErrorTarget = screenErrorTarget;
        _scheduler.MaxLoadsPerFrame = maxLoadsPerFrame;
        _scheduler.MaxUnloadsPerFrame = maxUnloadsPerFrame;

        _scheduler.Tick();

        // ��� ������Ʈ
        var active = _scheduler.ActiveNodes;
        activeNodes = active != null ? active.Count : 0;

        // activePoints�� ���� ����: gpuRenderer.totalPointCount ���(Ȱ�� ��� ��� ����Ʈ�ȴٰ� ����)
        activePoints = (gpuRenderer != null) ? gpuRenderer.totalPointCount : 0;
    }

    // ======= �����ٷ� �ݹ� ���� =======

    // �����ٷ��� Ư�� ��带 �ε��϶�� ��û�� �� ȣ��
    void OnRequestLoad(IOctreeNode node)
    {
        if (node == null) return;
        if (_requestedNodes.Contains(node.NodeId)) return;
        _requestedNodes.Add(node.NodeId);
        inflightLoads++;

        // �񵿱� �ε�
        _ = LoadNodeAsync(node);
    }

    // �����ٷ��� Ư�� ��带 ��ε��϶�� ��û�� �� ȣ��
    void OnRequestUnload(IOctreeNode node)
    {
        if (node == null) return;

        // GPU���� ����
        if (IsMainThread())
        {
            gpuRenderer.RemoveNode(node.NodeId);
        }
        else
        {
            _ = RunOnMainThreadAsync(() => gpuRenderer.RemoveNode(node.NodeId));
        }

        // ���� ����
        node.IsActive = false;
        // �ܺ� ��� �����̹Ƿ� IsLoaded �÷��״� ������� setter�� ���� �� �ִ�.
        // ���⼭�� ���� PcdOctree.Node�� �����Ͽ� ����.
        if (node is OctNodeAdapter ada && ada.Inner != null)
        {
            ada.Inner.IsLoaded = false;
        }

        _scheduler.NotifyUnloaded(node);
    }

    // Ȱ�� ��� ��� ���� ��: ���� ���� ����
    void OnActiveNodesChanged(IReadOnlyList<IOctreeNode> nodes)
    {
        if (gpuRenderer == null) return;
        // ����� ������ �׸��� �ð��� ����� ���� �� ������, ���⼱ ���ھ� ���� ����� �״�� ���
        var order = new List<int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++) order.Add(nodes[i].NodeId);

        if (IsMainThread()) gpuRenderer.SetDrawOrder(order);
        else _ = RunOnMainThreadAsync(() => gpuRenderer.SetDrawOrder(order));
    }

    // ======= ��� �ε�/����/���ε� =======

    async Task LoadNodeAsync(IOctreeNode schedNode)
    {
        try
        {
            if (schedNode is not OctNodeAdapter ada || ada.Inner == null)
                return;

            var node = ada.Inner;

            // 1) �ʿ� �� ����(on-demand)
            if (node.IsLeaf && node.Level + 1 < octreeMaxDepth)
            {
                // ���� ����: ���� ��� ����Ʈ�� ����ϰ�, ȭ��� ������ �Ӱ躸�� ����(�����) ��� ��
                // ���⼭�� �ܼ��� �ּ� ����Ʈ �̻��̸� ���� �õ�
                if (node.PointIds != null && node.PointIds.Count >= minPointsPerNode)
                {
                    // �ڽ� Bounds ������ ����Ʈ �й�
                    _octree.SubdivideOnDemand(node, GetPositionByPointId);
                }
            }

            // 2) ������ �׸� ��� ����: �ڽ��� �ִٸ� �ڽĵ��� �ε�, ������ �ڽ� �ε�
            if (!node.IsLeaf && node.Children != null)
            {
                // �ڽĵ��� �켱 �ε�(�����ٷ��� �� �ڽ��� ���������� ��û�� ���� ������,
                // ���⼭�� �θ� ��û���� �ڽ� ������ġ ������ ���ϰ� ���� ����)
                foreach (var c in node.Children)
                {
                    if (c == null) continue;
                    await EnsureNodeUploadedAsync(c);
                }
            }
            else
            {
                // ���� �Ǵ� ���� �Ұ�: �ڽ� ���ε�
                await EnsureNodeUploadedAsync(node);
            }
        }
        finally
        {
            _requestedNodes.Remove(schedNode.NodeId);
            inflightLoads = Mathf.Max(0, inflightLoads - 1);
            schedNode.IsRequested = false;
            _scheduler.NotifyLoaded(schedNode);
        }
    }

    async Task EnsureNodeUploadedAsync(PcdOctree.Node node)
    {
        if (node == null) return;
        if (node.IsLoaded) return;

        // ��忡 ����Ʈ�� ���� ��� ��ŵ
        var ids = node.PointIds;
        int count = ids?.Count ?? 0;
        if (count <= 0) return;

        // 2.1) �ʿ��� ����Ʈ�� �κ� �ε�
        var data = await _subloader.LoadPointsAsync(ids.ToArray());

        // 2.2) ����ȭ ����
        if (normalizeToOrigin)
        {
            // �ʱ� center�� �����ص��� �ʾҴٸ� ��Ʈ ���ÿ��� ���� center�� �����ϵ��� ����ȭ�ؾ� �ϳ�,
            // ���⼭�� ���� �������� �Ű� �������Ѵٰ� ����(Initialize���� rootSampleData�� �̹� center�� ����ȭ).
            // ���� ���� �ε�Ǵ� ��嵵 ���� ����(����)�� ������ ����.
            // �̹� _subloader�� ���� ��ǥ�� ��ȯ�ϹǷ� ���⼭ center�� ���� �ϰ�.
            // center�� ����� �����ص���.
        }

        // 2.3) AABB(��� Bounds) ���� �ʿ� ��
        // ��Ʈ���� ���� ������� Bounds�� �����Ǿ� ������, �� ��Ȯ�� ������ ���ϸ� ���⼭ ���� ����.

        // 2.4) GPU ���ε�
        await RunOnMainThreadAsync(() =>
        {
            if (gpuRenderer == null) return;
            gpuRenderer.AddOrUpdateNode(node.NodeId, data.positions, data.colors, node.Bounds);
        });

        node.IsLoaded = true;

        // 2.5) posCache ����(���� �� ����)
        CachePositions(ids, data.positions);
    }

    // ======= ����: ����Ʈ ID -> ��ǥ =======

    Vector3 GetPositionByPointId(int pid)
    {
        if (_posCache.TryGetValue(pid, out var v)) return v;
        // �ʿ� �� ���� �ε�(�ּ� ���). ���� ���񽺿����� ����/�񵿱� ���� ����.
        var d = _subloader.LoadPointsAsync(new[] { pid }).GetAwaiter().GetResult();
        var p = (d.pointCount > 0) ? d.positions[0] : Vector3.zero;
        _posCache[pid] = p;
        return p;
    }

    void CachePositions(IReadOnlyList<int> ids, IReadOnlyList<Vector3> positions)
    {
        if (ids == null || positions == null) return;
        int n = Math.Min(ids.Count, positions.Count);
        for (int i = 0; i < n; i++)
        {
            _posCache[ids[i]] = positions[i];
        }
    }

    // ======= IOctreeNode ����� =======
    // �����ٷ��� �������̽� IOctreeNode�� �䱸�ϹǷ�, PcdOctree.Node�� ���δ� ����� ����
    sealed class OctNodeAdapter : IOctreeNode
    {
        public readonly PcdOctree.Node Inner;
        public readonly IOctreeNode ParentNode;

        public OctNodeAdapter(PcdOctree.Node inner, IOctreeNode parent)
        {
            Inner = inner;
            ParentNode = parent;
        }

        public int NodeId => Inner.NodeId;
        public Bounds Bounds => Inner.Bounds;
        public int Level => Inner.Level;
        public int EstimatedPointCount => Inner.EstimatedPointCount;

        public bool IsRequested
        {
            get => Inner.IsRequested;
            set => Inner.IsRequested = value;
        }

        public bool IsLoaded => Inner.IsLoaded;

        public bool IsActive { get; set; }

        public bool HasChildren => Inner.Children != null;

        IReadOnlyList<IOctreeNode> _childrenCache;

        public IReadOnlyList<IOctreeNode> Children
        {
            get
            {
                if (Inner.Children == null) return Array.Empty<IOctreeNode>();
                if (_childrenCache != null) return _childrenCache;
                var list = new List<IOctreeNode>(Inner.Children.Length);
                for (int i = 0; i < Inner.Children.Length; i++)
                {
                    var c = Inner.Children[i];
                    if (c == null) continue;
                    list.Add(new OctNodeAdapter(c, this));
                }
                _childrenCache = list;
                return _childrenCache;
            }
        }

        public IOctreeNode Parent => ParentNode;
    }

    IOctreeNode Adapt(PcdOctree.Node n, IOctreeNode parent)
    {
        return new OctNodeAdapter(n, parent);
    }

    // ======= PcdIndexBuilder.PcdIndex -> PcdSubloader�� PcdIndex ��ȯ =======

    PcdIndex ToSubloaderIndex(PcdIndexBuilder.PcdIndex src, PcdLoader.Header header)
    {
        var dst = new PcdIndex
        {
            Mode = MapMode(src.Mode),
            Points = src.Points,
            DataStart = src.DataStart,
            FieldCount = src.FieldCount,
            SIZE = src.Size,
            COUNT = src.Count,
            FIELDS = src.Fields
        };

        // FIELDS �ε���
        dst.Ix = Array.IndexOf(dst.FIELDS, "x");
        dst.Iy = Array.IndexOf(dst.FIELDS, "y");
        dst.Iz = Array.IndexOf(dst.FIELDS, "z");
        dst.IRgb = Array.IndexOf(dst.FIELDS, "rgb");
        dst.IRgba = Array.IndexOf(dst.FIELDS, "rgba");
        dst.IIntensity = Array.IndexOf(dst.FIELDS, "intensity");

        switch (src.Mode)
        {
            case PcdIndexBuilder.PcdDataMode.ASCII:
                dst.LineOffsets = src.LineOffsets; // �״�� ���(���� �ε��� ����)
                break;

            case PcdIndexBuilder.PcdDataMode.Binary:
                dst.Stride = src.Stride;
                dst.FieldOffsets = src.FieldOffsets;
                dst.FieldSizes = new int[src.FieldCount];
                for (int i = 0; i < src.FieldCount; i++)
                {
                    dst.FieldSizes[i] = src.Size[i] * src.Count[i];
                }
                break;

            case PcdIndexBuilder.PcdDataMode.BinaryCompressed:
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
                else
                {
                    // ��� ��� ���
                    dst.Layout = BuildSoaLayoutFallback(dst);
                }
                break;
        }

        return dst;
    }

    static PcdDataMode MapMode(PcdIndexBuilder.PcdDataMode m)
    {
        return m switch
        {
            PcdIndexBuilder.PcdDataMode.ASCII => PcdDataMode.ASCII,
            PcdIndexBuilder.PcdDataMode.Binary => PcdDataMode.Binary,
            PcdIndexBuilder.PcdDataMode.BinaryCompressed => PcdDataMode.BinaryCompressed,
            _ => PcdDataMode.Binary
        };
    }

    static PcdIndex.SoaLayout BuildSoaLayoutFallback(PcdIndex idx)
    {
        int fields = idx.FieldCount;
        var L = new PcdIndex.SoaLayout
        {
            fields = fields,
            points = idx.Points,
            fieldByteSize = new int[fields],
            blockStart = new int[fields]
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

    // ======= ���� ������ ����ó =======

    static Task RunOnMainThreadAsync(Action action)
    {
        var go = FindObjectOfType<PcdEntry>(); // �̹� ������ Entry�� ���ν����� ť ����
        if (go == null)
        {
            // ����: ��� ����(���� �����尡 �ƴ� �� �־� Unity API ȣ���� ���ؾ� ��)
            action?.Invoke();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>();
        var method = typeof(PcdEntry).GetMethod("GetType"); // �ܼ� ���� ������

        // PcdEntry�� �ִ� static ť�� ���� ������ �� �����Ƿ�, Entry�� �����ϴ� RunOnMainThreadAsync�� ���÷������� ȣ���ϰų�
        // ���⼭ ��ü ����ó�� ���� �ȴ�. ������ Unity Main���� ���� ���� ���� ������� ������ ���:
        PcdEntryPost(action, tcs);
        return tcs.Task;
    }

    static void PcdEntryPost(Action action, TaskCompletionSource<bool> tcs)
    {
        // ������ ���� ����ġ: ���� �����ӿ� ����
        // �����δ� PcdEntry�� s_mainQueue�� �����ϴ� �� �̻��������� private�̹Ƿ� ���⼱ StartCoroutine�� ���.
        var host = GetOrCreateDispatcherHost();
        host.StartCoroutine(InvokeNextFrame(action, tcs));
    }

    static System.Collections.IEnumerator InvokeNextFrame(Action a, TaskCompletionSource<bool> tcs)
    {
        yield return null;
        try { a?.Invoke(); tcs?.TrySetResult(true); }
        catch (Exception e) { tcs?.TrySetException(e); }
    }

    // ���� ����ó ȣ��Ʈ
    static StreamingDispatcherHost _host;
    static StreamingDispatcherHost GetOrCreateDispatcherHost()
    {
        if (_host != null) return _host;
        var go = new GameObject("[PcdStreamingDispatcher]");
        DontDestroyOnLoad(go);
        _host = go.AddComponent<StreamingDispatcherHost>();
        return _host;
    }

    sealed class StreamingDispatcherHost : MonoBehaviour { }
}
