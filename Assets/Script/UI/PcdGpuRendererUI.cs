using UnityEngine;

[DefaultExecutionOrder(10000)]
public class PcdGpuRendererUI : MonoBehaviour
{
    [Header("Targets")]
    public PcdGpuRenderer targetRenderer;
    public PcdBillboardRenderFeature renderFeature; // URP 렌더 피처

    [Header("Entry/Streaming Targets")]
    public PcdEntry entry;
    public PcdStreamingController streaming;

    [Header("Window")]
    public bool showUI = false;
    public KeyCode toggleKey = KeyCode.F1;
    public string windowTitle = "Pcd Runtime Graphics Options";
    public int windowId = 732841;
    public Rect windowRect = new Rect(20, 20, 420, 640);
    public bool lockCursorWhenHidden = false;

    // 내부 상태
    Vector2 _scroll;
    GUIStyle _section;

    public static bool InputBlockedByUI { get; private set; }

    void Awake()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponent<PcdGpuRenderer>();
        if (entry == null)
            entry = FindObjectOfType<PcdEntry>(true);
        if (streaming == null)
            streaming = FindObjectOfType<PcdStreamingController>(true);
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            ToggleUI();

        if (!showUI && lockCursorWhenHidden)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    public void ToggleUI()
    {
        showUI = !showUI;
        if (showUI)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else
        {
            InputBlockedByUI = false;
        }
    }

    void OnGUI()
    {
        if (!showUI)
        {
            InputBlockedByUI = false;
            return;
        }

        if (_section == null)
        {
            _section = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        }

        var minW = Mathf.Min(Screen.width - 40, Mathf.Max(380, (int)windowRect.width));
        var minH = Mathf.Min(Screen.height - 40, Mathf.Max(360, (int)windowRect.height));
        windowRect.width = minW;
        windowRect.height = minH;

        GUI.color = Color.white;
        bool mouseInWindowRough = windowRect.Contains(GetMousePosGUI());

        windowRect = GUI.Window(windowId, windowRect, DrawWindow, windowTitle);

        // 창 그린 후 최종 입력 차단 판정
        bool block = mouseInWindowRough || windowRect.Contains(GetMousePosGUI());
        InputBlockedByUI = block;
    }

    static Vector2 GetMousePosGUI()
    {
        Vector2 m = Input.mousePosition;
        m.y = Screen.height - m.y;
        return m;
    }

    void DrawWindow(int id)
    {
        if (GUI.Button(new Rect(windowRect.width - 24, 4, 20, 20), "×"))
        {
            showUI = false;
            InputBlockedByUI = false;
        }

        GUILayout.Space(4);
        _scroll = GUILayout.BeginScrollView(_scroll);

        // 바인딩 체크
        DrawBindingHelpers();

        // Entry / Streaming UI
        if (entry != null) DrawEntrySection();
        if (streaming != null) DrawStreamingSection();

        if (targetRenderer != null)
        {
            DrawRendererSection();
        }

        if (renderFeature != null)
        {
            DrawRenderFeatureSection();
        }

        DrawShaderMaterialSection();
        DrawStatsSection();
        DrawWindowOptions();

        GUILayout.EndScrollView();

        GUI.DragWindow(new Rect(0, 0, 10000, 22));
    }

