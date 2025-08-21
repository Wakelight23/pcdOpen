/*using System;
using UnityEngine;

[ExecuteAlways]
public class PcdGpuRenderer : MonoBehaviour
{
    [Header("Render")]
    public Material pointMaterial;      // PcdPoint.shader ���
    [Range(0.001f, 0.2f)]
    public float pointSize = 0.02f;     // ���� ���̴��� ũ�� ���� ����(���� ����)
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
            // �����͸�� OnRenderObject ��� ȣ��� �� ������ ��ŵ
        }
#endif
        // ������ ���� ������ �Ǻ�: Unity API ���� ���� ���η� ���
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
        // if (GraphicsSettings.currentRenderPipeline != null) return; // SRP�� ���� ���
        if (posBuffer == null || pointMaterial == null || pointCount <= 0) return;

        // 1) ����/��� ���ε�
        pointMaterial.SetBuffer("_Positions", posBuffer);
        if (colBuffer != null) pointMaterial.SetBuffer("_Colors", colBuffer);
        pointMaterial.SetInt("_HasColor", (useColors && colBuffer != null) ? 1 : 0);
        pointMaterial.SetFloat("_PointSize", pointSize);

        // 2) Transform ��Ʈ������ ��Ƽ���� ���� ����
        pointMaterial.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);

        // 3) �н� ���� �� ��� ��ο�
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
    public Material pointMaterial;               // PcdPoint.shader ���
    [Range(0.001f, 0.2f)]
    public float pointSize = 0.02f;
    public bool useColors = true;

    [Header("Stats (overall)")]
    public int totalPointCount;
    public int activeNodeCount;

    // ��� ���� ����
    class NodeBuffers
    {
        public ComputeBuffer pos;
        public ComputeBuffer col;
        public int count;
        public Bounds bounds; // ����: �ø�/����
    }

    // ��庰 ���� ����
    readonly Dictionary<int, NodeBuffers> _nodes = new Dictionary<int, NodeBuffers>();

    // ���� ����(�����ٷ�/��Ʈ�ѷ��� �� ������ ������Ʈ ����)
    readonly List<int> _drawOrder = new List<int>();

    // �ӽ�: ���ν����� üũ
    static bool IsMainThread()
    {
        try { var _ = Time.frameCount; return true; }
        catch (UnityException) { return false; }
    }

    // ====== �ܺ� API ======

    // ��� ����Ʈ: ���� nodeId�� ��ü
    public void AddOrUpdateNode(int nodeId, Vector3[] positions, Color32[] colors, Bounds nodeBounds = default)
    {
        if (!IsMainThread())
        {
            Debug.LogError("[PcdGpuRenderer] AddOrUpdateNode must be called on main thread.");
            throw new UnityException("AddOrUpdateNode called off main thread");
        }
        if (positions == null || positions.Length == 0)
            throw new ArgumentException("positions is null/empty");

        // ���� ��� ����(�ִٸ�)
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

    // ��� ����
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

    // ��� ��� ����
    public void ClearAllNodes()
    {
        foreach (var kv in _nodes)
            ReleaseNodeBuffers(kv.Value);
        _nodes.Clear();
        _drawOrder.Clear();
        RecomputeStats();
    }

    // ���� ������ �ܺο��� ����(��: ����� ��� �켱)
    public void SetDrawOrder(IList<int> nodeIdsInOrder)
    {
        _drawOrder.Clear();
        if (nodeIdsInOrder != null)
            _drawOrder.AddRange(nodeIdsInOrder);
    }

    // ���� Ȱ�� ��� ID ����
    public IReadOnlyList<int> GetActiveNodeIds()
    {
        return _drawOrder;
    }

    // ��ü ����(��Ʈ������ DisposeRenderer ȣ��)
    public void DisposeRenderer()
    {
        ClearAllNodes();
    }

    // ====== ���� ���� ======

    void OnDisable()
    {
        // ������ play/off ������� ���� ����
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

    // SRP�� �ƴ� Built-in ���� ��ο�
    void OnRenderObject()
    {
        if (pointMaterial == null) return;
        if (_drawOrder.Count == 0) return;

        // Transform ��Ʈ���� ����
        pointMaterial.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
        pointMaterial.SetFloat("_PointSize", pointSize);

        // ��� ��ȸ ����
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

    // ----- ����: URP/HDRP ���� �� ScriptableRenderPass �� ���� ��� ���� -----
    // public void Render(ScriptableRenderContext context, CommandBuffer cmd) { ... }

    // ====== ��ƿ ======

    // ����׿�: �����Ϳ��� �ν����� ��ư���� ���� ������/���� ���� �� �� �ְ�
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
