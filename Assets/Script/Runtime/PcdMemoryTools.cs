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

        // ��Ʈ���� ��Ʈ�ѷ� ���� ����(�񵿱�)
        if (streaming != null) { await streaming.DisposeAsync(); }

        // 2) GPU ���ҽ� ����
        // 2-1) ����Ʈ ������ ���� ����
        if (optionalRenderer == null)
            optionalRenderer = Object.FindAnyObjectByType<PcdGpuRenderer>();
        if (optionalRenderer != null)
        {
            optionalRenderer.ClearAllNodes();
        }

        // 2-2) ������ �ý��� ���� ����(��� ���) ����
        if (billboardSys != null)
        {
            // ���������� OnDisable���� Unregister�ǹǷ� ���� ���� ���ʿ�
            // �ʿ��: ��� ��ϵ� �������� ��Ȱ��ȭ
            var all = Object.FindObjectsByType<PcdGpuRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var r in all) r.enabled = false;
        }

        // 2-4) URP �н� ���� RTHandle�� �����Ӹ��� ReAllocateIfNeeded�� �����Ǹ�,


        // 3) CPU �޸� ����(�ε���/ĳ��/�迭)

        // 4) ������� �ʴ� ����/GC
#if UNITY_EDITOR
        // �����Ϳ����� �� �� �� ���� ���� ����
        //await Resources.UnloadUnusedAssets();
        //System.GC.Collect();
#else
        // await Resources.UnloadUnusedAssets(); // ��Ÿ�ӿ����� ����
        // GC�� ��Ȳ�� ���� ���� ����. �ʿ� �� �Ʒ� ���� �ּ� ����
        // System.GC.Collect();
#endif
    }
}
