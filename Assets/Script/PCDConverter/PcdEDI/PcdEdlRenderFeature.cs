using UnityEngine;
using UnityEngine.Rendering.Universal;

public class PcdEdlRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public PcdEdlSettings edlSettings;
        public RenderPassEvent evt = RenderPassEvent.AfterRenderingTransparents;
        public string colorRTName = "_PcdColorRT";
        public string depthRTName = "_PcdDepthRT";
    }

    public Settings settings = new Settings();
    PcdEdlPass _pass;

    public override void Create()
    {
        if (settings.edlSettings == null)
            settings.edlSettings = ScriptableObject.CreateInstance<PcdEdlSettings>();
        _pass = new PcdEdlPass(settings);
        _pass.renderPassEvent = settings.evt;
    }

    // 1) 여기서는 EnqueuePass만
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null) return;
        renderer.EnqueuePass(_pass);
    }

    // 2) 카메라 타깃 핸들은 여기서 전달
    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
    {
        if (_pass == null) return;
        _pass.Setup(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
    }
}