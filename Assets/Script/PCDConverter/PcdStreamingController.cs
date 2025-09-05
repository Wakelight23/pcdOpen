using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public enum DrawSortMode
{
    FrontToBack, // ����� �ͺ��� �׸��� (Early-Z ����)
    BackToFront  // �� �ͺ��� �׸��� (����/���� � ����)
}

[DefaultExecutionOrder(-10)]
public sealed class PcdStreamingController : MonoBehaviour
{
    [Header("Data")]
    public string pcdPath;
    public bool useColors = true;
    public bool normalizeToOrigin = true;

    Vector3 _normalizeCenter;

    [Header("Rendering Sort")]
    [Tooltip("��ο� ���� ���� ���: Early-Z ����ȭ���� FrontToBack ����")]
    public DrawSortMode drawSortMode = DrawSortMode.FrontToBack;

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

    // ���� ����
    PcdLoader.Header _header;
    long _dataOffset;
    PcdIndexBuilder.PcdIndex _index;
    PcdSubloader _subloader;

    PcdOctree _octree;
    PcdStreamScheduler _scheduler;
    // �ε� ���    
    CancellationTokenSource _cts;

    // pointId -> position ĳ��(��Ʈ�� ���ҿ� ���Ǵ� lightweight ������ ������)
    // ������ �޸� ����� ���ϱ� ���� ��ü�� ä���� �ʰ� �ʿ��� ��ŭ�� ����.
    readonly Dictionary<int, Vector3> _posCache = new Dictionary<int, Vector3>(256 * 1024);

    // ��� �ε� ���� ����
    readonly HashSet<int> _requestedNodes = new HashSet<int>();

    [Header("Color Classification")]
    public bool enableRuntimeColorClassification = true;
    public RuntimePointClassifier colorClassifier;

    // ���� int3 ����ü(������ �߰�)
    struct int3 { public int x, y, z; public int3(int X, int Y, int Z) { x = X; y = Y; z = Z; } }

    // LOD cross-fade state per node
    sealed class LodFadeState { public float t; public float speed; public int mode; } // 0=off,1=alpha,2=dither
    readonly Dictionary<int, LodFadeState> _fade = new(2048);
    [SerializeField] float _fadeDuration = 0.25f; // seconds
    [SerializeField] int _fadeMode = 2; // 1=alpha, 2=dither

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

    void LateUpdate()
    {
        UpdateFade(Time.deltaTime);
    }

    #region Main Thread Helper
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

    // �ܺο��� ȣ��: ��� ���� �۾� �ߴ� + �޸�/���� �ʱ�ȭ
    public async Task DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch { }
        finally
        {
            // �ε� �Ϸ� ���: ���ö���Ʈ �۾��� Unity API�� �� �ǵ帮��
            // �ʿ� �� ���� ����
            await Task.Yield();
        }

        // �����ٷ� ����
        if (_scheduler != null)
        {
            _scheduler.Clear();
        }

        // GPU ���� ����
        if (gpuRenderer != null)
        {
            if (PcdStreamingController.IsMainThread())
                gpuRenderer.ClearAllNodes();
            else
                await RunOnMainThreadAsync(() => gpuRenderer.ClearAllNodes());
            gpuRenderer.SetDrawOrder(Array.Empty<int>());
        }

        // ���� �����̳� ����
        _requestedNodes.Clear();
        _posCache.Clear();

        _subloader = null;
        _index = null;
        _header = null;
        _octree = null;
        _scheduler = null;

