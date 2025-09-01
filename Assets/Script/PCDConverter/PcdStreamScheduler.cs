using System;
using System.Collections.Generic;
using UnityEngine;

public interface IOctreeNode
{
    int NodeId { get; }
    Bounds Bounds { get; }
    int Level { get; }

    // 포인트 수 추정/실수 모두 허용. 로드 전에는 추정치, 로드 후에는 실제값으로 업데이트 가능.
    int EstimatedPointCount { get; }

    // 상태
    bool IsRequested { get; set; }   // IO 요청 중
    bool IsLoaded { get; }           // GPU 버퍼까지 준비 완료
    bool IsActive { get; set; }      // 현재 프레임 렌더 활성 상태

    // 트리
    bool HasChildren { get; }
    IReadOnlyList<IOctreeNode> Children { get; }
    IOctreeNode Parent { get; }
}

public sealed class PcdStreamScheduler
{
    // 외부 연결 파라미터
    public Camera Camera;
    public Transform WorldTransform; // 포인트가 로컬 기준이면 해당 트랜스폼을 넘겨 월드변환용

    // 예산/정책
    public int PointBudget = 5_000_000;
    public int MaxLoadsPerFrame = 2;
    public int MaxUnloadsPerFrame = 4;
    public float ScreenErrorTarget = 2.0f; // 픽셀 단위 허용 오차(작을수록 고해상도 로드 유도)
    public float HysteresisFactor = 1.2f;  // LOD 들쭉날쭉 방지(하향 전환은 느슨하게)
    public float DistanceFalloff = 1.0f;   // 거리 감쇠 가중치
    public float PriorityBoostLoadedParent = 1.5f; // 부모가 이미 로드된 자식의 우선순위 가중
    public float ParentKeepBudgetFraction = 0.9f;

    // 콜백(외부 컨트롤러가 구현)
    public Action<IOctreeNode> RequestLoad;     // IO + GPU 업로드 트리거(비동기)
    public Action<IOctreeNode> RequestUnload;   // GPU 언로드 트리거
    public Action<IReadOnlyList<IOctreeNode>> OnActiveNodesChanged; // 렌더 활성 노드 리스트 전달

    // 루트 노드 세트(멀티 루트 지원)
    readonly List<IOctreeNode> _roots = new();

    // 내부 상태
    readonly List<IOctreeNode> _visibleCandidates = new(1024);
    readonly List<ScoredNode> _scored = new(1024);
    readonly HashSet<int> _activeSet = new();
    readonly List<IOctreeNode> _activeList = new(1024);

    // 프러스텀
    Plane[] _frustum = new Plane[6];

    struct ScoredNode
    {
        public IOctreeNode Node;
        public float Score;
        public float ScreenError;
        public int EstimatedPoints;
    }

    public void Clear()
    {
        _roots.Clear();
        _visibleCandidates.Clear();
        _scored.Clear();
        _activeSet.Clear();
        _activeList.Clear();
    }

    public void AddRoot(IOctreeNode root)
    {
        if (root != null) _roots.Add(root);
    }

    public void RemoveRoot(IOctreeNode root)
    {
        if (root == null) return;
        _roots.Remove(root);
    }

    public void Tick()
    {
        if (Camera == null) return;

        // 1) 프러스텀 구축
        GeometryUtility.CalculateFrustumPlanes(Camera, _frustum);

        // 2) 후보 수집(루트부터 내려가며 가시/근접 노드만 추림)
        _visibleCandidates.Clear();
        foreach (var r in _roots)
        {
            CollectVisibleCandidates(r);
        }

        // 3) 점수 계산
        _scored.Clear();
        foreach (var n in _visibleCandidates)
        {
            var s = ScoreNode(n, out float screenError);
            if (s <= 0f) continue;
            _scored.Add(new ScoredNode
            {
                Node = n,
                Score = s,
                ScreenError = screenError,
                EstimatedPoints = Math.Max(1, n.EstimatedPointCount)
            });
        }

        // 4) 우선순위 정렬(높은 점수 우선)
        _scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        // 5) 예산 내 활성 노드 선택 및 로드/언로드 결정을 수행
        SelectAndDispatch();
    }

    void CollectVisibleCandidates(IOctreeNode node)
    {
        // 프러스텀 테스트
        if (!IntersectsFrustum(node.Bounds)) return;

        _visibleCandidates.Add(node);

        // 화면오차 기준으로 더 세밀해야 할 가능성이 크면 자식들도 후보에 포함
        if (node.HasChildren)
        {
            // 자식 박스가 더 작으니, 근접/화면오차상 세밀화될 가능성 높음 → 후보에 미리 넣어 점수화
            foreach (var c in node.Children)
            {
                if (c != null) CollectVisibleCandidates(c);
            }
        }
    }

