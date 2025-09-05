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

        // 스트리밍 컨트롤러 완전 정리(비동기)
        if (streaming != null) { await streaming.DisposeAsync(); }

        // 2) GPU 리소스 정리
        // 2-1) 포인트 렌더러 버퍼 해제
        if (optionalRenderer == null)
            optionalRenderer = Object.FindAnyObjectByType<PcdGpuRenderer>();
        if (optionalRenderer != null)
        {
            optionalRenderer.ClearAllNodes();
        }

        // 2-2) 빌보드 시스템 내부 상태(등록 목록) 정리
        if (billboardSys != null)
        {
            // 렌더러들이 OnDisable에서 Unregister되므로 별도 해제 불필요
            // 필요시: 모든 등록된 렌더러를 비활성화
            var all = Object.FindObjectsByType<PcdGpuRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var r in all) r.enabled = false;
        }

        // 2-4) URP 패스 내부 RTHandle은 프레임마다 ReAllocateIfNeeded로 관리되며,


        // 3) CPU 메모리 정리(인덱스/캐시/배열)

        // 4) 사용하지 않는 에셋/GC
#if UNITY_EDITOR
        // 에디터에서는 한 번 더 강제 정리 가능
        //await Resources.UnloadUnusedAssets();
        //System.GC.Collect();
#else
        // await Resources.UnloadUnusedAssets(); // 런타임에서도 안전
        // GC는 상황에 따라 생략 가능. 필요 시 아래 라인 주석 해제
        // System.GC.Collect();
#endif
    }
}