    void DrawBindingHelpers()
    {
        GUILayout.Label("Bindings", _section);
        GUILayout.BeginVertical("box");

        // Target Renderer
        GUILayout.BeginHorizontal();
        GUILayout.Label("PcdGpuRenderer", GUILayout.Width(140));
        GUILayout.Label(targetRenderer != null ? targetRenderer.name : "(None)");
        if (GUILayout.Button("Find", GUILayout.Width(60)))
        {
            if (targetRenderer == null)
                targetRenderer = GetComponent<PcdGpuRenderer>();
            if (targetRenderer == null)
                targetRenderer = FindObjectOfType<PcdGpuRenderer>(true);
        }
        GUILayout.EndHorizontal();

        // Render Feature
        GUILayout.BeginHorizontal();
        GUILayout.Label("Render Feature", GUILayout.Width(140));
        GUILayout.Label(renderFeature != null ? renderFeature.name : "(None)");
        if (GUILayout.Button("Find", GUILayout.Width(60)))
        {
            if (renderFeature == null)
                renderFeature = FindObjectOfType<PcdBillboardRenderFeature>(true);
        }
        GUILayout.EndHorizontal();

        // Entry
        GUILayout.BeginHorizontal();
        GUILayout.Label("PcdEntry", GUILayout.Width(140));
        GUILayout.Label(entry != null ? entry.name : "(None)");
        if (GUILayout.Button("Find", GUILayout.Width(60)))
        {
            if (entry == null)
                entry = FindObjectOfType<PcdEntry>(true);
        }
        GUILayout.EndHorizontal();

        // Streaming Controller
        GUILayout.BeginHorizontal();
        GUILayout.Label("Streaming Ctrl", GUILayout.Width(140));
        GUILayout.Label(streaming != null ? streaming.name : "(None)");
        if (GUILayout.Button("Find", GUILayout.Width(60)))
        {
            if (streaming == null)
                streaming = FindObjectOfType<PcdStreamingController>(true);
        }
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUILayout.Space(6);
    }

    void DrawEntrySection()
    {
        GUILayout.Label("PcdEntry", _section);
        GUILayout.BeginVertical("box");

        // 경로/모드
        bool useStr = ToggleRow("Use Streaming Controller", entry.useStreamingController);
        if (useStr != entry.useStreamingController) entry.useStreamingController = useStr;

        //bool convCol = ToggleRow("Converter Use Colors", entry.converterUseColors);
        //if (convCol != entry.converterUseColors) entry.converterUseColors = convCol;

        // Targets
        GUILayout.Space(4);
        GUILayout.Label("Targets", _section);
        RowObj("GPU Renderer", ref entry.gpuRenderer, () =>
        {
            entry.gpuRenderer = entry.gpuRenderer ?? FindObjectOfType<PcdGpuRenderer>(true);
        });
        RowObj("GPU Point Material", ref entry.gpuPointMaterial);
        RowObj("Target Camera", ref entry.targetCamera, () =>
        {
            entry.targetCamera = entry.targetCamera ?? Camera.main;
        });
        RowObj("World Transform", ref entry.worldTransform, () =>
        {
            entry.worldTransform = entry.worldTransform ?? entry.transform;
        });

        // Visibility/Normalize
        GUILayout.Space(4);
        bool norm = ToggleRow("Normalize To Origin", entry.normalizeToOrigin);
        if (norm != entry.normalizeToOrigin) entry.normalizeToOrigin = norm;

        bool focus = ToggleRow("Force Camera Focus", entry.forceCameraFocus);
        if (focus != entry.forceCameraFocus) entry.forceCameraFocus = focus;

        // Streaming options
        GUILayout.Space(4);
        GUILayout.Label("Streaming Options", _section);
        entry.pointBudget = (int)SliderRow("Point Budget", entry.pointBudget, 100_000f, 50_000_000f);
        entry.screenErrorTarget = SliderRow("Screen Error Target", entry.screenErrorTarget, 0.25f, 16f);
        entry.maxLoadsPerFrame = (int)SliderRow("Max Loads/Frame", entry.maxLoadsPerFrame, 0f, 16f);
        entry.maxUnloadsPerFrame = (int)SliderRow("Max Unloads/Frame", entry.maxUnloadsPerFrame, 0f, 32f);
        entry.rootSampleCount = (int)SliderRow("Root Sample Count", entry.rootSampleCount, 1_000_000f, 5_000_000f);
        entry.octreeMaxDepth = (int)SliderRow("Octree Max Depth", entry.octreeMaxDepth, 1f, 16f);
        entry.minPointsPerNode = (int)SliderRow("Min Points/Node", entry.minPointsPerNode, 8f, 65_536f);
        entry.maxPointsPerNode = (int)SliderRow("Max Points/Node", entry.maxPointsPerNode, 256f, 1_000_000f);

        // Runtime options
        GUILayout.Space(4);
        bool rel = ToggleRow("Release CPU Arrays After Upload", entry.releaseCpuArraysAfterUpload);
        if (rel != entry.releaseCpuArraysAfterUpload) entry.releaseCpuArraysAfterUpload = rel;

        // Actions
        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Open PCD...")) entry.OpenFileAndLoadRuntime();
#if UNITY_EDITOR
        if (GUILayout.Button("Reload Last Path")) entry.SendMessage("ReloadLast", SendMessageOptions.DontRequireReceiver);
#endif
        if (GUILayout.Button("Clear Memory")) entry.ClearMemoryNow();
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUILayout.Space(6);
    }

