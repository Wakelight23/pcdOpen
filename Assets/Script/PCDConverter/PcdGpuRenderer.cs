/*using System;
using UnityEngine;

[ExecuteAlways]
public class PcdGpuRenderer : MonoBehaviour
{
    [Header("Render")]
    public Material pointMaterial;      // PcdPoint.shader 기반
    [Range(0.001f, 0.2f)]
    public float pointSize = 0.02f;     // 현재 셰이더는 크기 제어 없음(간단 렌더)
    public bool useColors = true;

    [Header("Stats")]
    public int pointCount;

    ComputeBuffer posBuffer;
    ComputeBuffer colBuffer;

    MaterialPropertyBlock _mpb;

    public void UploadData(Vector3[] positions, Color32[] colors)
    {
#if UNITY_EDITOR
        if (!UnityEditor.EditorApplication.isPlaying)
        {
            // 에디터모드 OnRenderObject 등에서 호출될 수 있으니 스킵
        }
#endif
        // 간단한 메인 스레드 판별: Unity API 접근 성공 여부로 대신
        try { var _ = Time.frameCount; }
        catch (UnityException)
        {
            Debug.LogError("[PCD] UploadData called off main thread!");
            throw;
        }

        ReleaseBuffers();

        if (positions == null || positions.Length == 0)
            throw new ArgumentException("positions is null/empty");

        pointCount = positions.Length;

        posBuffer = new ComputeBuffer(pointCount, sizeof(float) * 3, ComputeBufferType.Structured);
        posBuffer.SetData(positions);

        if (useColors && colors != null && colors.Length == pointCount)
        {
            colBuffer = new ComputeBuffer(pointCount, sizeof(byte) * 4, ComputeBufferType.Structured);
            colBuffer.SetData(colors);
        }

        if (pointMaterial == null)
            Debug.LogWarning("pointMaterial is null. Assign PcdPoint shader material.");
    }

    public void DisposeRenderer()
    {
        ReleaseBuffers();
    }

    void OnDisable()
    {
        ReleaseBuffers();
    }

    void OnDestroy()
    {
        ReleaseBuffers();
    }

    void ReleaseBuffers()
    {
        if (posBuffer != null) { posBuffer.Release(); posBuffer = null; }
        if (colBuffer != null) { colBuffer.Release(); colBuffer = null; }
        pointCount = 0;
    }

    void OnRenderObject()
    {
        // if (GraphicsSettings.currentRenderPipeline != null) return; // SRP는 별도 경로
        if (posBuffer == null || pointMaterial == null || pointCount <= 0) return;

        // 1) 버퍼/상수 바인딩
        pointMaterial.SetBuffer("_Positions", posBuffer);
        if (colBuffer != null) pointMaterial.SetBuffer("_Colors", colBuffer);
        pointMaterial.SetInt("_HasColor", (useColors && colBuffer != null) ? 1 : 0);
        pointMaterial.SetFloat("_PointSize", pointSize);

        // 2) Transform 매트릭스를 머티리얼에 직접 주입
        pointMaterial.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);

        // 3) 패스 설정 후 즉시 드로우
        if (!pointMaterial.SetPass(0)) return;
        Graphics.DrawProceduralNow(MeshTopology.Points, pointCount, 1);
    }
}
*/

