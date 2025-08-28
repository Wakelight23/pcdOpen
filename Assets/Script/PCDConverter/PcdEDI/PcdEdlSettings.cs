using UnityEngine;


[CreateAssetMenu(fileName = "PcdEDLSettings", menuName = "Rendering/Pcd EDL Settings", order = 10)]
[System.Serializable]
public class PcdEdlSettings : ScriptableObject
{
    [Header("EDL Parameters")]
    [Range(0.5f, 4.0f)] public float edlRadius = 2.0f;
    [Range(0.0f, 2.0f)] public float edlStrength = 0.5f;
    [Range(0.5f, 2.0f)] public float brightnessBoost = 1.0f;
    public bool highQuality = true; // 4 vs 8 방향 샘플

    [Header("RT Formats")]
    public RenderTextureFormat colorFormat = RenderTextureFormat.ARGBHalf; // RGBA16F
    public RenderTextureFormat depthFormat = RenderTextureFormat.RFloat;   // 포인트 전용 깊이
}

