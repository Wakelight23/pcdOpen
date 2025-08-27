using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PcdEdlPass : ScriptableRenderPass
{
    readonly PcdEdlRenderFeature.Settings _settings;

    // ī�޶� Ÿ�� ������
    RTHandle _cameraColor;
    RTHandle _cameraDepth;

    // ���� RT
    RTHandle _colorRT;
    RTHandle _depthRT;

    Material _edlMat;
    PcdGpuRenderer[] _renderers;

    static readonly int ID_PcdColor = Shader.PropertyToID("_PcdColor");
    static readonly int ID_PcdDepth = Shader.PropertyToID("_PcdDepth");
    static readonly int ID_Radius = Shader.PropertyToID("_EdlRadius");
    static readonly int ID_Strength = Shader.PropertyToID("_EdlStrength");
    static readonly int ID_Boost = Shader.PropertyToID("_BrightnessBoost");
    static readonly int ID_ScreenPx = Shader.PropertyToID("_ScreenSize");

    public PcdEdlPass(PcdEdlRenderFeature.Settings settings)
    {
        _settings = settings;
        _edlMat = CoreUtils.CreateEngineMaterial("Shaders/EDL");
    }

    // Feature.SetupRenderPasses���� ȣ���
    public void Setup(RTHandle cameraColor, RTHandle cameraDepth)
    {
        _cameraColor = cameraColor;
        _cameraDepth = cameraDepth; // �ʿ� ������ ���常
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        // ī�޶� Ÿ�� ��ũ���ͷ� ���� RT �Ҵ�
        var desc = renderingData.cameraData.cameraTargetDescriptor;
        desc.depthBufferBits = 0;
        desc.colorFormat = _settings.edlSettings.colorFormat;
        RenderingUtils.ReAllocateIfNeeded(ref _colorRT, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _settings.colorRTName);

        var depthDesc = renderingData.cameraData.cameraTargetDescriptor;
        depthDesc.depthBufferBits = 0;
        depthDesc.colorFormat = _settings.edlSettings.depthFormat;
        RenderingUtils.ReAllocateIfNeeded(ref _depthRT, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _settings.depthRTName);
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (_edlMat == null || _cameraColor == null) return;

        var cmd = CommandBufferPool.Get("Pcd EDL");
        try
        {
            // 1) ����Ʈ ���� RT�� ����
            cmd.SetRenderTarget(_colorRT, _depthRT);
            cmd.ClearRenderTarget(true, true, Color.clear);

            if (_renderers == null || _renderers.Length == 0)
                _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

            var cam = renderingData.cameraData.camera;
            foreach (var r in _renderers)
            {
                if (r == null || !r.isActiveAndEnabled) continue;
                r.RenderIndirect(cmd, cam);
            }

            // 2) EDL �ռ� �� ī�޶� �÷� Ÿ������ ��
            cmd.SetGlobalTexture(ID_PcdColor, _colorRT);
            cmd.SetGlobalTexture(ID_PcdDepth, _depthRT);
            cmd.SetGlobalVector(ID_ScreenPx, new Vector4(_colorRT.rt.width, _colorRT.rt.height, 0, 0));
            _edlMat.SetFloat(ID_Radius, _settings.edlSettings.edlRadius);
            _edlMat.SetFloat(ID_Strength, _settings.edlSettings.edlStrength);
            _edlMat.SetFloat(ID_Boost, _settings.edlSettings.brightnessBoost);
            if (_settings.edlSettings.highQuality) _edlMat.EnableKeyword("EDL_HIGH_QUALITY"); else _edlMat.DisableKeyword("EDL_HIGH_QUALITY");

            // URP 12+ : RTHandle ��� Blit ��ƿ ���
            Blitter.BlitCameraTexture(cmd, _colorRT, _cameraColor, _edlMat, 0);
        }
        finally
        {
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}