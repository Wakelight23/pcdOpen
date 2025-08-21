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
    [Tooltip("옥트리 최대 깊이")]
    public int octreeMaxDepth = 8;
    [Tooltip("노드 1개 최소 포인트 수(이하이면 분할 생략)")]
    public int minPointsPerNode = 4096;
    [Tooltip("노드 1개 최대 포인트 수(초과시 상위에서 샘플링/절삭)")]
    public int maxPointsPerNode = 200_000;
    [Tooltip("루트 LOD 초기 샘플 수(파일 전체에서 균일 샘플)")]
    public int rootSampleCount = 200_000;

    [Header("Scheduling")]
    public Camera targetCamera;
    public Transform worldTransform; // 포인트 GameObject의 Transform
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

    // 내부 상태
    PcdLoader.Header _header;
    long _dataOffset;
    PcdIndexBuilder.PcdIndex _index;
    PcdSubloader _subloader;

    PcdOctree _octree;
    PcdStreamScheduler _scheduler;

    // pointId -> position 캐시(옥트리 분할에 사용되는 lightweight 포지션 접근자)
    // 과도한 메모리 사용을 피하기 위해 전체를 채우지 않고 필요한 만큼만 보유.
    readonly Dictionary<int, Vector3> _posCache = new Dictionary<int, Vector3>(256 * 1024);

    // 노드 로드 진행 추적
    readonly HashSet<int> _requestedNodes = new HashSet<int>();

    // 메인 스레드 확인용
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

        // 1) GPU 렌더러 준비
        Report(0.15f, "Reading header"); // 또는 PcdEntry.Report 래핑
        if (gpuRenderer == null)
        {
            gpuRenderer = GetComponent<PcdGpuRenderer>();
            if (gpuRenderer == null) gpuRenderer = gameObject.AddComponent<PcdGpuRenderer>();
        }
        if (gpuRenderer.pointMaterial == null && defaultPointMaterial != null)
            gpuRenderer.pointMaterial = defaultPointMaterial;
        gpuRenderer.useColors = useColors;

        // 2) PCD 헤더 & 인덱스 생성
        Report(0.3f, "Building index");
        await Task.Run(() =>
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read, 1 << 20, System.IO.FileOptions.RandomAccess);
            _header = PcdLoader.ReadHeaderOnly(fs, out _dataOffset);

            var opt = new PcdIndexBuilder.BuildOptions
            {
                asciiLineIndexStride = 1,  // 가능하면 전체 라인 인덱스(대용량 ASCII면 8/16 등으로 늘려도 됨)
                scanBufferBytes = 1 << 20,
                verboseLog = false
            };
            _index = PcdIndexBuilder.Build(fs, _header, _dataOffset, opt);
        });

        // 3) PcdIndex -> PcdSubloader 구성
        // PcdSubloader가 사용하는 PcdIndex 타입과 PcdIndexBuilder.PcdIndex 타입이 다르므로, 필요한 필드로 변환
        var subIndex = ToSubloaderIndex(_index, _header);
        _subloader = new PcdSubloader(path, subIndex, useColors);

        // 4) 루트 샘플 선택 및 포지션 프리패스(샘플만)
        Report(0.45f, "Sampling root points");
        int[] rootSampleIds = await Task.Run(() => _subloader.BuildUniformSampleIds(Mathf.Min(rootSampleCount, subIndex.Points)));
        Report(0.5f, $"Root sample count: {rootSampleIds.Length}");
        // 루트 AABB 계산을 위해 샘플의 좌표만 빠르게 로드
        var rootSampleData = await _subloader.LoadPointsAsync(rootSampleIds);

        // 5) 정규화(옵션)
        Report(0.55f, "Normalizing root points");
        Vector3 center = (rootSampleData.boundsMin + rootSampleData.boundsMax) * 0.5f;
        if (normalizeToOrigin)
        {
            for (int i = 0; i < rootSampleData.pointCount; i++)
            {
                rootSampleData.positions[i] -= center;
            }
        }

        // 6) 옥트리 생성
        Report(0.6f, "Building octree");
        _octree = new PcdOctree();
        _octree.Configure(new PcdOctree.BuildParams
        {
            maxDepth = octreeMaxDepth,
            minPointsPerNode = minPointsPerNode,
            maxPointsPerNode = maxPointsPerNode
        });

        // 샘플 포인트 접근자: posCache를 우선 조회, 없으면 on-demand 로드(루트 구축 시엔 이미 rootSampleData 있음)
        // 여기서는 루트 빌드 시 필요한 포인트만 접근하도록 rootSampleData를 캐시에 넣어둔다.
        CachePositions(rootSampleIds, rootSampleData.positions);

        // positionsGetter: pointId -> Vector3
        Vector3 Getter(int pid)
        {
            if (_posCache.TryGetValue(pid, out var v)) return v;
            // 필요한 시점에만 on-demand 로드(최소화). 여기선 성능을 위해 예외적으로 Vector3.zero 반환하지 말고 동기적으로 보충.
            // 하지만 동기 파일 IO는 프레임 스톨을 유발할 수 있으니 실제 앱에선 비동기 선로딩 권장.
            var data = _subloader.LoadPointsAsync(new[] { pid }).GetAwaiter().GetResult();
            var p = data.pointCount > 0 ? data.positions[0] : Vector3.zero;
            if (normalizeToOrigin) p -= center;
            _posCache[pid] = p;
            return p;
        }

        var root = _octree.BuildInitial(rootSampleIds, Getter);

        // 7) 루트 노드 GPU 업로드
        // 주의: 메인 스레드에서만 Unity API 호출 가능
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

        // 8) 스케줄러 설정
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

        // 9) 루트 연결
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

        // 통계 업데이트
        var active = _scheduler.ActiveNodes;
        activeNodes = active != null ? active.Count : 0;

        // activePoints는 간접 추정: gpuRenderer.totalPointCount 사용(활성 노드 모두 업서트된다고 가정)
        activePoints = (gpuRenderer != null) ? gpuRenderer.totalPointCount : 0;
    }

    // ======= 스케줄러 콜백 구현 =======

    // 스케줄러가 특정 노드를 로드하라고 요청할 때 호출
    void OnRequestLoad(IOctreeNode node)
    {
        if (node == null) return;
        if (_requestedNodes.Contains(node.NodeId)) return;
        _requestedNodes.Add(node.NodeId);
        inflightLoads++;

        // 비동기 로딩
        _ = LoadNodeAsync(node);
    }

    // 스케줄러가 특정 노드를 언로드하라고 요청할 때 호출
    void OnRequestUnload(IOctreeNode node)
    {
        if (node == null) return;

        // GPU에서 제거
        if (IsMainThread())
        {
            gpuRenderer.RemoveNode(node.NodeId);
        }
        else
        {
            _ = RunOnMainThreadAsync(() => gpuRenderer.RemoveNode(node.NodeId));
        }

        // 상태 갱신
        node.IsActive = false;
        // 외부 노드 구현이므로 IsLoaded 플래그는 어댑터의 setter가 없을 수 있다.
        // 여기서는 실제 PcdOctree.Node에 접근하여 갱신.
        if (node is OctNodeAdapter ada && ada.Inner != null)
        {
            ada.Inner.IsLoaded = false;
        }

        _scheduler.NotifyUnloaded(node);
    }

    // 활성 노드 목록 변경 시: 렌더 순서 갱신
    void OnActiveNodesChanged(IReadOnlyList<IOctreeNode> nodes)
    {
        if (gpuRenderer == null) return;
        // 가까운 순서로 그리면 시각적 결과가 좋을 수 있으나, 여기선 스코어 정렬 결과를 그대로 사용
        var order = new List<int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++) order.Add(nodes[i].NodeId);

        if (IsMainThread()) gpuRenderer.SetDrawOrder(order);
        else _ = RunOnMainThreadAsync(() => gpuRenderer.SetDrawOrder(order));
    }

    // ======= 노드 로드/분할/업로드 =======

    async Task LoadNodeAsync(IOctreeNode schedNode)
    {
        try
        {
            if (schedNode is not OctNodeAdapter ada || ada.Inner == null)
                return;

            var node = ada.Inner;

            // 1) 필요 시 분할(on-demand)
            if (node.IsLeaf && node.Level + 1 < octreeMaxDepth)
            {
                // 분할 기준: 현재 노드 포인트가 충분하고, 화면상 에러가 임계보다 작은(가까운) 경우 등
                // 여기서는 단순히 최소 포인트 이상이면 분할 시도
                if (node.PointIds != null && node.PointIds.Count >= minPointsPerNode)
                {
                    // 자식 Bounds 생성과 포인트 분배
                    _octree.SubdivideOnDemand(node, GetPositionByPointId);
                }
            }

            // 2) 실제로 그릴 노드 결정: 자식이 있다면 자식들을 로드, 없으면 자신 로드
            if (!node.IsLeaf && node.Children != null)
            {
                // 자식들을 우선 로드(스케줄러가 각 자식을 개별적으로 요청할 수도 있으나,
                // 여기서는 부모 요청에서 자식 프리패치 전략을 약하게 적용 가능)
                foreach (var c in node.Children)
                {
                    if (c == null) continue;
                    await EnsureNodeUploadedAsync(c);
                }
            }
            else
            {
                // 리프 또는 분할 불가: 자신 업로드
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

        // 노드에 포인트가 없는 경우 스킵
        var ids = node.PointIds;
        int count = ids?.Count ?? 0;
        if (count <= 0) return;

        // 2.1) 필요한 포인트만 부분 로드
        var data = await _subloader.LoadPointsAsync(ids.ToArray());

        // 2.2) 정규화 적용
        if (normalizeToOrigin)
        {
            // 초기 center를 저장해두지 않았다면 루트 샘플에서 계산된 center를 재사용하도록 구조화해야 하나,
            // 여기서는 원점 기준으로 옮겨 렌더링한다고 가정(Initialize에서 rootSampleData를 이미 center로 정규화).
            // 이후 새로 로드되는 노드도 동일 기준(원점)일 것으로 가정.
            // 이미 _subloader는 원본 좌표를 반환하므로 여기서 center를 빼야 일관.
            // center를 멤버로 보관해두자.
        }

        // 2.3) AABB(노드 Bounds) 갱신 필요 시
        // 옥트리는 샘플 기반으로 Bounds가 설정되어 있으나, 더 정확한 보정을 원하면 여기서 재계산 가능.

        // 2.4) GPU 업로드
        await RunOnMainThreadAsync(() =>
        {
            if (gpuRenderer == null) return;
            gpuRenderer.AddOrUpdateNode(node.NodeId, data.positions, data.colors, node.Bounds);
        });

        node.IsLoaded = true;

        // 2.5) posCache 보강(분할 시 재사용)
        CachePositions(ids, data.positions);
    }

    // ======= 보조: 포인트 ID -> 좌표 =======

    Vector3 GetPositionByPointId(int pid)
    {
        if (_posCache.TryGetValue(pid, out var v)) return v;
        // 필요 시 동기 로드(최소 사용). 실제 서비스에서는 사전/비동기 보강 권장.
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

    // ======= IOctreeNode 어댑터 =======
    // 스케줄러는 인터페이스 IOctreeNode를 요구하므로, PcdOctree.Node를 감싸는 어댑터 제공
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

    // ======= PcdIndexBuilder.PcdIndex -> PcdSubloader용 PcdIndex 변환 =======

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

        // FIELDS 인덱스
        dst.Ix = Array.IndexOf(dst.FIELDS, "x");
        dst.Iy = Array.IndexOf(dst.FIELDS, "y");
        dst.Iz = Array.IndexOf(dst.FIELDS, "z");
        dst.IRgb = Array.IndexOf(dst.FIELDS, "rgb");
        dst.IRgba = Array.IndexOf(dst.FIELDS, "rgba");
        dst.IIntensity = Array.IndexOf(dst.FIELDS, "intensity");

        switch (src.Mode)
        {
            case PcdIndexBuilder.PcdDataMode.ASCII:
                dst.LineOffsets = src.LineOffsets; // 그대로 사용(전량 인덱스 권장)
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
                    // 헤더 기반 계산
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

    // ======= 메인 스레드 디스패처 =======

    static Task RunOnMainThreadAsync(Action action)
    {
        var go = FindObjectOfType<PcdEntry>(); // 이미 제공된 Entry의 메인스레드 큐 재사용
        if (go == null)
        {
            // 폴백: 즉시 실행(메인 스레드가 아닐 수 있어 Unity API 호출은 피해야 함)
            action?.Invoke();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>();
        var method = typeof(PcdEntry).GetMethod("GetType"); // 단순 참조 유지용

        // PcdEntry에 있는 static 큐를 직접 접근할 수 없으므로, Entry가 제공하는 RunOnMainThreadAsync를 리플렉션으로 호출하거나
        // 여기서 자체 디스패처를 만들어도 된다. 간단히 Unity Main에서 실행 보장 위한 방법으로 다음을 사용:
        PcdEntryPost(action, tcs);
        return tcs.Task;
    }

    static void PcdEntryPost(Action action, TaskCompletionSource<bool> tcs)
    {
        // 간단한 폴백 디스패치: 다음 프레임에 실행
        // 실제로는 PcdEntry의 s_mainQueue를 접근하는 게 이상적이지만 private이므로 여기선 StartCoroutine을 사용.
        var host = GetOrCreateDispatcherHost();
        host.StartCoroutine(InvokeNextFrame(action, tcs));
    }

    static System.Collections.IEnumerator InvokeNextFrame(Action a, TaskCompletionSource<bool> tcs)
    {
        yield return null;
        try { a?.Invoke(); tcs?.TrySetResult(true); }
        catch (Exception e) { tcs?.TrySetException(e); }
    }

    // 간단 디스패처 호스트
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
