using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

public sealed class PcdOctree
{
    // 노드 정의
    public sealed class Node
    {
        public int NodeId;                 // 전역 고유 ID
        public int Level;                  // 0이 루트
        public Bounds Bounds;              // 월드(또는 로컬) 좌표계 AABB
        public List<int> PointIds;         // 파일 내 포인트 인덱스(복제 없음)
        public Node[] Children;            // 8개 또는 null
        public bool IsLeaf => Children == null;

        // 렌더/스트리밍 상태 플래그(외부 스케줄러가 사용)
        public bool IsLoaded;              // GPU에 업로드 완료
        public bool IsRequested;           // IO/디코드 요청 중

        public int EstimatedPointCount => PointIds?.Count ?? 0;

        public void ClearPoints()
        {
            PointIds?.Clear();
        }
    }

    public Node Root { get; private set; }
    public int MaxDepth { get; private set; } = 8;         // 필요 시 조정
    public int MinPointsPerNode { get; private set; } = 4096;
    public int MaxPointsPerNode { get; private set; } = 200_000;

    // NodeId 발급
    int _nextId;

    // 생성자 정책 파라미터
    public struct BuildParams
    {
        public int maxDepth;
        public int minPointsPerNode;
        public int maxPointsPerNode;
    }

    public PcdOctree() { }

    public void Configure(BuildParams p)
    {
        if (p.maxDepth > 0) MaxDepth = p.maxDepth;
        if (p.minPointsPerNode > 0) MinPointsPerNode = p.minPointsPerNode;
        if (p.maxPointsPerNode > 0) MaxPointsPerNode = p.maxPointsPerNode;
    }

    // 초기 빌드: 샘플된 포인트 인덱스 + 해당 포인트들의 좌표로 루트 AABB 계산
    // positionsGetter는 pointId -> Vector3를 반환(예: 부분 디코드된 샘플 배열/콜백)
    public Node BuildInitial(IReadOnlyList<int> samplePointIds, Func<int, Vector3> positionsGetter)
    {
        if (samplePointIds == null || samplePointIds.Count == 0)
            throw new ArgumentException("samplePointIds is null/empty");

        _nextId = 0;

        var aabb = ComputeBoundsFromIndices(samplePointIds, positionsGetter);

        Root = new Node
        {
            NodeId = _nextId++,
            Level = 0,
            Bounds = aabb,
            PointIds = new List<int>(samplePointIds),
            Children = null,
            IsLoaded = false,
            IsRequested = false
        };

        return Root;
    }

    // 루트 외에 상위 레벨에서 더 많은 포인트 인덱스를 추가해 루트 밀도를 보강할 수도 있음
    public void AppendToRoot(IReadOnlyList<int> morePointIds)
    {
        if (Root == null) throw new InvalidOperationException("Octree not built");
        if (morePointIds == null || morePointIds.Count == 0) return;
        Root.PointIds.AddRange(morePointIds);
        // Bounds는 루트에서 고정. 필요 시 외부에서 Refit 호출
    }

    // 옥트리 리핏: 현재 할당된 포인트로 Bounds 재계산(필요 시)
    public void Refit(Func<int, Vector3> positionsGetter)
    {
        if (Root == null) return;
        RefitRecursive(Root, positionsGetter);
    }

    void RefitRecursive(Node n, Func<int, Vector3> posGet)
    {
        if (n.IsLeaf)
        {
            if (n.PointIds != null && n.PointIds.Count > 0)
                n.Bounds = ComputeBoundsFromIndices(n.PointIds, posGet);
            return;
        }

        // 자식부터
        var bSet = false;
        Bounds b = default;
        for (int i = 0; i < 8; i++)
        {
            var c = n.Children[i];
            if (c == null) continue;
            RefitRecursive(c, posGet);

            if (!bSet)
            {
                b = c.Bounds;
                bSet = true;
            }
            else
            {
                b.Encapsulate(c.Bounds);
            }
        }
        if (bSet) n.Bounds = b;
    }