    bool IntersectsFrustum(Bounds b)
    {
        var bounds = b;
        if (WorldTransform != null)
        {
            // 로컬→월드 변환된 AABB 근사: AABB를 OBB로 변환 후 보수적으로 AABB화
            // 간단 구현: 8코너를 변환해 새로운 AABB 생성
            var min = bounds.min; var max = bounds.max;
            Vector3[] corners = new Vector3[8] {
                new(min.x, min.y, min.z),
                new(max.x, min.y, min.z),
                new(min.x, max.y, min.z),
                new(max.x, max.y, min.z),
                new(min.x, min.y, max.z),
                new(max.x, min.y, max.z),
                new(min.x, max.y, max.z),
                new(max.x, max.y, max.z),
            };
            var T = WorldTransform.localToWorldMatrix;
            var wmin = (Vector3)T.MultiplyPoint3x4(corners[0]);
            var wmax = wmin;
            for (int i = 1; i < 8; i++)
            {
                var w = (Vector3)T.MultiplyPoint3x4(corners[i]);
                wmin = Vector3.Min(wmin, w);
                wmax = Vector3.Max(wmax, w);
            }
            bounds.SetMinMax(wmin, wmax);
        }
        for (int i = 0; i < 6; i++)
        {
            var plane = _frustum[i];
            // Bounds가 plane 바깥 완전히 있는지 검사
            var n = bounds.ClosestPoint(-plane.normal * plane.distance);
            // 간단화: Unity의 테스트 함수 사용
        }
        // Unity 제공 함수로 대체
        return GeometryUtility.TestPlanesAABB(_frustum, bounds);
    }

    float ScoreNode(IOctreeNode node, out float screenError)
    {
        // 카메라 거리, 스크린 투영 크기 기반 간이 스코어
        var center = node.Bounds.center;
        if (WorldTransform != null) center = WorldTransform.TransformPoint(center);

        var camPos = Camera.transform.position;
        float dist = Mathf.Max(0.001f, Vector3.Distance(camPos, center));

        // 화면오차 근사: 박스의 스크린 픽셀 높이 추정
        var size = node.Bounds.size;
        if (WorldTransform != null)
        {
            // 대략적 스케일 반영: 3축 평균 스케일
            var s = WorldTransform.lossyScale;
            size = new Vector3(size.x * Mathf.Abs(s.x), size.y * Mathf.Abs(s.y), size.z * Mathf.Abs(s.z));
        }
        float maxExtent = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        float fovRad = Camera.fieldOfView * Mathf.Deg2Rad;
        float projected = (maxExtent / dist) * (Camera.pixelHeight / (2f * Mathf.Tan(fovRad * 0.5f)));
        screenError = projected; // 단순화: 화면상 커버 픽셀

        // 목표 스크린오차 대비 부족하면(즉, 더 디테일 필요) 가중치를 크게
        float lodNeed = screenError / Mathf.Max(1e-3f, ScreenErrorTarget);

        // 거리 감쇠
        float distWeight = 1f / Mathf.Pow(1f + dist, DistanceFalloff);

        // 부모가 로드되어 있으면 하위 자식에 부스팅(시각적 균질성)
        float parentBoost = 1f;
        if (node.Parent != null && node.Parent.IsLoaded)
            parentBoost = PriorityBoostLoadedParent;

        // 노드 포인트 수가 크면 비용이 크므로 약간의 페널티
        float costPenalty = 1f / Mathf.Sqrt(Mathf.Max(1, node.EstimatedPointCount));

        // 최종 점수
        float score = lodNeed * distWeight * parentBoost * costPenalty;

        // 이미 충분히 작은 경우(즉, 스크린오차가 타깃보다 더 작음) 약한 점수로 처리해 로드 우선순위 낮춤
        if (lodNeed < 1f)
        {
            score *= 0.25f;
        }
        return score;
    }

