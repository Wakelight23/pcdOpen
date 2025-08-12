using UnityEngine;
using System;

[ExecuteAlways]
public class PcdGpuRenderer : MonoBehaviour
{
    [Header("Render")]
    public Material pointMaterial;      // �Ʒ� ���̴��� �Ҵ�
    [Range(0.001f, 0.2f)]
    public float pointSize = 0.02f;     // ���� ���� ȭ��� ũ��
    public bool useColors = true;

    [Header("Stats (read-only)")]
    public int pointCount;

    ComputeBuffer posBuffer;
    ComputeBuffer colBuffer;

    Bounds drawBounds = new Bounds(Vector3.zero, Vector3.one * 10000f); // ������ ĳ��

    public void UploadData(Vector3[] positions, Color32[] colors)
    {
        ReleaseBuffers();

        if (positions == null || positions.Length == 0)
            throw new ArgumentException("positions is null or empty");

        pointCount = positions.Length;

        posBuffer = new ComputeBuffer(pointCount, sizeof(float) * 3);
        posBuffer.SetData(positions);

        if (useColors && colors != null && colors.Length == pointCount)
        {
            colBuffer = new ComputeBuffer(pointCount, sizeof(byte) * 4);
            colBuffer.SetData(colors);
        }

        if (pointMaterial == null)
        {
            Debug.LogWarning("pointMaterial is null. Assign PcdPoint.shader-based material.");
        }
    }

    void OnDisable()
    {
        ReleaseBuffers();
    }

    void ReleaseBuffers()
    {
        if (posBuffer != null)
        {
            posBuffer.Release();
            posBuffer = null;
        }
        if (colBuffer != null)
        {
            colBuffer.Release();
            colBuffer = null;
        }
    }

    void UpdateMaterialParams()
    {
        if (pointMaterial == null) return;
        pointMaterial.SetBuffer("_Positions", posBuffer);
        if (colBuffer != null) pointMaterial.SetBuffer("_Colors", colBuffer);

        pointMaterial.SetFloat("_PointSize", pointSize);
        pointMaterial.SetInt("_HasColor", (useColors && colBuffer != null) ? 1 : 0);
    }

    void OnRenderObject()
    {
        // ī�޶� �� ������ �ʿ���ٸ� OnPostRender�ε� ����.
        if (posBuffer == null || pointMaterial == null || pointCount <= 0) return;

        UpdateMaterialParams();

        pointMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Points, pointCount, 1);
    }
}
