using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PcdBillboardRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public RenderPassEvent depthProxyEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent accumEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent normEdlEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        public string accumName = "_Accum";
        public RenderTextureFormat accumFormat = RenderTextureFormat.ARGB32;

        // Materials
        public Material splatAccumMaterial;     // Custom/PcdSplatAccum
        public Material normEdlMaterial;        // Shaders/NormEDL

        // LOD/Budget
        public int pointBudget = 2_000_000;
        [Range(0.5f, 3.0f)] public float sseThreshold = 1.25f;
        [Range(0.0f, 0.5f)] public float sseHysteresis = 0.15f;

        // EDL Params
        public PcdEdlSettings edlSettings;
        [Range(0.5f, 4.0f)] public float edlRadiusScaleK = 0.35f;
        public int edlDownsample = 1;
    }

    [SerializeField] public Settings settings = new Settings();

    AccumPass _accumPass;
    CombinedNormEdlPass _combinedPass;

    // Shared IDs
    static readonly int ID_KernelSharpness = Shader.PropertyToID("_KernelSharpness");
    static readonly int ID_Gaussian = Shader.PropertyToID("_Gaussian");
    static readonly int ID_EdlRadius = Shader.PropertyToID("_EdlRadius");
    static readonly int ID_EdlStrength = Shader.PropertyToID("_EdlStrength");
    static readonly int ID_BrightnessBoost = Shader.PropertyToID("_BrightnessBoost");
    static readonly int ID_SplatPxRadius = Shader.PropertyToID("_SplatPxRadius");
    static readonly int ID_KernelShape = Shader.PropertyToID("_KernelShape");
    static readonly int ID_GaussSigmaPx = Shader.PropertyToID("_GaussianSigmaPx");
    static readonly int ID_GlobalAvgPx = Shader.PropertyToID("_PcdAvgPointPx");

    public override void Create()
    {
        _accumPass = new AccumPass(settings) { renderPassEvent = settings.accumEvent };
        _combinedPass = new CombinedNormEdlPass(settings) { renderPassEvent = settings.normEdlEvent };
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData rd)
    {
        if (_combinedPass == null || _accumPass == null) return;
        _combinedPass.SetSources(_accumPass._accum);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_accumPass != null) renderer.EnqueuePass(_accumPass);
        if (_combinedPass != null) renderer.EnqueuePass(_combinedPass);
    }

    // ===== Accum (opaque color target) =====
    class AccumPass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _cameraDepth;
        public RTHandle _accum;
        PcdGpuRenderer[] _renderers;

        public AccumPass(Settings s) { _settings = s; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            _cameraDepth = rd.cameraData.renderer.cameraDepthTargetHandle;
            var desc = rd.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.colorFormat = _settings.accumFormat;
            RenderingUtils.ReAllocateIfNeeded(ref _accum, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _settings.accumName);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_settings.splatAccumMaterial == null) return;

            var cmd = CommandBufferPool.Get("PcdAccum Opaque");
            using (new ProfilingScope(cmd, new ProfilingSampler("Pcd Splat Accum (Opaque)")))
            {
                cmd.SetRenderTarget(_accum, _cameraDepth);
                cmd.ClearRenderTarget(false, true, Color.clear);

                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

                // ����ȭ �Ķ���� (����)
                _settings.splatAccumMaterial.SetFloat(ID_KernelSharpness, _settings.sseThreshold);
                _settings.splatAccumMaterial.SetFloat(ID_Gaussian, 0f);

                var cam = rd.cameraData.camera;

                // ��� px ��� �� ���� ����
                float sumPx = 0f; int sumPts = 0;
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    // LOD ���� �� ��� px ���ø�
                    float avgPxR = r.ComputeAveragePointPx(cam, 16); // ������ ���÷� ��� 
                    sumPx += avgPxR * r.totalPointCount;            // ���� ��տ� ���� 
                    sumPts += r.totalPointCount;

                    _settings.splatAccumMaterial.SetMatrix("_LocalToWorld", r.transform.localToWorldMatrix);
                    r.RenderSplatAccum(cmd, cam);
                }
                float globalAvgPx = (sumPts > 0) ? (sumPx / Mathf.Max(1, sumPts)) : 1f;
                cmd.SetGlobalFloat(ID_GlobalAvgPx, globalAvgPx);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    // ===== Combined: EDL-only using camera depth =====
    class CombinedNormEdlPass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _cameraColor;
        RTHandle _colorSrc;
        RTHandle _accumDs;

        Material _mat;
        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd EDL (Opaque)");

        public CombinedNormEdlPass(Settings s)
        {
            _settings = s;
            _mat = (s.normEdlMaterial != null) ? s.normEdlMaterial : CoreUtils.CreateEngineMaterial("Shaders/NormEDL");
        }

        public void SetSources(RTHandle colorSrc) { _colorSrc = colorSrc; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            _cameraColor = rd.cameraData.renderer.cameraColorTargetHandle;

            // ī�޶� ���� �ؽ�ó �ʿ� ���� (URP�� _CameraDepthTexture�� �غ�)
            ConfigureInput(ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Depth);

            int ds = Mathf.Max(1, _settings.edlDownsample);
            if (ds > 1)
            {
                var desc = rd.cameraData.cameraTargetDescriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.colorFormat = _settings.accumFormat;
                desc.width = Mathf.Max(1, desc.width / ds);
                desc.height = Mathf.Max(1, desc.height / ds);
                RenderingUtils.ReAllocateIfNeeded(ref _accumDs, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_Accum_EDL_DS");
            }
        }

        void UpdateEdlParams()
        {
            float avgPx = Shader.GetGlobalFloat(ID_GlobalAvgPx);
            float splatRadius = Mathf.Max(1f, 0.5f * Mathf.Max(1f, avgPx));
            float edlRadiusK = Mathf.Max(0.5f, _settings.edlRadiusScaleK);

            _mat.SetFloat(ID_EdlRadius, edlRadiusK);
            _mat.SetFloat(ID_SplatPxRadius, splatRadius);
            _mat.SetFloat(ID_EdlStrength, _settings.edlSettings.edlStrength);
            _mat.SetFloat(ID_BrightnessBoost, _settings.edlSettings.brightnessBoost);

            bool accumGaussian = _settings.splatAccumMaterial != null && _settings.splatAccumMaterial.GetFloat("_Gaussian") > 0.5f;
            _mat.SetFloat(ID_KernelShape, accumGaussian ? 2f : 1f);
            _mat.SetFloat(ID_GaussSigmaPx, Mathf.Max(0.5f, splatRadius * 0.5f));

            if (_settings.edlSettings.highQuality) _mat.EnableKeyword("EDL_HIGH_QUALITY");
            else _mat.DisableKeyword("EDL_HIGH_QUALITY");
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_cameraColor == null || _colorSrc == null || _mat == null) return;

            var cmd = CommandBufferPool.Get("Pcd EDL (Opaque)");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                UpdateEdlParams();

                int ds = Mathf.Max(1, _settings.edlDownsample);
                if (ds > 1 && _accumDs != null)
                {
                    Blitter.BlitCameraTexture(cmd, _colorSrc, _accumDs);
                    Blitter.BlitCameraTexture(cmd, _accumDs, _cameraColor, _mat, 0); // Pass 1 : EDLOnly
                }
                else
                {
                    Blitter.BlitCameraTexture(cmd, _colorSrc, _cameraColor, _mat, 0);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}


/*using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PcdBillboardRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public RenderPassEvent depthProxyEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent accumEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent normEdlEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        public string accumName = "_Accum";
        public RenderTextureFormat accumFormat = RenderTextureFormat.ARGB32;

        // Materials
        public Material splatAccumMaterial;     // Custom/PcdSplatAccum
        public Material normEdlMaterial;        // Shaders/NormEDL

        // LOD/Budget
        public int pointBudget = 2_000_000;
        [Range(0.5f, 3.0f)] public float sseThreshold = 1.25f;
        [Range(0.0f, 0.5f)] public float sseHysteresis = 0.15f;

        // EDL Params (ScriptableObject)
        public PcdEdlSettings edlSettings;
        [Range(0.5f, 4.0f)] public float edlRadiusScaleK = 0.35f;
        public int edlDownsample = 1;

        // RT Names
        public string edlDepthRTName = "_PcdDepthRT";
    }

    [SerializeField] public Settings settings = new Settings();

    // DepthProxyPass _depthPass;
    AccumPass _accumPass;
    CombinedNormEdlPass _combinedPass;

    // public RTHandle PcdDepth => _depthPass?.PcdDepthRT;

    // Shader IDs (����)
    static readonly int ID_PointSize = Shader.PropertyToID("_PointSize");
    static readonly int ID_KernelSharpness = Shader.PropertyToID("_KernelSharpness");
    static readonly int ID_Gaussian = Shader.PropertyToID("_Gaussian");
    static readonly int ID_PcdDepthRT = Shader.PropertyToID("_PcdDepthRT");
    static readonly int ID_DepthEps = Shader.PropertyToID("_DepthMatchEps");
    static readonly int ID_EdlRadius = Shader.PropertyToID("_EdlRadius");
    static readonly int ID_EdlStrength = Shader.PropertyToID("_EdlStrength");
    static readonly int ID_BrightnessBoost = Shader.PropertyToID("_BrightnessBoost");
    static readonly int ID_SplatPxRadius = Shader.PropertyToID("_SplatPxRadius");
    static readonly int ID_KernelShape = Shader.PropertyToID("_KernelShape");
    static readonly int ID_GaussSigmaPx = Shader.PropertyToID("_GaussianSigmaPx");
    static readonly int ID_PcdDepth = Shader.PropertyToID("_PcdDepth");
    static readonly int ID_GlobalAvgPx = Shader.PropertyToID("_PcdAvgPointPx");

    public override void Create()
    {
        // _depthPass = new DepthProxyPass(settings) { renderPassEvent = settings.depthProxyEvent };
        _accumPass = new AccumPass(settings) { renderPassEvent = settings.accumEvent };
        _combinedPass = new CombinedNormEdlPass(settings) { renderPassEvent = settings.normEdlEvent };
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData rd)
    {
        if (_combinedPass == null) return;
        var camColor = renderer.cameraColorTargetHandle;
        var camDepth = renderer.cameraDepthTargetHandle;

        // ������ ���: �÷��� ī�޶� �÷�, ���̴� ���Ͻ�(depth RT) �켱
        // _combinedPass.SetSources(camColor, _depthPass != null ? _depthPass.PcdDepthRT : camDepth);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // if (_depthPass != null) renderer.EnqueuePass(_depthPass);
        if (_accumPass != null) renderer.EnqueuePass(_accumPass);
        if (_combinedPass != null) renderer.EnqueuePass(_combinedPass);
    }

    // ===== DepthProxy (invDepth RFloat) =====
    class DepthProxyPass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _pcdDepthRT;
        PcdGpuRenderer[] _renderers;
        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd DepthProxy (invDepth)");
        public RTHandle PcdDepthRT => _pcdDepthRT;

        public DepthProxyPass(Settings s) { _settings = s; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            var desc = rd.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.colorFormat = _settings.edlSettings.depthFormat; // RFloat
            RenderingUtils.ReAllocateIfNeeded(ref _pcdDepthRT, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _settings.edlDepthRTName);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_pcdDepthRT == null) return;

            var cmd = CommandBufferPool.Get("Pcd DepthProxy");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

                var cam = rd.cameraData.camera;
                cmd.SetRenderTarget(_pcdDepthRT);
                cmd.ClearRenderTarget(false, true, Color.clear);

                float sumPx = 0f; int sumPts = 0;
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    float avgPxR = r.UpdateLodAndBudget(cam, _settings.pointBudget, _settings.sseThreshold, _settings.sseHysteresis);
                    sumPx += avgPxR * r.totalPointCount;
                    sumPts += r.totalPointCount;
                    r.RenderSplatDepthProxy(cmd, cam);
                }
                float globalAvgPx = (sumPts > 0) ? (sumPx / Mathf.Max(1, sumPts)) : 1f;
                cmd.SetGlobalFloat(ID_GlobalAvgPx, globalAvgPx);
                cmd.SetGlobalTexture(ID_PcdDepthRT, _pcdDepthRT);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    // ===== Accum (opaque color; still sets related params for sync) =====
    class AccumPass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _cameraDepth;
        RTHandle _accum;
        PcdGpuRenderer[] _renderers;

        public RTHandle Accum => _accum;
        RTHandle _pcdDepthRT;

        public AccumPass(Settings s) { _settings = s; }
        public void SetDepthSource(RTHandle depthRT) { _pcdDepthRT = depthRT; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            _cameraDepth = rd.cameraData.renderer.cameraDepthTargetHandle;
            var desc = rd.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.colorFormat = _settings.accumFormat;
            RenderingUtils.ReAllocateIfNeeded(ref _accum, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _settings.accumName);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_settings.splatAccumMaterial == null) return;

            var cmd = CommandBufferPool.Get("PcdAccum Opaque");
            using (new ProfilingScope(cmd, new ProfilingSampler("Pcd Splat Accum (Opaque)")))
            {
                cmd.SetRenderTarget(_accum, _cameraDepth);
                cmd.ClearRenderTarget(false, true, Color.clear);

                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

                // ����ȭ �Ķ���� (����)
                _settings.splatAccumMaterial.SetFloat(ID_KernelSharpness, _settings.sseThreshold);
                _settings.splatAccumMaterial.SetFloat(ID_Gaussian, 0f);
                if (_pcdDepthRT != null)
                {
                    _settings.splatAccumMaterial.SetTexture(ID_PcdDepthRT, _pcdDepthRT);
                    _settings.splatAccumMaterial.SetFloat(ID_DepthEps, _settings.edlSettings.depthMatchEps);
                }

                var cam = rd.cameraData.camera;
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    _settings.splatAccumMaterial.SetMatrix("_LocalToWorld", r.transform.localToWorldMatrix);
                    r.RenderSplatAccum(cmd, cam);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    // ===== Combined: EDL-only (opaque) =====
    class CombinedNormEdlPass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _cameraColor;
        RTHandle _colorSrc;   // ī�޶� �÷�
        RTHandle _pcdDepthRT; // invDepth or camera depth
        RTHandle _accumDs;

        Material _mat;
        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd EDL (Opaque)");

        public CombinedNormEdlPass(Settings s)
        {
            _settings = s;
            _mat = (s.normEdlMaterial != null) ? s.normEdlMaterial : CoreUtils.CreateEngineMaterial("Shaders/NormEDL");
        }

        public void SetSources(RTHandle colorSrc, RTHandle pcdDepth) { _colorSrc = colorSrc; _pcdDepthRT = pcdDepth; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            _cameraColor = rd.cameraData.renderer.cameraColorTargetHandle;

            int ds = Mathf.Max(1, _settings.edlDownsample);
            if (ds > 1)
            {
                var desc = rd.cameraData.cameraTargetDescriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.colorFormat = _settings.accumFormat;
                desc.width = Mathf.Max(1, desc.width / ds);
                desc.height = Mathf.Max(1, desc.height / ds);
                RenderingUtils.ReAllocateIfNeeded(ref _accumDs, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_Accum_EDL_DS");
            }
        }

        void UpdateEdlParams()
        {
            // ��� ����Ʈ ũ�� ��� �Ķ����
            float avgPx = Shader.GetGlobalFloat(ID_GlobalAvgPx);
            float splatRadiusPx = Mathf.Max(1f, 0.5f * Mathf.Max(1f, avgPx));
            float edlRadiusCoef = Mathf.Max(0.5f, _settings.edlRadiusScaleK);

            _mat.SetFloat(ID_EdlRadius, edlRadiusCoef);
            _mat.SetFloat(ID_SplatPxRadius, splatRadiusPx);
            _mat.SetFloat(ID_EdlStrength, _settings.edlSettings.edlStrength);
            _mat.SetFloat(ID_BrightnessBoost, _settings.edlSettings.brightnessBoost);
            _mat.SetTexture(ID_PcdDepth, _pcdDepthRT);

            bool accumGaussian = _settings.splatAccumMaterial != null && _settings.splatAccumMaterial.GetFloat("_Gaussian") > 0.5f;
            _mat.SetFloat(ID_KernelShape, accumGaussian ? 2f : 1f);
            _mat.SetFloat(ID_GaussSigmaPx, Mathf.Max(0.5f, splatRadiusPx * 0.5f));

            if (_settings.edlSettings.highQuality) _mat.EnableKeyword("EDL_HIGH_QUALITY");
            else _mat.DisableKeyword("EDL_HIGH_QUALITY");
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_cameraColor == null || _colorSrc == null || _pcdDepthRT == null || _mat == null) return;

            var cmd = CommandBufferPool.Get("Pcd EDL (Opaque)");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                UpdateEdlParams();

                int ds = Mathf.Max(1, _settings.edlDownsample);
                if (ds > 1 && _accumDs != null)
                {
                    Blitter.BlitCameraTexture(cmd, _colorSrc, _accumDs);
                    Blitter.BlitCameraTexture(cmd, _accumDs, _cameraColor, _mat, 1); // Pass 1 : EDL-only
                }
                else
                {
                    Blitter.BlitCameraTexture(cmd, _colorSrc, _cameraColor, _mat, 1);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}*/

// Old
/*using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// DepthProxy (invDepth) + Accum(RGBA= sumRGB + weightA) + Combined NormEDL(����ȭ+EDL 1�н�)
public class PcdBillboardRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        // ���� �̺�Ʈ(�ʿ� �� ������Ʈ�� �°� ����)
        public RenderPassEvent depthProxyEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent accumEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent normEdlEvent = RenderPassEvent.BeforeRenderingPostProcessing; // �ϳ��� �н��� ���� �ռ�

        // Accum RenderTarget
        public string accumName = "_Accum";
        public RenderTextureFormat accumFormat = RenderTextureFormat.ARGB32; // RGB=sum, A=weight

        // Materials
        public Material splatAccumMaterial;      // Custom/PcdSplatAccum (���� RT ����)
        public Material normEdlMaterial;         // Shaders/NormEDL

        // ����/LOD/EDL
        public int pointBudget = 2_000_000;
        [Range(0.5f, 3.0f)] public float sseThreshold = 1.25f;
        [Range(0.0f, 0.5f)] public float sseHysteresis = 0.15f;

        [Header("EDL Params")]
        public PcdEdlSettings edlSettings;
        [Range(0.5f, 4.0f)] public float edlRadiusScaleK = 0.35f; // avgPx ������
        [Tooltip("EDL ������ �ٿ���� �ػ󵵿��� ����(1=Ǯ�ػ�, 2=Half, 4=Quarter)")]
        public int edlDownsample = 1; // 1/2/4

        [Header("RT Names")]
        public string edlDepthRTName = "_PcdDepthRT"; // DepthProxy ��� �ؽ�ó��
    }

    [SerializeField] public Settings settings = new Settings();

    DepthProxyPass _depthPass;
    AccumPass _accumPass;
    CombinedNormEdlPass _combinedPass;

    public RTHandle PcdDepth => _depthPass?.PcdDepthRT;

    public override void Create()
    {
        _depthPass = new DepthProxyPass(settings) { renderPassEvent = settings.depthProxyEvent };
        _accumPass = new AccumPass(settings) { renderPassEvent = settings.accumEvent };
        _combinedPass = new CombinedNormEdlPass(settings) { renderPassEvent = settings.normEdlEvent };
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData rd)
    {
        if (_combinedPass == null) return;
        var camColor = renderer.cameraColorTargetHandle;
        var camDepth = renderer.cameraDepthTargetHandle;

        // ������ ���: �÷��� ī�޶� �÷��� �״��, ���̴� ī�޶�/���Ͻ� ����
        _combinedPass.SetSources(camColor, _depthPass != null ? _depthPass.PcdDepthRT : camDepth);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_depthPass != null) renderer.EnqueuePass(_depthPass);     // invDepth
        if (_accumPass != null) renderer.EnqueuePass(_accumPass);     // sumRGB + weightA
        if (_combinedPass != null) renderer.EnqueuePass(_combinedPass);  // Normalize + EDL (1 pass)
    }

    // ===== DepthProxy (���� ����: invDepth RFloat ���) =====
    class DepthProxyPass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _cameraColor, _cameraDepth;
        RTHandle _pcdDepthRT;
        PcdGpuRenderer[] _renderers;
        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd DepthProxy (invDepth)");
        public RTHandle PcdDepthRT => _pcdDepthRT;

        public DepthProxyPass(Settings s) { _settings = s; }
        public void Setup(RTHandle cameraColor, RTHandle cameraDepth) { _cameraColor = cameraColor; _cameraDepth = cameraDepth; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            var desc = rd.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.colorFormat = _settings.edlSettings.depthFormat; // RFloat ����
            RenderingUtils.ReAllocateIfNeeded(ref _pcdDepthRT, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _settings.edlDepthRTName);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_pcdDepthRT == null) return;
            var cmd = CommandBufferPool.Get("Pcd DepthProxy");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

                var cam = rd.cameraData.camera;
                cmd.SetRenderTarget(_pcdDepthRT);
                cmd.ClearRenderTarget(false, true, Color.clear);

                float sumPx = 0f; int sumPts = 0;
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    float avgPxR = r.UpdateLodAndBudget(cam, _settings.pointBudget, _settings.sseThreshold, _settings.sseHysteresis);
                    sumPx += avgPxR * r.totalPointCount;
                    sumPts += r.totalPointCount;
                    r.RenderSplatDepthProxy(cmd, cam);
                }
                float globalAvgPx = (sumPts > 0) ? (sumPx / Mathf.Max(1, sumPts)) : 1f;
                cmd.SetGlobalFloat("_PcdAvgPointPx", globalAvgPx);
                cmd.SetGlobalTexture("_PcdDepthRT", _pcdDepthRT);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    // ===== Accum: ���� RT (RGB=sum, A=weight) =====
    class AccumPass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _cameraColor, _cameraDepth;
        RTHandle _accum;
        PcdGpuRenderer[] _renderers;

        static readonly int ID_PointSize = Shader.PropertyToID("_PointSize");
        static readonly int ID_KernelSharpness = Shader.PropertyToID("_KernelSharpness");
        static readonly int ID_Gaussian = Shader.PropertyToID("_Gaussian");
        static readonly int ID_PcdDepthRT = Shader.PropertyToID("_PcdDepthRT");
        static readonly int ID_DepthEps = Shader.PropertyToID("_DepthMatchEps");

        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd Splat Accum (RGBA)");

        public RTHandle Accum => _accum;

        RTHandle _pcdDepthRT;

        public AccumPass(Settings s) { _settings = s; }
        public void Setup(RTHandle cameraColor, RTHandle cameraDepth) { _cameraColor = cameraColor; _cameraDepth = cameraDepth; }
        public void SetDepthSource(RTHandle depthRT) { _pcdDepthRT = depthRT; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            var desc = rd.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.colorFormat = _settings.accumFormat;
            RenderingUtils.ReAllocateIfNeeded(ref _accum, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _settings.accumName);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_settings.splatAccumMaterial == null) return;
            var cmd = CommandBufferPool.Get("PcdAccum RGBA");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                cmd.SetRenderTarget(_accum, _cameraDepth);
                cmd.ClearRenderTarget(false, true, Color.clear);

                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

                _settings.splatAccumMaterial.SetFloat(ID_PointSize, _settings.edlSettings ? _settings.edlSettings.edlRadius : 5f);
                _settings.splatAccumMaterial.SetFloat(ID_KernelSharpness, _settings.sseThreshold);
                _settings.splatAccumMaterial.SetFloat(ID_Gaussian, 0f);

                if (_pcdDepthRT != null)
                {
                    _settings.splatAccumMaterial.SetTexture(ID_PcdDepthRT, _pcdDepthRT);
                    _settings.splatAccumMaterial.SetFloat(ID_DepthEps, _settings.edlSettings.depthMatchEps);
                }

                var cam = rd.cameraData.camera;
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    _settings.splatAccumMaterial.SetMatrix("_LocalToWorld", r.transform.localToWorldMatrix);
                    r.RenderSplatAccum(cmd, cam);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    // ===== Combined: Normalize + EDL (1 pass, �ɼ�: �ٿ���� ��ó�� 1ȸ) =====
    class CombinedNormEdlPass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _cameraColor;
        RTHandle _colorSrc;   // ī�޶� �÷�
        RTHandle _pcdDepthRT; // ����(���� RFloat �Ǵ� ī�޶� ����)
        RTHandle _accumDs;      // optional downsampled Accum

        Material _mat;
        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd Normalize+EDL (Combined)");

        public CombinedNormEdlPass(Settings s)
        {
            _settings = s;
            _mat = CoreUtils.CreateEngineMaterial("Shaders/NormEDL");
        }
        public void SetSources(RTHandle colorSrc, RTHandle pcdDepth) { _colorSrc = colorSrc; _pcdDepthRT = pcdDepth; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            // �ʿ� �� �ٿ���� RT �غ�
            int ds = Mathf.Max(1, _settings.edlDownsample);
            if (ds > 1)
            {
                var desc = rd.cameraData.cameraTargetDescriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.colorFormat = _settings.accumFormat;
                desc.width = Mathf.Max(1, desc.width / ds);
                desc.height = Mathf.Max(1, desc.height / ds);
                RenderingUtils.ReAllocateIfNeeded(ref _accumDs, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_Accum_EDL_DS");
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_cameraColor == null || _colorSrc == null || _pcdDepthRT == null || _mat == null) return;

            var cmd = CommandBufferPool.Get("Pcd EDL (Opaque)");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                float avgPx = Shader.GetGlobalFloat(Shader.PropertyToID("_PcdAvgPointPx"));
                float splatRadiusPx = Mathf.Max(1f, 0.5f * Mathf.Max(1f, avgPx));
                float edlRadius = Mathf.Max(0.5f, _settings.edlRadiusScaleK);

                _mat.SetFloat("_EdlRadius", edlRadius);
                _mat.SetFloat("_SplatPxRadius", splatRadiusPx);
                bool accumGaussian = _settings.splatAccumMaterial != null && _settings.splatAccumMaterial.GetFloat("_Gaussian") > 0.5f;
                _mat.SetFloat("_KernelShape", accumGaussian ? 2f : 1f);
                _mat.SetFloat("_GaussianSigmaPx", Mathf.Max(0.5f, splatRadiusPx * 0.5f));
                _mat.SetFloat("_EdlStrength", _settings.edlSettings.edlStrength);
                _mat.SetFloat("_BrightnessBoost", _settings.edlSettings.brightnessBoost);
                _mat.SetTexture("_PcdDepth", _pcdDepthRT);

                if (_settings.edlSettings.highQuality) _mat.EnableKeyword("EDL_HIGH_QUALITY");
                else _mat.DisableKeyword("EDL_HIGH_QUALITY");

                // ī�޶� �÷� �� ī�޶� �÷��� EDL�� ����
                Blitter.BlitCameraTexture(cmd, _colorSrc, _cameraColor, _mat, 1); // �� �н� �ε���(EDL-only)
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}*/