using System.Threading.Tasks;
using UnityEngine;

public static class PcdMemoryTools
{
    public static async Task ClearAllAsync(PcdGpuRenderer optionalRenderer = null)
    {
        // 1) 렌더링/스트리밍 시스템 멈추기
        var entry = Object.FindAnyObjectByType<PcdEntry>();
        var streaming = Object.FindAnyObjectByType<PcdStreamingController>();
        var billboardSys = Object.FindAnyObjectByType<PcdBillboardRenderSystem>();
        var edlHook = Object.FindAnyObjectByType<PcdEdlCameraHook>();
        var urpRF = Object.FindAnyObjectByType<PcdEdlRenderFeature>();

        // 스트리밍 컨트롤러 완전 정리(비동기)
        if (streaming != null) { await streaming.DisposeAsync(); } // 내부에서 gpuRenderer.ClearAllNodes 호출 포함[10]

        // 2) GPU 리소스 정리
        // 2-1) 포인트 렌더러 버퍼 해제
        if (optionalRenderer == null)
            optionalRenderer = Object.FindAnyObjectByType<PcdGpuRenderer>();
        if (optionalRenderer != null)
        {
            optionalRenderer.ClearAllNodes(); // 내부에서 ComputeBuffer/Args Release[5]
            // RenderSystem 등록 해제는 OnDisable에서 처리되므로 여기서는 생략[5]
        }

        // 2-2) 빌보드 시스템 내부 상태(등록 목록) 정리
        if (billboardSys != null)
        {
            // 렌더러들이 OnDisable에서 Unregister되므로 별도 해제 불필요
            // 필요시: 모든 등록된 렌더러를 비활성화
            var all = Object.FindObjectsByType<PcdGpuRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var r in all) r.enabled = false;
        }

        // 2-3) EDL 훅의 RT 해제
        if (edlHook != null && edlHook.enabled)
        {
            edlHook.enabled = false; // OnDisable에서 RT/CB 해제[14]
        }

        // 2-4) URP 패스 내부 RTHandle은 프레임마다 ReAllocateIfNeeded로 관리되며,
        // Pass 인스턴스가 유지되는 경우 카메라 전환 시 자동 재할당됨. 명시적 해제가 필요하면
        // 패스에 Dispose/OnCameraCleanup에서 RTHandle.Release()를 추가하는 게 가장 안전.
        // 여기서는 렌더피처 토글로 우회:
        if (urpRF != null)
        {
            urpRF.SetActive(false); // 필요시 ScriptableRendererFeature 확장으로 토글 구현
        }

        // 3) CPU 메모리 정리(인덱스/캐시/배열)
        // - 스트리밍 DisposeAsync에서 _posCache/_requestedNodes/서브로더/인덱스/옥트리 null 처리 완료[10]
        // - 단발 로더 경로에서 업로드 후 releaseCpuArraysAfterUpload=true면 이미 null 처리함[3]

        // 4) 사용하지 않는 에셋/GC
#if UNITY_EDITOR
        // 에디터에서는 한 번 더 강제 정리 가능
        //await Resources.UnloadUnusedAssets(); // 네이티브 에셋 해제[25][28]
        //System.GC.Collect();                 // 관리 힙 회수(비용 큼)[25][28]
#else
        // await Resources.UnloadUnusedAssets(); // 런타임에서도 안전
        // GC는 상황에 따라 생략 가능. 필요 시 아래 라인 주석 해제
        // System.GC.Collect();
#endif
    }
}
