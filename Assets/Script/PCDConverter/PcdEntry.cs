#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using System;
using System.Threading.Tasks;

// SFB ��� ��(Windows/Mac ���ĵ��п� ����Ƽ�� ���� ���̾�α�)
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX
using SFB;
#endif

[DefaultExecutionOrder(-20)]
public class PcdEntry : MonoBehaviour
{
    [Header("Choose render/streaming path")]
    public bool useStreamingController = true; // true�� PcdStreamingController ���, false�� �ܹ� PcdLoader + PcdGpuRenderer

    [Header("Targets")]
    public PcdGpuRenderer gpuRenderer;          // GPU ���
    public Material gpuPointMaterial;           // Custom/PcdPoint ��� ��Ƽ����
    public Camera targetCamera;                 // ������ Camera.main ���
    public Transform worldTransform;            // ����Ʈ ��ǥ�� ����->���� ��ȯ ����(������ this.transform)

    [Header("Visibility helpers")]
    [Tooltip("���ε� ���� ��� ����Ʈ���� Center�� ���� ���� �������� ����ȭ�մϴ�.")]
    public bool normalizeToOrigin = true;

    [Tooltip("�ε� ���� ī�޶� ������ �߽����� ���� ��Ŀ���մϴ�.")]
    public bool forceCameraFocus = true;

    [Header("Streaming options")]
    [Tooltip("��Ʈ���� �����ٷ� ����Ʈ ����")]
    public int pointBudget = 5_000_000;
    [Tooltip("ȭ�� ���� Ÿ��(�������� �� ���� LOD ����)")]
    public float screenErrorTarget = 2.0f;
    public int maxLoadsPerFrame = 2;
    public int maxUnloadsPerFrame = 4;
    [Tooltip("��Ʈ �ʱ� ���� ����(��Ʈ���� ���)")]
    public int rootSampleCount = 200_000;
    [Tooltip("��Ʈ�� �ִ� ����(��Ʈ���� ���)")]
    public int octreeMaxDepth = 8;
    [Tooltip("�ּ�/�ִ� ����Ʈ(��� ���� ����)")]
    public int minPointsPerNode = 4096;
    public int maxPointsPerNode = 200_000;

    [Header("Runtime options")]
    [Tooltip("GPU ���ε� �� CPU �迭 ����(�ܹ� �δ� ���)")]
    public bool releaseCpuArraysAfterUpload = true;

    // ���� ����
    PcdStreamingController _controller; // ��Ʈ���� ��忡�� ���
    string _lastPath;

    // ===== ���� ���ν����� ����ó =====
    static PcdEntry s_dispatcherOwner;
    static readonly System.Collections.Concurrent.ConcurrentQueue<Action> s_mainQueue = new();

    public static event Action<float, string> OnProgress;


    void Awake()
    {
        if (s_dispatcherOwner == null) s_dispatcherOwner = this;

        // Ÿ�� ī�޶� �⺻��
        if (targetCamera == null) targetCamera = Camera.main;
        if (worldTransform == null) worldTransform = transform;

        // GPU ������ �⺻ ����
        if (gpuRenderer == null)
        {
            gpuRenderer = GetComponent<PcdGpuRenderer>();
            if (gpuRenderer == null) gpuRenderer = gameObject.AddComponent<PcdGpuRenderer>();
        }
        if (gpuRenderer.pointMaterial == null && gpuPointMaterial != null)
            gpuRenderer.pointMaterial = gpuPointMaterial;
    }

