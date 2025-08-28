using UnityEngine;

[DefaultExecutionOrder(10000)]
public class PcdGpuRendererUI : MonoBehaviour
{
    [Header("Target")]
    public PcdGpuRenderer targetRenderer;

    [Header("Color Mapping UI")]
    public PcdStreamingController streamingController;

    [Header("Window")]
    public bool showUI = false;
    public KeyCode toggleKey = KeyCode.F1;
    public string windowTitle = "PCD Renderer Settings";
    public int windowId = 732841;
    public Rect windowRect = new Rect(20, 20, 380, 600);
    public bool lockCursorWhenHidden = false;


    // 내부 상태
    Vector2 _scroll;

    // UI 입력 차단 관련
    public static bool InputBlockedByUI { get; private set; }
    private static PcdGpuRendererUI _activeInstance;

    void Awake()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponent<PcdGpuRenderer>();

        if (streamingController == null)
            streamingController = GetComponent<PcdStreamingController>();

        // 활성 인스턴스 등록
        _activeInstance = this;
    }

    void OnDestroy()
    {
        if (_activeInstance == this)
            _activeInstance = null;
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            ToggleUI();

        // UI 위에 마우스가 있는지 체크
        UpdateInputBlocking();

        // 포커스 잃은 뒤 마우스 락 해제 등 필요 시
        if (!showUI && lockCursorWhenHidden)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    void UpdateInputBlocking()
    {
        if (!showUI)
        {
            InputBlockedByUI = false;
            return;
        }

        Vector3 mousePos = Input.mousePosition;
        // Unity GUI 좌표계는 화면 좌상단이 (0,0)이므로 변환
        Vector2 guiMousePos = new Vector2(mousePos.x, Screen.height - mousePos.y);

        InputBlockedByUI = windowRect.Contains(guiMousePos);
    }

    public void ToggleUI()
    {
        showUI = !showUI;
        if (showUI)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    void OnGUI()
    {
        if (!showUI) return;

        // 화면 해상도에 비례하여 최소 크기 보정
        var minW = Mathf.Min(Screen.width - 40, Mathf.Max(380, (int)windowRect.width));
        var minH = Mathf.Min(Screen.height - 40, Mathf.Max(600, (int)windowRect.height));
        windowRect.width = minW;
        windowRect.height = minH;

        GUI.color = Color.white;
        windowRect = GUI.Window(windowId, windowRect, DrawWindow, windowTitle);
    }

    // ... 나머지 기존 코드는 동일 ...

    void DrawWindow(int id)
    {
        if (targetRenderer == null)
        {
            GUILayout.Label("No PcdGpuRenderer bound.");
            if (GUILayout.Button("Find on this GameObject"))
                targetRenderer = GetComponent<PcdGpuRenderer>();
            if (GUI.Button(new Rect(windowRect.width - 24, 4, 20, 20), "×"))
            {
                showUI = false;
            }
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
            return;
        }

        // 상단 Close 버튼
        if (GUI.Button(new Rect(windowRect.width - 24, 4, 20, 20), "×"))
        {
            showUI = false;
        }

        GUILayout.Space(4);
        _scroll = GUILayout.BeginScrollView(_scroll);

        // Render Settings Section
        DrawSectionHeader("Render Settings");

        // Material
        var mat = EditorLikeObjectField("Point Material", targetRenderer.pointMaterial);
        if (mat != targetRenderer.pointMaterial)
        {
            targetRenderer.pointMaterial = mat;
        }

        float sizePx = EditorLikeSlider("Point Size (px)", targetRenderer.pointSize, 0.5f, 64f);
        if (!Mathf.Approximately(sizePx, targetRenderer.pointSize)) targetRenderer.pointSize = sizePx;

        // Use Colors toggle
        bool useCol = EditorLikeToggle("Use Colors", targetRenderer.useColors);
        if (useCol != targetRenderer.useColors) targetRenderer.useColors = useCol;

        // Soft Edge
        float soft = EditorLikeSlider("Soft Edge", targetRenderer.softEdge, 0f, 1f);
        if (!Mathf.Approximately(soft, targetRenderer.softEdge)) targetRenderer.softEdge = soft;

        // Round Mask
        bool round = EditorLikeToggle("Round Mask", targetRenderer.roundMask);
        if (round != targetRenderer.roundMask) targetRenderer.roundMask = round;

        // Kernel Settings Section  
        GUILayout.Space(8);
        DrawSectionHeader("Kernel Settings");

        // Kernel Sharpness
        float kernelSharpness = EditorLikeSlider("Kernel Sharpness",
            targetRenderer.kernelSharpness, 0.5f, 3f);
        if (!Mathf.Approximately(kernelSharpness, targetRenderer.kernelSharpness))
            targetRenderer.kernelSharpness = kernelSharpness;

        // Gaussian Kernel
        bool gaussian = EditorLikeToggle("Gaussian Kernel", targetRenderer.gaussianKernel);
        if (gaussian != targetRenderer.gaussianKernel)
            targetRenderer.gaussianKernel = gaussian;

        // Streaming Controller Section
        if (streamingController != null)
        {
            GUILayout.Space(8);
            DrawSectionHeader("Streaming Settings");

            // Point Budget
            int pointBudget = EditorLikeIntSlider("Point Budget", streamingController.pointBudget, 100_000, 10_000_000);
            if (pointBudget != streamingController.pointBudget)
                streamingController.pointBudget = pointBudget;

            // Screen Error Target
            float screenError = EditorLikeSlider("Screen Error Target",
                streamingController.screenErrorTarget, 0.5f, 10f);
            if (!Mathf.Approximately(screenError, streamingController.screenErrorTarget))
                streamingController.screenErrorTarget = screenError;

            // Max Loads Per Frame
            int maxLoads = EditorLikeIntSlider("Max Loads/Frame",
                streamingController.maxLoadsPerFrame, 1, 10);
            if (maxLoads != streamingController.maxLoadsPerFrame)
                streamingController.maxLoadsPerFrame = maxLoads;

            // Max Unloads Per Frame
            int maxUnloads = EditorLikeIntSlider("Max Unloads/Frame",
                streamingController.maxUnloadsPerFrame, 1, 10);
            if (maxUnloads != streamingController.maxUnloadsPerFrame)
                streamingController.maxUnloadsPerFrame = maxUnloads;

            // Color Classification Section
            GUILayout.Space(8);
            DrawSectionHeader("Color Classification");

            // Enable Runtime Color Classification
            bool enableColorClass = EditorLikeToggle("Enable Runtime Classification",
                streamingController.enableRuntimeColorClassification);
            if (enableColorClass != streamingController.enableRuntimeColorClassification)
                streamingController.enableRuntimeColorClassification = enableColorClass;

            // RuntimePointClassifier Settings
            if (streamingController.colorClassifier != null && streamingController.enableRuntimeColorClassification)
            {
                var classifier = streamingController.colorClassifier;

                // settings 접근이 가능한지 체크
                if (HasSettingsProperty(classifier))
                {
                    DrawClassifierSettings(classifier);
                }
            }
        }

        // Stats Section
        GUILayout.Space(8);
        DrawSectionHeader("Statistics");
        GUILayout.Label($"Active Nodes: {targetRenderer.activeNodeCount}");
        GUILayout.Label($"Total Points: {targetRenderer.totalPointCount}");

        if (streamingController != null)
        {
            GUILayout.Label($"Active Points: {streamingController.activePoints}");
            GUILayout.Label($"Inflight Loads: {streamingController.inflightLoads}");
        }

        // Actions Section
        GUILayout.Space(8);
        DrawSectionHeader("Actions");
        if (GUILayout.Button("Rebuild Draw Order"))
        {
            var ids = targetRenderer.GetActiveNodeIds();
            targetRenderer.SetDrawOrder((System.Collections.Generic.IList<int>)ids);
        }

        // Window Settings Section
        GUILayout.Space(8);
        DrawSectionHeader("Window Settings");
        toggleKey = (KeyCode)EditorLikeEnumPopup("Toggle Key", toggleKey);
        lockCursorWhenHidden = EditorLikeToggle("Unlock Cursor When Hidden", lockCursorWhenHidden);

        GUILayout.Space(10);
        GUILayout.EndScrollView();

        GUI.DragWindow(new Rect(0, 0, 10000, 22));
    }

    // Classifier가 settings 프로퍼티를 가지고 있는지 체크
    bool HasSettingsProperty(RuntimePointClassifier classifier)
    {
        var type = classifier.GetType();
        return type.GetProperty("settings") != null || type.GetField("settings") != null;
    }

    // Classifier 설정 UI 그리기
    void DrawClassifierSettings(RuntimePointClassifier classifier)
    {
        try
        {
            // 리플렉션을 사용해 settings에 접근
            var type = classifier.GetType();
            var settingsField = type.GetField("settings");
            var settingsProperty = type.GetProperty("settings");

            object settings = null;
            if (settingsField != null)
                settings = settingsField.GetValue(classifier);
            else if (settingsProperty != null)
                settings = settingsProperty.GetValue(classifier);

            if (settings != null)
            {
                GUILayout.Space(4);
                GUILayout.Label("Classification Parameters", EditorStyles.miniBoldLabel);

                // settings의 각 필드를 체크하고 UI로 표시
                var settingsType = settings.GetType();

                // Surface Threshold
                DrawFloatField(settings, settingsType, "surfaceThreshold", "Surface Threshold", 0.01f, 0.1f);

                // Normal Smooth Radius
                DrawFloatField(settings, settingsType, "normalSmoothRadius", "Normal Smooth Radius", 0.05f, 0.5f);

                // K Neighbors
                DrawIntField(settings, settingsType, "kNeighbors", "K Neighbors", 4, 16);

                GUILayout.Space(4);
                GUILayout.Label("Color Enhancement", EditorStyles.miniBoldLabel);

                // Contrast Boost
                DrawFloatField(settings, settingsType, "contrastBoost", "Contrast Boost", 0.5f, 2f);

                // Saturation Boost
                DrawFloatField(settings, settingsType, "saturationBoost", "Saturation Boost", 0.5f, 2f);

                // Enable Outlier Removal
                DrawBoolField(settings, settingsType, "enableOutlierRemoval", "Enable Outlier Removal");

                // settings를 다시 할당
                if (settingsField != null)
                    settingsField.SetValue(classifier, settings);
                else if (settingsProperty != null)
                    settingsProperty.SetValue(classifier, settings);
            }

            // Use Job System
            DrawClassifierBoolField(classifier, "useJobSystem", "Use Job System");

            GUILayout.Space(4);
            GUILayout.Label("Color Mapping", EditorStyles.miniBoldLabel);

            // Color mapping display (read-only)
            DrawClassifierColorField(classifier, "interiorColor", "Interior Color");
            DrawClassifierColorField(classifier, "exteriorColor", "Exterior Color");
            DrawClassifierColorField(classifier, "boundaryColor", "Boundary Color");
            DrawClassifierColorField(classifier, "unknownColor", "Unknown Color");
        }
        catch (System.Exception e)
        {
            GUILayout.Label($"Settings unavailable: {e.Message}");
        }
    }

    void DrawFloatField(object settings, System.Type settingsType, string fieldName, string displayName, float min, float max)
    {
        try
        {
            var field = settingsType.GetField(fieldName);
            if (field != null && field.FieldType == typeof(float))
            {
                float value = (float)field.GetValue(settings);
                float newValue = EditorLikeSlider(displayName, value, min, max);
                if (!Mathf.Approximately(value, newValue))
                {
                    field.SetValue(settings, newValue);
                }
            }
        }
        catch { }
    }

    void DrawIntField(object settings, System.Type settingsType, string fieldName, string displayName, int min, int max)
    {
        try
        {
            var field = settingsType.GetField(fieldName);
            if (field != null && field.FieldType == typeof(int))
            {
                int value = (int)field.GetValue(settings);
                int newValue = EditorLikeIntSlider(displayName, value, min, max);
                if (value != newValue)
                {
                    field.SetValue(settings, newValue);
                }
            }
        }
        catch { }
    }

    void DrawBoolField(object settings, System.Type settingsType, string fieldName, string displayName)
    {
        try
        {
            var field = settingsType.GetField(fieldName);
            if (field != null && field.FieldType == typeof(bool))
            {
                bool value = (bool)field.GetValue(settings);
                bool newValue = EditorLikeToggle(displayName, value);
                if (value != newValue)
                {
                    field.SetValue(settings, newValue);
                }
            }
        }
        catch { }
    }

    void DrawClassifierBoolField(RuntimePointClassifier classifier, string fieldName, string displayName)
    {
        try
        {
            var type = classifier.GetType();
            var field = type.GetField(fieldName);
            var property = type.GetProperty(fieldName);

            if (field != null && field.FieldType == typeof(bool))
            {
                bool value = (bool)field.GetValue(classifier);
                bool newValue = EditorLikeToggle(displayName, value);
                if (value != newValue)
                {
                    field.SetValue(classifier, newValue);
                }
            }
            else if (property != null && property.PropertyType == typeof(bool))
            {
                bool value = (bool)property.GetValue(classifier);
                bool newValue = EditorLikeToggle(displayName, value);
                if (value != newValue && property.CanWrite)
                {
                    property.SetValue(classifier, newValue);
                }
            }
        }
        catch { }
    }

    void DrawClassifierColorField(RuntimePointClassifier classifier, string fieldName, string displayName)
    {
        try
        {
            var type = classifier.GetType();
            var field = type.GetField(fieldName);
            var property = type.GetProperty(fieldName);

            Color color = Color.white;

            if (field != null && field.FieldType == typeof(Color))
            {
                color = (Color)field.GetValue(classifier);
            }
            else if (property != null && property.PropertyType == typeof(Color))
            {
                color = (Color)property.GetValue(classifier);
            }

            EditorLikeColorField(displayName, color);
        }
        catch { }
    }

    void DrawSectionHeader(string title)
    {
        GUILayout.Space(4);
        GUILayout.Label(title, EditorStyles.boldLabel);
        GUILayout.Space(2);
    }

    // ----- 경량 "Editor-like" 위젯들 (런타임에서도 동작) -----

    Material EditorLikeObjectField(string label, Material current)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        string name = current != null ? current.name : "(None)";
        GUILayout.Label(name, GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
        return current;
    }

    bool EditorLikeToggle(string label, bool value)
    {
        GUILayout.BeginHorizontal();
        bool v = GUILayout.Toggle(value, label);
        GUILayout.EndHorizontal();
        return v;
    }

    float EditorLikeSlider(string label, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        GUILayout.Label(value.ToString("0.00"), GUILayout.Width(50));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        float v = GUILayout.HorizontalSlider(value, min, max);
        return v;
    }

    int EditorLikeIntSlider(string label, int value, int min, int max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        GUILayout.Label(value.ToString(), GUILayout.Width(80));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        int v = Mathf.RoundToInt(GUILayout.HorizontalSlider(value, min, max));
        return v;
    }

    void EditorLikeColorField(string label, Color color)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));

        // 색상 표시용 작은 박스
        var colorRect = GUILayoutUtility.GetRect(20, 16);
        GUI.DrawTexture(colorRect, EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, true, 0, color, 0, 0);

        GUILayout.Label($"({color.r:0.00}, {color.g:0.00}, {color.b:0.00})");
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    System.Enum EditorLikeEnumPopup(string label, KeyCode value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        GUILayout.Label(value.ToString(), GUILayout.Width(160));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        return value;
    }
}

// EditorStyles와 EditorGUIUtility를 런타임에서 사용하기 위한 폴백
public static class EditorStyles
{
    public static GUIStyle boldLabel = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
    public static GUIStyle miniBoldLabel = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 10 };
}

public static class EditorGUIUtility
{
    private static Texture2D _whiteTexture;
    public static Texture2D whiteTexture
    {
        get
        {
            if (_whiteTexture == null)
            {
                _whiteTexture = new Texture2D(1, 1);
                _whiteTexture.SetPixel(0, 0, Color.white);
                _whiteTexture.Apply();
            }
            return _whiteTexture;
        }
    }
}