    void SelectAndDispatch()
    {
        // 1) 현재 활성 노드 집계
        _activeList.Clear();
        int activePoints = 0;

        // 먼저 이미 로드된 노드들을 후보 점수에 따라 유지할지 결정
        HashSet<int> keepLoaded = new();
        foreach (var sc in _scored)
        {
            if (sc.Node.IsLoaded)
            {
                // 히스테리시스: 스크린오차가 충분히 낮아도 바로 버리지 않음
                bool keep = sc.ScreenError >= (ScreenErrorTarget / HysteresisFactor);
                if (keep)
                {
                    keepLoaded.Add(sc.Node.NodeId);
                }
            }
        }

        // 2) 높은 점수부터 예산 내로 활성/로드 요청
        int loadsThisFrame = 0;
        foreach (var sc in _scored)
        {
            var n = sc.Node;

            bool allowParentOverlay = (activePoints < (int)(PointBudget * ParentKeepBudgetFraction));

            // 프레임 예산 확인
            if (activePoints + sc.EstimatedPoints > PointBudget)
            {
                // 예산을 넘는다면, 이미 로드된 노드 중 낮은 점수 대상을 언로드 후보로 보낼 수도 있음
                continue;
            }

            if (n.IsLoaded)
            {
                // 유지 조건(keepLoaded) 검사
                if (!keepLoaded.Contains(n.NodeId))
                {
                    if (!allowParentOverlay) continue;
                }

                // 활성화
                MarkActive(n, ref activePoints);
            }
            else
            {
                // 아직 미로드 → 로드 요청
                if (!n.IsRequested && loadsThisFrame < MaxLoadsPerFrame)
                {
                    n.IsRequested = true;
                    RequestLoad?.Invoke(n);
                    loadsThisFrame++;
                }
            }
        }

        // 3) 활성 셋 업데이트 및 콜백
        RebuildActiveList();
        OnActiveNodesChanged?.Invoke(_activeList);

        // 4) 언로드 정책: 예산 초과 또는 프러스텀 이탈/점수 낮은 노드 언로드
        int unloadsThisFrame = 0;
        var toRemove = new List<int>();
        // 현재 활성 리스트를 해시로 만들어 O(1) 포함 체크
        var visibleNow = new HashSet<int>(_activeList.Count);
        for (int i = 0; i < _activeList.Count; i++)
            visibleNow.Add(_activeList[i].NodeId);

        // 스냅샷을 만들어 원본 HashSet을 열거하지 않음
        var activeSnapshot = new List<int>(_activeSet);

        for (int s = 0; s < activeSnapshot.Count; s++)
        {
            var id = activeSnapshot[s];

            bool stillVisible = visibleNow.Contains(id);
            if (!stillVisible && unloadsThisFrame < MaxUnloadsPerFrame)
            {
                var node = FindNodeByIdInScoredOrRoots(id);
                if (node != null && node.IsLoaded)
                {
                    RequestUnload?.Invoke(node);
                    unloadsThisFrame++;
                    toRemove.Add(id);
                }
            }
        }
        // 활성 집합에서 제거
        foreach (var id in toRemove) _activeSet.Remove(id);
    }

    void MarkActive(IOctreeNode n, ref int activePoints)
    {
        if (!_activeSet.Contains(n.NodeId))
        {
            _activeSet.Add(n.NodeId);
        }
        n.IsActive = true;
        activePoints += Math.Max(1, n.EstimatedPointCount);
    }

    void RebuildActiveList()
    {
        _activeList.Clear();
        // 현재 프레임에 활성으로 표시된 것만 수집
        // 기준: _activeSet
        foreach (var sc in _scored)
        {
            if (_activeSet.Contains(sc.Node.NodeId))
            {
                _activeList.Add(sc.Node);
                sc.Node.IsActive = true;
            }
            else
            {
                sc.Node.IsActive = false;
            }
        }
        // 만약 스코어링에 없지만 활성셋에 남아 있는 노드가 있다면(경계 케이스),
        // 루트/트리에서 찾아 리스트를 유지할 수 있으나, 여기서는 보수적으로 제외.
    }

    IOctreeNode FindNodeByIdInScoredOrRoots(int id)
    {
        for (int i = 0; i < _scored.Count; i++)
            if (_scored[i].Node.NodeId == id) return _scored[i].Node;

        // 간단한 DFS로 루트 트리에서 탐색(빈도는 낮아야 함)
        foreach (var r in _roots)
        {
            var n = DfsFind(r, id);
            if (n != null) return n;
        }
        return null;
    }

    IOctreeNode DfsFind(IOctreeNode cur, int id)
    {
        if (cur.NodeId == id) return cur;
        if (!cur.HasChildren) return null;
        foreach (var c in cur.Children)
        {
            var found = DfsFind(c, id);
            if (found != null) return found;
        }
        return null;
    }

    // 외부에서 로드/언로드 완료 후 상태 반영 시 호출(선택)
    public void NotifyLoaded(IOctreeNode node)
    {
        // 외부에서 node.IsRequested=false; node.IsLoaded=true; 처리를 한 뒤 호출 가능
        // 여기서는 별도 내부 처리 없음
    }

    public void NotifyUnloaded(IOctreeNode node)
    {
        // 외부에서 node.IsLoaded=false; 처리 후 호출 가능
        // 활성 집합에서도 제거
        _activeSet.Remove(node.NodeId);
    }

    public IReadOnlyList<IOctreeNode> ActiveNodes => _activeList;
}
