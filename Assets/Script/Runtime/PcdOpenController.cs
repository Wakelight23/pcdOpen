using System;
using System.Collections;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PcdOpenController : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Button openBtn;
    [SerializeField] private Button reloadBtn;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private GameObject progressSpinner;

    [Header("ProgressBar")]
    [SerializeField] private GameObject progressPanel;   // 전체 패널(표시/숨김)
    [SerializeField] private UnityEngine.UI.Slider progressBar;
    [SerializeField] private TMP_Text progressLabel;

    [Header("Targets")]
    [SerializeField] private PcdEntry pcdEntry;

    private string lastPath;

    void Awake()
    {
        if (openBtn) openBtn.onClick.AddListener(() => _ = OpenAndLoadAsync());
        if (reloadBtn) reloadBtn.onClick.AddListener(() => _ = ReloadAsync());
        SetProgress(false);
        SetProgressUI(false);
        SetStatus("Ready.");
    }
    void OnEnable()
    {
        PcdEntry.OnProgress += HandleProgress;
    }
    void OnDisable()
    {
        PcdEntry.OnProgress -= HandleProgress;
    }

    void HandleProgress(float t, string label)
    {
        SetProgressUI(true);
        SetProgressValue(t, label);
        if (t >= 1f) SetProgressUI(false);
    }


    async Task OpenAndLoadAsync()
    {
        try
        {
            SetStatus("Select PCD...");
            SetProgress(true);

            var res = await PickPcdPathAsync();
            if (!res.OK || res.Paths == null || res.Paths.Length == 0)
            {
                SetStatus(string.IsNullOrEmpty(res.Error) ? "Canceled." : $"Error: {res.Error}");
                return;
            }

            lastPath = res.Paths[0];
            await LoadPathAsync(lastPath);
        }
        catch (Exception e)
        {
            Debug.LogError(e);
            SetStatus($"Error: {e.Message}");
        }
        finally
        {
            SetProgress(false);
            StartCoroutine(DelayTime());
            SetProgressUI(false);
        }
    }

    async Task ReloadAsync()
    {
        if (string.IsNullOrEmpty(lastPath))
        {
            SetStatus("No previous file.");
            return;
        }

        try
        {
            SetProgress(true);
            await LoadPathAsync(lastPath);
        }
        catch (Exception e)
        {
            Debug.LogError(e);
            SetStatus($"Error: {e.Message}");
        }
        finally
        {
            SetProgress(false);
            StartCoroutine(DelayTime());
            SetProgressUI(false);
        }
    }

    IEnumerator DelayTime()
    {
        yield return new WaitForSeconds(100.0f);
    }

    async Task LoadPathAsync(string path)
    {
        if (pcdEntry == null) pcdEntry = FindAnyObjectByType<PcdEntry>();
        if (pcdEntry == null)
        {
            SetStatus("PcdEntry not found.");
            return;
        }

        SetStatus("Loading...");
        await pcdEntry.InitializeWithPathAsync(path);
        SetStatus($"Loaded: {System.IO.Path.GetFileName(path)}");
    }

    void SetProgress(bool on)
    {
        if (progressSpinner) progressSpinner.SetActive(on);
    }

    void SetStatus(string msg)
    {
        if (statusText) statusText.text = msg;
    }

    // -------- 파일 선택기 --------

    struct FilePickResult
    {
        public bool OK;
        public string[] Paths;
        public string Error;
    }

    async Task<FilePickResult> PickPcdPathAsync()
    {
        var result = new FilePickResult { OK = false, Paths = null, Error = null };

#if UNITY_EDITOR
        try
        {
            string path = UnityEditor.EditorUtility.OpenFilePanel("Select PCD", "", "pcd");
            if (!string.IsNullOrEmpty(path))
            {
                result.OK = true;
                result.Paths = new[] { path };
            }
        }
        catch (Exception e)
        {
            result.OK = false;
            result.Error = e.Message;
        }
#elif UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX
        try
        {
            // SFB(StandaloneFileBrowser) 패키지를 사용하는 경우
            var filters = new[] { new SFB.ExtensionFilter("PCD files", "pcd") };
            var paths = SFB.StandaloneFileBrowser.OpenFilePanel("Select PCD", "", filters, false);
            if (paths != null && paths.Length > 0)
            {
                result.OK = true;
                result.Paths = paths;
            }
        }
        catch (Exception e)
        {
            result.OK = false;
            result.Error = e.Message;
        }
#else
        result.OK = false;
        result.Error = "This platform does not support native file picker in this sample.";
#endif
        await Task.Yield();
        return result;
    }

    // 진행도 보조
    void SetProgressUI(bool on)
    {
        if (progressSpinner) progressSpinner.SetActive(on);
        if (progressPanel) progressPanel.SetActive(on);
        if (progressBar) progressBar.value = 0f;
    }

    void SetProgressValue(float t, string label = null)
    {
        if (progressBar) progressBar.value = Mathf.Clamp01(t);
        if (progressLabel)
        {
            if (!string.IsNullOrEmpty(label)) progressLabel.text = label;
        }
    }
}
