using UnityEngine;

public sealed class IndirectRenderer : MonoBehaviour
{
    public Mesh ProxyMesh; // point sprite or simple quad for point rendering
    public Material IndirectMaterial;
    public ComputeShader CullingCS;
    public TileStreamingManager Streaming;

    private GraphicsBuffer argsBuffer;
    private GraphicsBuffer visibilityBuffer;

    static readonly int _VertexBuffer = Shader.PropertyToID("_VertexBuffer");
    static readonly int _Visibility = Shader.PropertyToID("_Visibility");
    static readonly int _Args = Shader.PropertyToID("_Args");

    void OnEnable()
    {
        argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, 5 * sizeof(uint));
        visibilityBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1024 * 1024, sizeof(uint));
    }

    void Update()
    {
        // 1) GPU culling
        int kernel = CullingCS.FindKernel("CSMain");
        CullingCS.SetBuffer(kernel, _Visibility, visibilityBuffer);
        // set camera planes, aabbs, counts ... (omitted)
        CullingCS.Dispatch(kernel, 256, 1, 1);

        // 2) Build draw args
        uint indexCount = ProxyMesh != null ? ProxyMesh.GetIndexCount(0) : 1u;
        uint instanceCount = 0; // readback or tracked count
        var args = new uint[5] { indexCount, instanceCount, 0, 0, 0 };
        argsBuffer.SetData(args);

        // 3) Issue indirect draw
        IndirectMaterial.SetBuffer(_VertexBuffer, StreamingSharedVertexBuffer());
        IndirectMaterial.SetBuffer(_Visibility, visibilityBuffer);
        Graphics.DrawMeshInstancedIndirect(ProxyMesh, 0, IndirectMaterial, new Bounds(Vector3.zero, Vector3.one * 100000f), argsBuffer);
    }

    GraphicsBuffer StreamingSharedVertexBuffer()
    {
        // expose from TileStreamingManager or a registry singleton
        return FindObjectOfType<TileStreamingManager>()?.GetType()
            .GetField("vertexBuffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(FindObjectOfType<TileStreamingManager>()) as GraphicsBuffer;
    }

    void OnDisable()
    {
        argsBuffer?.Dispose();
        visibilityBuffer?.Dispose();
    }
}