    void DrawStreamingSection()
    {
        GUILayout.Label("Streaming Controller", _section);
        GUILayout.BeginVertical("box");

        // 바인딩 표시
        GUILayout.BeginHorizontal();
        GUILayout.Label("Controller", GUILayout.Width(140));
        GUILayout.Label(streaming != null ? streaming.name : "(None)");
        if (GUILayout.Button("Find", GUILayout.Width(60)))
            streaming = FindObjectOfType<PcdStreamingController>(true);
        GUILayout.EndHorizontal();

        if (streaming == null) { GUILayout.EndVertical(); GUILayout.Space(6); return; }

        // Data
        GUILayout.Space(4);
        bool useCol = ToggleRow("Use Colors", streaming.useColors);
        if (useCol != streaming.useColors) streaming.useColors = useCol;

        bool norm = ToggleRow("Normalize To Origin", streaming.normalizeToOrigin);
        if (norm != streaming.normalizeToOrigin) streaming.normalizeToOrigin = norm;

        // Sort
        GUILayout.Space(4);
        GUILayout.Label("Draw Sort Mode", _section);
        var modes = new[] { "FrontToBack", "BackToFront" };
        int cur = streaming.drawSortMode == DrawSortMode.FrontToBack ? 0 : 1;
        int next = GUILayout.SelectionGrid(cur, modes, 2);
        if (next != cur) streaming.drawSortMode = next == 0 ? DrawSortMode.FrontToBack : DrawSortMode.BackToFront;

        // Octree/LOD
        GUILayout.Space(4);
        streaming.octreeMaxDepth = (int)SliderRow("Octree Max Depth", streaming.octreeMaxDepth, 1f, 16f);
        streaming.minPointsPerNode = (int)SliderRow("Min Points/Node", streaming.minPointsPerNode, 8f, 65_536f);
        streaming.maxPointsPerNode = (int)SliderRow("Max Points/Node", streaming.maxPointsPerNode, 256f, 1_000_000f);
        streaming.rootSampleCount = (int)SliderRow("Root Sample Count", streaming.rootSampleCount, 10_000f, 2_000_000f);

        // Scheduling
        GUILayout.Space(4);
        streaming.pointBudget = (int)SliderRow("Point Budget", streaming.pointBudget, 100_000f, 50_000_000f);
        streaming.screenErrorTarget = SliderRow("Screen Error Target", streaming.screenErrorTarget, 0.25f, 16f);
        streaming.maxLoadsPerFrame = (int)SliderRow("Max Loads/Frame", streaming.maxLoadsPerFrame, 0f, 16f);
        streaming.maxUnloadsPerFrame = (int)SliderRow("Max Unloads/Frame", streaming.maxUnloadsPerFrame, 0f, 32f);

        // Rendering targets
        GUILayout.Space(4);
        RowObj("GPU Renderer", ref streaming.gpuRenderer, () =>
        {
            streaming.gpuRenderer = streaming.gpuRenderer ?? FindObjectOfType<PcdGpuRenderer>(true);
        });
        RowObj("Default Point Material", ref streaming.defaultPointMaterial);

        // Runtime Color Classification
        GUILayout.Space(4);
        bool cls = ToggleRow("Enable Runtime Color Classification", streaming.enableRuntimeColorClassification);
        if (cls != streaming.enableRuntimeColorClassification) streaming.enableRuntimeColorClassification = cls;
        RowObj("Color Classifier", ref streaming.colorClassifier);

        // Cache
        //GUILayout.Space(4);
        //bool useCache = ToggleRow("Use PCD Cache", streaming.usePcdCache);
        //if (useCache != streaming.usePcdCache) streaming.usePcdCache = useCache;

        //GUILayout.BeginHorizontal();
        //GUILayout.Label("Cache Dataset Dir", GUILayout.Width(140));
        //GUILayout.Label(string.IsNullOrEmpty(streaming.cacheDatasetDir) ? "(empty)" : streaming.cacheDatasetDir);
        //GUILayout.EndHorizontal();

        // Status
        GUILayout.Space(4);
        GUILayout.Label($"Active Nodes: {streaming.activeNodes}");
        GUILayout.Label($"Active Points: {streaming.activePoints}");
        GUILayout.Label($"Inflight Loads: {streaming.inflightLoads}");

        // Actions
        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Init From pcdPath")) streaming.InitializeFromInspector();
        if (GUILayout.Button("Dispose/Reset")) _ = streaming.DisposeAsync();
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUILayout.Space(6);
    }

