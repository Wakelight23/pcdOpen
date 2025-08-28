using System.Threading.Tasks;
using UnityEngine;

public static class PcdMemoryTools
{
    public static async Task ClearAllAsync(PcdGpuRenderer optionalRenderer = null)
    {
        // 1) ������/��Ʈ���� �ý��� ���߱�
        var entry = Object.FindAnyObjectByType<PcdEntry>();
        var streaming = Object.FindAnyObjectByType<PcdStreamingController>();
        var billboardSys = Object.FindAnyObjectByType<PcdBillboardRenderSystem>();
        var edlHook = Object.FindAnyObjectByType<PcdEdlCameraHook>();
        var urpRF = Object.FindAnyObjectByType<PcdEdlRenderFeature>();

        // ��Ʈ���� ��Ʈ�ѷ� ���� ����(�񵿱�)
        if (streaming != null) { await streaming.DisposeAsync(); } // ���ο��� gpuRenderer.ClearAllNodes ȣ�� ����[10]

        // 2) GPU ���ҽ� ����
        // 2-1) ����Ʈ ������ ���� ����
        if (optionalRenderer == null)
            optionalRenderer = Object.FindAnyObjectByType<PcdGpuRenderer>();
        if (optionalRenderer != null)
        {
            optionalRenderer.ClearAllNodes(); // ���ο��� ComputeBuffer/Args Release[5]
            // RenderSystem ��� ������ OnDisable���� ó���ǹǷ� ���⼭�� ����[5]
        }

        // 2-2) ������ �ý��� ���� ����(��� ���) ����
        if (billboardSys != null)
        {
            // ���������� OnDisable���� Unregister�ǹǷ� ���� ���� ���ʿ�
            // �ʿ��: ��� ��ϵ� �������� ��Ȱ��ȭ
            var all = Object.FindObjectsByType<PcdGpuRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var r in all) r.enabled = false;
        }

        // 2-3) EDL ���� RT ����
        if (edlHook != null && edlHook.enabled)
        {
            edlHook.enabled = false; // OnDisable���� RT/CB ����[14]
        }

        // 2-4) URP �н� ���� RTHandle�� �����Ӹ��� ReAllocateIfNeeded�� �����Ǹ�,
        // Pass �ν��Ͻ��� �����Ǵ� ��� ī�޶� ��ȯ �� �ڵ� ���Ҵ��. ����� ������ �ʿ��ϸ�
        // �н��� Dispose/OnCameraCleanup���� RTHandle.Release()�� �߰��ϴ� �� ���� ����.
        // ���⼭�� ������ó ��۷� ��ȸ:
        if (urpRF != null)
        {
            urpRF.SetActive(false); // �ʿ�� ScriptableRendererFeature Ȯ������ ��� ����
        }

        // 3) CPU �޸� ����(�ε���/ĳ��/�迭)
        // - ��Ʈ���� DisposeAsync���� _posCache/_requestedNodes/����δ�/�ε���/��Ʈ�� null ó�� �Ϸ�[10]
        // - �ܹ� �δ� ��ο��� ���ε� �� releaseCpuArraysAfterUpload=true�� �̹� null ó����[3]

        // 4) ������� �ʴ� ����/GC
#if UNITY_EDITOR
        // �����Ϳ����� �� �� �� ���� ���� ����
        //await Resources.UnloadUnusedAssets(); // ����Ƽ�� ���� ����[25][28]
        //System.GC.Collect();                 // ���� �� ȸ��(��� ŭ)[25][28]
#else
        // await Resources.UnloadUnusedAssets(); // ��Ÿ�ӿ����� ����
        // GC�� ��Ȳ�� ���� ���� ����. �ʿ� �� �Ʒ� ���� �ּ� ����
        // System.GC.Collect();
#endif
    }
}
