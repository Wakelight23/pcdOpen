#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using System;
using System.Threading.Tasks;

// SFB ��� ��
#if UNITY_STANDALONE_WIN
using SFB; // StandaloneFileBrowser ���ӽ����̽�
#endif

public class PcdEntry : MonoBehaviour
{
    [Header("Choose render path")]
    public bool useGpuRenderer = true;

    [Header("Targets")]
    public PcdViewer particleViewer;      // ParticleSystem ���(�ɼ�)
    public PcdGpuRenderer gpuRenderer;    // GPU ���
    public Material gpuPointMaterial;     // Custom/PcdPoint ��� ��Ƽ����

    [Header("Visibility helpers")]
    [Tooltip("���ε� ���� ��� ����Ʈ���� Center�� ���� ���� �������� ����ȭ�մϴ�.")]
    public bool normalizeToOrigin = true;

    [Tooltip("�ε� ���� ī�޶� ������ �߽����� ���� ��Ŀ���մϴ�.")]
    public bool forceCameraFocus = true;

    [Header("Runtime options")]
    [Tooltip("GPU ���ε� �� CPU �迭 ����")]
    public bool releaseCpuArraysAfterUpload = true;

    // ���� ����: ���� ���ν����� ����ó/�ڷ�ƾ ��Ÿ��
    static PcdEntry s_dispatcherOwner;
    static readonly System.Collections.Concurrent.ConcurrentQueue<Action> s_mainQueue = new();

