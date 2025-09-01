using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// DepthProxy (invDepth) + Accum(RGBA= sumRGB + weightA) + Combined NormEDL(정규화+EDL 1패스)
public class PcdBillboardRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        // 권장 이벤트(필요 시 프로젝트에 맞게 조정)
        public RenderPassEvent depthProxyEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent accumEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent normEdlEvent = RenderPassEvent.BeforeRenderingPostProcessing; // 하나의 패스로 최종 합성

        // Accum RenderTarget
        public string accumName = "_Accum";
        public RenderTextureFormat accumFormat = RenderTextureFormat.ARGB32; // RGB=sum, A=weight

        // Materials
        public Material splatAccumMaterial;      // Custom/PcdSplatAccum (단일 RT 버전)
        public Material normEdlMaterial;         // Shaders/NormEDL

        // 예산/LOD/EDL
        public int pointBudget = 2_000_000;
        [Range(0.5f, 3.0f)] public float sseThreshold = 1.25f;
        [Range(0.0f, 0.5f)] public float sseHysteresis = 0.15f;

        [Header("EDL Params")]
        public PcdEdlSettings edlSettings;
        [Range(0.5f, 4.0f)] public float edlRadiusScaleK = 0.35f; // avgPx 스케일
        [Tooltip("EDL 연산을 다운샘플 해상도에서 수행(1=풀해상도, 2=Half, 4=Quarter)")]
        public int edlDownsample = 1; // 1/2/4

        [Header("RT Names")]
        public string edlDepthRTName = "_PcdDepthRT"; // DepthProxy 출력 텍스처명
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
        if (_depthPass == null || _accumPass == null || _combinedPass == null) return;

        var camColor = renderer.cameraColorTargetHandle;
        var camDepth = renderer.cameraDepthTargetHandle;

        _depthPass.Setup(camColor, camDepth);
        _accumPass.Setup(camColor, camDepth);
        _combinedPass.Setup(camColor, camDepth);

        _accumPass.SetDepthSource(_depthPass.PcdDepthRT);
        _combinedPass.SetSources(_accumPass.Accum, _depthPass.PcdDepthRT);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_depthPass != null) renderer.EnqueuePass(_depthPass);     // invDepth
        if (_accumPass != null) renderer.EnqueuePass(_accumPass);     // sumRGB + weightA
        if (_combinedPass != null) renderer.EnqueuePass(_combinedPass);  // Normalize + EDL (1 pass)
    }

    // ===== DepthProxy (변경 없음: invDepth RFloat 출력) =====
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
            desc.colorFormat = _settings.edlSettings.depthFormat; // RFloat 권장
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

    // ===== Accum: 단일 RT (RGB=sum, A=weight) =====
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

    // ===== Combined: Normalize + EDL (1 pass, 옵션: 다운샘플 전처리 1회) =====
    class CombinedNormEdlPass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _cameraColor, _cameraDepth;
        RTHandle _accumSrc;     // Accum RGBA
        RTHandle _pcdDepthRT;   // invDepth
        RTHandle _accumDs;      // optional downsampled Accum

        Material _mat;
        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd Normalize+EDL (Combined)");

        public CombinedNormEdlPass(Settings s)
        {
            _settings = s;
            _mat = CoreUtils.CreateEngineMaterial("Shaders/NormEDL");
        }

        public void Setup(RTHandle cameraColor, RTHandle cameraDepth) { _cameraColor = cameraColor; _cameraDepth = cameraDepth; }
        public void SetSources(RTHandle accum, RTHandle pcdDepth) { _accumSrc = accum; _pcdDepthRT = pcdDepth; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            // 필요 시 다운샘플 RT 준비
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
            if (_cameraColor == null || _accumSrc == null || _pcdDepthRT == null || _mat == null) return;

            var cmd = CommandBufferPool.Get("Pcd Norm+EDL");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                // 파라미터
                float avgPx = Shader.GetGlobalFloat(Shader.PropertyToID("_PcdAvgPointPx"));
                float splatRadiusPx = Mathf.Max(1f, 0.5f * Mathf.Max(1f, avgPx));
                float edlRadiusCoef = Mathf.Max(0.5f, _settings.edlRadiusScaleK); // 계수 안전 범위
                float edlRadius = edlRadiusCoef; // 셰이더에서 픽셀 반경과 곱해 사용

                _mat.SetFloat("_EdlRadius", edlRadius);
                _mat.SetFloat("_SplatPxRadius", splatRadiusPx);

                // 커널 형태 전달: Accum의 가우시안 사용 유무를 그대로 전달하거나 옵션화
                // 여기서는 Accum 단계의 Gaussian 사용 여부를 머티리얼 기반으로 파악하기 어려우므로,
                // 전역 정책/옵션으로 두거나, splatAccumMaterial의 _Gaussian을 참조해 동기화.
                bool accumGaussian = _settings.splatAccumMaterial != null && _settings.splatAccumMaterial.GetFloat("_Gaussian") > 0.5f;
                _mat.SetFloat("_KernelShape", accumGaussian ? 2f : 1f /* 1=square, 2=gaussian; 원형 디스크를 쓰는 경우 0 */);

                // 가우시안 시그마(px): 포인트 반경과 비슷한 규모로 설정
                float sigmaPx = Mathf.Max(0.5f, splatRadiusPx * 0.5f);
                _mat.SetFloat("_GaussianSigmaPx", sigmaPx);

                _mat.SetFloat("_EdlStrength", _settings.edlSettings.edlStrength);
                _mat.SetFloat("_BrightnessBoost", _settings.edlSettings.brightnessBoost);
                _mat.SetTexture("_PcdDepth", _pcdDepthRT);

                if (_settings.edlSettings.highQuality) _mat.EnableKeyword("EDL_HIGH_QUALITY");
                else _mat.DisableKeyword("EDL_HIGH_QUALITY");

                // 선택: 다운샘플
                int ds = Mathf.Max(1, _settings.edlDownsample);
                if (ds > 1 && _accumDs != null)
                {
                    Blitter.BlitCameraTexture(cmd, _accumSrc, _accumDs); // 누적 텍스처를 저해상도로
                    Blitter.BlitCameraTexture(cmd, _accumDs, _cameraColor, _mat, 0); // 정규화+EDL→최종
                }
                else
                {
                    // 풀해상도 1패스
                    Blitter.BlitCameraTexture(cmd, _accumSrc, _cameraColor, _mat, 0);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}

// 4Pass
/*using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// PcdBillboardRenderFeature: DepthProxy(전면 가시성) + Accum + Normalize + EDL
public class PcdBillboardRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {

        public RenderPassEvent depthProxyEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent accumEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent normalizeEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        public RenderPassEvent edlEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        // Accum/Weight/Depth
        public string accumColorName = "_AccumColor";
        public string accumWeightName = "_AccumWeight";
        public string accumDepthName = "_AccumDepth";
        public RenderTextureFormat accumColorFormat = RenderTextureFormat.ARGB32;
        public RenderTextureFormat accumWeightFormat = RenderTextureFormat.RFloat;
        public RenderTextureFormat accumDepthFormat = RenderTextureFormat.RFloat;

        // 머티리얼
        public Material splatAccumMaterial;   // Custom/PcdSplatAccum
        public Material normalizeMaterial;    // Custom/PcdSplatNormalize

        public int pointBudget = 2_000_000;  // 총 활성 포인트 상한
        [Range(0.5f, 3.0f)] public float sseThreshold = 1.25f; // spacing*P 임계
        [Range(0.0f, 0.5f)] public float sseHysteresis = 0.15f; // 토글 히스테리시스
        [Range(0.5f, 4.0f)] public float edlRadiusScaleK = 0.35f; // EDL 반경 스케일 인자

        // Accum 파라미터
        [Range(0.5f, 64f)] public float pointSize = 5.0f;
        [Range(0.5f, 3f)] public float kernelSharpness = 1.5f;
        public bool gaussianKernel = false;

        // EDL/DepthProxy 설정
        public PcdEdlSettings edlSettings;      // edlSettings.depthFormat, edlStrength 등 포함
        public string edlDepthRTName = "_PcdDepthRT";
        public string edlSourceColorRTName = "_EdlSourceColor";
    }

    [SerializeField] public Settings settings = new Settings();


    DepthProxyPass _depthPass;
    AccumPass _accumPass;
    NormalizePass _normPass;
    EdlPass _edlPass;

    public RTHandle AccumDepth => _accumPass?.AccumDepth;
    public RTHandle PcdDepth => _depthPass?.PcdDepthRT;

    public override void Create()
    {
        _depthPass = new DepthProxyPass(settings) { renderPassEvent = settings.depthProxyEvent };
        _accumPass = new AccumPass(settings) { renderPassEvent = settings.accumEvent };
        _normPass = new NormalizePass(settings) { renderPassEvent = settings.normalizeEvent };
        _edlPass = new EdlPass(settings) { renderPassEvent = settings.edlEvent };
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
    {
        if (_depthPass == null || _accumPass == null || _normPass == null || _edlPass == null) return;

        var camColor = renderer.cameraColorTargetHandle;
        var camDepth = renderer.cameraDepthTargetHandle;

        // 타깃 전달
        _depthPass.Setup(camColor, camDepth);
        _accumPass.Setup(camColor, camDepth);
        _normPass.Setup(camColor, camDepth);
        _edlPass.Setup(camColor, camDepth);

        // Accum → Normalize 소스 연결
        _normPass.SetSources(_accumPass.AccumColor, _accumPass.AccumWeight);

        // DepthProxy 출력 전달(EDL은 필수, Accum은 선택: 프런트 일치 누적을 원하면 주입)
        _edlPass.SetDepthSource(_depthPass.PcdDepthRT);
        _accumPass.SetDepthSource(_depthPass.PcdDepthRT); // 필요 시 셰이더에서 전면 일치 비교 사용
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_depthPass != null) renderer.EnqueuePass(_depthPass); // 1) DepthProxy
        if (_accumPass != null) renderer.EnqueuePass(_accumPass); // 2) Accum
        if (_normPass != null) renderer.EnqueuePass(_normPass);  // 3) Normalize
        if (_edlPass != null) renderer.EnqueuePass(_edlPass);   // 4) EDL
    }

    // ================= DepthProxy Pass =================
    class DepthProxyPass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _cameraColor, _cameraDepth;
        RTHandle _pcdDepthRT; // RFloat invDepth
        PcdGpuRenderer[] _renderers;

        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd DepthProxy (invDepth)");
        public RTHandle PcdDepthRT => _pcdDepthRT;

        public DepthProxyPass(Settings settings) { _settings = settings; }

        public void Setup(RTHandle cameraColor, RTHandle cameraDepth)
        {
            _cameraColor = cameraColor;
            _cameraDepth = cameraDepth;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            var desc = rd.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.colorFormat = _settings.edlSettings.depthFormat; // 보통 RFloat
            RenderingUtils.ReAllocateIfNeeded(
                ref _pcdDepthRT, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _settings.edlDepthRTName
            );
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

                // invDepth 누적
                cmd.SetRenderTarget(_pcdDepthRT);
                cmd.ClearRenderTarget(false, true, Color.clear);

                float sumPx = 0f; int sumPts = 0;
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    // LOD + 예산 선택(전역 예산은 Settings에 배치)
                    float avgPxR = r.UpdateLodAndBudget(cam, _settings.pointBudget, _settings.sseThreshold, _settings.sseHysteresis);
                    // 가중 평균(활성 포인트 기준)
                    sumPx += avgPxR * r.totalPointCount;
                    sumPts += r.totalPointCount;
                    r.RenderSplatDepthProxy(cmd, cam);
                }

                //foreach (var r in _renderers)
                //{
                //    if (r == null || !r.isActiveAndEnabled) continue;

                //}

                float globalAvgPx = (sumPts > 0) ? (sumPx / Mathf.Max(1, sumPts)) : _settings.pointSize;
                cmd.SetGlobalFloat("_PcdAvgPointPx", globalAvgPx);

                // 전역으로도 바인딩(선택)
                cmd.SetGlobalTexture("_PcdDepthRT", _pcdDepthRT);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    // ================= Accumulation Pass =================
    class AccumPass : ScriptableRenderPass
    {
        readonly Settings _settings;

        RTHandle _cameraColor, _cameraDepth;
        RTHandle _accumColor, _accumWeight, _accumDepth;

        // 외부 접근
        public RTHandle AccumDepth => _accumDepth;
        public RTHandle AccumColor => _accumColor;
        public RTHandle AccumWeight => _accumWeight;

        // DepthProxy 입력(선택)
        RTHandle _pcdDepthRT;
        static readonly int ID_PointSize = Shader.PropertyToID("_PointSize");
        static readonly int ID_KernelSharpness = Shader.PropertyToID("_KernelSharpness");
        static readonly int ID_Gaussian = Shader.PropertyToID("_Gaussian");
        static readonly int ID_PcdDepthRT = Shader.PropertyToID("_PcdDepthRT");
        static readonly int ID_DepthEps = Shader.PropertyToID("_DepthMatchEps");

        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd Splat Accum");
        PcdGpuRenderer[] _renderers;

        public AccumPass(Settings settings) { _settings = settings; }
        public void Setup(RTHandle cameraColor, RTHandle cameraDepth) { _cameraColor = cameraColor; _cameraDepth = cameraDepth; }
        public void SetDepthSource(RTHandle depthRT) { _pcdDepthRT = depthRT; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            var desc = rd.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.colorFormat = _settings.accumColorFormat;
            RenderingUtils.ReAllocateIfNeeded(ref _accumColor, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _settings.accumColorName);

            var descW = rd.cameraData.cameraTargetDescriptor;
            descW.depthBufferBits = 0;
            descW.colorFormat = _settings.accumWeightFormat;
            RenderingUtils.ReAllocateIfNeeded(ref _accumWeight, descW, FilterMode.Point, TextureWrapMode.Clamp, name: _settings.accumWeightName);

            var descD = rd.cameraData.cameraTargetDescriptor;
            descD.depthBufferBits = 0;
            descD.colorFormat = _settings.accumDepthFormat;
            RenderingUtils.ReAllocateIfNeeded(ref _accumDepth, descD, FilterMode.Point, TextureWrapMode.Clamp, name: _settings.accumDepthName);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_settings.splatAccumMaterial == null) return;

            var cmd = CommandBufferPool.Get("PcdAccum");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                var mrt = new RenderTargetIdentifier[] { _accumColor.nameID, _accumWeight.nameID };
                cmd.SetRenderTarget(mrt, _cameraDepth.nameID);
                cmd.ClearRenderTarget(false, true, Color.clear);

                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

                _settings.splatAccumMaterial.SetFloat(ID_PointSize, _settings.pointSize);
                _settings.splatAccumMaterial.SetFloat(ID_KernelSharpness, _settings.kernelSharpness);
                _settings.splatAccumMaterial.SetFloat(ID_Gaussian, _settings.gaussianKernel ? 1f : 0f);

                // 전면 프록시 전달(Accum 셰이더에서 사용할 경우)
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

    // ================= Normalize Pass =================
    class NormalizePass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _cameraColor, _cameraDepth;
        RTHandle _accumColor, _accumWeight;

        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd Splat Normalize");

        public NormalizePass(Settings settings) { _settings = settings; }
        public void Setup(RTHandle cameraColor, RTHandle cameraDepth) { _cameraColor = cameraColor; _cameraDepth = cameraDepth; }
        public void SetSources(RTHandle accumColor, RTHandle accumWeight) { _accumColor = accumColor; _accumWeight = accumWeight; }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_settings.normalizeMaterial == null) return;
            if (_accumColor == null || _accumWeight == null) return;

            var camData = rd.cameraData;
            if (camData.isPreviewCamera || camData.isSceneViewCamera) return;
            if (_cameraColor == null || _cameraColor.rt == null) return;

            var cmd = CommandBufferPool.Get("PcdNormalize");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                _settings.normalizeMaterial.SetTexture("_ColorAccum", _accumColor);
                _settings.normalizeMaterial.SetTexture("_WeightAccum", _accumWeight);
                Blitter.BlitCameraTexture(cmd, _accumColor, _cameraColor, _settings.normalizeMaterial, 0);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    // ================= EDL Pass =================
    class EdlPass : ScriptableRenderPass
    {
        readonly Settings _settings;
        RTHandle _cameraColor, _cameraDepth;

        // 외부로부터 주입받는 깊이 프록시
        RTHandle _pcdDepthRT;

        // 카메라 컬러 임시 복사
        RTHandle _edlSourceColor;

        Material _edlMat;
        PcdGpuRenderer[] _renderers;

        static readonly int ID_PcdColor = Shader.PropertyToID("_PcdColor");
        static readonly int ID_PcdDepth = Shader.PropertyToID("_PcdDepth");
        static readonly int ID_Radius = Shader.PropertyToID("_EdlRadius");
        static readonly int ID_Strength = Shader.PropertyToID("_EdlStrength");
        static readonly int ID_Boost = Shader.PropertyToID("_BrightnessBoost");
        static readonly int ID_ScreenPx = Shader.PropertyToID("_ScreenPx"); // 이름 수정

        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd EDL (Front-only)");

        public EdlPass(Settings settings)
        {
            _settings = settings;
            _edlMat = CoreUtils.CreateEngineMaterial("Shaders/EDL");
        }

        public void Setup(RTHandle cameraColor, RTHandle cameraDepth) { _cameraColor = cameraColor; _cameraDepth = cameraDepth; }
        public void SetDepthSource(RTHandle depthRT) { _pcdDepthRT = depthRT; }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            RenderingUtils.ReAllocateIfNeeded(ref _edlSourceColor, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _settings.edlSourceColorRTName);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_cameraColor == null || _pcdDepthRT == null) return;

            var cmd = CommandBufferPool.Get("Pcd EDL");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                // 0) 카메라 컬러 → 임시 복사
                Blitter.BlitCameraTexture(cmd, _cameraColor, _edlSourceColor, Vector2.one);

                // (선택) 필요시 ColorLite 경로를 추가하려면 여기서 호출
                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

                float avgPx = Shader.GetGlobalFloat(Shader.PropertyToID("_PcdAvgPointPx"));

                // 1) EDL 합성
                cmd.SetGlobalTexture(ID_PcdColor, _edlSourceColor);
                cmd.SetGlobalTexture(ID_PcdDepth, _pcdDepthRT);
                cmd.SetGlobalVector(ID_ScreenPx, new Vector4(_edlSourceColor.rt.width, _edlSourceColor.rt.height, 0, 0));

                _edlMat.SetFloat(ID_Radius, _settings.edlSettings.edlRadius);
                _edlMat.SetFloat(ID_Strength, _settings.edlSettings.edlStrength);
                _edlMat.SetFloat(ID_Boost, _settings.edlSettings.brightnessBoost);
                float radiusScaled = _settings.edlSettings.edlRadius * Mathf.Max(1f, _settings.edlRadiusScaleK * Mathf.Max(1f, avgPx));
                _edlMat.SetFloat(ID_Radius, radiusScaled);
                if (_settings.edlSettings.highQuality) _edlMat.EnableKeyword("EDL_HIGH_QUALITY");
                else _edlMat.DisableKeyword("EDL_HIGH_QUALITY");

                // 2) 최종: 카메라 컬러에 블릿
                Blitter.BlitCameraTexture(cmd, _edlSourceColor, _cameraColor, _edlMat, 0);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}*/


/*using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// PcdBillboardRenderFeature: 포인트 스플랫 MRT 누적(Accum) + 정규화(Normalize) 2패스
public class PcdBillboardRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        // 이벤트: FinalBlit 전에 Normalize가 실행되도록 권장 타이밍
        public RenderPassEvent depthProxyEvent = RenderPassEvent.BeforeRenderingOpaques;
        public RenderPassEvent accumEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent normalizeEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        public RenderPassEvent edlEvent = RenderPassEvent.AfterRenderingPostProcessing;

        // RT 이름
        public string accumColorName = "_AccumColor";
        public string accumWeightName = "_AccumWeight";
        public string accumDepthName = "_AccumDepth";

        // RT 포맷
        public RenderTextureFormat accumColorFormat = RenderTextureFormat.ARGB32;  // Half → 32bit
        public RenderTextureFormat accumWeightFormat = RenderTextureFormat.RFloat; // RHalf → RFloat
        public RenderTextureFormat accumDepthFormat = RenderTextureFormat.RFloat;   // 포인트용 깊이(옵션)

        // 머티리얼
        public Material splatAccumMaterial;   // Custom/PcdSplatAccum
        public Material normalizeMaterial;    // Custom/PcdSplatNormalize

        // Accum 파라미터
        [Range(0.5f, 64f)] public float pointSize = 5.0f;
        [Range(0.5f, 3f)] public float kernelSharpness = 1.5f;
        public bool gaussianKernel = false;

        // EDL 설정
        public PcdEdlSettings edlSettings;
        public string edlDepthRTName = "_PcdDepthRT";
        public string edlSourceColorRTName = "_EdlSourceColor";

    }

    [SerializeField] public Settings settings = new Settings();

    AccumPass _accumPass;
    NormalizePass _normPass;
    EdlPass _edlPass;

    public RTHandle AccumDepth => _accumPass?.AccumDepth; // 외부에서 깊이 핸들 접근용

    public override void Create()
    {
        _accumPass = new AccumPass(settings) { renderPassEvent = settings.accumEvent };
        _normPass = new NormalizePass(settings) { renderPassEvent = settings.normalizeEvent };
        _edlPass = new EdlPass(settings) { renderPassEvent = settings.edlEvent };
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
    {
        if (_accumPass == null || _normPass == null || _edlPass == null) return;

        // 카메라 타깃 핸들 전달
        var camColor = renderer.cameraColorTargetHandle;
        var camDepth = renderer.cameraDepthTargetHandle;
        _accumPass.Setup(camColor, camDepth);
        _normPass.Setup(camColor, camDepth);
        _edlPass.Setup(camColor, camDepth);

        // Accum → Normalize RTHandle 직접 공유(중요)
        _normPass.SetSources(_accumPass.AccumColor, _accumPass.AccumWeight);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_accumPass != null) renderer.EnqueuePass(_accumPass);
        if (_normPass != null) renderer.EnqueuePass(_normPass);
        if (_edlPass != null) renderer.EnqueuePass(_edlPass);
    }

    // ================= Accumulation Pass =================
    class AccumPass : ScriptableRenderPass
    {
        readonly Settings _settings;

        // 카메라 타깃
        RTHandle _cameraColor;
        RTHandle _cameraDepth;

        // 내부 MRT 타깃
        RTHandle _accumColor;   // RGB = color * weight
        RTHandle _accumWeight;  // R   = weight
        RTHandle _accumDepth;   // 필요시 ZTest용(옵션)

        // 외부에서 접근 가능하도록 프로퍼티 제공
        public RTHandle AccumDepth => _accumDepth;
        public RTHandle AccumColor => _accumColor;
        public RTHandle AccumWeight => _accumWeight;

        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd Splat Accum");
        static readonly int ID_PointSize = Shader.PropertyToID("_PointSize");
        static readonly int ID_KernelSharpness = Shader.PropertyToID("_KernelSharpness");
        static readonly int ID_Gaussian = Shader.PropertyToID("_Gaussian");

        PcdGpuRenderer[] _renderers;

        public AccumPass(Settings settings) { _settings = settings; }

        public void Setup(RTHandle cameraColor, RTHandle cameraDepth)
        {
            _cameraColor = cameraColor;
            _cameraDepth = cameraDepth;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            // 카메라 디스크립터 기반 RT 재할당
            var desc = rd.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.colorFormat = _settings.accumColorFormat;
            RenderingUtils.ReAllocateIfNeeded(ref _accumColor, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _settings.accumColorName);

            var descW = rd.cameraData.cameraTargetDescriptor;
            descW.depthBufferBits = 0;
            descW.colorFormat = _settings.accumWeightFormat;
            RenderingUtils.ReAllocateIfNeeded(ref _accumWeight, descW, FilterMode.Point, TextureWrapMode.Clamp, name: _settings.accumWeightName);

            var descD = rd.cameraData.cameraTargetDescriptor;
            descD.depthBufferBits = 0;
            descD.colorFormat = _settings.accumDepthFormat;
            RenderingUtils.ReAllocateIfNeeded(ref _accumDepth, descD, FilterMode.Point, TextureWrapMode.Clamp, name: _settings.accumDepthName);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_settings.splatAccumMaterial == null) return;

            var cmd = CommandBufferPool.Get("PcdAccum");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                // MRT 바인딩 - 순서가 중요합니다
                var colorTargets = new RenderTargetIdentifier[] {
                    _accumColor.nameID,    // SV_Target0
                    _accumWeight.nameID    // SV_Target1
                };

                // MRT 바인딩 + 클리어
                cmd.SetRenderTarget(colorTargets, _accumDepth.nameID);
                cmd.ClearRenderTarget(true, true, Color.clear);

                // 렌더러 수집(실무에서는 캐시 권장)
                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

                // 공통 파라미터
                _settings.splatAccumMaterial.SetFloat(ID_PointSize, _settings.pointSize);
                _settings.splatAccumMaterial.SetFloat(ID_KernelSharpness, _settings.kernelSharpness);
                _settings.splatAccumMaterial.SetFloat(ID_Gaussian, _settings.gaussianKernel ? 1f : 0f);

                var cam = rd.cameraData.camera;
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    _settings.splatAccumMaterial.SetMatrix("_LocalToWorld", r.transform.localToWorldMatrix);

                    // PcdGpuRenderer에 DMII 누적 호출
                    r.RenderSplatAccum(cmd, cam);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // 카메라 프레임 종료: 핸들 해제는 RendererFeature.Dispose에서 일괄 처리 권장
        }
    }

    // ================= Normalize Pass =================
    class NormalizePass : ScriptableRenderPass
    {
        readonly Settings _settings;

        RTHandle _cameraColor;
        RTHandle _cameraDepth;

        // Accum 출력 공유(직접 참조)
        RTHandle _accumColor;
        RTHandle _accumWeight;

        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd Splat Normalize");
        // static readonly int ID_ColorAccum = Shader.PropertyToID("_ColorAccum");
        // static readonly int ID_WeightAccum = Shader.PropertyToID("_WeightAccum");

        public NormalizePass(Settings settings) { _settings = settings; }

        public void Setup(RTHandle cameraColor, RTHandle cameraDepth)
        {
            _cameraColor = cameraColor;
            _cameraDepth = cameraDepth;
        }

        // Accum에서 만든 핸들을 직접 전달받음
        public void SetSources(RTHandle accumColor, RTHandle accumWeight)
        {
            _accumColor = accumColor;
            _accumWeight = accumWeight;
        }

        // 재할당하지 않음
        // public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd) { }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_settings.normalizeMaterial == null) return;
            if (_accumColor == null || _accumWeight == null) return;

            // 카메라 타입/핸들 유효성 가드
            var camData = rd.cameraData;
            if (camData.isPreviewCamera || camData.isSceneViewCamera) return; // 프리뷰/씬뷰 스킵
            if (_cameraColor == null || _cameraColor.rt == null) return;      // RTHandle 미초기화 가드

            var cmd = CommandBufferPool.Get("PcdNormalize");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                _settings.normalizeMaterial.SetTexture("_ColorAccum", _accumColor);
                _settings.normalizeMaterial.SetTexture("_WeightAccum", _accumWeight);

                // SetRenderTarget 대신 Blitter 이용(권장)
                Blitter.BlitCameraTexture(cmd, _accumColor, _cameraColor, _settings.normalizeMaterial, 0);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }
    }

    // ================= EDL Pass =================
    class EdlPass : ScriptableRenderPass
    {
        readonly Settings _settings;

        RTHandle _cameraColor;
        RTHandle _cameraDepth;

        // 깊이 프록시용 내부 RT
        RTHandle _pcdDepthRT;

        // 카메라 컬러 임시 복사용
        RTHandle _edlSourceColor;

        Material _edlMat;
        PcdGpuRenderer[] _renderers;

        static readonly int ID_PcdColor = Shader.PropertyToID("_PcdColor");
        static readonly int ID_PcdDepth = Shader.PropertyToID("_PcdDepth");
        static readonly int ID_Radius = Shader.PropertyToID("_EdlRadius");
        static readonly int ID_Strength = Shader.PropertyToID("_EdlStrength");
        static readonly int ID_Boost = Shader.PropertyToID("_BrightnessBoost");
        static readonly int ID_ScreenPx = Shader.PropertyToID("_ScreenPx");

        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd EDL (Reuse Camera Color)");

        public EdlPass(Settings settings)
        {
            _settings = settings;
            _edlMat = CoreUtils.CreateEngineMaterial("Shaders/EDL");
        }

        public void Setup(RTHandle cameraColor, RTHandle cameraDepth)
        {
            _cameraColor = cameraColor;
            _cameraDepth = cameraDepth;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            // 임시 컬러 복사 대상
            RenderingUtils.ReAllocateIfNeeded(ref _edlSourceColor, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _settings.edlSourceColorRTName);

            var depthDesc = renderingData.cameraData.cameraTargetDescriptor;
            depthDesc.depthBufferBits = 0;
            depthDesc.msaaSamples = 1;
            depthDesc.colorFormat = _settings.edlSettings.depthFormat; // RFloat 권장
            RenderingUtils.ReAllocateIfNeeded(ref _pcdDepthRT, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _settings.edlDepthRTName);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_cameraColor == null) return; // 카메라 타깃 필수
            var cmd = CommandBufferPool.Get("Pcd EDL");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                // 렌더러 수집(캐시 가능)
                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true); // 전역 수집

                var cam = renderingData.cameraData.camera;

                // 0) 카메라 컬러를 임시로 복사 (읽기/쓰기 분리)
                //    _edlSourceColor는 이후 ColorLite 출력 목적지로 재사용됨
                Blitter.BlitCameraTexture(cmd, _cameraColor, _edlSourceColor, Vector2.one);

                // 1) 깊이 프록시 렌더: _pcdDepthRT를 color 타깃으로 사용
                cmd.SetRenderTarget(_pcdDepthRT);
                cmd.ClearRenderTarget(false, true, Color.clear);
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    // 포인트 중심 invDepth(1/depth01)를 RFloat로 누적 기록
                    r.RenderSplatDepthProxy(cmd, cam);
                }

                // 1.5) 전면 마스크 적용 ColorLite 렌더(선택 경로):
                cmd.SetGlobalTexture("_PcdDepthRT", _pcdDepthRT); // ColorLite 셰이더가 샘플 
                cmd.SetRenderTarget(_edlSourceColor);
                cmd.ClearRenderTarget(false, true, Color.clear);
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    r.RenderSplatColorLite(cmd, cam);
                }

                // 2) EDL 합성: 컬러 입력은 _edlSourceColor(전면 필터 결과), 깊이는 _pcdDepthRT
                cmd.SetGlobalTexture(ID_PcdColor, _edlSourceColor);
                cmd.SetGlobalTexture(ID_PcdDepth, _pcdDepthRT);
                cmd.SetGlobalVector(ID_ScreenPx, new Vector4(_edlSourceColor.rt.width, _edlSourceColor.rt.height, 0, 0));
                _edlMat.SetFloat(ID_Radius, _settings.edlSettings.edlRadius);
                _edlMat.SetFloat(ID_Strength, _settings.edlSettings.edlStrength);
                _edlMat.SetFloat(ID_Boost, _settings.edlSettings.brightnessBoost);
                if (_settings.edlSettings.highQuality) _edlMat.EnableKeyword("EDL_HIGH_QUALITY"); else _edlMat.DisableKeyword("EDL_HIGH_QUALITY");

                // 3) 최종: EDL 결과를 카메라 컬러로 블릿
                Blitter.BlitCameraTexture(cmd, _edlSourceColor, _cameraColor, _edlMat, 0);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

}*/

// PcdBillboardPass
/*using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PcdBillboardRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public RenderPassEvent renderEvent = RenderPassEvent.AfterRenderingOpaques; // or BeforeRenderingPostProcessing
        public RenderPassEvent accumulateEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent normalizeEvent = RenderPassEvent.BeforeRenderingTransparents;
        public Material normalizeMaterial; // PcdSplatNormalize용
        public LayerMask layerMask = ~0;
        public string profilingName = "Pcd Weighted Splats";
        // 누적 RT 포맷
        public GraphicsFormat colorAccumFormat = GraphicsFormat.R16G16B16A16_SFloat;
        public GraphicsFormat weightAccumFormat = GraphicsFormat.R16_SFloat;
    }

    class PcdBillboardPass : ScriptableRenderPass
    {
        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("PcdBillboardPass");
        RTHandle _colorTarget;
        RTHandle _depthTarget;

        public void SetupEvent(RenderPassEvent evt) { renderPassEvent = evt; }
        public void SetupTargets(RTHandle color, RTHandle depth)
        {
            _colorTarget = color; _depthTarget = depth;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd)
        {
            // Bind to camera color/depth to ensure we draw into the correct target
            ConfigureTarget(_colorTarget, _depthTarget);
            // Billboard doesn’t need depth sampling; if you need it, uncomment:
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void Execute(ScriptableRenderContext ctx, ref RenderingData rd)
        {
            var sys = PcdBillboardRenderSystem.Instance;
            if (sys == null) return;

            var cmd = CommandBufferPool.Get("PcdBillboards");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                // Draw all registered billboard renderers via DMII
                sys.RenderAll(cmd, rd.cameraData.camera);
            }
            ctx.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    public Settings settings = new Settings();
    PcdBillboardPass _pass;

    public override void Create()
    {
        _pass = new PcdBillboardPass();
        _pass.SetupEvent(settings.renderEvent);
    }

    // For URP 12+/2022.1+ : pass targets are given here (RTHandle)
    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData rd)
    {
        if (_pass == null) return;
        _pass.SetupTargets(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
        // If the pass needs depth/normal inputs: _pass.ConfigureInput(ScriptableRenderPassInput.Depth);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData rd)
    {
        if (_pass == null) return;
        renderer.EnqueuePass(_pass);
    }


}*/