    void DrawRendererSection()
    {
        GUILayout.Label("Renderer", _section);
        GUILayout.BeginVertical("box");

        // Colors
        bool useCol = ToggleRow("Use Colors", targetRenderer.useColors);
        if (useCol != targetRenderer.useColors) targetRenderer.useColors = useCol;

        // Point Size
        float sizePx = SliderRow("Point Size (px)", targetRenderer.pointSize, 0.5f, 64f);
        if (!Mathf.Approximately(sizePx, targetRenderer.pointSize)) targetRenderer.pointSize = sizePx;

        // Kernel
        float sharp = SliderRow("Kernel Sharpness", targetRenderer.kernelSharpness, 0.5f, 3f);
        if (!Mathf.Approximately(sharp, targetRenderer.kernelSharpness)) targetRenderer.kernelSharpness = sharp;
        bool gauss = ToggleRow("Gaussian Kernel", targetRenderer.gaussianKernel);
        if (gauss != targetRenderer.gaussianKernel) targetRenderer.gaussianKernel = gauss;

        // Depth front match
        float eps = SliderRow("Depth Match Eps", targetRenderer.depthMatchEps, 1e-5f, 5e-1f);
        if (!Mathf.Approximately(eps, targetRenderer.depthMatchEps)) targetRenderer.depthMatchEps = eps;

        // LOD size policy
        float minPx = SliderRow("Min Pixel Size", targetRenderer.minPixelSize, 0.25f, 8f);
        if (!Mathf.Approximately(minPx, targetRenderer.minPixelSize)) targetRenderer.minPixelSize = minPx;
        float maxPx = SliderRow("Max Pixel Size", targetRenderer.maxPixelSize, 4f, 128f);
        if (!Mathf.Approximately(maxPx, targetRenderer.maxPixelSize)) targetRenderer.maxPixelSize = maxPx;
        float sizeK = SliderRow("Size K", targetRenderer.sizeK, 0.25f, 4f);
        if (!Mathf.Approximately(sizeK, targetRenderer.sizeK)) targetRenderer.sizeK = sizeK;

        // Distance fades
        float nearF = SliderRow("Distance Fade Near", targetRenderer.distanceFadeNear, 0.1f, 200f);
        if (!Mathf.Approximately(nearF, targetRenderer.distanceFadeNear)) targetRenderer.distanceFadeNear = nearF;
        float farF = SliderRow("Distance Fade Far", targetRenderer.distanceFadeFar, 1f, 2000f);
        if (!Mathf.Approximately(farF, targetRenderer.distanceFadeFar)) targetRenderer.distanceFadeFar = farF;
        float parentFade = SliderRow("Parent Fade Base", targetRenderer.parentFadeBase, 0.4f, 1.0f);
        if (!Mathf.Approximately(parentFade, targetRenderer.parentFadeBase)) targetRenderer.parentFadeBase = parentFade;

        // Material-level LOD tint (node level feed)
        int maxDepthForMat = (int)SliderRow("Max Depth (Material)", targetRenderer.maxDepthForMaterial, 1f, 12f);
        if (maxDepthForMat != targetRenderer.maxDepthForMaterial) targetRenderer.maxDepthForMaterial = maxDepthForMat;

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Rebuild Draw Order"))
        {
            var ids = targetRenderer.GetActiveNodeIds();
            targetRenderer.SetDrawOrder((System.Collections.Generic.IList<int>)ids);
        }
        if (GUILayout.Button("Clear All Nodes"))
        {
            targetRenderer.ClearAllNodes();
        }
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUILayout.Space(6);
    }

