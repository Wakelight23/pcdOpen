using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// Job System���� ����� �� �ֵ��� struct�� ���� (class �� struct)
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

    // �⺻�� ������ ���� ���� ������Ƽ
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
    [Tooltip("Job System�� ������� ����")]
    public bool useJobSystem = true;

    [Header("Color Mapping")]
    public Color interiorColor = new Color(0.2f, 0.8f, 0.2f); // ���
    public Color exteriorColor = new Color(0.8f, 0.2f, 0.2f); // ������  
    public Color boundaryColor = new Color(0.2f, 0.2f, 0.8f); // �Ķ���
    public Color unknownColor = Color.white;

    // �з� ��� ����
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
                settings = settings, // struct�̹Ƿ� �� �����
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
        // ������ �з� ���� (���� ���������� �� ������ �˰��� ���)
        var classification = ClassifyPointType(position, bounds, allPositions);

        Color baseColor = classification switch
        {
            PointClassification.Interior => interiorColor,
            PointClassification.Exterior => exteriorColor,
            PointClassification.Boundary => boundaryColor,
            _ => unknownColor
        };

        // ���� ��� ����
        var enhancedColor = ApplyColorEnhancement(baseColor, originalColor);

        return new Color32(
            (byte)(enhancedColor.r * 255),
            (byte)(enhancedColor.g * 255),
            (byte)(enhancedColor.b * 255),
            255);
    }

    PointClassification ClassifyPointType(Vector3 position, Bounds bounds, Vector3[] allPositions)
    {
        // ���κ����� �Ÿ� ��� ���� �з�
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
        // ���� ä�� ���
        var original = new Color(originalColor.r / 255f, originalColor.g / 255f, originalColor.b / 255f);

        // ��� ����
        var contrast = Color.Lerp(original, baseColor, settings.contrastBoost - 1.0f);

        // ä�� ����
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
        [ReadOnly] public ColorClassificationSettings settings; // struct�̹Ƿ� �� Ÿ��
        [ReadOnly] public Bounds bounds;

        // ���� ����
        [ReadOnly] public float4 interiorColor;
        [ReadOnly] public float4 exteriorColor;
        [ReadOnly] public float4 boundaryColor;
        [ReadOnly] public float4 unknownColor;

        public NativeArray<Color32> outputColors;

        public void Execute(int index)
        {
            var position = positions[index];
            var originalColor = inputColors.Length > index ? inputColors[index] : new Color32(255, 255, 255, 255);

            // ����Ʈ �з�
            var classification = ClassifyPoint(position);

            // ���� ����
            float4 baseColor = classification switch
            {
                0 => interiorColor,  // Interior
                1 => exteriorColor,  // Exterior
                2 => boundaryColor,  // Boundary
                _ => unknownColor    // Unknown
            };

            // ���� ��� ����
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

            // ��� ����
            var contrast = math.lerp(original, baseColor, settings.contrastBoost - 1.0f);

            // ä�� ���� (������ grayscale ���)
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