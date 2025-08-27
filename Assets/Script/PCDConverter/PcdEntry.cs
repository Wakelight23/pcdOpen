#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using System;
using System.Threading.Tasks;

// SFB 사용 시(Windows/Mac 스탠드얼론용 네이티브 파일 다이얼로그)
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX
using SFB;
#endif

[DefaultExecutionOrder(-20)]
public class PcdEntry : MonoBehaviour
{
    [Header("Choose render/streaming path")]
    public bool useStreamingController = true; // true면 PcdStreamingController 사용, false면 단발 PcdLoader + PcdGpuRenderer

    [Header("Targets")]
    public PcdGpuRenderer gpuRenderer;          // GPU 경로
    public Material gpuPointMaterial;           // Custom/PcdPoint 기반 머티리얼
    public Camera targetCamera;                 // 없으면 Camera.main 사용
    public Transform worldTransform;            // 포인트 좌표의 로컬->월드 변환 기준(없으면 this.transform)

    [Header("Visibility helpers")]
    [Tooltip("업로드 전에 모든 포인트에서 Center를 빼서 원점 기준으로 정규화합니다.")]
    public bool normalizeToOrigin = true;

    [Tooltip("로드 직후 카메라를 데이터 중심으로 강제 포커싱합니다.")]
    public bool forceCameraFocus = true;

    [Header("Streaming options")]
    [Tooltip("스트리밍 스케줄러 포인트 예산")]
    public int pointBudget = 5_000_000;
    [Tooltip("화면 에러 타깃(작을수록 더 높은 LOD 유도)")]
    public float screenErrorTarget = 2.0f;
    public int maxLoadsPerFrame = 2;
    public int maxUnloadsPerFrame = 4;
    [Tooltip("루트 초기 샘플 개수(스트리밍 모드)")]
    public int rootSampleCount = 200_000;
    [Tooltip("옥트리 최대 깊이(스트리밍 모드)")]
    public int octreeMaxDepth = 8;
    [Tooltip("최소/최대 포인트(노드 분할 기준)")]
    public int minPointsPerNode = 4096;
    public int maxPointsPerNode = 200_000;

    [Header("Runtime options")]
    [Tooltip("GPU 업로드 후 CPU 배열 해제(단발 로더 경로)")]
    public bool releaseCpuArraysAfterUpload = true;

    // 내부 상태
    PcdStreamingController _controller; // 스트리밍 모드에서 사용
    string _lastPath;

    // ===== 간단 메인스레드 디스패처 =====
    static PcdEntry s_dispatcherOwner;
    static readonly System.Collections.Concurrent.ConcurrentQueue<Action> s_mainQueue = new();

    public static event Action<float, string> OnProgress;


    void Awake()
    {
        if (s_dispatcherOwner == null) s_dispatcherOwner = this;

        // 타겟 카메라 기본값
        if (targetCamera == null) targetCamera = Camera.main;
        if (worldTransform == null) worldTransform = transform;

        // GPU 렌더러 기본 생성
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

        // 스트리밍 컨트롤러가 있다면, 매 프레임 카메라/옵션 갱신(인스펙터에서 실시간 조정 반영)
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

    // ====== UI 버튼(OnClick)에서 호출 ======
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

    // ====== 메인 진입: 경로를 받아 초기화 ======
    public async Task InitializeWithPathAsync(string path)
    {
        _lastPath = path;

        // 기존 리소스 정리
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
            // 간단 포커스: 렌더러의 총합 바운즈가 없으니, 스트리밍 컨트롤러 또는 단발 데이터로 포커스
            FocusCameraHeuristics();
        }

        Debug.Log($"[PcdEntry] Initialized: streaming={useStreamingController}, path={path}");
    }

    // ====== 스트리밍 컨트롤러 경로 ======
    async Task InitializeStreamingAsync(string path)
    {
        // 컨트롤러 준비
        Report(0.05f, "Prepare components");
        _controller = GetComponent<PcdStreamingController>();
        if (_controller == null) _controller = gameObject.AddComponent<PcdStreamingController>();

        // 컨트롤러 파라미터 주입
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

    // ====== 단발 로더 + GPU 업로드 경로 ======
    async Task InitializeSingleLoadAsync(string path)
    {
        try
        {
            // 백그라운드 스레드에서 로드
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

            // 정규화
            if (normalizeToOrigin)
            {
                var c = (data.boundsMin + data.boundsMax) * 0.5f;
                for (int i = 0; i < data.pointCount; i++) data.positions[i] -= c;
                data.boundsMin -= c; data.boundsMax -= c;
            }

            // GPU 업로드(메인 스레드)
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
                gpuRenderer.ClearAllNodes(); // 단일 노드를 1개로 취급
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

    // ====== 카메라 포커스 ======
    void FocusCameraHeuristics()
    {
        var cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null) return;

        // 스트리밍 모드면, 대략 현재 GPU 렌더러의 노드들을 합친 바운즈가 없으므로, 간단히 엔트리 Transform 원점 근방으로 세팅
        // 단발 로드의 경우엔 AddOrUpdateNode에서 준 노드 바운즈를 그대로 사용했으니 이미 그릴 수 있음.
        // 여기서는 통일적으로 엔트리 기준 Bounds를 추정해서 포커스하자.

        // 간단 근사: 렌더러 총 포인트 수가 있으면 카메라 거리를 적절히 당김
        float dist = 5f;
        if (gpuRenderer != null)
        {
            // 대략 포인트 수가 많을수록 더 멀리서 보이도록
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

    // ====== 리소스 정리 ======
    void CleanupPrevious()
    {
        // 스트리밍 컨트롤러의 내부 상태까지 완전 정리
        if (_controller != null)
        {
            try
            {
                // async 무시하기 싫다면 Wait/Result 대신 fire-and-forget로 스케줄하고 다음 프레임에 진행
                _ = _controller.DisposeAsync();
            }
            catch { /* ignore */ }
        }

        // GPU 렌더러 정리
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
        // 필요 시 옵션으로 제공: 강제 리소스/GC 회수
        Resources.UnloadUnusedAssets(); // 애셋 참조 끊긴 것 정리(시간 소요될 수 있음)
        System.GC.Collect();            // 큰 매니지드 배열 회수(빈번 호출은 지양)
#endif
    }

    // ====== 메인 스레드에서 동기 실행 보장 ======
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

    // ====== 디버그/유틸 ======
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
