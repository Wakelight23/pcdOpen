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
    // static readonly int ID_KernelSharpness = Shader.PropertyToID("_KernelSharpness");
    // static readonly int ID_Gaussian = Shader.PropertyToID("_Gaussian");
    static readonly int ID_EdlRadius = Shader.PropertyToID("_EdlRadius");
    static readonly int ID_EdlStrength = Shader.PropertyToID("_EdlStrength");
    static readonly int ID_BrightnessBoost = Shader.PropertyToID("_BrightnessBoost");
    static readonly int ID_SplatPxRadius = Shader.PropertyToID("_SplatPxRadius");
    static readonly int ID_KernelShape = Shader.PropertyToID("_KernelShape");
    static readonly int ID_GaussSigmaPx = Shader.PropertyToID("_GaussianSigmaPx");
    static readonly int ID_GlobalAvgPx = Shader.PropertyToID("_PcdAvgPointPx");

    #region lnitialization
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
    #endregion

    #region Accum (opaque color target)
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
                float sumPx = 0f; int sumPts = 0;
                float globalAvgPx = (sumPts > 0) ? (sumPx / Mathf.Max(1, sumPts)) : 1f;
                bool accumGaussian = _settings.splatAccumMaterial.GetFloat("_Gaussian") > 0.5f;

                cmd.SetRenderTarget(_accum, _cameraDepth);
                cmd.ClearRenderTarget(false, true, Color.clear);
                cmd.SetGlobalFloat(ID_GlobalAvgPx, globalAvgPx);

                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

                // 동기화 파라미터 (선택)
                _settings.splatAccumMaterial.SetFloat("_KernelShape", accumGaussian ? 2f : 1f);
                _settings.splatAccumMaterial.SetFloat("_GaussianSigma", Mathf.Max(0.5f, globalAvgPx * 0.5f));
                _settings.splatAccumMaterial.SetFloat("_GaussianHardK", 0.05f); // 필요 시 인스펙터 노출

                var cam = rd.cameraData.camera;

                // 평균 px 계산 및 전역 세팅

                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    // LOD 갱신 겸 평균 px 샘플링
                    float avgPxR = r.ComputeAveragePointPx(cam, 16); // 가벼운 샘플러 사용 
                    sumPx += avgPxR * r.totalPointCount;            // 가중 평균용 누적 
                    sumPts += r.totalPointCount;

                    _settings.splatAccumMaterial.SetMatrix("_LocalToWorld", r.transform.localToWorldMatrix);
                    r.RenderSplatAccum(cmd, cam);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
    #endregion

    #region Combined: EDL-only using camera depth
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

            // 카메라 깊이 텍스처 필요 선언 (URP가 _CameraDepthTexture를 준비)
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
    #endregion
}