    void Awake()
    {
        // ���� �ν��Ͻ��� ����ó �����ڷ� ���
        if (s_dispatcherOwner == null) s_dispatcherOwner = this;
    }
    void Update()
    {
        while (s_mainQueue.TryDequeue(out var act))
        {
            try { act?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    static void PostToMainThread(Action a) => s_mainQueue.Enqueue(a);

#if UNITY_EDITOR
    [ContextMenu("Load PCD (Editor)")]
    public void LoadPcdEditor()
    {
        string path = EditorUtility.OpenFilePanel("Select PCD", "", "pcd");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            // ���� ���ҽ� ����
            CleanupPrevious();

            var data = PcdLoader.LoadFromFile(path);
            if (data == null || data.positions == null || data.pointCount <= 0)
            {
                Debug.LogError("Invalid or empty PCD.");
                return;
            }

            Debug.Log($"[PCD] AABB min={data.boundsMin} max={data.boundsMax} center={data.Center} size={data.Size} count={data.pointCount}");

            if (normalizeToOrigin)
            {
                var c = data.Center;
                for (int i = 0; i < data.pointCount; i++)
                    data.positions[i] -= c;

                data.boundsMin -= c;
                data.boundsMax -= c;
            }

            if (useGpuRenderer)
            {
                if (gpuRenderer == null)
                    gpuRenderer = GetComponent<PcdGpuRenderer>();
                if (gpuRenderer == null)
                    gpuRenderer = gameObject.AddComponent<PcdGpuRenderer>();

                if (gpuRenderer.pointMaterial == null && gpuPointMaterial != null)
                    gpuRenderer.pointMaterial = gpuPointMaterial;

                gpuRenderer.useColors = data.colors != null && data.colors.Length == data.pointCount;
                gpuRenderer.pointSize = 0.02f;

                gpuRenderer.UploadData(data.positions, data.colors);
            }
            else
            {
                if (particleViewer == null)
                    particleViewer = GetComponent<PcdViewer>();
                if (particleViewer == null)
                    particleViewer = gameObject.AddComponent<PcdViewer>();

                particleViewer.pointSize = 0.02f;
                particleViewer.useColors = data.colors != null && data.colors.Length == data.pointCount;
                particleViewer.LoadAndShow(path);
            }

            // ī�޶� ���� ��Ŀ��
            if (forceCameraFocus)
            {
                FocusMainCamera(
                    normalizeToOrigin ? Vector3.zero : data.Center,
                    data.Size
                );
            }

            Debug.Log($"PCD loaded: {data.pointCount} points");
        }
        catch (Exception e)
        {
            Debug.LogError(e);
        }
    }
#endif

    // ===== ��Ÿ�� ���� ������ =====

    // 1) ��ư OnClick�� ���� ������ �� �ִ� �޼���
    public void OpenFileAndLoadRuntime()
    {
        // UI �����忡�� ���� �� ���ο��� �񵿱� ����
        _ = OpenAndLoadRoutine();
    }

    async Task OpenAndLoadRoutine()
    {
        try
        {
            string path = await PickPcdPathAsync();
            if (string.IsNullOrEmpty(path)) return;

            await LoadPcdRuntimeAsync(path);
        }
        catch (Exception ex)
        {
            Debug.LogError(ex);
        }
    }

    // 2) ���� ���� (SFB ����)
    async Task<string> PickPcdPathAsync()
    {
        string result = null;

#if UNITY_STANDALONE_WIN
        // ���� ���̾�α״� ���ν����忡�� �ٷ� ȣ��
        try
        {
            var filters = new[] { new ExtensionFilter("PCD files", "pcd") };
            var paths = StandaloneFileBrowser.OpenFilePanel("Select PCD", "", filters, false);
            if (paths != null && paths.Length > 0)
                result = paths[0];
        }
        catch (Exception e)
        {
            Debug.LogError($"File dialog error: {e.Message}");
        }
#else
        Debug.LogWarning("File picker is only implemented for Windows Standalone in this sample.");
#endif
        await Task.Yield(); // ���Ļ� await ����
        return result;
    }

    // 3) �ε�/���� ����
    public async Task LoadPcdRuntimeAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        // ���� ���ҽ� ����(���ν�����)
        CleanupPrevious();

        // ��׶��� �ε�
        PcdData data = null;
        Exception loadErr = null;
        await Task.Run(() =>
        {
            try
            {
                data = PcdLoader.LoadFromFile(path);
            }
            catch (Exception e)
            {
                loadErr = e;
            }
        }).ConfigureAwait(false);

        if (loadErr != null) throw loadErr;
        if (data == null || data.positions == null || data.pointCount <= 0)
            throw new Exception("Invalid or empty PCD.");

        await RunOnMainThreadAsync(() =>
        {
            // ����ȭ
            if (normalizeToOrigin)
            {
                var c = data.Center;
                for (int i = 0; i < data.pointCount; i++) data.positions[i] -= c;
                data.boundsMin -= c; data.boundsMax -= c;
            }

            // GPU/Particle ���� (ComputeBuffer ����)
            if (useGpuRenderer)
            {
                if (gpuRenderer == null) gpuRenderer = GetComponent<PcdGpuRenderer>();
                if (gpuRenderer == null) gpuRenderer = gameObject.AddComponent<PcdGpuRenderer>();
                if (gpuRenderer.pointMaterial == null && gpuPointMaterial != null)
                    gpuRenderer.pointMaterial = gpuPointMaterial;

                gpuRenderer.useColors = data.colors != null && data.colors.Length == data.pointCount;
                gpuRenderer.pointSize = 0.02f;
                gpuRenderer.UploadData(data.positions, data.colors);
            }
            else
            {
                if (particleViewer == null) particleViewer = GetComponent<PcdViewer>();
                if (particleViewer == null) particleViewer = gameObject.AddComponent<PcdViewer>();
                particleViewer.pointSize = 0.02f;
                particleViewer.useColors = data.colors != null && data.colors.Length == data.pointCount;
                particleViewer.LoadAndShow(path);
            }

            if (forceCameraFocus)
                FocusMainCamera(normalizeToOrigin ? Vector3.zero : data.Center, data.Size);

            var orbit = FindAnyObjectByType<PcdViewerOrbitControllerRaycast>();
            if (orbit != null)
            {
                orbit.boundsCenter = normalizeToOrigin ? Vector3.zero : data.Center;
                orbit.boundsSize = data.Size;
            }
        }).ConfigureAwait(false);

        // 3) CPU �迭 ������ ��𼭵� ����������, �����ϰ� ���� ���� ��/�� ��� OK
        if (releaseCpuArraysAfterUpload && useGpuRenderer)
        {
            data.positions = null; data.colors = null; data.intensity = null;
        }
    }

    // ===== ���� ���� �޼���/ī�޶� ��ƿ =====

    void CleanupPrevious()
    {
        // GPU ������ ����
        if (gpuRenderer != null)
        {
            gpuRenderer.DisposeRenderer();
        }

        // ��ƼŬ ��� ����(�����ο� Clear�� �ִٸ� ȣ��)
        if (particleViewer != null)
        {
            try
            {
                var method = particleViewer.GetType().GetMethod("Clear", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                method?.Invoke(particleViewer, null);
            }
            catch { /* optional */ }
        }

#if UNITY_EDITOR
        Resources.UnloadUnusedAssets();
        System.GC.Collect();
#endif
    }

    // ��ƿ: ���� �����忡�� ���������� ����ǵ��� ����
    static Task RunOnMainThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        PostToMainThread(() =>
        {
            try { action(); tcs.TrySetResult(true); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }


    public void FrameNow(PcdData data)
    {
        var cam = Camera.main;
        if (cam == null || data == null || data.pointCount <= 0) return;

        var center = (data.boundsMin + data.boundsMax) * 0.5f;
        var size = (data.boundsMax - data.boundsMin);
        CameraFocusUtil.FocusCameraOnBounds(cam, center, size, cam.fieldOfView, 1.2f);
    }

    void FocusMainCamera(Vector3 center, Vector3 size)
    {
        var cam = Camera.main;
        if (cam == null) return;

        float maxExtent = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 0.5f;
        float fovRad = cam.fieldOfView * Mathf.Deg2Rad;
        float dist = (Mathf.Max(0.001f, maxExtent) * 1.5f) / Mathf.Tan(Mathf.Max(1e-3f, fovRad * 0.5f));

        cam.transform.LookAt(center);
        cam.transform.position = center - cam.transform.forward * Mathf.Max(dist, 1f);

        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = Mathf.Max(100000f, dist * 100f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.05f, 0.05f, 0.05f, 1f);
        cam.cullingMask = ~0; // Everything

        Debug.Log($"[Camera] pos={cam.transform.position}, lookAt={center}, near={cam.nearClipPlane}, far={cam.farClipPlane}, fov={cam.fieldOfView}, dist={dist:0.###}");
    }
}
