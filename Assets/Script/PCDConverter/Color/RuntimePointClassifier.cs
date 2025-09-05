using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// Job System에서 사용할 수 있도록 struct로 변경 (class → struct)
[System.Serializable]
public struct ColorClassificationSettings
{
    [Header("Classification Parameters")]
    [Range(0.01f, 0.1f)]
    public float surfaceThreshold;

    [Range(0.05f, 0.5f)]
    public float normalSmoothRadius;

    [Range(4, 16)]
    public int kNeighbors;

    [Header("Color Enhancement")]
    [Range(0.5f, 2.0f)]
    public float contrastBoost;

    [Range(0.5f, 2.0f)]
    public float saturationBoost;

    public bool enableOutlierRemoval;

    // 기본값 설정을 위한 정적 프로퍼티
    public static ColorClassificationSettings Default => new ColorClassificationSettings
    {
        surfaceThreshold = 0.02f,
        normalSmoothRadius = 0.1f,
        kNeighbors = 8,
        contrastBoost = 1.2f,
        saturationBoost = 1.1f,
        enableOutlierRemoval = true
    };
}

public class RuntimePointClassifier : MonoBehaviour
{
    [Header("Color Classification Settings")]
    public ColorClassificationSettings settings = ColorClassificationSettings.Default;

    [Header("Performance")]
    [Tooltip("Job System을 사용할지 여부")]
    public bool useJobSystem = true;

    [Header("Color Mapping")]
    public Color interiorColor = new Color(0.2f, 0.8f, 0.2f); // 녹색
    public Color exteriorColor = new Color(0.8f, 0.2f, 0.2f); // 빨간색  
    public Color boundaryColor = new Color(0.2f, 0.2f, 0.8f); // 파란색
    public Color unknownColor = Color.white;

    // 분류 결과 저장
    public enum PointClassification
    {
        Interior = 0,
        Exterior = 1,
        Boundary = 2,
        Unknown = 3
    }

    public Color32[] ClassifyAndAdjustColors(Vector3[] positions, Color32[] originalColors, Bounds bounds)
    {
        if (positions == null || positions.Length == 0)
            return originalColors ?? new Color32[0];

        if (useJobSystem)
        {
            return ProcessWithJobSystem(positions, originalColors, bounds);
        }
        else
        {
            return ProcessDirectly(positions, originalColors, bounds);
        }
    }

    Color32[] ProcessWithJobSystem(Vector3[] positions, Color32[] originalColors, Bounds bounds)
    {
        int count = positions.Length;
        var outputColors = new Color32[count];

        using (var positionArray = new NativeArray<Vector3>(positions, Allocator.TempJob))
        using (var inputColorArray = new NativeArray<Color32>(originalColors ?? new Color32[count], Allocator.TempJob))
        using (var outputColorArray = new NativeArray<Color32>(outputColors, Allocator.TempJob))
        {
            var job = new ColorClassificationJob
            {
                positions = positionArray,
                inputColors = inputColorArray,
                settings = settings, // struct이므로 값 복사됨
                bounds = bounds,
                outputColors = outputColorArray,
                interiorColor = ToFloat4(interiorColor),
                exteriorColor = ToFloat4(exteriorColor),
                boundaryColor = ToFloat4(boundaryColor),
                unknownColor = ToFloat4(unknownColor)
            };

            var jobHandle = job.Schedule(count, Mathf.Max(1, count / 32));
            jobHandle.Complete();

            outputColorArray.CopyTo(outputColors);
        }

        return outputColors;
    }

    Color32[] ProcessDirectly(Vector3[] positions, Color32[] originalColors, Bounds bounds)
    {
        var result = new Color32[positions.Length];

        for (int i = 0; i < positions.Length; i++)
        {
            result[i] = ClassifyPoint(positions[i], originalColors?[i] ?? Color.white, bounds, positions);
        }

        return result;
    }

    Color32 ClassifyPoint(Vector3 position, Color32 originalColor, Bounds bounds, Vector3[] allPositions)
    {
        // 간단한 분류 로직 (실제 구현에서는 더 복잡한 알고리즘 사용)
        var classification = ClassifyPointType(position, bounds, allPositions);

        Color baseColor = classification switch
        {
            PointClassification.Interior => interiorColor,
            PointClassification.Exterior => exteriorColor,
            PointClassification.Boundary => boundaryColor,
            _ => unknownColor
        };

        // 색상 향상 적용
        var enhancedColor = ApplyColorEnhancement(baseColor, originalColor);

        return new Color32(
            (byte)(enhancedColor.r * 255),
            (byte)(enhancedColor.g * 255),
            (byte)(enhancedColor.b * 255),
            255);
    }

