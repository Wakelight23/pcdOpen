using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public class PcdGpuRenderer : MonoBehaviour
{
    [Header("Render")]
    public Material pointMaterial;               // Custom/PcdBillboardIndirect ���
    [Range(0.5f, 64.0f)]
    public float pointSize = 4.0f;               // �ȼ� ����
    public bool useColors = true;
    [Range(0, 1)] public float softEdge = 0.1f;
    public bool roundMask = true;

    [Header("Stats (overall)")]
    public int totalPointCount;
    public int activeNodeCount;

    public enum PointSizeMode { Fixed, Attenuated, Adaptive }
    [Header("Point Size")]
    public PointSizeMode sizeMode = PointSizeMode.Fixed;
    [Range(0.5f, 64f)] public float minPixelSize = 1.0f;
    [Range(0.5f, 128f)] public float maxPixelSize = 8.0f;
    [Range(0.01f, 10f)] public float attenuationScale = 1.0f;   // 1/d ������ ���
    [Range(0.0f, 16f)] public float adaptiveScale = 0.0f;        // Adaptive ����

    // ��� ���� ����
    class NodeBuffers
    {
        public ComputeBuffer pos;
        public ComputeBuffer col;
        public ComputeBuffer args; // indirect args
        public int count;
        public Bounds bounds;
    }

    readonly Dictionary<int, NodeBuffers> _nodes = new();
    readonly List<int> _drawOrder = new();

    // ���� ���� �޽�
    static Mesh s_quad;
    static readonly uint[] s_argsTemplate = new uint[5]; // indexCountPerInstance, instanceCount, startIndex, baseVertex, startInstance

    // Fade
    readonly Dictionary<int, (float fade, int mode)> _nodeFades = new();
    static readonly int ID_LodFade = Shader.PropertyToID("_LodFade");
    static readonly int ID_DitherFade = Shader.PropertyToID("_DitherFade"); // 0/1

    // splatAccum
    // [SerializeField] Material pointMaterial; // Shader "Custom/PcdSplatAccum"
    public float kernelSharpness = 1.5f;
    public bool gaussianKernel = false;
    static readonly int ID_Positions = Shader.PropertyToID("_Positions");
    static readonly int ID_Colors = Shader.PropertyToID("_Colors");
    static readonly int ID_HasColor = Shader.PropertyToID("_HasColor");
    static readonly int ID_PointSize = Shader.PropertyToID("_PointSize");
    static readonly int ID_KernelSharpness = Shader.PropertyToID("_KernelSharpness");
    static readonly int ID_Gaussian = Shader.PropertyToID("_Gaussian");
    static readonly int ID_L2W = Shader.PropertyToID("_LocalToWorld");

    // ���ν����� üũ
    static bool IsMainThread()
    {
        try { var _ = Time.frameCount; return true; }
        catch (UnityException) { return false; }
    }

    void EnsureQuad()
    {
        if (s_quad != null) return;
        s_quad = new Mesh { name = "Pcd_UnitQuad" };
        var verts = new Vector3[]
        {
            new Vector3(-0.5f, -0.5f, 0),
            new Vector3( 0.5f, -0.5f, 0),
            new Vector3(-0.5f,  0.5f, 0),
            new Vector3( 0.5f,  0.5f, 0),
        };
        var idx = new int[] { 0, 1, 2, 2, 1, 3 };
        s_quad.SetVertices(verts);
        s_quad.SetIndices(idx, MeshTopology.Triangles, 0);
        s_quad.UploadMeshData(true);
    }

    // Fade mode
    public void SetPerNodeLodFade(IList<(int nodeId, float fade, int mode)> items)
    {
        _nodeFades.Clear();
        if (items == null) return;
        for (int i = 0; i < items.Count; i++) _nodeFades[items[i].nodeId] = (items[i].fade, items[i].mode);
    }

    // ====== �ܺ� API ======

    public void AddOrUpdateNode(int nodeId, Vector3[] positions, Color32[] colors, Bounds nodeBounds = default)
    {
        if (!IsMainThread())
        {
            Debug.LogError("[PcdGpuRenderer] AddOrUpdateNode must be called on main thread.");
            throw new UnityException("AddOrUpdateNode called off main thread");
        }
        if (positions == null || positions.Length == 0)
            throw new ArgumentException("positions is null/empty");

        RemoveNode(nodeId);

        var nb = new NodeBuffers
        {
            count = positions.Length,
            bounds = nodeBounds
        };

        // Structured buffer
        nb.pos = new ComputeBuffer(nb.count, sizeof(float) * 3, ComputeBufferType.Structured);
        nb.pos.SetData(positions);

        if (useColors && colors != null && colors.Length == nb.count)
        {
            var packed = new uint[nb.count];
            for (int i = 0; i < nb.count; i++)
            {
                var c = colors[i];
                // 0xFFRRGGBB: DecodeRGB(u)�� ��ġ
                packed[i] = 0xFF000000u | ((uint)c.r << 16) | ((uint)c.g << 8) | (uint)c.b;
            }
            nb.col = new ComputeBuffer(nb.count, sizeof(uint), ComputeBufferType.Structured);
            nb.col.SetData(packed);
        }

        // Indirect args (for quad) : indexCountPerInstance, instanceCount, startIndex, baseVertex, startInstance
        EnsureQuad();
        uint indexCount = (uint)s_quad.GetIndexCount(0);
        nb.args = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
        var args = s_argsTemplate;
        args[0] = indexCount;
        args[1] = (uint)nb.count;
        args[2] = 0;
        args[3] = 0;
        args[4] = 0;
        nb.args.SetData(args);

        _nodes[nodeId] = nb;
        _drawOrder.Add(nodeId);

        RecomputeStats();
    }

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

    public void ClearAllNodes()
    {
        foreach (var kv in _nodes)
            ReleaseNodeBuffers(kv.Value);
        _nodes.Clear();
        _drawOrder.Clear();
        RecomputeStats();
    }

    public void SetDrawOrder(IList<int> nodeIdsInOrder)
    {
        _drawOrder.Clear();
        if (nodeIdsInOrder != null)
            _drawOrder.AddRange(nodeIdsInOrder);
    }

    public IReadOnlyList<int> GetActiveNodeIds() => _drawOrder;

    public void DisposeRenderer()
    {
        ClearAllNodes();
    }

    void OnEnable()
    {
        // RenderSystem ���
        var sys = PcdBillboardRenderSystem.Instance;
        if (sys != null) sys.Register(this);
    }

    void OnDisable()
    {
        var sys = PcdBillboardRenderSystem.Instance;
        if (sys != null) sys.Unregister(this);
        ClearAllNodes();
    }

    void OnDestroy() => ClearAllNodes();


    void ReleaseNodeBuffers(NodeBuffers nb)
    {
        if (nb == null) return;
        if (nb.pos != null) { nb.pos.Release(); nb.pos = null; }
        if (nb.col != null) { nb.col.Release(); nb.col = null; }
        if (nb.args != null) { nb.args.Release(); nb.args = null; }
    }

    void RecomputeStats()
    {
        int total = 0;
        foreach (var nb in _nodes.Values)
            total += nb.count;
        totalPointCount = total;
        activeNodeCount = _nodes.Count;
    }

    // RenderIndirect (SRP��)
    /*public void RenderIndirect(CommandBuffer cmd, Camera cam)
    {
        if (pointMaterial == null) return;
        if (_drawOrder.Count == 0) return;
        if (renderMode != RenderMode.IndirectBillboard) return;

        EnsureQuad();

        // ���� ���
        pointMaterial.SetFloat("_PointSize", pointSize);
        pointMaterial.SetFloat("_SoftEdge", softEdge);
        pointMaterial.SetFloat("_RoundMask", roundMask ? 1f : 0f);
        pointMaterial.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
        pointMaterial.SetInt("_HasColor", 0);

        // Color
        //pointMaterial.SetColor("_Tint", Color.white);
        pointMaterial.SetVector("_DistRange", new Vector2(10.0f, 200f));
        pointMaterial.SetVector("_ColorAtten", new Vector2(0.1f, 0f));

        // ȭ��/��� ����
        // pointMaterial.SetVector("_PcdScreenSize", new Vector4(cam.pixelWidth, cam.pixelHeight, 0, 0));
        pointMaterial.SetMatrix("_View", cam.worldToCameraMatrix);
        pointMaterial.SetMatrix("_Proj", cam.projectionMatrix);

        // ����¡ �Ķ���� ����
        pointMaterial.SetFloat("_MinPixel", minPixelSize);
        pointMaterial.SetFloat("_MaxPixel", maxPixelSize);
        pointMaterial.SetFloat("_AttenScale", attenuationScale);
        pointMaterial.SetFloat("_AdaptiveScale", adaptiveScale);

        // ��� Ű���� ���
        pointMaterial.DisableKeyword("POINTSIZE_FIXED");
        pointMaterial.DisableKeyword("POINTSIZE_ATTEN");
        pointMaterial.DisableKeyword("POINTSIZE_ADAPTIVE");
        switch (sizeMode)
        {
            case PointSizeMode.Fixed: pointMaterial.EnableKeyword("POINTSIZE_FIXED"); break;
            case PointSizeMode.Attenuated: pointMaterial.EnableKeyword("POINTSIZE_ATTEN"); break;
            case PointSizeMode.Adaptive: pointMaterial.EnableKeyword("POINTSIZE_ADAPTIVE"); break;
        }

        for (int i = 0; i < _drawOrder.Count; i++)
        {
            int nodeId = _drawOrder[i];
            if (!_nodes.TryGetValue(nodeId, out var nb)) continue;
            if (nb == null || nb.count <= 0 || nb.pos == null || nb.args == null) continue;

            pointMaterial.SetBuffer("_Positions", nb.pos);

            // Fade �Ķ���� ����
            float fade = 1f; int fmode = 0;
            if (_nodeFades.TryGetValue(nodeId, out var f)) { fade = f.fade; fmode = f.mode; }
            pointMaterial.SetFloat(ID_LodFade, fade);
            pointMaterial.SetFloat(ID_DitherFade, (fmode == 2) ? 1f : 0f);

            if (useColors && nb.col != null)
            {

                pointMaterial.SetBuffer("_Colors", nb.col);
                pointMaterial.SetInt("_HasColor", 1);
            }
            else
            {
                pointMaterial.SetInt("_HasColor", 0);
            }

            var bounds = nb.bounds.size.sqrMagnitude > 0
                ? nb.bounds
                : new Bounds(transform.position, Vector3.one * 1000000f); // �ø� ���� �ӽ�ġ

            // �ε�Ʈ ��ο�
            cmd.DrawMeshInstancedIndirect(
                s_quad,
                0,
                pointMaterial,
                0,              // ���̴� �н� �ε���(���� 0)
                nb.args,
                0,
                null
            );
        }
    }*/

    // splatAccum ������ (SRP��)
    public void RenderSplatAccum(CommandBuffer cmd, Camera cam)
    {
        if (_drawOrder.Count == 0) return;
        if (pointMaterial == null)
        {
            // ���� ����(������Ʈ�� ���̴� ����)
            var sh = Shader.Find("Custom/PcdSplatAccum");
            if (sh == null) return;
            pointMaterial = new Material(sh) { hideFlags = HideFlags.DontSave };
        }
        EnsureQuad();

        // ���� ���(��Ƽ���� ������Ƽ ������ε� ����)
        pointMaterial.SetFloat(ID_PointSize, pointSize);
        pointMaterial.SetFloat(ID_KernelSharpness, kernelSharpness);
        pointMaterial.SetFloat(ID_Gaussian, gaussianKernel ? 1f : 0f);
        pointMaterial.SetMatrix(ID_L2W, transform.localToWorldMatrix);

        for (int i = 0; i < _drawOrder.Count; i++)
        {
            int nodeId = _drawOrder[i];
            if (!_nodes.TryGetValue(nodeId, out var nb)) continue;
            if (nb == null || nb.count <= 0 || nb.pos == null || nb.args == null) continue;

            pointMaterial.SetBuffer(ID_Positions, nb.pos);

            if (useColors && nb.col != null)
            {
                pointMaterial.SetBuffer(ID_Colors, nb.col);
                pointMaterial.SetInt(ID_HasColor, 1);
            }
            else
            {
                pointMaterial.SetInt(ID_HasColor, 0);
            }

            var bounds = nb.bounds.size.sqrMagnitude > 0
                ? nb.bounds
                : new Bounds(transform.position, Vector3.one * 1000000f);

            cmd.DrawMeshInstancedIndirect(
                s_quad,
                0,
                pointMaterial,
                0,       // PcdSplatAccum�� Pass 0�� ����
                nb.args,
                0,
                null
            );
        }
    }
}