    void DrawRenderFeatureSection()
    {
        GUILayout.Label("URP Feature (EDL/Accum)", _section);
        GUILayout.BeginVertical("box");

        var s = renderFeature.settings;

        // Point Budget + SSE in feature’s DepthProxy LOD aggregation
        int budget = (int)SliderRow("Point Budget", s.pointBudget, 100000f, 20000000f);
        if (budget != s.pointBudget) s.pointBudget = budget;
        float sse = SliderRow("SSE Threshold", s.sseThreshold, 0.25f, 4f);
        if (!Mathf.Approximately(sse, s.sseThreshold)) s.sseThreshold = sse;
        float hyst = SliderRow("SSE Hysteresis", s.sseHysteresis, 0.0f, 0.5f);
        if (!Mathf.Approximately(hyst, s.sseHysteresis)) s.sseHysteresis = hyst;

        // Accum RT format
        GUILayout.BeginHorizontal();
        GUILayout.Label("Accum Format", GUILayout.Width(140));
        var fmt = (RenderTextureFormat)EnumPopup((System.Enum)s.accumFormat);
        if (fmt != s.accumFormat) s.accumFormat = fmt;
        GUILayout.EndHorizontal();

        // EDL params
        if (s.edlSettings != null)
        {
            GUILayout.Space(4);
            GUILayout.Label("EDL", _section);
            bool hq = ToggleRow("High Quality", s.edlSettings.highQuality);
            if (hq != s.edlSettings.highQuality) s.edlSettings.highQuality = hq;

            float edlStrength = SliderRow("EDL Strength", s.edlSettings.edlStrength, 0f, 4f);
            if (!Mathf.Approximately(edlStrength, s.edlSettings.edlStrength)) s.edlSettings.edlStrength = edlStrength;

            float edlRadius = SliderRow("EDL Base Radius(px)", s.edlSettings.edlRadius, 0.5f, 16f);
            if (!Mathf.Approximately(edlRadius, s.edlSettings.edlRadius)) s.edlSettings.edlRadius = edlRadius;

            float brightBoost = SliderRow("Brightness Boost", s.edlSettings.brightnessBoost, 0.1f, 4f);
            if (!Mathf.Approximately(brightBoost, s.edlSettings.brightnessBoost)) s.edlSettings.brightnessBoost = brightBoost;

            // Downsample factor for EDL pass
            int ds = (int)SliderRow("EDL Downsample", s.edlDownsample, 1f, 4f);
            ds = Mathf.Clamp(ds, 1, 4);
            if (ds != s.edlDownsample) s.edlDownsample = ds;

            // Depth proxy format
            GUILayout.BeginHorizontal();
            GUILayout.Label("Depth RT Format", GUILayout.Width(140));
            var depFmt = (RenderTextureFormat)EnumPopup((System.Enum)s.edlSettings.depthFormat);
            if (depFmt != s.edlSettings.depthFormat) s.edlSettings.depthFormat = depFmt;
            GUILayout.EndHorizontal();

            // Depth match eps used by splat shaders (propagated through passes)
            float matchEps = SliderRow("Depth Match Eps", s.edlSettings.depthMatchEps, 1e-5f, 5e-1f);
            if (!Mathf.Approximately(matchEps, s.edlSettings.depthMatchEps)) s.edlSettings.depthMatchEps = matchEps;
        }

        // Radius scale by average px (global)
        float radiusK = SliderRow("EDL Radius Scale K", s.edlRadiusScaleK, 0.0f, 4.0f);
        if (!Mathf.Approximately(radiusK, s.edlRadiusScaleK)) s.edlRadiusScaleK = radiusK;

        GUILayout.EndVertical();
        GUILayout.Space(6);
    }

