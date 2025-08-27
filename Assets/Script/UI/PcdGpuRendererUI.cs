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

    // ���� ����
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

        // ��Ŀ�� ���� �� ���콺 �� ���� �� �ʿ� ��
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

        // ȭ�� �ػ󵵿� ����Ͽ� �ּ� ũ�� ����
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
            if (GUI.Button(new Rect(windowRect.width - 24, 4, 20, 20), "��"))
            {
                showUI = false;
            }
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
            return;
        }

        // ��� Close ��ư
        if (GUI.Button(new Rect(windowRect.width - 24, 4, 20, 20), "��"))
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
            // �ʿ� �� ���� ť ����
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
            // �ܼ��� ���� order ���� ������(�ٸ� �ý��ۿ��� ������)
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

    // ----- �淮 "Editor-like" ������ (��Ÿ�ӿ����� ����) -----

    Material EditorLikeObjectField(string label, Material current)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        string name = current != null ? current.name : "(None)";
        GUILayout.Label(name, GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
        // ��Ÿ�ӿ� �巡��&��� ObjectField�� �����Ϸ��� Ŀ���� ������ �ʿ�.
        // ���⼭�� ǥ�ø� �ϰ�, ������ �ٸ� ����(�ڵ�/�ν�����)�� ���.
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
        return value; // ��Ÿ�ӿ����� ���� ���� ������ �ܼ�ȭ(�ʿ� �� ��Ӵٿ� ���� ����)
    }

    System.Enum EditorLikeEnumPopup(string label, PcdGpuRenderer.RenderMode value)
    {
        // ���� ���(��尡 �þ ��� ��Ӵٿ� ���� ����)
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        if (GUILayout.Button(value.ToString(), GUILayout.Width(160)))
        {
            // ���� ����̹Ƿ� ��� ����. ��尡 ���� ���� ��ȯ ���� ���� �߰�
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
