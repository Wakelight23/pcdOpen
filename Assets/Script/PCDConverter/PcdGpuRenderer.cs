using System;
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