    // 필요 시 특정 노드를 8분할하고, 자식에 포인트 인덱스를 분배
    // 반환: 생성된 자식 배열(이미 있으면 기존 참조 반환)
    public Node[] SubdivideOnDemand(Node node, Func<int, Vector3> positionsGetter)
    {
        if (node == null) throw new ArgumentNullException(nameof(node));
        if (!node.IsLeaf) return node.Children; // 이미 분할됨
        if (node.Level + 1 >= MaxDepth) return null; // 더 못 내려감

        var ids = node.PointIds;
        if (ids == null || ids.Count == 0) return null;

        // 너무 적으면 분할 의미 없음
        if (ids.Count < MinPointsPerNode) return null;

        // 자식 8개 Bounds 계산
        var childBounds = SplitBoundsIntoOctants(node.Bounds);

        // 자식 노드 생성
        var children = new Node[8];
        for (int i = 0; i < 8; i++)
        {
            children[i] = new Node
            {
                NodeId = _nextId++,
                Level = node.Level + 1,
                Bounds = childBounds[i],
                PointIds = new List<int>(Mathf.Min(ids.Count / 8 + 64, MaxPointsPerNode)),
                Children = null
            };
        }

        // 포인트 인덱스를 자식에 분배
        for (int k = 0; k < ids.Count; k++)
        {
            int pid = ids[k];
            var p = positionsGetter(pid);
            int ci = Classify(childBounds, p);
            if (ci >= 0)
            {
                var child = children[ci];
                if (child.PointIds.Count < MaxPointsPerNode)
                    child.PointIds.Add(pid);
                // MaxPointsPerNode 초과 시 해당 노드의 샘플링/절삭은 외부 전략에 위임
            }
        }

        // 자식이 모두 비면 분할 취소
        bool any = false;
        for (int i = 0; i < 8; i++)
        {
            if (children[i].PointIds.Count > 0)
            {
                any = true;
            }
        }
        if (!any) return null;

        node.Children = children;

        // 메모리 절약: 부모 포인트는 해제(부모는 LOD 대표 샘플만 유지하고 싶다면 외부 정책으로 축소)
        node.PointIds = null;

        return node.Children;
    }

    // LOD/프러스텀 기반 선택: 예산 내에서 그릴 노드 반환
    // screenErrorFunc(nodeBounds, camera) -> 에러 값(작을수록 높은 LOD 필요)
    // targetBudget: 최대 점 개수
    public void SelectVisible(
        Camera cam,
        Func<Bounds, Camera, float> screenErrorFunc,
        int targetBudget,
        List<Node> outNodesToDraw,
        Func<Node, int> effectivePointCountGetter = null)
    {
        if (Root == null || cam == null) return;
        outNodesToDraw.Clear();

        var frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);
        var open = new PriorityQueue<Node, float>(); // 작은 에러(가까운/큰) 우선

        // 루트부터
        if (GeometryUtility.TestPlanesAABB(frustumPlanes, Root.Bounds))
        {
            float e0 = screenErrorFunc(Root.Bounds, cam);
            open.Enqueue(Root, e0);
        }

        int budget = 0;
        var temp = new List<Node>(64);

