using UnityEngine;

[CreateAssetMenu(
    fileName = "PcdEdlSettings",
    menuName = "Pcd/EDL Settings",
    order = 0)]
public class PcdEdlSettings : ScriptableObject
{
    [Header("EDL Parameters")]
    [Tooltip("���� ������ �ݰ�(�ȼ�). EDL���� �ֺ� ���̸� Ž���ϴ� �Ÿ��Դϴ�.")]
    [Range(0.25f, 8.0f)] public float edlRadius = 2.0f;

    [Tooltip("���� ���� ����. ���� Ŭ���� ���� ��� �������ϴ�.")]
    [Range(0.0f, 3.0f)] public float edlStrength = 0.5f;

    [Tooltip("EDL ���� �� ��� ���� ���.")]
    [Range(0.25f, 3.0f)] public float brightnessBoost = 1.0f;

    [Tooltip("8����(High) / 4����(Low) ���� ����.")]
    public bool highQuality = true;

    [Header("Front Visibility / Depth Proxy")]
    [Tooltip("���� ���ü� ���Ͻ�(invDepth) RT ����. ��κ� RFloat ����.")]
    public RenderTextureFormat depthFormat = RenderTextureFormat.RFloat;

    [Tooltip("invDepth ���� ��ġ ������. Accum/ColorLite���� _PcdDepthRT�� ���� �� ���.")]
    [Range(1e-5f, 5e-1f)] public float depthMatchEps = 0.001f;

    [Tooltip("Accum ���̴����� ���� ��ġ ������ ������� ����(����ġ ����Ʈ�� weight=0/�Ǵ� discard).")]
    public bool frontOnlyInAccum = true;

    [Tooltip("ColorLite ��ο��� ���� ��ġ ���͸� ������� ����.")]
    public bool frontOnlyInColorLite = true;

    [Header("Color/Intermediate (optional)")]
    [Tooltip("EDL �Է� �÷� �ӽ� RT ����(�ʿ� �� ���).")]
    public RenderTextureFormat colorFormat = RenderTextureFormat.ARGBHalf;

#if UNITY_EDITOR
    void OnValidate()
    {
        // �Ϻ� �÷������� RFloat ������ �� ��ü ���
        if (depthFormat == RenderTextureFormat.RFloat == false)
        {
            // �ǵ������� �����: ������Ʈ/�÷����� ���� ��ü ������ ���� �� ����
        }
    }
#endif
}
