using UnityEngine;

[DefaultExecutionOrder(10000)]
public class PcdGpuRendererUI : MonoBehaviour
{
    [Header("Target")]
    public PcdGpuRenderer targetRenderer;

    [Header("Window")]
    public bool showUI = false;
    public KeyCode toggleKey = KeyCode.F1;
    public string windowTitle = "PcdGpuRenderer Settings";
    public int windowId = 732841;
    public Rect windowRect = new Rect(20, 20, 360, 360);
    public bool lockCursorWhenHidden = false;

    // 내부 상태
    Vector2 _scroll;

    void Awake()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponent<PcdGpuRenderer>();
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            ToggleUI();

        // 포커스 잃은 뒤 마우스 락 해제 등 필요 시
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
    }

    void OnGUI()
    {
        if (!showUI) return;

        // 화면 해상도에 비례하여 최소 크기 보정
        var minW = Mathf.Min(Screen.width - 40, Mathf.Max(320, (int)windowRect.width));
        var minH = Mathf.Min(Screen.height - 40, Mathf.Max(260, (int)windowRect.height));
        windowRect.width = minW;
        windowRect.height = minH;

        GUI.color = Color.white;
        windowRect = GUI.Window(windowId, windowRect, DrawWindow, windowTitle);
    }

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

        // Render mode
        var rm = (PcdGpuRenderer.RenderMode)EditorLikeEnumPopup("Render Mode", targetRenderer.renderMode);
        if (rm != targetRenderer.renderMode)
        {
            targetRenderer.renderMode = rm;
        }

        // Material
        var mat = EditorLikeObjectField("Point Material", targetRenderer.pointMaterial);
        if (mat != targetRenderer.pointMaterial)
        {
            targetRenderer.pointMaterial = mat;
        }

        // Toggles & sliders
        bool useCol = EditorLikeToggle("Use Colors", targetRenderer.useColors);
        if (useCol != targetRenderer.useColors) targetRenderer.useColors = useCol;

        float sizePx = EditorLikeSlider("Point Size (px)", targetRenderer.pointSize, 0.5f, 64f);
        if (!Mathf.Approximately(sizePx, targetRenderer.pointSize)) targetRenderer.pointSize = sizePx;

        float soft = EditorLikeSlider("Soft Edge", targetRenderer.softEdge, 0f, 1f);
        if (!Mathf.Approximately(soft, targetRenderer.softEdge)) targetRenderer.softEdge = soft;

        bool round = EditorLikeToggle("Round Mask", targetRenderer.roundMask);
        if (round != targetRenderer.roundMask) targetRenderer.roundMask = round;

        bool alpha = EditorLikeToggle("Alpha Blend (on/off)",
    targetRenderer.pointMaterial != null && targetRenderer.pointMaterial.GetFloat("_AlphaBlend") > 0.5f);
        if (alpha != (targetRenderer.pointMaterial.GetFloat("_AlphaBlend") > 0.5f))
        {
            targetRenderer.pointMaterial.SetFloat("_AlphaBlend", alpha ? 1f : 0f);
            // 필요 시 렌더 큐 조정
            targetRenderer.pointMaterial.renderQueue = alpha
                ? (int)UnityEngine.Rendering.RenderQueue.Transparent
                : (int)UnityEngine.Rendering.RenderQueue.Geometry;
        }


        GUILayout.Space(8);
        GUILayout.Label("Stats");
        GUILayout.Label($"Active Nodes: {targetRenderer.activeNodeCount}");
        GUILayout.Label($"Total Points: {targetRenderer.totalPointCount}");

        GUILayout.Space(8);
        GUILayout.Label("Actions");
        if (GUILayout.Button("Rebuild Draw Order (keep)"))
        {
            // 단순히 현재 order 유지 재적용(다른 시스템에서 제공됨)
            var ids = targetRenderer.GetActiveNodeIds();
            targetRenderer.SetDrawOrder((System.Collections.Generic.IList<int>)ids);
        }

        GUILayout.Space(8);
        GUILayout.Label("Window");
        toggleKey = (KeyCode)EditorLikeEnumPopup("Toggle Key", toggleKey);
        lockCursorWhenHidden = EditorLikeToggle("Unlock Cursor When Hidden", lockCursorWhenHidden);

        GUILayout.Space(10);
        GUILayout.EndScrollView();

        GUI.DragWindow(new Rect(0, 0, 10000, 22));
    }

    // ----- 경량 "Editor-like" 위젯들 (런타임에서도 동작) -----

    Material EditorLikeObjectField(string label, Material current)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        string name = current != null ? current.name : "(None)";
        GUILayout.Label(name, GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
        // 런타임에 드래그&드롭 ObjectField를 제공하려면 커스텀 구현이 필요.
        // 여기서는 표시만 하고, 변경은 다른 수단(코드/인스펙터)을 사용.
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

    System.Enum EditorLikeEnumPopup(string label, System.Enum value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        GUILayout.Label(value.ToString(), GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
        return value; // 런타임에서는 선택 변경 위젯을 단순화(필요 시 드롭다운 직접 구현)
    }

    System.Enum EditorLikeEnumPopup(string label, PcdGpuRenderer.RenderMode value)
    {
        // 간단 토글(모드가 늘어날 경우 드롭다운 구현 권장)
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        if (GUILayout.Button(value.ToString(), GUILayout.Width(160)))
        {
            // 단일 모드이므로 토글 없음. 모드가 여러 개면 순환 변경 로직 추가
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        return value;
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
