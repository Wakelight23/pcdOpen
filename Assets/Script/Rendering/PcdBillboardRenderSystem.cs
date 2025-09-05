using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/*
 * 렌더링을 위해 PcdGpuRenderer 인스턴스를 등록/관리하는 싱글톤 시스템
 */
public sealed class PcdBillboardRenderSystem : MonoBehaviour
{
    public static PcdBillboardRenderSystem Instance { get; private set; }

    readonly List<PcdGpuRenderer> _renderers = new(64);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        if (Instance == null)
        {
            var go = new GameObject("[PcdBillboardRenderSystem]");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<PcdBillboardRenderSystem>();
        }
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning($"[PcdBillboardRenderSystem] Duplicate destroyed on {gameObject.scene.name}");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        _renderers.Clear();
    }

    public void Register(PcdGpuRenderer r)
    {
        if (r == null) return;
        if (Instance != this && Instance != null)
        {
            // route to live instance if registered on a stale one (scene timing)
            Instance.Register(r);
            return;
        }
        if (!_renderers.Contains(r)) _renderers.Add(r);
    }

    public void Unregister(PcdGpuRenderer r)
    {
        if (r == null) return;
        if (Instance != this && Instance != null)
        {
            Instance.Unregister(r);
            return;
        }
        _renderers.Remove(r);
    }

    public void RenderAll(CommandBuffer cmd, Camera cam)
    {
        for (int i = 0; i < _renderers.Count; i++)
        {
            var r = _renderers[i];
            if (r == null || !r.isActiveAndEnabled) continue;
            r.RenderSplatAccum(cmd, cam);
        }
    }
}
