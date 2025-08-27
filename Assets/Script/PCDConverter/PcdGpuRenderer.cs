using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public class PcdGpuRenderer : MonoBehaviour
{
    [Header("Render")]
    public Material pointMaterial;               // Custom/PcdBillboardIndirect 기반
    [Range(0.5f, 64.0f)]
    public float pointSize = 5.0f;               // 픽셀 단위
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
    [Range(0.01f, 10f)] public float attenuationScale = 1.0f;   // 1/d 스케일 계수
    [Range(0.0f, 16f)] public float adaptiveScale = 0.0f;        // Adaptive 가산

    // 내부 렌더 모드
    public enum RenderMode { IndirectBillboard }
    public RenderMode renderMode = RenderMode.IndirectBillboard;

    // 노드 버퍼 구조
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

    // 공유 쿼드 메쉬
    static Mesh s_quad;
    static readonly uint[] s_argsTemplate = new uint[5]; // indexCountPerInstance, instanceCount, startIndex, baseVertex, startInstance

    // 메인스레드 체크
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

    // ====== 외부 API ======

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
                // 0xFFRRGGBB: DecodeRGB(u)와 일치
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
        // RenderSystem 등록
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

    // RenderIndirect (SRP용)
    public void RenderIndirect(CommandBuffer cmd, Camera cam)
    {
        if (pointMaterial == null) return;
        if (_drawOrder.Count == 0) return;
        if (renderMode != RenderMode.IndirectBillboard) return;

        EnsureQuad();

        // 공통 상수
        pointMaterial.SetFloat("_PointSize", pointSize);
        pointMaterial.SetFloat("_SoftEdge", softEdge);
        pointMaterial.SetFloat("_RoundMask", roundMask ? 1f : 0f);
        pointMaterial.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
        pointMaterial.SetInt("_HasColor", 0);

        // Color
        //pointMaterial.SetColor("_Tint", Color.white);
        pointMaterial.SetVector("_DistRange", new Vector2(10.0f, 200f));
        pointMaterial.SetVector("_ColorAtten", new Vector2(0.4f, 0.2f));

        // 화면/행렬 전달
        // pointMaterial.SetVector("_PcdScreenSize", new Vector4(cam.pixelWidth, cam.pixelHeight, 0, 0));
        pointMaterial.SetMatrix("_View", cam.worldToCameraMatrix);
        pointMaterial.SetMatrix("_Proj", cam.projectionMatrix);

        // 사이징 파라미터 전달
        pointMaterial.SetFloat("_MinPixel", minPixelSize);
        pointMaterial.SetFloat("_MaxPixel", maxPixelSize);
        pointMaterial.SetFloat("_AttenScale", attenuationScale);
        pointMaterial.SetFloat("_AdaptiveScale", adaptiveScale);

        // 모드 키워드 토글
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
                : new Bounds(transform.position, Vector3.one * 1000000f); // 컬링 배제 임시치

            // 인디렉트 드로우
            cmd.DrawMeshInstancedIndirect(
                s_quad,
                0,
                pointMaterial,
                0,              // 쉐이더 패스 인덱스(보통 0)
                nb.args,
                0,
                null
            );
        }
    }

    // RenderSplatAccum (URP/HDRP용, MRT 누적)
    /*public void RenderSplatAccum(CommandBuffer cmd, Camera cam)
    {
        if (pointMaterial == null) return;
        if (_drawOrder.Count == 0) return;
        if (renderMode != RenderMode.IndirectBillboard) return;

        EnsureQuad();

        // Accumulation용 공통 상수 설정
        // 주의: 이 머티리얼은 MRT(Additive) 전용 셰이더(PcdSplatAccum 등)를 가정
        pointMaterial.SetFloat("_PointSize", pointSize);
        // weighted splats에서 소프트에지는 커널 샤프니스/가우시안으로 대체되지만
        // 동일 머티리얼을 공유한다면 필요한 파라미터를 설정
        //pointMaterial.SetFloat("_KernelSharpness", kernelSharpness);
        //pointMaterial.SetFloat("_Gaussian", useGaussian ? 1f : 0f);

        // 간단한 로컬→월드 전달(셰이더에서 _LocalToWorld 사용)
        pointMaterial.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);

        for (int i = 0; i < _drawOrder.Count; i++)
        {
            int nodeId = _drawOrder[i];
            if (!_nodes.TryGetValue(nodeId, out var nb)) continue;
            if (nb == null || nb.count <= 0 || nb.pos == null || nb.args == null) continue;

            // 필수 버퍼 바인딩
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

            // 넓은 Bounds로 컬링 회피(필요 시 nb.bounds 사용)
            var bounds = nb.bounds.size.sqrMagnitude > 0
                ? nb.bounds
                : new Bounds(transform.position, Vector3.one * 1000000f);

            // MRT 누적(Additive)이 설정된 패스 인덱스(보통 0)로 인디렉트 드로우
            cmd.DrawMeshInstancedIndirect(
                s_quad,
                0,
                pointMaterial,
                0,      // accumulation shader pass index
                nb.args,
                0,
                null
            );
        }
    }*/
}
