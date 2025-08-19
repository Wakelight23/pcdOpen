#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using System;
using System.Threading.Tasks;

// SFB 사용 시
#if UNITY_STANDALONE_WIN
using SFB; // StandaloneFileBrowser 네임스페이스
#endif

public class PcdEntry : MonoBehaviour
{
    [Header("Choose render path")]
    public bool useGpuRenderer = true;

    [Header("Targets")]
    public PcdViewer particleViewer;      // ParticleSystem 경로(옵션)
    public PcdGpuRenderer gpuRenderer;    // GPU 경로
    public Material gpuPointMaterial;     // Custom/PcdPoint 기반 머티리얼

    [Header("Visibility helpers")]
    [Tooltip("업로드 전에 모든 포인트에서 Center를 빼서 원점 기준으로 정규화합니다.")]
    public bool normalizeToOrigin = true;

    [Tooltip("로드 직후 카메라를 데이터 중심으로 강제 포커싱합니다.")]
    public bool forceCameraFocus = true;

    [Header("Runtime options")]
    [Tooltip("GPU 업로드 후 CPU 배열 해제")]
    public bool releaseCpuArraysAfterUpload = true;

    // 내부 상태: 간단 메인스레드 디스패처/코루틴 스타터
    static PcdEntry s_dispatcherOwner;
    static readonly System.Collections.Concurrent.ConcurrentQueue<Action> s_mainQueue = new();

    void Awake()
    {
        // 최초 인스턴스를 디스패처 소유자로 사용
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
            // 이전 리소스 정리
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

            // 카메라 강제 포커스
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

    // ===== 런타임 통합 진입점 =====

    // 1) 버튼 OnClick에 직접 연결할 수 있는 메서드
    public void OpenFileAndLoadRuntime()
    {
        // UI 스레드에서 시작 → 내부에서 비동기 진행
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

    // 2) 파일 선택 (SFB 권장)
    async Task<string> PickPcdPathAsync()
    {
        string result = null;

#if UNITY_STANDALONE_WIN
        // 파일 다이얼로그는 메인스레드에서 바로 호출
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
        await Task.Yield(); // 형식상 await 유지
        return result;
    }

    // 3) 로드/적용 통합
    public async Task LoadPcdRuntimeAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        // 이전 리소스 정리(메인스레드)
        CleanupPrevious();

        // 백그라운드 로딩
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
            // 정규화
            if (normalizeToOrigin)
            {
                var c = data.Center;
                for (int i = 0; i < data.pointCount; i++) data.positions[i] -= c;
                data.boundsMin -= c; data.boundsMax -= c;
            }

            // GPU/Particle 적용 (ComputeBuffer 포함)
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

        // 3) CPU 배열 해제는 어디서든 가능하지만, 안전하게 메인 람다 안/밖 모두 OK
        if (releaseCpuArraysAfterUpload && useGpuRenderer)
        {
            data.positions = null; data.colors = null; data.intensity = null;
        }
    }

    // ===== 기존 보조 메서드/카메라 유틸 =====

    void CleanupPrevious()
    {
        // GPU 렌더러 정리
        if (gpuRenderer != null)
        {
            gpuRenderer.DisposeRenderer();
        }

        // 파티클 뷰어 정리(구현부에 Clear가 있다면 호출)
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

    // 유틸: 메인 스레드에서 동기적으로 실행되도록 보장
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
