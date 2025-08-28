using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(Camera))]
public class PcdEdlCameraHook : MonoBehaviour
{
    public PcdEdlSettings settings;
    public Shader edlShader;
    RenderTexture _colorRT, _depthRT;
    Material _edlMat;
    CommandBuffer _cb;
    Camera _cam;
    PcdGpuRenderer[] _renderers;

    void OnEnable()
    {
        _cam = GetComponent<Camera>();
        if (settings == null) settings = ScriptableObject.CreateInstance<PcdEdlSettings>();
        if (_edlMat == null && edlShader != null) _edlMat = new Material(edlShader);
        _cb = new CommandBuffer { name = "Pcd EDL (Built-in)" };
        _cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, _cb);
    }

    void OnDisable()
    {
        if (_cb != null) { _cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _cb); _cb.Release(); _cb = null; }
        if (_colorRT != null) { _colorRT.Release(); _colorRT = null; }
        if (_depthRT != null) { _depthRT.Release(); _depthRT = null; }
    }

    void OnPreRender()
    {
        EnsureRTs();
        _cb.Clear();

        // 1) 포인트 전용 RT 렌더
        _cb.SetRenderTarget(_colorRT.colorBuffer, _depthRT.colorBuffer);
        _cb.ClearRenderTarget(true, true, Color.clear);

        if (_renderers == null || _renderers.Length == 0)
            _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

        foreach (var r in _renderers)
        {
            if (r == null || !r.isActiveAndEnabled) continue;
            r.RenderSplatAccum(_cb, _cam);
        }

        // 2) EDL 합성
        _edlMat.SetFloat("_EdlRadius", settings.edlRadius);
        _edlMat.SetFloat("_EdlStrength", settings.edlStrength);
        _edlMat.SetFloat("_BrightnessBoost", settings.brightnessBoost);
        if (settings.highQuality) _edlMat.EnableKeyword("EDL_HIGH_QUALITY"); else _edlMat.DisableKeyword("EDL_HIGH_QUALITY");
        _edlMat.SetTexture("_PcdColor", _colorRT);
        _edlMat.SetTexture("_PcdDepth", _depthRT);
        _edlMat.SetVector("_ScreenSize", new Vector4(_colorRT.width, _colorRT.height, 0, 0));
        _edlMat.SetVector("_ScreenPx", new Vector4(_colorRT.width, _colorRT.height, 0, 0));

        int temp = Shader.PropertyToID("_PcdEdlTemp");
        _cb.GetTemporaryRT(temp, _cam.pixelWidth, _cam.pixelHeight, 0, FilterMode.Bilinear, RenderTextureFormat.ARGB32);
        _cb.Blit(_colorRT, temp, _edlMat, 0);
        _cb.Blit(temp, BuiltinRenderTextureType.CameraTarget);
        _cb.ReleaseTemporaryRT(temp);
    }

    void EnsureRTs()
    {
        if (_colorRT == null || _colorRT.width != _cam.pixelWidth || _colorRT.height != _cam.pixelHeight)
        {
            if (_colorRT != null) _colorRT.Release();
            _colorRT = new RenderTexture(_cam.pixelWidth, _cam.pixelHeight, 0, settings.colorFormat) { name = "PcdColorRT", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _colorRT.Create();
        }
        if (_depthRT == null || _depthRT.width != _cam.pixelWidth || _depthRT.height != _cam.pixelHeight)
        {
            if (_depthRT != null) _depthRT.Release();
            _depthRT = new RenderTexture(_cam.pixelWidth, _cam.pixelHeight, 0, settings.depthFormat) { name = "PcdDepthRT", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _depthRT.Create();
        }
    }
}