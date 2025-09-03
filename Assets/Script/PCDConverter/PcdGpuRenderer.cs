using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public class PcdGpuRenderer : MonoBehaviour
{
    [Header("Render")]
    public Material pointMaterial;                // Shader "Custom/PcdSplatAccum"
    [Range(0.5f, 64.0f)]
    public float pointSize = 4.0f;                // default px when no LOD meta is present
    public bool useColors = true;

    [Header("Gap fill / LOD size")]
    public float minPixelSize = 1.25f;
    public float maxPixelSize = 32.0f;
    public float sizeK = 1.0f;                    // px = sizeK * spacing * projection
    public float parentFadeBase = 0.7f;           // per-level fade (0.6~0.8)
    public float distanceFadeNear = 5.0f;
    public float distanceFadeFar = 200.0f;

    [Header("Accum kernel")]
    public float kernelSharpness = 1.5f;
    public bool gaussianKernel = false;

    [Header("EDL helper passes")]
    [SerializeField] Material splatDepthProxyMaterial; // Shader "Custom/PcdSplatDepthProxy"
    [SerializeField] Material splatColorLiteMaterial;  // Shader "Custom/PcdSplatColorLite"
    [Range(1e-5f, 5e-1f)]
    public float depthMatchEps = 0.001f;               // for ColorLite/Accum optional front-match

    [Header("Stats (overall)")]
    public int totalPointCount;
    public int activeNodeCount;

    // Node GPU buffers
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

    // Shared unit quad
    static Mesh s_quad;
    static readonly uint[] s_argsTemplate = new uint[8];

    // LOD meta (spacing, level)
    readonly Dictionary<int, (float spacing, int level)> _nodeMeta = new();

    // Point Budget LOD eval
    struct NodeEval { public int id; public int count; public float sse; public float px; public float score; }

    // Shader IDs
    static readonly int ID_Positions = Shader.PropertyToID("_Positions");
    static readonly int ID_Colors = Shader.PropertyToID("_Colors");
    static readonly int ID_HasColor = Shader.PropertyToID("_HasColor");
    static readonly int ID_PointSize = Shader.PropertyToID("_PointSize");
    static readonly int ID_KernelSharpness = Shader.PropertyToID("_KernelSharpness");
    static readonly int ID_Gaussian = Shader.PropertyToID("_Gaussian");
    static readonly int ID_L2W = Shader.PropertyToID("_LocalToWorld");
    static readonly int ID_HasSRGB = Shader.PropertyToID("_HasSRGB");
    static readonly int ID_NodeFade = Shader.PropertyToID("_NodeFade");
    static readonly int ID_DepthMatchEps = Shader.PropertyToID("_DepthMatchEps");
    static readonly int ID_NodeLevel = Shader.PropertyToID("_NodeLevel");
    static readonly int ID_MaxDepth = Shader.PropertyToID("_MaxDepth");
    static readonly int ID_PcdDepthRT = Shader.PropertyToID("_PcdDepthRT");

    // ===== Helpers =====
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

    // pixels per meter at distance d
    float ProjectPixelsPerWorld(Camera cam, float distance)
    {
        float H = Mathf.Max(1, cam.pixelHeight);
        float P = H / (2.0f * Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f));
        return P / Mathf.Max(distance, 1e-3f);
    }

    float ComputeNodeFade(int level, float distance)
    {
        float levelFade = Mathf.Pow(Mathf.Clamp01(parentFadeBase), Mathf.Max(0, level));
        float t = Mathf.InverseLerp(distanceFadeFar, distanceFadeNear, distance); // far->near
        float distFade = Mathf.Lerp(0.5f, 1.0f, Mathf.Clamp01(t));
        return Mathf.Clamp01(levelFade * distFade);
    }

    public void SetNodeMeta(int nodeId, float spacing, int level) => _nodeMeta[nodeId] = (spacing, level);
    float QueryNodeSpacing(int nodeId) => _nodeMeta.TryGetValue(nodeId, out var m) ? Mathf.Max(1e-6f, m.spacing) : 1.0f;
    int QueryNodeLevel(int nodeId) => _nodeMeta.TryGetValue(nodeId, out var m) ? Mathf.Max(0, m.level) : 0;

    // unified px for all passes
    float GetNodePointSizePx(Camera cam, NodeBuffers nb, int nodeId)
    {
        float nodeSpacing = QueryNodeSpacing(nodeId);
        Vector3 centerLS = (nb.bounds.size.sqrMagnitude > 0) ? nb.bounds.center : transform.position;
        float distance = Vector3.Distance(cam.transform.position, transform.TransformPoint(centerLS));
        float P = ProjectPixelsPerWorld(cam, Mathf.Max(distance, 1e-3f));
        float px = Mathf.Clamp(sizeK * nodeSpacing * P, minPixelSize, maxPixelSize);
        return px;
    }

    void BindNodeBuffersToMaterial(Material mat, NodeBuffers nb, bool hasColor)
    {
        mat.SetBuffer(ID_Positions, nb.pos);
        if (useColors && hasColor && nb.col != null)
        {
            mat.SetBuffer(ID_Colors, nb.col);
            mat.SetInt(ID_HasColor, 1);
        }
        else
        {
            mat.SetInt(ID_HasColor, 0);
        }
    }

    void RecomputeStats()
    {
        int total = 0;
        foreach (var nb in _nodes.Values) total += nb.count;
        totalPointCount = total;
        activeNodeCount = _nodes.Count;
    }

    public float UpdateLodAndBudget(Camera cam, int pointBudget, float sseThreshold, float hysteresis)
    {
        if (_drawOrder.Count == 0) return pointSize;
        List<NodeEval> evals = new(_drawOrder.Count);
        foreach (var nodeId in _drawOrder)
        {
            if (!_nodes.TryGetValue(nodeId, out var nb)) continue;
            float px = GetNodePointSizePx(cam, nb, nodeId);           // 화면상 포인트 px
            float spacing = QueryNodeSpacing(nodeId);
            float P = px / Mathf.Max(spacing * sizeK, 1e-6f);         // ProjectPixelsPerWorld 역산
            float sse = spacing * P;                                  // SSE ≈ spacing*P
                                                                      // SSE가 낮을수록(화면 기여가 작을수록) 우선순위 낮음
            float score = (sse >= sseThreshold) ? (sse) : (sse * 0.25f);
            evals.Add(new NodeEval { id = nodeId, count = nb.count, sse = sse, px = px, score = score });
        }
        // 근사 우선순위: score*count가 큰 노드를 먼저(화면 기여/밀도 가중)
        evals.Sort((a, b) => (b.score * b.count).CompareTo(a.score * a.count));

        _drawOrder.Clear();
        int accu = 0;
        double sumPx = 0; int sumN = 0;
        foreach (var e in evals)
        {
            // 히스테리시스: SSE가 임계보다 약간 작아도 유지
            bool pass = (e.sse >= sseThreshold * (1.0f - hysteresis)) || accu == 0;
            if (!pass) continue;
            if (accu + e.count > pointBudget) break;
            _drawOrder.Add(e.id);
            accu += e.count;
            sumPx += e.px * e.count;
            sumN += e.count;
        }
        if (sumN == 0) { RecomputeStats(); return pointSize; }
        RecomputeStats();
        return (float)(sumPx / sumN); // 가중 평균 px
    }

    // 평균 포인트 크기만 필요할 때
    public float ComputeAveragePointPx(Camera cam, int maxNodesSample = 16)
    {
        if (_drawOrder.Count == 0) return pointSize;
        double sum = 0; int n = 0;
        for (int i = 0; i < _drawOrder.Count && i < maxNodesSample; ++i)
        {
            int id = _drawOrder[i];
            if (!_nodes.TryGetValue(id, out var nb)) continue;
            sum += GetNodePointSizePx(cam, nb, id);
            n++;
        }
        return (n > 0) ? (float)(sum / n) : pointSize;
    }

    // ===== External API =====
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

        var nb = new NodeBuffers { count = positions.Length, bounds = nodeBounds };

        nb.pos = new ComputeBuffer(nb.count, sizeof(float) * 3, ComputeBufferType.Structured);
        nb.pos.SetData(positions);

        if (useColors && colors != null && colors.Length == nb.count)
        {
            var packed = new uint[nb.count];
            for (int i = 0; i < nb.count; i++)
            {
                var c = colors[i];
                packed[i] = 0xFF000000u | ((uint)c.r << 16) | ((uint)c.g << 8) | (uint)c.b; // 0xAARRGGBB
            }
            nb.col = new ComputeBuffer(nb.count, sizeof(uint), ComputeBufferType.Structured);
            nb.col.SetData(packed);
        }

        EnsureQuad();
        uint indexCount = (uint)s_quad.GetIndexCount(0);

        // 안전한 방식: 요소 5개, stride 4
        nb.args = new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments);

        uint[] args = new uint[5];
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
            if (nb.pos != null) { nb.pos.Release(); nb.pos = null; }
            if (nb.col != null) { nb.col.Release(); nb.col = null; }
            if (nb.args != null) { nb.args.Release(); nb.args = null; }
            _nodes.Remove(nodeId);
            _drawOrder.Remove(nodeId);
            RecomputeStats();
        }
    }

    public void ClearAllNodes()
    {
        foreach (var kv in _nodes)
        {
            var nb = kv.Value;
            if (nb.pos != null) { nb.pos.Release(); nb.pos = null; }
            if (nb.col != null) { nb.col.Release(); nb.col = null; }
            if (nb.args != null) { nb.args.Release(); nb.args = null; }
        }
        _nodes.Clear();
        _drawOrder.Clear();
        RecomputeStats();
    }

    public void SetDrawOrder(IList<int> nodeIdsInOrder)
    {
        _drawOrder.Clear();
        if (nodeIdsInOrder != null) _drawOrder.AddRange(nodeIdsInOrder);
    }

    public IReadOnlyList<int> GetActiveNodeIds() => _drawOrder;

    public void DisposeRenderer() => ClearAllNodes();

    void OnEnable()
    {
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



    // ===== Passes =====

    // Front-only color (optional helper for EDL debug or pre-mask)
    public void RenderSplatColorLite(CommandBuffer cmd, Camera cam)
    {
        if (_drawOrder.Count == 0) return;

        if (splatColorLiteMaterial == null)
        {
            var sh = Shader.Find("Custom/PcdSplatColorLite");
            if (sh == null) return;
            splatColorLiteMaterial = new Material(sh) { hideFlags = HideFlags.DontSave };
        }
        EnsureQuad();

        splatColorLiteMaterial.SetMatrix(ID_L2W, transform.localToWorldMatrix);
        splatColorLiteMaterial.SetFloat(ID_HasSRGB, 1.0f);
        splatColorLiteMaterial.SetFloat(ID_DepthMatchEps, depthMatchEps); // shader uses _PcdDepthRT globally

        for (int i = 0; i < _drawOrder.Count; i++)
        {
            int nodeId = _drawOrder[i];
            if (!_nodes.TryGetValue(nodeId, out var nb)) continue;
            if (nb == null || nb.count <= 0 || nb.pos == null || nb.args == null) continue;

            float px = GetNodePointSizePx(cam, nb, nodeId);
            splatColorLiteMaterial.SetFloat(ID_PointSize, px);

            BindNodeBuffersToMaterial(splatColorLiteMaterial, nb, nb.col != null);

            cmd.DrawMeshInstancedIndirect(
                s_quad, 0, splatColorLiteMaterial, 0, nb.args, 0, null
            );
        }
    }

    // ==== Splat Accumulation (main pass) ====
    public int maxDepthForMaterial = 8; // 인스펙터 노출해도 됨
    public void RenderSplatAccum(CommandBuffer cmd, Camera cam)
    {
        if (_drawOrder.Count == 0) return;
        if (pointMaterial == null)
        {
            var sh = Shader.Find("Custom/PcdSplatAccum");
            if (sh == null) return;
            pointMaterial = new Material(sh) { hideFlags = HideFlags.DontSave };
        }
        EnsureQuad();

        pointMaterial.SetFloat(ID_KernelSharpness, kernelSharpness);
        pointMaterial.SetFloat(ID_Gaussian, gaussianKernel ? 1f : 0f);
        pointMaterial.SetMatrix(ID_L2W, transform.localToWorldMatrix);
        pointMaterial.SetFloat(ID_DepthMatchEps, depthMatchEps);

        for (int i = 0; i < _drawOrder.Count; i++)
        {
            int nodeId = _drawOrder[i];
            if (!_nodes.TryGetValue(nodeId, out var nb)) continue;
            if (nb == null || nb.count <= 0 || nb.pos == null || nb.args == null) continue;

            float px = GetNodePointSizePx(cam, nb, nodeId);

            Vector3 centerLS = (nb.bounds.size.sqrMagnitude > 0) ? nb.bounds.center : transform.position;
            float distance = Vector3.Distance(cam.transform.position, transform.TransformPoint(centerLS));
            float nodeFade = ComputeNodeFade(QueryNodeLevel(nodeId), distance);

            int level = QueryNodeLevel(nodeId);

            BindNodeBuffersToMaterial(pointMaterial, nb, nb.col != null);
            pointMaterial.SetFloat(ID_PointSize, px);
            pointMaterial.SetFloat(ID_NodeFade, nodeFade);
            pointMaterial.SetFloat(ID_NodeLevel, (float)level);
            pointMaterial.SetFloat(ID_MaxDepth, (float)maxDepthForMaterial);

            cmd.DrawMeshInstancedIndirect(s_quad, 0, pointMaterial, 0, nb.args, 0, null);
        }
    }
}