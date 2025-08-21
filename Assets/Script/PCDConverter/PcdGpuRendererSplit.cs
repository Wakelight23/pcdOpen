using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public class PcdGpuRendererSplit : MonoBehaviour
{
    [Header("Render")]
    public Material pointMaterial;             // PcdPoint.shader ���
    [Range(0.001f, 1f)]
    public float pointSize = 0.02f;
    public bool useColors = true;

    [Header("Chunking")]
    [Tooltip("���۸� �� ���� ���� ����Ʈ�� ���� ���ε��մϴ�.")]
    public int maxPointsPerBuffer = 10_000_000; // 1õ�� ����Ʈ ���� (�޸�/����̹��� �°� ����)

    [Header("Stats")]
    public int totalPointCount;

    // ���� ����
    readonly List<ComputeBuffer> _posBuffers = new();
    readonly List<ComputeBuffer> _colBuffers = new();
    readonly List<int> _counts = new();

    // SRP ����: ī�޶� ��ο� �� ��� ����
    bool _subscribedToSrp;

    void OnEnable()
    {
        EnsureSrpSubscription();
    }

    void OnDisable()
    {
        UnsubscribeSrp();
        ReleaseBuffers();
    }

    void OnDestroy()
    {
        UnsubscribeSrp();
        ReleaseBuffers();
    }

    void EnsureSrpSubscription()
    {
        if (_subscribedToSrp) return;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRenderingSrp;
        _subscribedToSrp = true;
    }

    void UnsubscribeSrp()
    {
        if (!_subscribedToSrp) return;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRenderingSrp;
        _subscribedToSrp = false;
    }

    public void DisposeRenderer()
    {
        ReleaseBuffers();
    }

    void ReleaseBuffers()
    {
        foreach (var b in _posBuffers) { b?.Release(); }
        foreach (var b in _colBuffers) { b?.Release(); }
        _posBuffers.Clear();
        _colBuffers.Clear();
        _counts.Clear();
        totalPointCount = 0;
    }

    public void BeginStreamingUpload()
    {
        ReleaseBuffers();
    }

    public void UploadChunk(Vector3[] positions, Color32[] colors, int count)
    {
        if (count <= 0) return;
        // positions/ colors �迭�� count ��ŭ�� ��ȿ�ϴٰ� ����
        var posBuf = new ComputeBuffer(count, sizeof(float) * 3, ComputeBufferType.Structured);
        posBuf.SetData(positions, 0, 0, count);
        _posBuffers.Add(posBuf);

        if (useColors && colors != null && colors.Length >= count)
        {
            var colBuf = new ComputeBuffer(count, sizeof(byte) * 4, ComputeBufferType.Structured);
            colBuf.SetData(colors, 0, 0, count);
            _colBuffers.Add(colBuf);
        }
        else
        {
            _colBuffers.Add(null);
        }

        _counts.Add(count);
        totalPointCount += count;

        EnsureSrpSubscription();
    }

    public void EndStreamingUpload()
    {
        // ��� �α� �� �ʿ� ��
    }


    public void UploadData(Vector3[] positions, Color32[] colors)
    {
#if UNITY_EDITOR
        if (!UnityEditor.EditorApplication.isPlaying)
        {
            // ������ ��忡���� ������ ����������, ��ο� Ÿ�̹��� ���������ο� ���� �ٸ� �� ����
        }
#endif
        // ���ν����忡���� Unity API ���� ���
        try { var _ = Time.frameCount; }
        catch (UnityException)
        {
            Debug.LogError("[PCD] UploadData called off main thread!");
            throw;
        }

        if (positions == null || positions.Length == 0)
            throw new ArgumentException("positions is null/empty");

        if (maxPointsPerBuffer <= 0) maxPointsPerBuffer = 1_000_000;

        ReleaseBuffers();

        totalPointCount = positions.Length;

        int start = 0;
        while (start < totalPointCount)
        {
            int count = Mathf.Min(maxPointsPerBuffer, totalPointCount - start);

            // positions slice
            var posBuf = new ComputeBuffer(count, sizeof(float) * 3, ComputeBufferType.Structured);
            posBuf.SetData(positions, start, 0, count);
            _posBuffers.Add(posBuf);

            // optional colors slice
            if (useColors && colors != null && colors.Length == totalPointCount)
            {
                var colBuf = new ComputeBuffer(count, sizeof(byte) * 4, ComputeBufferType.Structured);
                colBuf.SetData(colors, start, 0, count);
                _colBuffers.Add(colBuf);
            }
            else
            {
                _colBuffers.Add(null);
            }

            _counts.Add(count);
            start += count;
        }

        if (pointMaterial == null)
            Debug.LogWarning("pointMaterial is null. Assign PcdPoint shader material.");

        // SRP �̺�Ʈ ���� ����
        EnsureSrpSubscription();
    }

    // Built-in ����������: OnRenderObject���� ��ο�
    void OnRenderObject()
    {
        // SRP(HDRP/URP)������ OnRenderObject�� ȣ��� �� ������, ���� ��δ� SRP �̺�Ʈ�� ���
        // Built-in�� ���� Ȯ���� �׸����� ���� �б�
        if (GraphicsSettings.currentRenderPipeline != null) return;
        DrawAllChunksForCurrentCamera();
    }

    // SRP ���������� ī�޶� ������ ���� �������� ��ο�
    void OnBeginCameraRenderingSrp(ScriptableRenderContext ctx, Camera cam)
    {
        if (GraphicsSettings.currentRenderPipeline == null) return; // Built-in�̸� ���⼭ �׸��� ����
        if (cam == null) return;
        // �ʿ��� ��� ī�޶� ���͸�(����/���Ӻ�/���̾�) ����
        DrawAllChunks(cam);
    }

    void DrawAllChunksForCurrentCamera()
    {
        var cam = Camera.current; // Built-in������ ��ȿ
        if (cam == null) return;
        DrawAllChunks(cam);
    }

    void DrawAllChunks(Camera cam)
    {
        if (_posBuffers.Count == 0 || pointMaterial == null) return;

        var VP = cam.projectionMatrix * cam.worldToCameraMatrix;
        // ���̴� ��� ����
        pointMaterial.SetFloat("_PointSize", pointSize);
        pointMaterial.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
        pointMaterial.SetMatrix("_ViewProj", VP);

        // ī�޶� ����� ��Ƽ���� �����ؾ� �ϴ� ���̴���� ���⼭ �߰� ���� ����
        // pointMaterial.SetMatrix("_ViewProj", cam.projectionMatrix * cam.worldToCameraMatrix);

        Debug.Log($"[PCD] Draw cam={cam.name}, pipeline={(GraphicsSettings.currentRenderPipeline != null ? "SRP" : "Built-in")}, chunks={_posBuffers.Count}");

        for (int i = 0; i < _posBuffers.Count; i++)
        {
            var pos = _posBuffers[i];
            var col = _colBuffers[i];
            int count = _counts[i];

            if (pos == null || count <= 0) continue;

            pointMaterial.SetBuffer("_Positions", pos);


            if (useColors && col != null)
            {
                pointMaterial.SetBuffer("_Colors", col);
                pointMaterial.SetInt("_HasColor", 1);
            }
            else
            {
                pointMaterial.SetInt("_HasColor", 0);
            }

            if (!pointMaterial.SetPass(0))
            {
                Debug.LogWarning("[PCD] SetPass(0) failed");
                return;
            }

            // ��� ��ο�
            Graphics.DrawProceduralNow(MeshTopology.Points, count, 1);
        }
    }
}
