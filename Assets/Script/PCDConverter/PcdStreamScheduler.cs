using System;
using System.Collections.Generic;
using UnityEngine;

public interface IOctreeNode
{
    int NodeId { get; }
    Bounds Bounds { get; }
    int Level { get; }

    // ����Ʈ �� ����/�Ǽ� ��� ���. �ε� ������ ����ġ, �ε� �Ŀ��� ���������� ������Ʈ ����.
    int EstimatedPointCount { get; }

    // ����
    bool IsRequested { get; set; }   // IO ��û ��
    bool IsLoaded { get; }           // GPU ���۱��� �غ� �Ϸ�
    bool IsActive { get; set; }      // ���� ������ ���� Ȱ�� ����

    // Ʈ��
    bool HasChildren { get; }
    IReadOnlyList<IOctreeNode> Children { get; }
    IOctreeNode Parent { get; }
}

public sealed class PcdStreamScheduler
{
    // �ܺ� ���� �Ķ����
    public Camera Camera;
    public Transform WorldTransform; // ����Ʈ�� ���� �����̸� �ش� Ʈ�������� �Ѱ� ���庯ȯ��

    // ����/��å
    public int PointBudget = 5_000_000;
    public int MaxLoadsPerFrame = 2;
    public int MaxUnloadsPerFrame = 4;
    public float ScreenErrorTarget = 2.0f; // �ȼ� ���� ��� ����(�������� ���ػ� �ε� ����)
    public float HysteresisFactor = 1.2f;  // LOD ���߳��� ����(���� ��ȯ�� �����ϰ�)
    public float DistanceFalloff = 1.0f;   // �Ÿ� ���� ����ġ
    public float PriorityBoostLoadedParent = 1.5f; // �θ� �̹� �ε�� �ڽ��� �켱���� ����
    public float ParentKeepBudgetFraction = 0.9f;

    // �ݹ�(�ܺ� ��Ʈ�ѷ��� ����)
    public Action<IOctreeNode> RequestLoad;     // IO + GPU ���ε� Ʈ����(�񵿱�)
    public Action<IOctreeNode> RequestUnload;   // GPU ��ε� Ʈ����
    public Action<IReadOnlyList<IOctreeNode>> OnActiveNodesChanged; // ���� Ȱ�� ��� ����Ʈ ����

    // ��Ʈ ��� ��Ʈ(��Ƽ ��Ʈ ����)
    readonly List<IOctreeNode> _roots = new();

    // ���� ����
    readonly List<IOctreeNode> _visibleCandidates = new(1024);
    readonly List<ScoredNode> _scored = new(1024);
    readonly HashSet<int> _activeSet = new();
    readonly List<IOctreeNode> _activeList = new(1024);

    // ��������
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

        // 1) �������� ����
        GeometryUtility.CalculateFrustumPlanes(Camera, _frustum);

        // 2) �ĺ� ����(��Ʈ���� �������� ����/���� ��常 �߸�)
        _visibleCandidates.Clear();
        foreach (var r in _roots)
        {
            CollectVisibleCandidates(r);
        }

        // 3) ���� ���
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

