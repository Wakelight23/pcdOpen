using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// PcdBillboardRenderFeature: 포인트 스플랫 MRT 누적(Accum) + 정규화(Normalize) 2패스
public class PcdBillboardRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        // 이벤트: FinalBlit 전에 Normalize가 실행되도록 권장 타이밍
        public RenderPassEvent accumEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent normalizeEvent = RenderPassEvent.BeforeRenderingPostProcessing;

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
    }

    [SerializeField] public Settings settings = new Settings();

    AccumPass _accumPass;
    NormalizePass _normPass;

    public override void Create()
    {
        _accumPass = new AccumPass(settings) { renderPassEvent = settings.accumEvent };
        _normPass = new NormalizePass(settings) { renderPassEvent = settings.normalizeEvent };
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
    {
        if (_accumPass == null || _normPass == null) return;

        // 카메라 타깃 핸들 전달
        _accumPass.Setup(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
        _normPass.Setup(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);

        // Accum → Normalize RTHandle 직접 공유(중요)
        _normPass.SetSources(_accumPass.AccumColor, _accumPass.AccumWeight);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_accumPass == null || _normPass == null) return;
        renderer.EnqueuePass(_accumPass);
        renderer.EnqueuePass(_normPass);
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

        public RTHandle AccumColor => _accumColor;
        public RTHandle AccumWeight => _accumWeight;

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
        static readonly int ID_ColorAccum = Shader.PropertyToID("_ColorAccum");
        static readonly int ID_WeightAccum = Shader.PropertyToID("_WeightAccum");

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

            var cmd = CommandBufferPool.Get("PcdNormalize");
            using (new ProfilingScope(cmd, s_Profiler))
            {
                // 카메라 컬러 타깃 설정
                cmd.SetRenderTarget(_cameraColor);
                // 정규화 머티리얼 입력
                /*cmd.SetGlobalTexture("_ColorAccum", _accumColor);
                cmd.SetGlobalTexture("_WeightAccum", _accumWeight);*/
                _settings.normalizeMaterial.SetTexture("_ColorAccum", _accumColor);
                _settings.normalizeMaterial.SetTexture("_WeightAccum", _accumWeight);

                // 풀스크린 삼각형 직접 그리기
                cmd.DrawProcedural(Matrix4x4.identity, _settings.normalizeMaterial, 0, MeshTopology.Triangles, 3);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }
    }
}




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