using System;
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class PcdGpuRenderer : MonoBehaviour
{
    [Header("Render")]
    public Material pointMaterial;               // PcdPoint.shader 기반
    [Range(0.001f, 0.2f)]
    public float pointSize = 0.02f;
    public bool useColors = true;

    [Header("Stats (overall)")]
    public int totalPointCount;
    public int activeNodeCount;

    // 노드 버퍼 구조
    class NodeBuffers
    {
        public ComputeBuffer pos;
        public ComputeBuffer col;
        public int count;
        public Bounds bounds; // 선택: 컬링/통계용
    }

    // 노드별 버퍼 보관
    readonly Dictionary<int, NodeBuffers> _nodes = new Dictionary<int, NodeBuffers>();

    // 렌더 순서(스케줄러/컨트롤러가 매 프레임 업데이트 가능)
    readonly List<int> _drawOrder = new List<int>();

    // 임시: 메인스레드 체크
    static bool IsMainThread()
    {
        try { var _ = Time.frameCount; return true; }
        catch (UnityException) { return false; }
    }

    // ====== 외부 API ======

    // 노드 업서트: 동일 nodeId면 교체
    public void AddOrUpdateNode(int nodeId, Vector3[] positions, Color32[] colors, Bounds nodeBounds = default)
    {
        if (!IsMainThread())
        {
            Debug.LogError("[PcdGpuRenderer] AddOrUpdateNode must be called on main thread.");
            throw new UnityException("AddOrUpdateNode called off main thread");
        }
        if (positions == null || positions.Length == 0)
            throw new ArgumentException("positions is null/empty");

        // 기존 노드 제거(있다면)
        RemoveNode(nodeId);

        var nb = new NodeBuffers
        {
            count = positions.Length,
            bounds = nodeBounds
        };

        nb.pos = new ComputeBuffer(nb.count, sizeof(float) * 3, ComputeBufferType.Structured);
        nb.pos.SetData(positions);

        if (useColors && colors != null && colors.Length == nb.count)
        {
            nb.col = new ComputeBuffer(nb.count, sizeof(byte) * 4, ComputeBufferType.Structured);
            nb.col.SetData(colors);
        }

        _nodes[nodeId] = nb;
        _drawOrder.Add(nodeId);

        RecomputeStats();
    }

    // 노드 제거
    public void RemoveNode(int nodeId)
    {
        if (_nodes.TryGetValue(nodeId, out var nb))
        {
            ReleaseNodeBuffers(nb);
            _nodes.Remove(nodeId);
            _drawOrder.Remove(nodeId);
            RecomputeStats();
        }
    }

    // 모든 노드 제거
    public void ClearAllNodes()
    {
        foreach (var kv in _nodes)
            ReleaseNodeBuffers(kv.Value);
        _nodes.Clear();
        _drawOrder.Clear();
        RecomputeStats();
    }

    // 렌더 순서를 외부에서 갱신(예: 가까운 노드 우선)
    public void SetDrawOrder(IList<int> nodeIdsInOrder)
    {
        _drawOrder.Clear();
        if (nodeIdsInOrder != null)
            _drawOrder.AddRange(nodeIdsInOrder);
    }

    // 현재 활성 노드 ID 나열
    public IReadOnlyList<int> GetActiveNodeIds()
    {
        return _drawOrder;
    }

    // 전체 해제(엔트리에서 DisposeRenderer 호출)
    public void DisposeRenderer()
    {
        ClearAllNodes();
    }

    // ====== 내부 구현 ======

    void OnDisable()
    {
        // 에디터 play/off 상관없이 안전 해제
        ClearAllNodes();
    }

    void OnDestroy()
    {
        ClearAllNodes();
    }

    void ReleaseNodeBuffers(NodeBuffers nb)
    {
        if (nb == null) return;
        if (nb.pos != null) { nb.pos.Release(); nb.pos = null; }
        if (nb.col != null) { nb.col.Release(); nb.col = null; }
    }

    void RecomputeStats()
    {
        int total = 0;
        foreach (var nb in _nodes.Values)
            total += nb.count;

        totalPointCount = total;
        activeNodeCount = _nodes.Count;
    }

    // SRP가 아닌 Built-in 렌더 경로용
    void OnRenderObject()
    {
        if (pointMaterial == null) return;
        if (_drawOrder.Count == 0) return;

        // Transform 매트릭스 주입
        pointMaterial.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
        pointMaterial.SetFloat("_PointSize", pointSize);

        // 노드 순회 렌더
        for (int i = 0; i < _drawOrder.Count; i++)
        {
            int nodeId = _drawOrder[i];
            if (!_nodes.TryGetValue(nodeId, out var nb)) continue;
            if (nb == null || nb.count <= 0 || nb.pos == null) continue;

            pointMaterial.SetBuffer("_Positions", nb.pos);

            if (useColors && nb.col != null)
            {
                pointMaterial.SetBuffer("_Colors", nb.col);
                pointMaterial.SetInt("_HasColor", 1);
            }
            else
            {
                pointMaterial.SetInt("_HasColor", 0);
            }

            if (!pointMaterial.SetPass(0)) continue;

            Graphics.DrawProceduralNow(MeshTopology.Points, nb.count, 1);
        }
    }

    // ----- 선택: URP/HDRP 지원 시 ScriptableRenderPass 등 별도 경로 제공 -----
    // public void Render(ScriptableRenderContext context, CommandBuffer cmd) { ... }

    // ====== 유틸 ======

    // 디버그용: 에디터에서 인스펙터 버튼으로 강제 리빌드/정리 등을 할 수 있게
#if UNITY_EDITOR
    [ContextMenu("Debug/Print Stats")]
    void DebugPrint()
    {
        Debug.Log($"[PcdGpuRenderer] nodes={_nodes.Count}, drawOrder={_drawOrder.Count}, totalPoints={totalPointCount}");
    }

    [ContextMenu("Debug/Clear All Nodes")]
    void DebugClear()
    {
        ClearAllNodes();
        Debug.Log("[PcdGpuRenderer] Cleared all nodes.");
    }
#endif
}
