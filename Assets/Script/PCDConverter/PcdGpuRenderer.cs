using System;
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