        while (open.Count > 0)
        {
            open.TryDequeue(out var node, out var err);

            // 예산 검사: 이 노드의 비용
            int nodePoints = effectivePointCountGetter != null
                ? effectivePointCountGetter(node)
                : node.EstimatedPointCount;

            if (nodePoints <= 0)
            {
                // 포인트가 없으면 자식이 있을 수 있으므로 확장 시도
                if (!node.IsLeaf)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        var c = node.Children[i];
                        if (c == null) continue;
                        if (!GeometryUtility.TestPlanesAABB(frustumPlanes, c.Bounds)) continue;
                        open.Enqueue(c, screenErrorFunc(c.Bounds, cam));
                    }
                }
                continue;
            }

            // 자식이 있고 확대(세밀화)가 합리적이면 큐에 자식 추가
            bool shouldRefine = !node.IsLeaf && ShouldRefineNode(node, cam, err);

            if (shouldRefine)
            {
                for (int i = 0; i < 8; i++)
                {
                    var c = node.Children[i];
                    if (c == null) continue;
                    if (!GeometryUtility.TestPlanesAABB(frustumPlanes, c.Bounds)) continue;
                    float ce = screenErrorFunc(c.Bounds, cam);
                    open.Enqueue(c, ce);
                }
                continue;
            }

            // 이 노드를 선택
            if (budget + nodePoints > targetBudget)
            {
                // 예산 초과: 더 이상 선택하지 않고 종료
                break;
            }

            outNodesToDraw.Add(node);
            budget += nodePoints;
        }
    }

    // 노드 세밀화 기준(간단): 화면 에러 임계값 비교 + 거리/레벨 가중
    public Func<float> GetDefaultScreenErrorThreshold = () => 2.0f;

    bool ShouldRefineNode(Node node, Camera cam, float nodeError)
    {
        float th = GetDefaultScreenErrorThreshold();
        // 더 작은 에러일수록 세밀화 필요도가 높다고 가정할 수도 있으나,
        // 일반적으로는 "에러가 임계값보다 크면 더 높은 LOD가 필요"로 사용
        // 여기서는 Potree 유사하게 "화면 투영 크기가 충분히 크면 refine"으로 해석
        // 즉, nodeError가 임계치보다 작을수록 가까워 화면상 크게 보임 → refine
        return nodeError < th;
    }

    // 8분할된 Bounds 계산
    static Bounds[] SplitBoundsIntoOctants(Bounds b)
    {
        var c = b.center;
        var e = b.extents * 0.5f; // 자식은 절반 크기
        var child = new Bounds[8];

        // 옥트리 인덱싱: (x:0/1, y:0/1, z:0/1)
        // 0:(-,-,-) 1:(+,-,-) 2:(-,+,-) 3:(+,+,-) 4:(-,-,+) 5:(+,-,+) 6:(-,+,+) 7:(+,+,+)
        int idx = 0;
        for (int xi = -1; xi <= 1; xi += 2)
            for (int yi = -1; yi <= 1; yi += 2)
                for (int zi = -1; zi <= 1; zi += 2)
                {
                    var cc = new Vector3(c.x + e.x * xi, c.y + e.y * yi, c.z + e.z * zi);
                    child[idx++] = new Bounds(cc, e * 2f);
                }

        return child;
    }

    // 포인트 좌표가 어느 자식 Bounds에 속하는지
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Classify(Bounds[] children, in Vector3 p)
    {
        // 최악의 경우 8개 전부 검사
        for (int i = 0; i < 8; i++)
        {
            if (children[i].Contains(p)) return i;
        }
        // 경계 부동소수 오차로 Contains가 false일 수 있으므로,
        // 가까운 센터 방향으로 분류하는 fallback:
        var c0 = children[0].center;
        int best = 0;
        float bestD = (p - c0).sqrMagnitude;
        for (int i = 1; i < 8; i++)
        {
            float d = (p - children[i].center).sqrMagnitude;
            if (d < bestD) { best = i; bestD = d; }
        }
        return best;
    }

    // 샘플 인덱스들의 AABB 계산
    static Bounds ComputeBoundsFromIndices(IReadOnlyList<int> ids, Func<int, Vector3> posGet)
    {
        if (ids == null || ids.Count == 0)
            return new Bounds(Vector3.zero, Vector3.zero);

        // 기존 코드
        // var v0 = posGet(ids);

        // 수정된 코드
        var v0 = posGet(ids[0]);
        Vector3 minV = v0, maxV = v0;

        for (int i = 1; i < ids.Count; i++)
        {
            var v = posGet(ids[i]);
            if (v.x < minV.x) minV.x = v.x; if (v.y < minV.y) minV.y = v.y; if (v.z < minV.z) minV.z = v.z;
            if (v.x > maxV.x) maxV.x = v.x; if (v.y > maxV.y) maxV.y = v.y; if (v.z > maxV.z) maxV.z = v.z;
        }

        var b = new Bounds();
        b.SetMinMax(minV, maxV);
        return b;
    }

    // 간단한 우선순위 큐(.NET 6 이상 PriorityQueue 사용 대체용) — Unity 옛 런타임 대비
    // Unity 2021+에선 System.Collections.Generic.PriorityQueue 사용 가능.
    // 하위 호환을 위해 간단 힙 구현 포함.
    sealed class PriorityQueue<TItem, TPriority> where TPriority : IComparable<TPriority>
    {
        List<(TItem item, TPriority pri)> _heap = new();

        public int Count => _heap.Count;

        public void Enqueue(TItem item, TPriority pri)
        {
            _heap.Add((item, pri));
            HeapifyUp(_heap.Count - 1);
        }

        public bool TryDequeue(out TItem item, out TPriority pri)
        {
            if (_heap.Count == 0) { item = default; pri = default; return false; }
            (item, pri) = _heap[0];
            var last = _heap[_heap.Count - 1];
            _heap.RemoveAt(_heap.Count - 1);
            if (_heap.Count > 0)
            {
                _heap[0] = last;
                HeapifyDown(0);
            }
            return true;
        }

        void HeapifyUp(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_heap[i].pri.CompareTo(_heap[p].pri) >= 0) break;
                (_heap[i], _heap[p]) = (_heap[p], _heap[i]);
                i = p;
            }
        }

        void HeapifyDown(int i)
        {
            while (true)
            {
                int l = (i << 1) + 1;
                int r = l + 1;
                int smallest = i;
                if (l < _heap.Count && _heap[l].pri.CompareTo(_heap[smallest].pri) < 0) smallest = l;
                if (r < _heap.Count && _heap[r].pri.CompareTo(_heap[smallest].pri) < 0) smallest = r;
                if (smallest == i) break;
                (_heap[i], _heap[smallest]) = (_heap[smallest], _heap[i]);
                i = smallest;
            }
        }
    }
}
