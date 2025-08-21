using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public class PcdGpuRendererSplit : MonoBehaviour
{
    [Header("Render")]
    public Material pointMaterial;             // PcdPoint.shader 기반
    [Range(0.001f, 1f)]
    public float pointSize = 0.02f;
    public bool useColors = true;

    [Header("Chunking")]
    [Tooltip("버퍼를 이 개수 단위 포인트로 분할 업로드합니다.")]
    public int maxPointsPerBuffer = 10_000_000; // 1천만 포인트 단위 (메모리/드라이버에 맞게 조절)

    [Header("Stats")]
    public int totalPointCount;

    // 분할 버퍼
    readonly List<ComputeBuffer> _posBuffers = new();
    readonly List<ComputeBuffer> _colBuffers = new();
    readonly List<int> _counts = new();

    // SRP 대응: 카메라별 드로우 훅 등록 여부
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
        // positions/ colors 배열은 count 만큼만 유효하다고 가정
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
        // 통계 로그 등 필요 시
    }


    public void UploadData(Vector3[] positions, Color32[] colors)
    {
#if UNITY_EDITOR
        if (!UnityEditor.EditorApplication.isPlaying)
        {
            // 에디터 모드에서도 동작은 가능하지만, 드로우 타이밍은 파이프라인에 따라 다를 수 있음
        }
#endif
        // 메인스레드에서만 Unity API 접근 허용
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

        // SRP 이벤트 연결 보장
        EnsureSrpSubscription();
    }

    // Built-in 파이프라인: OnRenderObject에서 드로우
    void OnRenderObject()
    {
        // SRP(HDRP/URP)에서도 OnRenderObject가 호출될 수 있으나, 공식 경로는 SRP 이벤트를 사용
        // Built-in일 때만 확실히 그리도록 조건 분기
        if (GraphicsSettings.currentRenderPipeline != null) return;
        DrawAllChunksForCurrentCamera();
    }

    // SRP 파이프라인 카메라 렌더링 시작 시점에서 드로우
    void OnBeginCameraRenderingSrp(ScriptableRenderContext ctx, Camera cam)
    {
        if (GraphicsSettings.currentRenderPipeline == null) return; // Built-in이면 여기서 그리지 않음
        if (cam == null) return;
        // 필요한 경우 카메라 필터링(씬뷰/게임뷰/레이어) 가능
        DrawAllChunks(cam);
    }

    void DrawAllChunksForCurrentCamera()
    {
        var cam = Camera.current; // Built-in에서만 유효
        if (cam == null) return;
        DrawAllChunks(cam);
    }

    void DrawAllChunks(Camera cam)
    {
        if (_posBuffers.Count == 0 || pointMaterial == null) return;

        var VP = cam.projectionMatrix * cam.worldToCameraMatrix;
        // 셰이더 상수 설정
        pointMaterial.SetFloat("_PointSize", pointSize);
        pointMaterial.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
        pointMaterial.SetMatrix("_ViewProj", VP);

        // 카메라 행렬을 머티리얼에 전달해야 하는 셰이더라면 여기서 추가 세팅 가능
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

            // 즉시 드로우
            Graphics.DrawProceduralNow(MeshTopology.Points, count, 1);
        }
    }
}
