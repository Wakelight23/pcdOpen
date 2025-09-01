using UnityEngine;

[CreateAssetMenu(
    fileName = "PcdEdlSettings",
    menuName = "Pcd/EDL Settings",
    order = 0)]
public class PcdEdlSettings : ScriptableObject
{
    [Header("EDL Parameters")]
    [Tooltip("샘플 오프셋 반경(픽셀). EDL에서 주변 깊이를 탐색하는 거리입니다.")]
    [Range(0.25f, 8.0f)] public float edlRadius = 2.0f;

    [Tooltip("윤곽 감쇠 강도. 값이 클수록 음영 대비가 강해집니다.")]
    [Range(0.0f, 3.0f)] public float edlStrength = 0.5f;

    [Tooltip("EDL 적용 후 밝기 보정 계수.")]
    [Range(0.25f, 3.0f)] public float brightnessBoost = 1.0f;

    [Tooltip("8방향(High) / 4방향(Low) 샘플 선택.")]
    public bool highQuality = true;

    [Header("Front Visibility / Depth Proxy")]
    [Tooltip("전면 가시성 프록시(invDepth) RT 포맷. 대부분 RFloat 권장.")]
    public RenderTextureFormat depthFormat = RenderTextureFormat.RFloat;

    [Tooltip("invDepth 전면 일치 허용오차. Accum/ColorLite에서 _PcdDepthRT와 비교할 때 사용.")]
    [Range(1e-5f, 5e-1f)] public float depthMatchEps = 0.001f;

    [Tooltip("Accum 셰이더에서 전면 일치 누적을 사용할지 여부(비일치 포인트는 weight=0/또는 discard).")]
    public bool frontOnlyInAccum = true;

    [Tooltip("ColorLite 경로에서 전면 일치 필터를 사용할지 여부.")]
    public bool frontOnlyInColorLite = true;

    [Header("Color/Intermediate (optional)")]
    [Tooltip("EDL 입력 컬러 임시 RT 포맷(필요 시 사용).")]
    public RenderTextureFormat colorFormat = RenderTextureFormat.ARGBHalf;

#if UNITY_EDITOR
    void OnValidate()
    {
        // 일부 플랫폼에서 RFloat 미지원 시 대체 경고
        if (depthFormat == RenderTextureFormat.RFloat == false)
        {
            // 의도적으로 비워둠: 프로젝트/플랫폼에 따라 교체 로직을 넣을 수 있음
        }
    }
#endif
}
