using UnityEngine;
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
            if (_cameraColor == null || _accumSrc == null || _pcdDepthRT == null || _mat == null) return;

            var cmd = CommandBufferPool.Get("Pcd Norm+EDL");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                // �Ķ����
                float avgPx = Shader.GetGlobalFloat(Shader.PropertyToID("_PcdAvgPointPx"));
                float splatRadiusPx = Mathf.Max(1f, 0.5f * Mathf.Max(1f, avgPx));
                float edlRadiusCoef = Mathf.Max(0.5f, _settings.edlRadiusScaleK); // ��� ���� ����
                float edlRadius = edlRadiusCoef; // ���̴����� �ȼ� �ݰ�� ���� ���

                _mat.SetFloat("_EdlRadius", edlRadius);
                _mat.SetFloat("_SplatPxRadius", splatRadiusPx);

                // Ŀ�� ���� ����: Accum�� ����þ� ��� ������ �״�� �����ϰų� �ɼ�ȭ
                // ���⼭�� Accum �ܰ��� Gaussian ��� ���θ� ��Ƽ���� ������� �ľ��ϱ� �����Ƿ�,
                // ���� ��å/�ɼ����� �ΰų�, splatAccumMaterial�� _Gaussian�� ������ ����ȭ.
                bool accumGaussian = _settings.splatAccumMaterial != null && _settings.splatAccumMaterial.GetFloat("_Gaussian") > 0.5f;
                _mat.SetFloat("_KernelShape", accumGaussian ? 2f : 1f /* 1=square, 2=gaussian; ���� ��ũ�� ���� ��� 0 */);

                // ����þ� �ñ׸�(px): ����Ʈ �ݰ�� ����� �Ը�� ����
                float sigmaPx = Mathf.Max(0.5f, splatRadiusPx * 0.5f);
                _mat.SetFloat("_GaussianSigmaPx", sigmaPx);

                _mat.SetFloat("_EdlStrength", _settings.edlSettings.edlStrength);
                _mat.SetFloat("_BrightnessBoost", _settings.edlSettings.brightnessBoost);
                _mat.SetTexture("_PcdDepth", _pcdDepthRT);

                if (_settings.edlSettings.highQuality) _mat.EnableKeyword("EDL_HIGH_QUALITY");
                else _mat.DisableKeyword("EDL_HIGH_QUALITY");

                // ����: �ٿ����
                int ds = Mathf.Max(1, _settings.edlDownsample);
                if (ds > 1 && _accumDs != null)
                {
                    Blitter.BlitCameraTexture(cmd, _accumSrc, _accumDs); // ���� �ؽ�ó�� ���ػ󵵷�
                    Blitter.BlitCameraTexture(cmd, _accumDs, _cameraColor, _mat, 0); // ����ȭ+EDL������
                }
                else
                {
                    // Ǯ�ػ� 1�н�
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

// PcdBillboardRenderFeature: DepthProxy(���� ���ü�) + Accum + Normalize + EDL
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

        // ��Ƽ����
        public Material splatAccumMaterial;   // Custom/PcdSplatAccum
        public Material normalizeMaterial;    // Custom/PcdSplatNormalize

        public int pointBudget = 2_000_000;  // �� Ȱ�� ����Ʈ ����
        [Range(0.5f, 3.0f)] public float sseThreshold = 1.25f; // spacing*P �Ӱ�
        [Range(0.0f, 0.5f)] public float sseHysteresis = 0.15f; // ��� �����׸��ý�
        [Range(0.5f, 4.0f)] public float edlRadiusScaleK = 0.35f; // EDL �ݰ� ������ ����

        // Accum �Ķ����
        [Range(0.5f, 64f)] public float pointSize = 5.0f;
        [Range(0.5f, 3f)] public float kernelSharpness = 1.5f;
        public bool gaussianKernel = false;

        // EDL/DepthProxy ����
        public PcdEdlSettings edlSettings;      // edlSettings.depthFormat, edlStrength �� ����
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

        // Ÿ�� ����
        _depthPass.Setup(camColor, camDepth);
        _accumPass.Setup(camColor, camDepth);
        _normPass.Setup(camColor, camDepth);
        _edlPass.Setup(camColor, camDepth);

        // Accum �� Normalize �ҽ� ����
        _normPass.SetSources(_accumPass.AccumColor, _accumPass.AccumWeight);

        // DepthProxy ��� ����(EDL�� �ʼ�, Accum�� ����: ����Ʈ ��ġ ������ ���ϸ� ����)
        _edlPass.SetDepthSource(_depthPass.PcdDepthRT);
        _accumPass.SetDepthSource(_depthPass.PcdDepthRT); // �ʿ� �� ���̴����� ���� ��ġ �� ���
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
            desc.colorFormat = _settings.edlSettings.depthFormat; // ���� RFloat
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

                // invDepth ����
                cmd.SetRenderTarget(_pcdDepthRT);
                cmd.ClearRenderTarget(false, true, Color.clear);

                float sumPx = 0f; int sumPts = 0;
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    // LOD + ���� ����(���� ������ Settings�� ��ġ)
                    float avgPxR = r.UpdateLodAndBudget(cam, _settings.pointBudget, _settings.sseThreshold, _settings.sseHysteresis);
                    // ���� ���(Ȱ�� ����Ʈ ����)
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

                // �������ε� ���ε�(����)
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

        // �ܺ� ����
        public RTHandle AccumDepth => _accumDepth;
        public RTHandle AccumColor => _accumColor;
        public RTHandle AccumWeight => _accumWeight;

        // DepthProxy �Է�(����)
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

                // ���� ���Ͻ� ����(Accum ���̴����� ����� ���)
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

        // �ܺηκ��� ���Թ޴� ���� ���Ͻ�
        RTHandle _pcdDepthRT;

        // ī�޶� �÷� �ӽ� ����
        RTHandle _edlSourceColor;

        Material _edlMat;
        PcdGpuRenderer[] _renderers;

        static readonly int ID_PcdColor = Shader.PropertyToID("_PcdColor");
        static readonly int ID_PcdDepth = Shader.PropertyToID("_PcdDepth");
        static readonly int ID_Radius = Shader.PropertyToID("_EdlRadius");
        static readonly int ID_Strength = Shader.PropertyToID("_EdlStrength");
        static readonly int ID_Boost = Shader.PropertyToID("_BrightnessBoost");
        static readonly int ID_ScreenPx = Shader.PropertyToID("_ScreenPx"); // �̸� ����

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
                // 0) ī�޶� �÷� �� �ӽ� ����
                Blitter.BlitCameraTexture(cmd, _cameraColor, _edlSourceColor, Vector2.one);

                // (����) �ʿ�� ColorLite ��θ� �߰��Ϸ��� ���⼭ ȣ��
                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

                float avgPx = Shader.GetGlobalFloat(Shader.PropertyToID("_PcdAvgPointPx"));

                // 1) EDL �ռ�
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

                // 2) ����: ī�޶� �÷��� ��
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

// PcdBillboardRenderFeature: ����Ʈ ���÷� MRT ����(Accum) + ����ȭ(Normalize) 2�н�
public class PcdBillboardRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        // �̺�Ʈ: FinalBlit ���� Normalize�� ����ǵ��� ���� Ÿ�̹�
        public RenderPassEvent depthProxyEvent = RenderPassEvent.BeforeRenderingOpaques;
        public RenderPassEvent accumEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent normalizeEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        public RenderPassEvent edlEvent = RenderPassEvent.AfterRenderingPostProcessing;

        // RT �̸�
        public string accumColorName = "_AccumColor";
        public string accumWeightName = "_AccumWeight";
        public string accumDepthName = "_AccumDepth";

        // RT ����
        public RenderTextureFormat accumColorFormat = RenderTextureFormat.ARGB32;  // Half �� 32bit
        public RenderTextureFormat accumWeightFormat = RenderTextureFormat.RFloat; // RHalf �� RFloat
        public RenderTextureFormat accumDepthFormat = RenderTextureFormat.RFloat;   // ����Ʈ�� ����(�ɼ�)

        // ��Ƽ����
        public Material splatAccumMaterial;   // Custom/PcdSplatAccum
        public Material normalizeMaterial;    // Custom/PcdSplatNormalize

        // Accum �Ķ����
        [Range(0.5f, 64f)] public float pointSize = 5.0f;
        [Range(0.5f, 3f)] public float kernelSharpness = 1.5f;
        public bool gaussianKernel = false;

        // EDL ����
        public PcdEdlSettings edlSettings;
        public string edlDepthRTName = "_PcdDepthRT";
        public string edlSourceColorRTName = "_EdlSourceColor";

    }

    [SerializeField] public Settings settings = new Settings();

    AccumPass _accumPass;
    NormalizePass _normPass;
    EdlPass _edlPass;

    public RTHandle AccumDepth => _accumPass?.AccumDepth; // �ܺο��� ���� �ڵ� ���ٿ�

    public override void Create()
    {
        _accumPass = new AccumPass(settings) { renderPassEvent = settings.accumEvent };
        _normPass = new NormalizePass(settings) { renderPassEvent = settings.normalizeEvent };
        _edlPass = new EdlPass(settings) { renderPassEvent = settings.edlEvent };
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
    {
        if (_accumPass == null || _normPass == null || _edlPass == null) return;

        // ī�޶� Ÿ�� �ڵ� ����
        var camColor = renderer.cameraColorTargetHandle;
        var camDepth = renderer.cameraDepthTargetHandle;
        _accumPass.Setup(camColor, camDepth);
        _normPass.Setup(camColor, camDepth);
        _edlPass.Setup(camColor, camDepth);

        // Accum �� Normalize RTHandle ���� ����(�߿�)
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

        // ī�޶� Ÿ��
        RTHandle _cameraColor;
        RTHandle _cameraDepth;

        // ���� MRT Ÿ��
        RTHandle _accumColor;   // RGB = color * weight
        RTHandle _accumWeight;  // R   = weight
        RTHandle _accumDepth;   // �ʿ�� ZTest��(�ɼ�)

        // �ܺο��� ���� �����ϵ��� ������Ƽ ����
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
            // ī�޶� ��ũ���� ��� RT ���Ҵ�
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
                // MRT ���ε� - ������ �߿��մϴ�
                var colorTargets = new RenderTargetIdentifier[] {
                    _accumColor.nameID,    // SV_Target0
                    _accumWeight.nameID    // SV_Target1
                };

                // MRT ���ε� + Ŭ����
                cmd.SetRenderTarget(colorTargets, _accumDepth.nameID);
                cmd.ClearRenderTarget(true, true, Color.clear);

                // ������ ����(�ǹ������� ĳ�� ����)
                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true);

                // ���� �Ķ����
                _settings.splatAccumMaterial.SetFloat(ID_PointSize, _settings.pointSize);
                _settings.splatAccumMaterial.SetFloat(ID_KernelSharpness, _settings.kernelSharpness);
                _settings.splatAccumMaterial.SetFloat(ID_Gaussian, _settings.gaussianKernel ? 1f : 0f);

                var cam = rd.cameraData.camera;
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    _settings.splatAccumMaterial.SetMatrix("_LocalToWorld", r.transform.localToWorldMatrix);

                    // PcdGpuRenderer�� DMII ���� ȣ��
                    r.RenderSplatAccum(cmd, cam);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // ī�޶� ������ ����: �ڵ� ������ RendererFeature.Dispose���� �ϰ� ó�� ����
        }
    }

    // ================= Normalize Pass =================
    class NormalizePass : ScriptableRenderPass
    {
        readonly Settings _settings;

        RTHandle _cameraColor;
        RTHandle _cameraDepth;

        // Accum ��� ����(���� ����)
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

        // Accum���� ���� �ڵ��� ���� ���޹���
        public void SetSources(RTHandle accumColor, RTHandle accumWeight)
        {
            _accumColor = accumColor;
            _accumWeight = accumWeight;
        }

        // ���Ҵ����� ����
        // public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd) { }

        public override void Execute(ScriptableRenderContext context, ref RenderingData rd)
        {
            if (_settings.normalizeMaterial == null) return;
            if (_accumColor == null || _accumWeight == null) return;

            // ī�޶� Ÿ��/�ڵ� ��ȿ�� ����
            var camData = rd.cameraData;
            if (camData.isPreviewCamera || camData.isSceneViewCamera) return; // ������/���� ��ŵ
            if (_cameraColor == null || _cameraColor.rt == null) return;      // RTHandle ���ʱ�ȭ ����

            var cmd = CommandBufferPool.Get("PcdNormalize");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                _settings.normalizeMaterial.SetTexture("_ColorAccum", _accumColor);
                _settings.normalizeMaterial.SetTexture("_WeightAccum", _accumWeight);

                // SetRenderTarget ��� Blitter �̿�(����)
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

        // ���� ���Ͻÿ� ���� RT
        RTHandle _pcdDepthRT;

        // ī�޶� �÷� �ӽ� �����
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
            // �ӽ� �÷� ���� ���
            RenderingUtils.ReAllocateIfNeeded(ref _edlSourceColor, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _settings.edlSourceColorRTName);

            var depthDesc = renderingData.cameraData.cameraTargetDescriptor;
            depthDesc.depthBufferBits = 0;
            depthDesc.msaaSamples = 1;
            depthDesc.colorFormat = _settings.edlSettings.depthFormat; // RFloat ����
            RenderingUtils.ReAllocateIfNeeded(ref _pcdDepthRT, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _settings.edlDepthRTName);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_cameraColor == null) return; // ī�޶� Ÿ�� �ʼ�
            var cmd = CommandBufferPool.Get("Pcd EDL");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                // ������ ����(ĳ�� ����)
                if (_renderers == null || _renderers.Length == 0)
                    _renderers = GameObject.FindObjectsOfType<PcdGpuRenderer>(true); // ���� ����

                var cam = renderingData.cameraData.camera;

                // 0) ī�޶� �÷��� �ӽ÷� ���� (�б�/���� �и�)
                //    _edlSourceColor�� ���� ColorLite ��� �������� �����
                Blitter.BlitCameraTexture(cmd, _cameraColor, _edlSourceColor, Vector2.one);

                // 1) ���� ���Ͻ� ����: _pcdDepthRT�� color Ÿ������ ���
                cmd.SetRenderTarget(_pcdDepthRT);
                cmd.ClearRenderTarget(false, true, Color.clear);
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    // ����Ʈ �߽� invDepth(1/depth01)�� RFloat�� ���� ���
                    r.RenderSplatDepthProxy(cmd, cam);
                }

                // 1.5) ���� ����ũ ���� ColorLite ����(���� ���):
                cmd.SetGlobalTexture("_PcdDepthRT", _pcdDepthRT); // ColorLite ���̴��� ���� 
                cmd.SetRenderTarget(_edlSourceColor);
                cmd.ClearRenderTarget(false, true, Color.clear);
                foreach (var r in _renderers)
                {
                    if (r == null || !r.isActiveAndEnabled) continue;
                    r.RenderSplatColorLite(cmd, cam);
                }

                // 2) EDL �ռ�: �÷� �Է��� _edlSourceColor(���� ���� ���), ���̴� _pcdDepthRT
                cmd.SetGlobalTexture(ID_PcdColor, _edlSourceColor);
                cmd.SetGlobalTexture(ID_PcdDepth, _pcdDepthRT);
                cmd.SetGlobalVector(ID_ScreenPx, new Vector4(_edlSourceColor.rt.width, _edlSourceColor.rt.height, 0, 0));
                _edlMat.SetFloat(ID_Radius, _settings.edlSettings.edlRadius);
                _edlMat.SetFloat(ID_Strength, _settings.edlSettings.edlStrength);
                _edlMat.SetFloat(ID_Boost, _settings.edlSettings.brightnessBoost);
                if (_settings.edlSettings.highQuality) _edlMat.EnableKeyword("EDL_HIGH_QUALITY"); else _edlMat.DisableKeyword("EDL_HIGH_QUALITY");

                // 3) ����: EDL ����� ī�޶� �÷��� ��
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
        public Material normalizeMaterial; // PcdSplatNormalize��
        public LayerMask layerMask = ~0;
        public string profilingName = "Pcd Weighted Splats";
        // ���� RT ����
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
            // Billboard doesn��t need depth sampling; if you need it, uncomment:
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