    void DrawShaderMaterialSection()
    {
        if (targetRenderer == null) return;
        var mat = targetRenderer.pointMaterial;
        if (mat == null) return;

        GUILayout.Label("Splat Shader (Material)", _section);
        GUILayout.BeginVertical("box");

        // Distance color controls (Custom/PcdSplatAccum properties)
        bool useDist = ReadMaterialToggle(mat, "_UseDistanceColor", true);
        bool newUseDist = ToggleRow("Use Distance Color", useDist);
        if (newUseDist != useDist) mat.SetFloat("_UseDistanceColor", newUseDist ? 1f : 0f);

        // Colors
        Color nearCol = ReadMaterialColor(mat, "_NearColor", Color.white);
        Color farCol = ReadMaterialColor(mat, "_FarColor", new Color(0.6f, 0.9f, 1f, 1f));
        GUILayout.BeginHorizontal();
        GUILayout.Label("Near Color", GUILayout.Width(140));
        var newNear = RGBField(nearCol);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("Far Color", GUILayout.Width(140));
        var newFar = RGBField(farCol);
        GUILayout.EndHorizontal();
        if (newNear != nearCol) mat.SetColor("_NearColor", newNear);
        if (newFar != farCol) mat.SetColor("_FarColor", newFar);

        // Near/Far distances
        float nearD = ReadMaterialFloat(mat, "_NearDist", 2f);
        float farD = ReadMaterialFloat(mat, "_FarDist", 25f);
        float newNearD = SliderRow("Near Distance", nearD, 0.01f, 100f);
        float newFarD = SliderRow("Far Distance", farD, 0.02f, 1000f);
        newFarD = Mathf.Max(newFarD, newNearD + 0.01f);
        if (!Mathf.Approximately(newNearD, nearD)) mat.SetFloat("_NearDist", newNearD);
        if (!Mathf.Approximately(newFarD, farD)) mat.SetFloat("_FarDist", newFarD);

        // Mode enum (0=Replace,1=Multiply,2=Overlay)
        float mode = ReadMaterialFloat(mat, "_DistMode", 0f);
        int imode = (int)mode;
        GUILayout.BeginHorizontal();
        GUILayout.Label("Distance Color Mode", GUILayout.Width(140));
        string[] modes = { "Replace", "Multiply", "Overlay" };
        int newMode = GUILayout.SelectionGrid(Mathf.Clamp(imode, 0, 2), modes, 3);
        GUILayout.EndHorizontal();
        if (newMode != imode) mat.SetFloat("_DistMode", newMode);

        // Kernel params also surfaced on material (mirror fields)
        float kSharp = ReadMaterialFloat(mat, "_KernelSharpness", 1.5f);
        float newSharp = SliderRow("Mat Kernel Sharpness", kSharp, 0.5f, 3f);
        if (!Mathf.Approximately(newSharp, kSharp)) mat.SetFloat("_KernelSharpness", newSharp);

        bool g = ReadMaterialToggle(mat, "_Gaussian", false);
        bool newG = ToggleRow("Mat Gaussian Kernel", g);
        if (newG != g) mat.SetFloat("_Gaussian", newG ? 1f : 0f);

        // EDL influence from material (color attenuation strength)
        float edlS = ReadMaterialFloat(mat, "_EdlStrength", 1.0f);
        float newEdlS = SliderRow("Mat EDL Strength", edlS, 0f, 4f);
        if (!Mathf.Approximately(newEdlS, edlS)) mat.SetFloat("_EdlStrength", newEdlS);

        GUILayout.EndVertical();
        GUILayout.Space(6);
    }

