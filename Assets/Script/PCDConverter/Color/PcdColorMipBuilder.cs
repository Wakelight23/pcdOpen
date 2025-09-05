// ���� ��� �÷� �� ������
using System.Collections.Generic;
using UnityEngine;

public static class PcdColorMipBuilder
{
    // childIndicesPerParent: �θ� ����Ʈ p���� �ڽ� ����Ʈ �ε��� ���(���� ���/���� LOD)
    public static Color32[] BuildAverageColors(Vector3[] childPositions, Color32[] childColors, List<int>[] childIndicesPerParent)
    {
        int parentCount = childIndicesPerParent.Length;
        var parentColors = new Color32[parentCount];

        for (int p = 0; p < parentCount; p++)
        {
            var list = childIndicesPerParent[p];
            if (list == null || list.Count == 0)
            {
                parentColors[p] = new Color32(255, 255, 255, 255); // ��������� ���
                continue;
            }

            // ���� ����(�����÷� ���� ���� uint)
            ulong sumR = 0, sumG = 0, sumB = 0;
            int n = list.Count;

            for (int k = 0; k < n; k++)
            {
                int ci = list[k];
                var c = childColors[ci];
                sumR += c.r;
                sumG += c.g;
                sumB += c.b;
            }

            byte r = (byte)(sumR / (ulong)n);
            byte g = (byte)(sumG / (ulong)n);
            byte b = (byte)(sumB / (ulong)n);
            parentColors[p] = new Color32(r, g, b, 255); // A�� 255 ����
        }
        return parentColors;
    }

    // ���ʽ�: ���� ���(�Ÿ� ���, ��: �θ� ��ǥ�� posP�� �ڽ� posC ���� ����ġ)
    public static Color32[] BuildWeightedColors(Vector3[] childPositions, Color32[] childColors, Vector3[] parentPositions, List<int>[] childIndicesPerParent, float radius)
    {
        int parentCount = childIndicesPerParent.Length;
        var parentColors = new Color32[parentCount];
        float invEps = 1.0f / Mathf.Max(1e-6f, radius);

        for (int p = 0; p < parentCount; p++)
        {
            var list = childIndicesPerParent[p];
            if (list == null || list.Count == 0)
            {
                parentColors[p] = new Color32(255, 255, 255, 255);
                continue;
            }

            double sumW = 0;
            double rSum = 0, gSum = 0, bSum = 0;
            Vector3 posP = parentPositions[p];

            for (int k = 0; k < list.Count; k++)
            {
                int ci = list[k];
                float d = Vector3.Distance(posP, childPositions[ci]) * invEps;   // 0..~1
                float w = 1.0f / (1.0f + d);                                     // ���� ����(����)
                var c = childColors[ci];

                sumW += w;
                rSum += w * c.r;
                gSum += w * c.g;
                bSum += w * c.b;
            }

            if (sumW <= 0) sumW = 1;
            byte r = (byte)Mathf.Clamp((float)(rSum / sumW), 0, 255);
            byte g = (byte)Mathf.Clamp((float)(gSum / sumW), 0, 255);
            byte b = (byte)Mathf.Clamp((float)(bSum / sumW), 0, 255);
            parentColors[p] = new Color32(r, g, b, 255);
        }
        return parentColors;
    }
}