    PointClassification ClassifyPointType(Vector3 position, Bounds bounds, Vector3[] allPositions)
    {
        // 경계로부터의 거리 기반 간단 분류
        var center = bounds.center;
        var extents = bounds.extents;

        var normalizedPos = new Vector3(
            (position.x - center.x) / extents.x,
            (position.y - center.y) / extents.y,
            (position.z - center.z) / extents.z
        );

        float distanceFromBoundary = 1.0f - normalizedPos.magnitude;

        if (distanceFromBoundary < settings.surfaceThreshold)
            return PointClassification.Boundary;
        else if (normalizedPos.magnitude < 0.8f)
            return PointClassification.Interior;
        else
            return PointClassification.Exterior;
    }

    Color ApplyColorEnhancement(Color baseColor, Color32 originalColor)
    {
        // 대비와 채도 향상
        var original = new Color(originalColor.r / 255f, originalColor.g / 255f, originalColor.b / 255f);

        // 대비 적용
        var contrast = Color.Lerp(original, baseColor, settings.contrastBoost - 1.0f);

        // 채도 적용
        float gray = contrast.grayscale;
        var saturated = Color.Lerp(new Color(gray, gray, gray), contrast, settings.saturationBoost);

        return saturated;
    }

    float4 ToFloat4(Color c)
    {
        return new float4(c.r, c.g, c.b, c.a);
    }

    [BurstCompile]
    public struct ColorClassificationJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vector3> positions;
        [ReadOnly] public NativeArray<Color32> inputColors;
        [ReadOnly] public ColorClassificationSettings settings; // struct이므로 값 타입
        [ReadOnly] public Bounds bounds;

        // 색상 매핑
        [ReadOnly] public float4 interiorColor;
        [ReadOnly] public float4 exteriorColor;
        [ReadOnly] public float4 boundaryColor;
        [ReadOnly] public float4 unknownColor;

        public NativeArray<Color32> outputColors;

        public void Execute(int index)
        {
            var position = positions[index];
            var originalColor = inputColors.Length > index ? inputColors[index] : new Color32(255, 255, 255, 255);

            // 포인트 분류
            var classification = ClassifyPoint(position);

            // 색상 선택
            float4 baseColor = classification switch
            {
                0 => interiorColor,  // Interior
                1 => exteriorColor,  // Exterior
                2 => boundaryColor,  // Boundary
                _ => unknownColor    // Unknown
            };

            // 색상 향상 적용
            var enhanced = ApplyEnhancement(baseColor, originalColor);

            outputColors[index] = enhanced;
        }

        int ClassifyPoint(Vector3 position)
        {
            var center = bounds.center;
            var extents = bounds.extents;

            var normalizedPos = new float3(
                (position.x - center.x) / extents.x,
                (position.y - center.y) / extents.y,
                (position.z - center.z) / extents.z
            );

            float distanceFromCenter = math.length(normalizedPos);
            float distanceFromBoundary = 1.0f - distanceFromCenter;

            if (distanceFromBoundary < settings.surfaceThreshold)
                return 2; // Boundary
            else if (distanceFromCenter < 0.8f)
                return 0; // Interior
            else
                return 1; // Exterior
        }

        Color32 ApplyEnhancement(float4 baseColor, Color32 originalColor)
        {
            var original = new float4(
                originalColor.r / 255f,
                originalColor.g / 255f,
                originalColor.b / 255f,
                originalColor.a / 255f
            );

            // 대비 적용
            var contrast = math.lerp(original, baseColor, settings.contrastBoost - 1.0f);

            // 채도 적용 (간단한 grayscale 계산)
            float gray = contrast.x * 0.299f + contrast.y * 0.587f + contrast.z * 0.114f;
            var grayColor = new float4(gray, gray, gray, contrast.w);
            var saturated = math.lerp(grayColor, contrast, settings.saturationBoost);

            return new Color32(
                (byte)(math.clamp(saturated.x, 0f, 1f) * 255),
                (byte)(math.clamp(saturated.y, 0f, 1f) * 255),
                (byte)(math.clamp(saturated.z, 0f, 1f) * 255),
                255
            );
        }
    }
}