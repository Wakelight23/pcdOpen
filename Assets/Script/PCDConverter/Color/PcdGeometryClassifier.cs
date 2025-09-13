using System.Collections.Generic;
using UnityEngine;

public class PcdGeometryClassifier
{
    public enum PointType
    {
        Interior = 0,
        Exterior = 1,
        Boundary = 2,
        Unknown = 3
    }

    // Ray casting을 이용한 내부/외부 판별
    public static PointType ClassifyPoint(Vector3 point, Vector3[] allPoints, float searchRadius = 2.0f)
    {
        // 1. 주변 포인트 밀도 기반 분류
        var neighbors = GetNeighborsInRadius(point, allPoints, searchRadius);
        float density = neighbors.Count / (4.0f * Mathf.PI * searchRadius * searchRadius * searchRadius / 3.0f);

        // 2. 표면 법선 추정
        var normal = EstimateSurfaceNormal(point, neighbors);
        float normalMagnitude = normal.magnitude;

        // 3. 분류 기준
        if (density > 0.8f && normalMagnitude < 0.3f) return PointType.Interior;
        if (density < 0.3f) return PointType.Exterior;
        if (normalMagnitude > 0.7f) return PointType.Boundary;

        return PointType.Unknown;
    }

    private static List<Vector3> GetNeighborsInRadius(Vector3 center, Vector3[] points, float radius)
    {
        var neighbors = new List<Vector3>();
        float radiusSq = radius * radius;

        foreach (var p in points)
        {
            if ((p - center).sqrMagnitude <= radiusSq)
                neighbors.Add(p);
        }
        return neighbors;
    }

    private static Vector3 EstimateSurfaceNormal(Vector3 center, List<Vector3> neighbors)
    {
        if (neighbors.Count < 3) return Vector3.zero;

        // PCA를 이용한 법선 추정
        Vector3 centroid = Vector3.zero;
        foreach (var p in neighbors) centroid += p;
        centroid /= neighbors.Count;

        Matrix4x4 covariance = Matrix4x4.zero;
        foreach (var p in neighbors)
        {
            Vector3 diff = p - centroid;
            covariance.m00 += diff.x * diff.x;
            covariance.m01 += diff.x * diff.y;
            covariance.m02 += diff.x * diff.z;
            covariance.m11 += diff.y * diff.y;
            covariance.m12 += diff.y * diff.z;
            covariance.m22 += diff.z * diff.z;
        }

        // 최소 고유벡터를 법선으로 사용
        return EstimateSmallestEigenvector(covariance);
    }

    private static Vector3 EstimateSmallestEigenvector(Matrix4x4 m)
    {
        // 간단한 Power iteration 방법으로 최소 고유벡터 추정
        Vector3 v = Vector3.up;
        for (int i = 0; i < 10; i++)
        {
            v = MultiplyMatrixVector(m, v).normalized;
        }
        return v;
    }

    private static Vector3 MultiplyMatrixVector(Matrix4x4 m, Vector3 v)
    {
        // 3D 벡터를 w=1인 동차좌표로 간주하여 4x4 행렬과 곱셈
        float x = m.m00 * v.x + m.m01 * v.y + m.m02 * v.z + m.m03;
        float y = m.m10 * v.x + m.m11 * v.y + m.m12 * v.z + m.m13;
        float z = m.m20 * v.x + m.m21 * v.y + m.m22 * v.z + m.m23;

        return new Vector3(x, y, z);
    }
}