        _cts?.Dispose();
        _cts = null;
    }
    #endregion

    #region PCD Load
    public async Task InitializeAsync(string path)
    {
        pcdPath = path;

        await DisposeAsync(); // ���ʱ�ȭ ���� �׻� ���� ����
        _cts = new CancellationTokenSource();

        // 1) GPU ������ �غ�
        PcdEntry.Report(0.15f, "Reading header"); // �Ǵ� PcdEntry.Report ����
        if (gpuRenderer == null)
        {
            gpuRenderer = GetComponent<PcdGpuRenderer>();
            if (gpuRenderer == null) gpuRenderer = gameObject.AddComponent<PcdGpuRenderer>();
        }
        if (gpuRenderer.pointMaterial == null && defaultPointMaterial != null)
            gpuRenderer.pointMaterial = defaultPointMaterial;
        gpuRenderer.useColors = useColors;

        // 2) PCD ��� & �ε��� ����
        PcdEntry.Report(0.3f, "Building index");
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
        PcdEntry.Report(0.45f, "Sampling root points");
        int[] rootSampleIds = await Task.Run(() => _subloader.BuildUniformSampleIds(Mathf.Min(rootSampleCount, subIndex.Points)));
        PcdEntry.Report(0.5f, $"Root sample count: {rootSampleIds.Length}");
        // ��Ʈ AABB ����� ���� ������ ��ǥ�� ������ �ε�
        var rootSampleData = await _subloader.LoadPointsAsync(rootSampleIds);

        // 5) ����ȭ(�ɼ�)
        PcdEntry.Report(0.55f, "Normalizing root points");
        Vector3 center = (rootSampleData.boundsMin + rootSampleData.boundsMax) * 0.5f;
        if (normalizeToOrigin)
        {
            for (int i = 0; i < rootSampleData.pointCount; i++)
            {
                rootSampleData.positions[i] -= center;
            }
        }

        // 6) ��Ʈ�� ����
        PcdEntry.Report(0.6f, "Building octree");
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
        // 6.5) ��Ʈ LOD �÷� �� ����(����)
        // ��Ʈ�� uniform �����̶� child/parent ������ 1:1�� �� ������,
        // ���⼭�� ���� ��ü�� ���� LOD�� ����, ���� �迭�� parent�� ���
        var parentPositions = rootSampleData.positions;
        var childPositions = rootSampleData.positions;
        var childColors = rootSampleData.colors;

        var grid = new int3(8, 8, 8); // ��� ũ��/������ �°� ����
        // var parentColors = BuildVoxelAveragedColors(parentPositions, childPositions, childColors, root.Bounds, grid);

        // InitializeAsync ������ ��Ʈ ��� ó�� �κ�
        var parentColors = enableRuntimeColorClassification && colorClassifier != null ? colorClassifier.ClassifyAndAdjustColors(parentPositions, childColors, root.Bounds) : BuildVoxelAveragedColors(parentPositions, childPositions, childColors, root.Bounds, grid);


        // 7) ��Ʈ ��� GPU ���ε�
        // ����: ���� �����忡���� Unity API ȣ�� ����
        if (!IsMainThread())
        {
            await RunOnMainThreadAsync(() =>
            {
                gpuRenderer.ClearAllNodes();
                gpuRenderer.AddOrUpdateNode(root.NodeId, rootSampleData.positions, parentColors, root.Bounds);
            });
        }
        else
        {
            gpuRenderer.ClearAllNodes();
            gpuRenderer.AddOrUpdateNode(root.NodeId, rootSampleData.positions, parentColors, root.Bounds);
        }
        PcdEntry.Report(0.75f, "Uploading root node to GPU");
        root.IsLoaded = true;

        // 8) �����ٷ� ����
        PcdEntry.Report(0.8f, "Initializing scheduler");
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
        PcdEntry.Report(1.0f, "Done");

        _normalizeCenter = normalizeToOrigin ? center : Vector3.zero;

        Debug.Log("[PcdStreamingController] Initialized.");
    }
    #endregion

    # region Scheduler Callbacks

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
        BeginFadeOut(node.NodeId);
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

        // �����ٷ� ���� �ݿ��� ���� �����ӿ� ����(���� �� ���� ����)
        StartCoroutine(NotifyUnloadedNextFrame(node));

    }

    System.Collections.IEnumerator NotifyUnloadedNextFrame(IOctreeNode node)
    {
        yield return null; // ���� ������
        _scheduler?.NotifyUnloaded(node);
    }

    // ī�޶� �Ÿ� ���: �����ٷ��� ī�޶� �˰� �ִٰ� ����
    float ComputeCameraDistanceSqr(IOctreeNode node)
    {
        if (_scheduler == null || _scheduler.Camera == null) return float.MaxValue;

        var camPos = _scheduler.Camera.transform.position;
        var center = node.Bounds.center;
        if (worldTransform != null)
            center = worldTransform.TransformPoint(center);

        return (center - camPos).sqrMagnitude;
    }


    // Ȱ�� ��� ��� ���� ��: ���� ���� ����
    void OnActiveNodesChanged(IReadOnlyList<IOctreeNode> nodes)
    {
        if (gpuRenderer == null) return;
        if (nodes == null || nodes.Count == 0)
        {
            if (IsMainThread())
            {
                gpuRenderer.SetDrawOrder(null);
                // ��Ÿ �ʱ�ȭ�� �ʿ��ϸ� ���⼭ gpuRenderer �ʿ� ���� API�� ȣ���ϵ��� Ȯ�� ����
            }
            else
            {
                _ = RunOnMainThreadAsync(() =>
                {
                    gpuRenderer.SetDrawOrder(null);
                });
            }
            return;
        }

        // 1) ���� �纻 �����
        var sorted = new List<IOctreeNode>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
            sorted.Add(nodes[i]);

        // 2) �Ÿ� ��� ����
        if (drawSortMode == DrawSortMode.FrontToBack)
        {
            sorted.Sort((a, b) =>
            {
                float da = ComputeCameraDistanceSqr(a);
                float db = ComputeCameraDistanceSqr(b);
                return da.CompareTo(db);
            });
        }
        else
        {
            sorted.Sort((a, b) =>
            {
                float da = ComputeCameraDistanceSqr(a);
                float db = ComputeCameraDistanceSqr(b);
                return db.CompareTo(da);
            });
        }

        // 3) ��ο� ���� �� ���̵� ����
        var order = new List<int>(sorted.Count);
        var nodeFades = new List<(int nodeId, float fade, int mode)>(sorted.Count);

        // 4) ��� ��Ÿ(spacing, level) ����
        // IOctreeNode�� Level�� ����. spacing�� PcdOctree.Node�� �����ϹǷ� ����� ���� ����
        var metas = new List<(int nodeId, float spacing, int level)>(sorted.Count);

        for (int i = 0; i < sorted.Count; i++)
        {
            var n = sorted[i];
            order.Add(n.NodeId);

            int mode;
            float t = GetFadeFactor(n.NodeId, out mode);
            nodeFades.Add((n.NodeId, t, mode));

            // ������ �ٿ�ĳ����: OctNodeAdapter -> Inner(PcdOctree.Node)
            float spacing = 1.0f;
            int level = n.Level;
            if (n is OctNodeAdapter ada && ada.Inner != null)
            {
                // Inner.Level�� IOctreeNode.Level�� ������ �ǹ�
                level = ada.Inner.Level;
                // Spacing �ʵ�� PcdOctree.Node���� ����(���� �ܰ迡�� ä���� �־�� ��)
                // ������ �⺻�� ����(1.0f)
                var s = ada.Inner.GetType().GetField("Spacing", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (s != null)
                {
                    object v = s.GetValue(ada.Inner);
                    if (v is float f && f > 0) spacing = f;
                }
            }
            metas.Add((n.NodeId, spacing, level));
        }

        // 5) ���ν����忡�� �ϰ� Ŀ��(SetNodeMeta -> SetPerNodeLodFade -> SetDrawOrder)
        if (IsMainThread())
        {
            // ��Ÿ ����
            for (int i = 0; i < metas.Count; i++)
            {
                var m = metas[i];
                gpuRenderer.SetNodeMeta(m.nodeId, m.spacing, m.level);
            }
            // ���̵�/��ο� ���� ����
            gpuRenderer.SetDrawOrder(order);
        }
        else
        {
            _ = RunOnMainThreadAsync(() =>
            {
                for (int i = 0; i < metas.Count; i++)
                {
                    var m = metas[i];
                    gpuRenderer.SetNodeMeta(m.nodeId, m.spacing, m.level);
                }
                gpuRenderer.SetDrawOrder(order);
            });
        }
    }
    #endregion

    #region Node loading, Subdivision, and Uploading
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
        if (node == null || node.IsLoaded) return;

        // ��忡 ����Ʈ�� ���� ��� ��ŵ
        var ids = node.PointIds;
        int count = ids?.Count ?? 0;
        if (count <= 0) return;

        // �ʿ��� ����Ʈ�� �κ� �ε�
        var data = await _subloader.LoadPointsAsync(ids.ToArray());
        var positions = data.positions;
        var colors = data.colors;

        // ��Ÿ�� ���� �з� ����
        if (enableRuntimeColorClassification && colorClassifier != null)
        {
            colors = colorClassifier.ClassifyAndAdjustColors(positions, colors, node.Bounds);
        }
        else
        {
            // ���� ���� ���ȭ ���
            var grid = new int3(8, 8, 8);
            colors = BuildVoxelAveragedColors(positions, positions, colors, node.Bounds, grid);
        }

        // ����ȭ ����
        if (normalizeToOrigin)
        {
            for (int i = 0; i < data.pointCount; i++) data.positions[i] -= _normalizeCenter;
        }

        // GPU ���ε�
        await RunOnMainThreadAsync(() =>
        {
            if (gpuRenderer == null) return;
            gpuRenderer.AddOrUpdateNode(node.NodeId, data.positions, colors, node.Bounds);
        });

        node.IsLoaded = true;

        // posCache ����(���� �� ����)
        CachePositions(ids, data.positions);
        BeginFadeIn(node.NodeId);
    }
    #endregion

    #region Helper : pointId -> position
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
    #endregion

    #region IOctreeNode Adapter
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
    #endregion

    #region PcdIndexBuilder.PcdIndex -> PcdSubloader PcdIndex Convert
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
    #endregion

    #region Main Thread Dispatcher
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
    #endregion

    #region LOD Fade Update
    void UpdateFade(float dt)
    {
        if (_fade.Count == 0) return;
        var keys = System.Buffers.ArrayPool<int>.Shared.Rent(_fade.Count);
        int n = 0;
        foreach (var kv in _fade) keys[n++] = kv.Key;
        for (int i = 0; i < n; i++)
        {
            var id = keys[i];
            var st = _fade[id];
            st.t = Mathf.Clamp01(st.t + st.speed * dt);
            if (st.t >= 1f) _fade.Remove(id);
        }
        System.Buffers.ArrayPool<int>.Shared.Return(keys);
    }

    void BeginFadeIn(int nodeId)
    {
        _fade[nodeId] = new LodFadeState { t = 0f, speed = 1f / Mathf.Max(1e-3f, _fadeDuration), mode = _fadeMode };
    }
    void BeginFadeOut(int nodeId)
    {
        _fade[nodeId] = new LodFadeState { t = 1f, speed = -1f / Mathf.Max(1e-3f, _fadeDuration), mode = _fadeMode };
    }
    float GetFadeFactor(int nodeId, out int mode)
    {
        if (_fade.TryGetValue(nodeId, out var st)) { mode = st.mode; return st.t; }
        mode = 0; return 1f;
    }

    #endregion

    #region Color Utils
    public enum PointClassification
    {
        Interior = 0,   // ����
        Exterior = 1,   // �ܺ�  
        Boundary = 2,   // ���
        Unknown = 3     // �̺з�
    }

    // ��� Bounds�� Nx,Ny,Nz ���ڷ� ���� �ڽ� ����Ʈ�� binning �� ���� ��� RGB�� ���Ѵ�.
    static Color32[] BuildVoxelAveragedColors(
     Vector3[] parentPositions,
     Vector3[] childPositions,
     Color32[] childColors,
     Bounds nodeBounds,
     int3 grid)
    {
        int px = Mathf.Max(1, grid.x), py = Mathf.Max(1, grid.y), pz = Mathf.Max(1, grid.z);
        int cellCount = px * py * pz;

        // ���� ����(���Ͼ� ���� ����)
        var sumR = new double[cellCount];
        var sumG = new double[cellCount];
        var sumB = new double[cellCount];
        var wsum = new double[cellCount];

        // ����: �ֵ� �� ����(Ʈ�� ��տ�)
        var lumaLists = new System.Collections.Generic.List<float>[cellCount];
        for (int i = 0; i < cellCount; i++) lumaLists[i] = new System.Collections.Generic.List<float>(32);

        Vector3 min = nodeBounds.min;
        Vector3 size = nodeBounds.size;
        Vector3 cell = new Vector3(
            Mathf.Max(1e-6f, size.x / px),
            Mathf.Max(1e-6f, size.y / py),
            Mathf.Max(1e-6f, size.z / pz)
        );

        // �� �� �߽� �̸� ���
        var cellCenters = new Vector3[cellCount];
        int idx = 0;
        for (int iz = 0; iz < pz; iz++)
            for (int iy = 0; iy < py; iy++)
                for (int ix = 0; ix < px; ix++, idx++)
                {
                    var cmin = new Vector3(min.x + ix * cell.x, min.y + iy * cell.y, min.z + iz * cell.z);
                    cellCenters[idx] = cmin + 0.5f * cell;
                }

        // �ڽ� ����Ʈ �� �� binning + �Ÿ� ���� ����
        for (int i = 0; i < childPositions.Length; i++)
        {
            Vector3 p = childPositions[i];
            // �� �ε���
            float fx = Mathf.Clamp01((p.x - min.x) / Mathf.Max(1e-6f, size.x));
            float fy = Mathf.Clamp01((p.y - min.y) / Mathf.Max(1e-6f, size.y));
            float fz = Mathf.Clamp01((p.z - min.z) / Mathf.Max(1e-6f, size.z));
            int ix = Mathf.Min(px - 1, Mathf.FloorToInt(fx * px));
            int iy = Mathf.Min(py - 1, Mathf.FloorToInt(fy * py));
            int iz = Mathf.Min(pz - 1, Mathf.FloorToInt(fz * pz));
            int ci = (iz * py + iy) * px + ix;

            // �� ���� ���� �� ����
            Color32 c8 = (childColors != null && i < childColors.Length) ? childColors[i] : new Color32(255, 255, 255, 255);

            // sRGB �� Linear
            Vector3 cLin = new Vector3(
                GammaToLinear01(c8.r / 255f),
                GammaToLinear01(c8.g / 255f),
                GammaToLinear01(c8.b / 255f)
            );

            // �Ÿ� ����ġ(�� �߽� ����, �� �밢������ ����ȭ)
            float dist = (p - cellCenters[ci]).magnitude;
            float cellDiag = cell.magnitude + 1e-6f;
            float w = Mathf.Clamp01(1f - (dist / cellDiag)); // �߽� �������� ���ߡ�

            // ����
            sumR[ci] += cLin.x * w;
            sumG[ci] += cLin.y * w;
            sumB[ci] += cLin.z * w;
            wsum[ci] += w;

            // �ֵ�(���Ͼ�) ���
            float l = 0.299f * cLin.x + 0.587f * cLin.y + 0.114f * cLin.z;
            lumaLists[ci].Add(l);
        }

        // ���̺� Ʈ�� ����
        const float trimRatio = 0.10f; // ��/���� 10% ����

        // ���� ��ջ� ���(���Ͼ��sRGB, Ʈ�� �ݿ�)
        var cellColor = new Color32[cellCount];
        for (int c = 0; c < cellCount; c++)
        {
            double wsumC = wsum[c];
            Vector3 avgLin;

            if (wsumC > 1e-8)
            {
                // 1) �ʱ� ���� ���(���Ͼ�)
                avgLin = new Vector3(
                    (float)(sumR[c] / wsumC),
                    (float)(sumG[c] / wsumC),
                    (float)(sumB[c] / wsumC)
                );

                // 2) �ι���Ʈ ����: �ֵ� Ʈ��
                var L = lumaLists[c];
                if (L.Count >= 8)
                {
                    L.Sort();
                    int lo = Mathf.RoundToInt(L.Count * trimRatio);
                    int hi = L.Count - lo;
                    double lSum = 0; int lCnt = 0;
                    for (int k = lo; k < hi; k++) { lSum += L[k]; lCnt++; }
                    if (lCnt > 0)
                    {
                        float lMean = (float)(lSum / lCnt);
                        // ��� �ֵ��� �µ��� ���� ��� ����
                        float curLum = 0.299f * avgLin.x + 0.587f * avgLin.y + 0.114f * avgLin.z;
                        float gain = Mathf.Clamp(curLum > 1e-5f ? lMean / curLum : 1f, 0.8f, 1.2f);
                        avgLin *= gain;
                    }
                }

                // 3) ä��/��� ���� ����(HSL �ٻ�)
                avgLin = BoostSaturationContrast(avgLin, satBoost: 0.2f, contrast: 1.08f);

                // 4) Linear �� sRGB, ��ŷ
                Vector3 avgSrgb = new Vector3(
                    LinearToGamma01(avgLin.x),
                    LinearToGamma01(avgLin.y),
                    LinearToGamma01(avgLin.z)
                );
                byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(avgSrgb.x * 255f), 0, 255);
                byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(avgSrgb.y * 255f), 0, 255);
                byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(avgSrgb.z * 255f), 0, 255);
                cellColor[c] = new Color32(r, g, b, 255);
            }
            else
            {
                cellColor[c] = new Color32(255, 255, 255, 255);
            }
        }

        // �θ� ����Ʈ�� �� �Ҵ�: �θ� ��ġ�� ���� ���� �� ���
        var parentColors = new Color32[parentPositions.Length];
        for (int p = 0; p < parentPositions.Length; p++)
        {
            Vector3 q = parentPositions[p];
            float fx = Mathf.Clamp01((q.x - min.x) / Mathf.Max(1e-6f, size.x));
            float fy = Mathf.Clamp01((q.y - min.y) / Mathf.Max(1e-6f, size.y));
            float fz = Mathf.Clamp01((q.z - min.z) / Mathf.Max(1e-6f, size.z));
            int ix = Mathf.Min(px - 1, Mathf.FloorToInt(fx * px));
            int iy = Mathf.Min(py - 1, Mathf.FloorToInt(fy * py));
            int iz = Mathf.Min(pz - 1, Mathf.FloorToInt(fz * pz));
            int ci = (iz * py + iy) * px + ix;
            parentColors[p] = cellColor[ci];
        }
        return parentColors;

        // ����: sRGB<->Linear �ٻ� (��Ȯ ������ UnityEngine.Mathf.GammaToLinearSpace ��� ����)
        static float GammaToLinear01(float c) { return Mathf.Approximately(c, 0f) ? 0f : Mathf.Pow(c, 2.2f); }
        static float LinearToGamma01(float c) { return c <= 0f ? 0f : Mathf.Pow(c, 1f / 2.2f); }

        // ����: ä��/��� ����(���Ͼ� ���� �Է�)
        static Vector3 BoostSaturationContrast(Vector3 rgbLin, float satBoost, float contrast)
        {
            // �ֵ� �и�
            float y = 0.299f * rgbLin.x + 0.587f * rgbLin.y + 0.114f * rgbLin.z;
            Vector3 diff = rgbLin - new Vector3(y, y, y);
            // ä�� �ν�Ʈ
            diff *= (1f + Mathf.Clamp(satBoost, 0f, 1f));
            // ��� �ణ ����(�ǹ�=0.5*���Ͼ� �ٻ� �� ���ֵ� ������ ����� 0.25��)
            float pivot = 0.25f;
            Vector3 outc = (rgbLin - new Vector3(pivot, pivot, pivot)) * Mathf.Clamp(contrast, 0.5f, 2.0f) + new Vector3(pivot, pivot, pivot);
            // ä�� �ݿ�
            outc = new Vector3(y, y, y) + diff;
            // Ŭ����
            outc.x = Mathf.Clamp01(outc.x); outc.y = Mathf.Clamp01(outc.y); outc.z = Mathf.Clamp01(outc.z);
            return outc;
        }
    }

    #endregion

}
