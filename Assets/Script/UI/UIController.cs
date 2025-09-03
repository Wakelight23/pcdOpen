using UnityEngine;

public class UIController : MonoBehaviour
{
    /*[Header("Panel Toggle")] // NEW
    [SerializeField] private Button togglePanelBtn;       // NEW: 패널 토글 버튼
    [SerializeField] private GameObject targetPanel;      // NEW: 토글할 패널 GameObject
    [SerializeField] private TMP_Text togglePanelLabel;   // NEW: 버튼 라벨(선택)

    void Awake()
    {
        if (togglePanelBtn) togglePanelBtn.onClick.AddListener(TogglePanel);
        RefreshTogglePanelLabel();
    }

    void TogglePanel() // NEW
    {
        bool next = !targetPanel.activeSelf;
        targetPanel.SetActive(next);
        RefreshTogglePanelLabel();
    }

    void RefreshTogglePanelLabel() // NEW
    {
        if (!togglePanelLabel || !targetPanel) return;
        togglePanelLabel.text = targetPanel.activeSelf ? "패널 끄기" : "패널 켜기";
    }*/
}