    void DrawStatsSection()
    {
        if (targetRenderer == null) return;

        GUILayout.Label("Stats", _section);
        GUILayout.BeginVertical("box");
        GUILayout.Label($"Active Nodes: {targetRenderer.activeNodeCount}");
        GUILayout.Label($"Total Points: {targetRenderer.totalPointCount}");
        GUILayout.EndVertical();
        GUILayout.Space(6);
    }

    void DrawWindowOptions()
    {
        GUILayout.Label("Window", _section);
        GUILayout.BeginVertical("box");

        GUILayout.BeginHorizontal();
        GUILayout.Label("Toggle Key", GUILayout.Width(140));
        GUILayout.Label(toggleKey.ToString(), GUILayout.Width(160));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        bool unlock = ToggleRow("Unlock Cursor When Hidden", lockCursorWhenHidden);
        if (unlock != lockCursorWhenHidden) lockCursorWhenHidden = unlock;

        GUILayout.EndVertical();
    }

    // ----- 공용 위젯 -----

    void RowObj<T>(string label, ref T obj, System.Action onFind = null) where T : UnityEngine.Object
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        GUILayout.Label(obj != null ? obj.name : "(None)");
        if (GUILayout.Button("Find", GUILayout.Width(60))) onFind?.Invoke();
        GUILayout.EndHorizontal();
    }

    bool ToggleRow(string label, bool value)
    {
        GUILayout.BeginHorizontal();
        bool v = GUILayout.Toggle(value, label);
        GUILayout.EndHorizontal();
        return v;
    }

    float SliderRow(string label, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(160));
        GUILayout.Label(value.ToString("0.###"), GUILayout.Width(60));
        GUILayout.EndHorizontal();
        float v = GUILayout.HorizontalSlider(value, min, max);
        return v;
    }

    System.Enum EnumPopup(System.Enum value)
    {
        // 런타임에 드롭다운을 간단히 표시: 현재값만 표시(선택은 생략)
        GUILayout.Label(value.ToString(), GUILayout.ExpandWidth(true));
        return value;
    }

    Color RGBField(Color c)
    {
        // 간단 RGB 수치 슬라이더
        GUILayout.BeginVertical();
        float r = GUILayout.HorizontalSlider(c.r, 0f, 1f);
        float g = GUILayout.HorizontalSlider(c.g, 0f, 1f);
        float b = GUILayout.HorizontalSlider(c.b, 0f, 1f);
        GUILayout.EndVertical();
        return new Color(r, g, b, 1f);
    }

    // ----- 머티리얼 파라미터 읽기 -----

    static bool ReadMaterialToggle(Material m, string name, bool def = false)
    {
        if (m.HasProperty(name)) return m.GetFloat(name) > 0.5f;
        return def;
    }

    static float ReadMaterialFloat(Material m, string name, float def = 0)
    {
        if (m.HasProperty(name)) return m.GetFloat(name);
        return def;
    }

    static Color ReadMaterialColor(Material m, string name, Color def)
    {
        if (m.HasProperty(name)) return m.GetColor(name);
        return def;
    }
}