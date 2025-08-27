using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PcdBillboardRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public RenderPassEvent renderEvent = RenderPassEvent.AfterRenderingOpaques; // or BeforeRenderingPostProcessing
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
}


/*using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PcdBillboardRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public RenderPassEvent accumulateEvent = RenderPassEvent.AfterRenderingOpaques;
        public RenderPassEvent normalizeEvent = RenderPassEvent.BeforeRenderingTransparents;
        public Material normalizeMaterial; // PcdSplatNormalize용
        public LayerMask layerMask = ~0;
        public string profilingName = "Pcd Weighted Splats";
        // 누적 RT 포맷
        public GraphicsFormat colorAccumFormat = GraphicsFormat.R16G16B16A16_SFloat;
        public GraphicsFormat weightAccumFormat = GraphicsFormat.R16_SFloat;
    }

    class AccumulatePass : ScriptableRenderPass
    {
        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd Accumulate");

        RTHandle _colorAccum;
        RTHandle _weightAccum;

        public RTHandle ColorAccum => _colorAccum;
        public RTHandle WeightAccum => _weightAccum;

        readonly FilteringSettings _filtering;
        readonly string _profName;
        readonly GraphicsFormat _colorFmt;
        readonly GraphicsFormat _weightFmt;

        public AccumulatePass(string profName, LayerMask mask, GraphicsFormat colorFmt, GraphicsFormat weightFmt)
        {
            _profName = profName;
            _filtering = new FilteringSettings(RenderQueueRange.all, mask);
            _colorFmt = colorFmt;
            _weightFmt = weightFmt;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;

            // Color accum RT
            desc.graphicsFormat = _colorFmt;
            RenderingUtils.ReAllocateIfNeeded(
                ref _colorAccum, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_PcdColorAccum"
            );

            // Weight accum RT
            desc.graphicsFormat = _weightFmt;
            RenderingUtils.ReAllocateIfNeeded(
                ref _weightAccum, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_PcdWeightAccum"
            );

            ConfigureTarget(new RTHandle[] { _colorAccum, _weightAccum });
            ConfigureClear(ClearFlag.Color, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cmd = CommandBufferPool.Get(_profName);
            using (new ProfilingScope(cmd, s_Profiler))
            {
                var sys = PcdBillboardRenderSystem.Instance;
                if (sys != null)
                {
                    // 누적 렌더 시, MRT가 현재 ConfigureTarget으로 설정돼 있어야 함
                    sys.RenderAccumulation(cmd, renderingData.cameraData.camera);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public void Dispose()
        {
            _colorAccum?.Release();
            _weightAccum?.Release();
        }
    }


    class NormalizePass : ScriptableRenderPass
    {
        static readonly ProfilingSampler s_Profiler = new ProfilingSampler("Pcd Normalize");

        Material _mat;
        AccumulatePass _srcPass;
        string _profName;

        // 셰이더 프로퍼티 ID (PcdSplatNormalize와 일치)
        static readonly int _ColorAccumID = Shader.PropertyToID("_ColorAccum");
        static readonly int _WeightAccumID = Shader.PropertyToID("_WeightAccum");

        public NormalizePass(string profName, Material mat, AccumulatePass src)
        {
            _profName = profName;
            _mat = mat;
            _srcPass = src;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var renderer = renderingData.cameraData.renderer;
            var target = renderer.cameraColorTargetHandle;

            if (_mat == null || _srcPass == null)
                return;
            if (_srcPass.ColorAccum == null || _srcPass.WeightAccum == null)
                return;

            var cmd = CommandBufferPool.Get(_profName);
            using (new ProfilingScope(cmd, s_Profiler))
            {
                // 텍스처 바인딩: 셰이더 TEXTURE2D와 동일한 이름
                _mat.SetTexture(_ColorAccumID, _srcPass.ColorAccum);
                _mat.SetTexture(_WeightAccumID, _srcPass.WeightAccum);

                // 카메라 컬러 타깃으로 정규화 출력
                Blitter.BlitCameraTexture(cmd, _srcPass.ColorAccum, target, _mat, 0);
                // Fallback:
                // cmd.Blit(BuiltinRenderTextureType.None, target, _mat, 0);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }


    public Settings settings = new Settings();
    AccumulatePass _accumPass;
    NormalizePass _normalizePass;

    public override void Create()
    {
        _accumPass = new AccumulatePass(
            settings.profilingName, settings.layerMask,
            settings.colorAccumFormat, settings.weightAccumFormat)
        { renderPassEvent = settings.accumulateEvent };

        _normalizePass = new NormalizePass(settings.profilingName, settings.normalizeMaterial, _accumPass)
        { renderPassEvent = settings.normalizeEvent };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.normalizeMaterial == null) return;
        renderer.EnqueuePass(_accumPass);
        renderer.EnqueuePass(_normalizePass);
    }

    protected override void Dispose(bool disposing)
    {
        _accumPass?.Dispose();
    }
}*/
