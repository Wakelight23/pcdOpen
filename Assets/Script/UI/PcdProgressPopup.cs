using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PcdProgressPopup : MonoBehaviour
{
    [SerializeField] GameObject panel;
    [SerializeField] Slider slider;
    [SerializeField] TMP_Text label;
    void OnEnable()
    {
        PcdEntry.OnProgress += OnProgress;
    }
    void OnDisable()
    {
        PcdEntry.OnProgress -= OnProgress;
    }

    void OnProgress(float t, string txt)
    {
        if (panel != null && !panel.activeSelf) panel.SetActive(true);
        if (slider != null) slider.value = Mathf.Clamp01(t);
        if (label != null) label.text = txt ?? "";
        if (t >= 0.999f)
        {
            // »ìÂ¦ Áö¿¬ ÈÄ ´Ý±â
            CancelInvoke(nameof(HideSoon));
            Invoke(nameof(HideSoon), 0.25f);
        }
    }

    void HideSoon()
    {
        if (panel != null) panel.SetActive(false);
    }
}