    void Update()
    {
        while (s_mainQueue.TryDequeue(out var act))
        {
            try { act?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // ��Ʈ���� ��Ʈ�ѷ��� �ִٸ�, �� ������ ī�޶�/�ɼ� ����(�ν����Ϳ��� �ǽð� ���� �ݿ�)
        if (_controller != null)
        {
            if (_controller != null)
            {
                _controller.useColors = (gpuRenderer != null) ? gpuRenderer.useColors : true;
                _controller.normalizeToOrigin = normalizeToOrigin;

                _controller.targetCamera = targetCamera != null ? targetCamera : Camera.main;
                _controller.worldTransform = worldTransform != null ? worldTransform : transform;

                _controller.pointBudget = pointBudget;
                _controller.screenErrorTarget = screenErrorTarget;
                _controller.maxLoadsPerFrame = maxLoadsPerFrame;
                _controller.maxUnloadsPerFrame = maxUnloadsPerFrame;
            }
        }
    }

    public static void PostToMainThread(Action a) => s_mainQueue.Enqueue(a);



#if UNITY_EDITOR
    [ContextMenu("Load PCD (Editor)")]
    public void LoadPcdEditor()
    {
        string path = EditorUtility.OpenFilePanel("Select PCD", "", "pcd");
        if (string.IsNullOrEmpty(path)) return;
        _ = InitializeWithPathAsync(path);
    }
#endif

    // ====== UI ��ư(OnClick)���� ȣ�� ======
    public void OpenFileAndLoadRuntime()
    {
        _ = OpenAndLoadRoutine();
    }

    async Task OpenAndLoadRoutine()
    {
        try
        {
            string path = await PickPcdPathAsync();
            if (string.IsNullOrEmpty(path)) return;
            await InitializeWithPathAsync(path);
        }
        catch (Exception ex)
        {
            Debug.LogError(ex);
        }
    }

    async Task<string> PickPcdPathAsync()
    {
        string result = null;

#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX
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
        Debug.LogWarning("File picker is only implemented for Windows/OSX Standalone in this sample.");
#endif
        await Task.Yield();
        return result;
    }

    // ====== ���� ����: ��θ� �޾� �ʱ�ȭ ======
    public async Task InitializeWithPathAsync(string path)
    {
        _lastPath = path;

        // ���� ���ҽ� ����
        CleanupPrevious();

        if (useStreamingController)
        {

            await InitializeStreamingAsync(path);
        }
        else
        {
            await InitializeSingleLoadAsync(path);
        }

        if (forceCameraFocus)
        {
            // ���� ��Ŀ��: �������� ���� �ٿ�� ������, ��Ʈ���� ��Ʈ�ѷ� �Ǵ� �ܹ� �����ͷ� ��Ŀ��
            FocusCameraHeuristics();
        }

        Debug.Log($"[PcdEntry] Initialized: streaming={useStreamingController}, path={path}");
    }

    // ====== ��Ʈ���� ��Ʈ�ѷ� ��� ======
    async Task InitializeStreamingAsync(string path)
    {
        // ��Ʈ�ѷ� �غ�
        Report(0.05f, "Prepare components");
        _controller = GetComponent<PcdStreamingController>();
        if (_controller == null) _controller = gameObject.AddComponent<PcdStreamingController>();

        // ��Ʈ�ѷ� �Ķ���� ����
        _controller.pcdPath = path;
        _controller.useColors = (gpuRenderer != null) ? gpuRenderer.useColors : true;
        _controller.normalizeToOrigin = normalizeToOrigin;

        _controller.octreeMaxDepth = octreeMaxDepth;
        _controller.minPointsPerNode = minPointsPerNode;
        _controller.maxPointsPerNode = maxPointsPerNode;
        _controller.rootSampleCount = rootSampleCount;

        _controller.pointBudget = pointBudget;
        _controller.screenErrorTarget = screenErrorTarget;
        _controller.maxLoadsPerFrame = maxLoadsPerFrame;
        _controller.maxUnloadsPerFrame = maxUnloadsPerFrame;

        _controller.targetCamera = targetCamera != null ? targetCamera : Camera.main;
        _controller.worldTransform = worldTransform != null ? worldTransform : transform;

        _controller.gpuRenderer = gpuRenderer;
        if (_controller.gpuRenderer != null && _controller.gpuRenderer.pointMaterial == null && gpuPointMaterial != null)
            _controller.gpuRenderer.pointMaterial = gpuPointMaterial;

        Report(0.1f, "Start streaming init");
        await _controller.InitializeAsync(path);
        Report(0.95f, "Finalize");
    }

    // ====== �ܹ� �δ� + GPU ���ε� ��� ======
    async Task InitializeSingleLoadAsync(string path)
    {
        try
        {
            // ��׶��� �����忡�� �ε�
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

            // ����ȭ
            if (normalizeToOrigin)
            {
                var c = (data.boundsMin + data.boundsMax) * 0.5f;
                for (int i = 0; i < data.pointCount; i++) data.positions[i] -= c;
                data.boundsMin -= c; data.boundsMax -= c;
            }

            // GPU ���ε�(���� ������)
            await RunOnMainThreadAsync(() =>
            {
                if (gpuRenderer == null)
                {
                    gpuRenderer = GetComponent<PcdGpuRenderer>();
                    if (gpuRenderer == null) gpuRenderer = gameObject.AddComponent<PcdGpuRenderer>();
                }
                if (gpuRenderer.pointMaterial == null && gpuPointMaterial != null)
                    gpuRenderer.pointMaterial = gpuPointMaterial;

                gpuRenderer.useColors = (data.colors != null && data.colors.Length == data.pointCount);
                gpuRenderer.pointSize = 0.02f;
                gpuRenderer.ClearAllNodes(); // ���� ��带 1���� ���
                gpuRenderer.AddOrUpdateNode(0, data.positions, data.colors, new Bounds((data.boundsMin + data.boundsMax) * 0.5f, data.boundsMax - data.boundsMin));
            });

            if (releaseCpuArraysAfterUpload)
            {
                data.positions = null; data.colors = null; data.intensity = null;
            }
        }
        catch (Exception e)
        {
            Debug.LogError(e);
        }
    }

    // ====== ī�޶� ��Ŀ�� ======
    void FocusCameraHeuristics()
    {
        var cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null) return;

        // ��Ʈ���� ����, �뷫 ���� GPU �������� ������ ��ģ �ٿ�� �����Ƿ�, ������ ��Ʈ�� Transform ���� �ٹ����� ����
        // �ܹ� �ε��� ��쿣 AddOrUpdateNode���� �� ��� �ٿ�� �״�� ��������� �̹� �׸� �� ����.
        // ���⼭�� ���������� ��Ʈ�� ���� Bounds�� �����ؼ� ��Ŀ������.

        // ���� �ٻ�: ������ �� ����Ʈ ���� ������ ī�޶� �Ÿ��� ������ ���
        float dist = 5f;
        if (gpuRenderer != null)
        {
            // �뷫 ����Ʈ ���� �������� �� �ָ��� ���̵���
            var count = Mathf.Max(1, gpuRenderer.totalPointCount);
            dist = Mathf.Clamp(Mathf.Log10(count) * 0.5f + 3f, 3f, 100f);
        }

        var center = worldTransform != null ? worldTransform.position : transform.position;
        cam.transform.LookAt(center);
        cam.transform.position = center - cam.transform.forward * dist;

        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = Mathf.Max(100000f, dist * 100f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.05f, 0.05f, 0.05f, 1f);

        Debug.Log($"[PcdEntry] Camera focused. pos={cam.transform.position}, lookAt={center}, near={cam.nearClipPlane}, far={cam.farClipPlane}, dist={dist:0.###}");
    }

    // ====== ���ҽ� ���� ======
    void CleanupPrevious()
    {
        // ��Ʈ���� ��Ʈ�ѷ��� ���� ���±��� ���� ����
        if (_controller != null)
        {
            try
            {
                // async �����ϱ� �ȴٸ� Wait/Result ��� fire-and-forget�� �������ϰ� ���� �����ӿ� ����
                _ = _controller.DisposeAsync();
            }
            catch { /* ignore */ }
        }

        // GPU ������ ����
        if (gpuRenderer != null)
        {
            try
            {
                gpuRenderer.ClearAllNodes();
                gpuRenderer.SetDrawOrder(Array.Empty<int>());
            }
            catch { /* ignore */ }
        }

#if UNITY_EDITOR
        // �ʿ� �� �ɼ����� ����: ���� ���ҽ�/GC ȸ��
        Resources.UnloadUnusedAssets(); // �ּ� ���� ���� �� ����(�ð� �ҿ�� �� ����)
        System.GC.Collect();            // ū �Ŵ����� �迭 ȸ��(��� ȣ���� ����)
#endif
    }

    // ====== ���� �����忡�� ���� ���� ���� ======
    public static Task RunOnMainThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        PostToMainThread(() =>
        {
            try { action?.Invoke(); tcs.TrySetResult(true); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }

    // ====== �����/��ƿ ======
#if UNITY_EDITOR
    [ContextMenu("Reload Last Path")]
    void ReloadLast()
    {
        if (string.IsNullOrEmpty(_lastPath))
        {
            Debug.LogWarning("[PcdEntry] No last path.");
            return;
        }
        _ = InitializeWithPathAsync(_lastPath);
    }
#endif

    #region ProgressBar Util
    static public void Report(float t, string label = null)
    {
        OnProgress?.Invoke(Mathf.Clamp01(t), label);
    }
    #endregion
}