        // 4) �켱���� ����(���� ���� �켱)
        _scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        // 5) ���� �� Ȱ�� ��� ���� �� �ε�/��ε� ������ ����
        SelectAndDispatch();
    }

    void CollectVisibleCandidates(IOctreeNode node)
    {
        // �������� �׽�Ʈ
        if (!IntersectsFrustum(node.Bounds)) return;

        _visibleCandidates.Add(node);

        // ȭ����� �������� �� �����ؾ� �� ���ɼ��� ũ�� �ڽĵ鵵 �ĺ��� ����
        if (node.HasChildren)
        {
            // �ڽ� �ڽ��� �� ������, ����/ȭ������� ����ȭ�� ���ɼ� ���� �� �ĺ��� �̸� �־� ����ȭ
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
            // ���á���� ��ȯ�� AABB �ٻ�: AABB�� OBB�� ��ȯ �� ���������� AABBȭ
            // ���� ����: 8�ڳʸ� ��ȯ�� ���ο� AABB ����
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
            // Bounds�� plane �ٱ� ������ �ִ��� �˻�
            var n = bounds.ClosestPoint(-plane.normal * plane.distance);
            // ����ȭ: Unity�� �׽�Ʈ �Լ� ���
        }
        // Unity ���� �Լ��� ��ü
        return GeometryUtility.TestPlanesAABB(_frustum, bounds);
    }

    float ScoreNode(IOctreeNode node, out float screenError)
    {
        // ī�޶� �Ÿ�, ��ũ�� ���� ũ�� ��� ���� ���ھ�
        var center = node.Bounds.center;
        if (WorldTransform != null) center = WorldTransform.TransformPoint(center);

        var camPos = Camera.transform.position;
        float dist = Mathf.Max(0.001f, Vector3.Distance(camPos, center));

        // ȭ����� �ٻ�: �ڽ��� ��ũ�� �ȼ� ���� ����
        var size = node.Bounds.size;
        if (WorldTransform != null)
        {
            // �뷫�� ������ �ݿ�: 3�� ��� ������
            var s = WorldTransform.lossyScale;
            size = new Vector3(size.x * Mathf.Abs(s.x), size.y * Mathf.Abs(s.y), size.z * Mathf.Abs(s.z));
        }
        float maxExtent = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        float fovRad = Camera.fieldOfView * Mathf.Deg2Rad;
        float projected = (maxExtent / dist) * (Camera.pixelHeight / (2f * Mathf.Tan(fovRad * 0.5f)));
        screenError = projected; // �ܼ�ȭ: ȭ��� Ŀ�� �ȼ�

        // ��ǥ ��ũ������ ��� �����ϸ�(��, �� ������ �ʿ�) ����ġ�� ũ��
        float lodNeed = screenError / Mathf.Max(1e-3f, ScreenErrorTarget);

        // �Ÿ� ����
        float distWeight = 1f / Mathf.Pow(1f + dist, DistanceFalloff);

        // �θ� �ε�Ǿ� ������ ���� �ڽĿ� �ν���(�ð��� ������)
        float parentBoost = 1f;
        if (node.Parent != null && node.Parent.IsLoaded)
            parentBoost = PriorityBoostLoadedParent;

        // ��� ����Ʈ ���� ũ�� ����� ũ�Ƿ� �ణ�� ���Ƽ
        float costPenalty = 1f / Mathf.Sqrt(Mathf.Max(1, node.EstimatedPointCount));

        // ���� ����
        float score = lodNeed * distWeight * parentBoost * costPenalty;

        // �̹� ����� ���� ���(��, ��ũ�������� Ÿ�꺸�� �� ����) ���� ������ ó���� �ε� �켱���� ����
        if (lodNeed < 1f)
        {
            score *= 0.25f;
        }
        return score;
    }

    void SelectAndDispatch()
    {
        // 1) ���� Ȱ�� ��� ����
        _activeList.Clear();
        int activePoints = 0;

        // ���� �̹� �ε�� ������ �ĺ� ������ ���� �������� ����
        HashSet<int> keepLoaded = new();
        foreach (var sc in _scored)
        {
            if (sc.Node.IsLoaded)
            {
                // �����׸��ý�: ��ũ�������� ����� ���Ƶ� �ٷ� ������ ����
                bool keep = sc.ScreenError >= (ScreenErrorTarget / HysteresisFactor);
                if (keep)
                {
                    keepLoaded.Add(sc.Node.NodeId);
                }
            }
        }

        // 2) ���� �������� ���� ���� Ȱ��/�ε� ��û
        int loadsThisFrame = 0;
        foreach (var sc in _scored)
        {
            var n = sc.Node;

            bool allowParentOverlay = (activePoints < (int)(PointBudget * ParentKeepBudgetFraction));

            // ������ ���� Ȯ��
            if (activePoints + sc.EstimatedPoints > PointBudget)
            {
                // ������ �Ѵ´ٸ�, �̹� �ε�� ��� �� ���� ���� ����� ��ε� �ĺ��� ���� ���� ����
                continue;
            }

            if (n.IsLoaded)
            {
                // ���� ����(keepLoaded) �˻�
                if (!keepLoaded.Contains(n.NodeId))
                {
                    if (!allowParentOverlay) continue;
                }

                // Ȱ��ȭ
                MarkActive(n, ref activePoints);
            }
            else
            {
                // ���� �̷ε� �� �ε� ��û
                if (!n.IsRequested && loadsThisFrame < MaxLoadsPerFrame)
                {
                    n.IsRequested = true;
                    RequestLoad?.Invoke(n);
                    loadsThisFrame++;
                }
            }
        }

        // 3) Ȱ�� �� ������Ʈ �� �ݹ�
        RebuildActiveList();
        OnActiveNodesChanged?.Invoke(_activeList);

        // 4) ��ε� ��å: ���� �ʰ� �Ǵ� �������� ��Ż/���� ���� ��� ��ε�
        int unloadsThisFrame = 0;
        var toRemove = new List<int>();
        // ���� Ȱ�� ����Ʈ�� �ؽ÷� ����� O(1) ���� üũ
        var visibleNow = new HashSet<int>(_activeList.Count);
        for (int i = 0; i < _activeList.Count; i++)
            visibleNow.Add(_activeList[i].NodeId);

        // �������� ����� ���� HashSet�� �������� ����
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
        // Ȱ�� ���տ��� ����
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
        // ���� �����ӿ� Ȱ������ ǥ�õ� �͸� ����
        // ����: _activeSet
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
        // ���� ���ھ�� ������ Ȱ���¿� ���� �ִ� ��尡 �ִٸ�(��� ���̽�),
        // ��Ʈ/Ʈ������ ã�� ����Ʈ�� ������ �� ������, ���⼭�� ���������� ����.
    }

    IOctreeNode FindNodeByIdInScoredOrRoots(int id)
    {
        for (int i = 0; i < _scored.Count; i++)
            if (_scored[i].Node.NodeId == id) return _scored[i].Node;

        // ������ DFS�� ��Ʈ Ʈ������ Ž��(�󵵴� ���ƾ� ��)
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

    // �ܺο��� �ε�/��ε� �Ϸ� �� ���� �ݿ� �� ȣ��(����)
    public void NotifyLoaded(IOctreeNode node)
    {
        // �ܺο��� node.IsRequested=false; node.IsLoaded=true; ó���� �� �� ȣ�� ����
        // ���⼭�� ���� ���� ó�� ����
    }

    public void NotifyUnloaded(IOctreeNode node)
    {
        // �ܺο��� node.IsLoaded=false; ó�� �� ȣ�� ����
        // Ȱ�� ���տ����� ����
        _activeSet.Remove(node.NodeId);
    }

    public IReadOnlyList<IOctreeNode> ActiveNodes => _activeList;
}
