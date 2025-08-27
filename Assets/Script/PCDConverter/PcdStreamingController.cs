using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public enum DrawSortMode
{
    FrontToBack, // 가까운 것부터 그리기 (Early-Z 유리)
    BackToFront  // 먼 것부터 그리기 (블렌딩/투명 등에 유리)
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
    [Tooltip("드로우 순서 정렬 모드: Early-Z 최적화에는 FrontToBack 권장")]
    public DrawSortMode drawSortMode = DrawSortMode.FrontToBack;

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

    // 내부 상태
    PcdLoader.Header _header;
    long _dataOffset;
    PcdIndexBuilder.PcdIndex _index;
    PcdSubloader _subloader;

    PcdOctree _octree;
    PcdStreamScheduler _scheduler;
    // 로드 취소    
    CancellationTokenSource _cts;

    // pointId -> position 캐시(옥트리 분할에 사용되는 lightweight 포지션 접근자)
    // 과도한 메모리 사용을 피하기 위해 전체를 채우지 않고 필요한 만큼만 보유.
    readonly Dictionary<int, Vector3> _posCache = new Dictionary<int, Vector3>(256 * 1024);

    // 노드 로드 진행 추적
    readonly HashSet<int> _requestedNodes = new HashSet<int>();

    // 노드 Bounds를 Nx,Ny,Nz 격자로 나눠 자식 포인트를 binning 후 셀별 평균 RGB를 구한다.
    static Color32[] BuildVoxelAveragedColors(
        Vector3[] parentPositions,       // 상위 LOD(대표) 포인트
        Vector3[] childPositions,        // 자식(하위 LOD) 포인트
        Color32[] childColors,           // 자식 색
        Bounds nodeBounds,               // 노드 AABB
        int3 grid)                       // 예: (8,8,8)
    {
        int px = Mathf.Max(1, grid.x), py = Mathf.Max(1, grid.y), pz = Mathf.Max(1, grid.z);
        int cellCount = px * py * pz;

        // 누적 버퍼
        var sumR = new ulong[cellCount];
        var sumG = new ulong[cellCount];
        var sumB = new ulong[cellCount];
        var cnt = new int[cellCount];

        Vector3 min = nodeBounds.min;
        Vector3 size = nodeBounds.size;
        Vector3 cell = new Vector3(
            Mathf.Max(1e-6f, size.x / px),
            Mathf.Max(1e-6f, size.y / py),
            Mathf.Max(1e-6f, size.z / pz)
        );

        // child 포인트를 voxel로 binning
        for (int i = 0; i < childPositions.Length; i++)
        {
            Vector3 p = childPositions[i];
            // 노드 바운즈 안으로 클램프
            float fx = Mathf.Clamp((p.x - min.x) / size.x, 0f, 0.999999f);
            float fy = Mathf.Clamp((p.y - min.y) / size.y, 0f, 0.999999f);
            float fz = Mathf.Clamp((p.z - min.z) / size.z, 0f, 0.999999f);

            int ix = Mathf.FloorToInt(fx * px);
            int iy = Mathf.FloorToInt(fy * py);
            int iz = Mathf.FloorToInt(fz * pz);

            int ci = (iz * py + iy) * px + ix;
            var c = childColors != null && i < childColors.Length ? childColors[i] : new Color32(255, 255, 255, 255);
            sumR[ci] += c.r; sumG[ci] += c.g; sumB[ci] += c.b; cnt[ci]++;
        }

        // parent 포인트별 평균색 할당: parent도 동일 voxel로 양자화하여 그 셀 평균을 사용
        var parentColors = new Color32[parentPositions.Length];
        for (int p = 0; p < parentPositions.Length; p++)
        {
            Vector3 q = parentPositions[p];
            float fx = Mathf.Clamp((q.x - min.x) / size.x, 0f, 0.999999f);
            float fy = Mathf.Clamp((q.y - min.y) / size.y, 0f, 0.999999f);
            float fz = Mathf.Clamp((q.z - min.z) / size.z, 0f, 0.999999f);

            int ix = Mathf.FloorToInt(fx * px);
            int iy = Mathf.FloorToInt(fy * py);
            int iz = Mathf.FloorToInt(fz * pz);
            int ci = (iz * py + iy) * px + ix;

            if (ci >= 0 && ci < cellCount && cnt[ci] > 0)
            {
                byte r = (byte)(sumR[ci] / (ulong)cnt[ci]);
                byte g = (byte)(sumG[ci] / (ulong)cnt[ci]);
                byte b = (byte)(sumB[ci] / (ulong)cnt[ci]);
                parentColors[p] = new Color32(r, g, b, 255);
            }
            else
            {
                parentColors[p] = new Color32(255, 255, 255, 255); // 폴백
            }
        }
        return parentColors;
    }

    // 간단 int3 구조체(없으면 추가)
    struct int3 { public int x, y, z; public int3(int X, int Y, int Z) { x = X; y = Y; z = Z; } }


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

    // 외부에서 호출: 모든 진행 작업 중단 + 메모리/상태 초기화
    public async Task DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch { }
        finally
        {
            // 로딩 완료 대기: 인플라이트 작업이 Unity API를 안 건드리게
            // 필요 시 작은 지연
            await Task.Yield();
        }

        // 스케줄러 해제
        if (_scheduler != null)
        {
            _scheduler.Clear();
        }

        // GPU 버퍼 정리
        if (gpuRenderer != null)
        {
            if (PcdStreamingController.IsMainThread())
                gpuRenderer.ClearAllNodes();
            else
                await RunOnMainThreadAsync(() => gpuRenderer.ClearAllNodes());
            gpuRenderer.SetDrawOrder(Array.Empty<int>());
        }

        // 상태 컨테이너 정리
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

    public async Task InitializeAsync(string path)
    {
        pcdPath = path;

        await DisposeAsync(); // 재초기화 전에 항상 완전 정리
        _cts = new CancellationTokenSource();

        // 1) GPU 렌더러 준비
        PcdEntry.Report(0.15f, "Reading header"); // 또는 PcdEntry.Report 래핑
        if (gpuRenderer == null)
        {
            gpuRenderer = GetComponent<PcdGpuRenderer>();
            if (gpuRenderer == null) gpuRenderer = gameObject.AddComponent<PcdGpuRenderer>();
        }
        if (gpuRenderer.pointMaterial == null && defaultPointMaterial != null)
            gpuRenderer.pointMaterial = defaultPointMaterial;
        gpuRenderer.useColors = useColors;

        // 2) PCD 헤더 & 인덱스 생성
        PcdEntry.Report(0.3f, "Building index");
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
        PcdEntry.Report(0.45f, "Sampling root points");
        int[] rootSampleIds = await Task.Run(() => _subloader.BuildUniformSampleIds(Mathf.Min(rootSampleCount, subIndex.Points)));
        PcdEntry.Report(0.5f, $"Root sample count: {rootSampleIds.Length}");
        // 루트 AABB 계산을 위해 샘플의 좌표만 빠르게 로드
        var rootSampleData = await _subloader.LoadPointsAsync(rootSampleIds);

        // 5) 정규화(옵션)
        PcdEntry.Report(0.55f, "Normalizing root points");
        Vector3 center = (rootSampleData.boundsMin + rootSampleData.boundsMax) * 0.5f;
        if (normalizeToOrigin)
        {
            for (int i = 0; i < rootSampleData.pointCount; i++)
            {
                rootSampleData.positions[i] -= center;
            }
        }

        // 6) 옥트리 생성
        PcdEntry.Report(0.6f, "Building octree");
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
        // 6.5) 루트 LOD 컬러 밉 생성(선택)
        // 루트는 uniform 샘플이라 child/parent 개념이 1:1일 수 있지만,
        // 여기서는 샘플 자체를 상위 LOD로 보고, 동일 배열을 parent로 사용
        var parentPositions = rootSampleData.positions;
        var childPositions = rootSampleData.positions;
        var childColors = rootSampleData.colors;

        var grid = new int3(8, 8, 8); // 노드 크기/분포에 맞게 조정
        var parentColors = BuildVoxelAveragedColors(parentPositions, childPositions, childColors, root.Bounds, grid);

        // 7) 루트 노드 GPU 업로드
        // 주의: 메인 스레드에서만 Unity API 호출 가능
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

        // 8) 스케줄러 설정
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

        // 9) 루트 연결
        _scheduler.Clear();
        _scheduler.AddRoot(Adapt(root, null));
        PcdEntry.Report(1.0f, "Done");

        _normalizeCenter = normalizeToOrigin ? center : Vector3.zero;

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

        // 스케줄러 상태 반영은 다음 프레임에 수행(열거 중 수정 방지)
        StartCoroutine(NotifyUnloadedNextFrame(node));
    }

    System.Collections.IEnumerator NotifyUnloadedNextFrame(IOctreeNode node)
    {
        yield return null; // 다음 프레임
        _scheduler?.NotifyUnloaded(node);
    }

    // 카메라 거리 계산: 스케줄러가 카메라를 알고 있다고 가정
    float ComputeCameraDistanceSqr(IOctreeNode node)
    {
        if (_scheduler == null || _scheduler.Camera == null) return float.MaxValue;

        var camPos = _scheduler.Camera.transform.position;
        var center = node.Bounds.center;
        if (worldTransform != null)
            center = worldTransform.TransformPoint(center);

        return (center - camPos).sqrMagnitude;
    }


    // 활성 노드 목록 변경 시: 렌더 순서 갱신
    void OnActiveNodesChanged(IReadOnlyList<IOctreeNode> nodes)
    {
        if (gpuRenderer == null) return;
        if (nodes == null || nodes.Count == 0)
        {
            if (IsMainThread()) gpuRenderer.SetDrawOrder(null);
            else _ = RunOnMainThreadAsync(() => gpuRenderer.SetDrawOrder(null));
            return;
        }

        // 1) 정렬용 임시 리스트 복사
        var sorted = new List<IOctreeNode>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
            sorted.Add(nodes[i]);

        // 2) 카메라 거리 기반 정렬
        // FrontToBack: 오름차순 (가까운 것 먼저)
        // BackToFront: 내림차순 (먼 것 먼저)
        if (drawSortMode == DrawSortMode.FrontToBack)
        {
            sorted.Sort((a, b) =>
            {
                float da = ComputeCameraDistanceSqr(a);
                float db = ComputeCameraDistanceSqr(b);
                return da.CompareTo(db); // 가까운 것 먼저
            });
        }
        else // BackToFront
        {
            sorted.Sort((a, b) =>
            {
                float da = ComputeCameraDistanceSqr(a);
                float db = ComputeCameraDistanceSqr(b);
                return db.CompareTo(da); // 먼 것 먼저
            });
        }

        // 3) 정렬된 ID 배열로 변환
        var order = new List<int>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
            order.Add(sorted[i].NodeId);

        // 4) 렌더러에 적용
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
        if (node == null || node.IsLoaded) return;

        // 노드에 포인트가 없는 경우 스킵
        var ids = node.PointIds;
        int count = ids?.Count ?? 0;
        if (count <= 0) return;

        // 필요한 포인트만 부분 로드
        var data = await _subloader.LoadPointsAsync(ids.ToArray());
        var positions = data.positions;
        var colors = data.colors;

        // voxel binning으로 노드 내부 평균 색(상위 LOD용 대표색) 생성
        var grid = new int3(8, 8, 8); // 필요 시 레벨별로 4/8/16 등 조정
        var avgColors = BuildVoxelAveragedColors(positions, positions, colors, node.Bounds, grid);

        // 정규화 적용
        if (normalizeToOrigin)
        {
            for (int i = 0; i < data.pointCount; i++) data.positions[i] -= _normalizeCenter;
        }

        // GPU 업로드
        await RunOnMainThreadAsync(() =>
        {
            if (gpuRenderer == null) return;
            gpuRenderer.AddOrUpdateNode(node.NodeId, data.positions, avgColors, node.Bounds);
        });

        node.IsLoaded = true;

        // posCache 보강(분할 시 재사용)
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
