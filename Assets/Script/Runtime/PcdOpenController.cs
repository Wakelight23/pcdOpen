using System;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PcdOpenController : MonoBehaviour
{
    [SerializeField] private Button openBtn;
    [SerializeField] private Button reloadBtn;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private GameObject progressSpinner;
    [SerializeField] private PcdEntry pcdEntry;

    private string lastPath;

    void Awake()
    {
        if (openBtn) openBtn.onClick.AddListener(() => _ = OpenAndLoadAsync());
        if (reloadBtn) reloadBtn.onClick.AddListener(() => _ = ReloadAsync());
        SetProgress(false);
        SetStatus("Ready.");
    }

    async Task OpenAndLoadAsync()
    {
        try
        {
            SetStatus("Select PCD...");
            SetProgress(true);

#if UNITY_STANDALONE_OSX || UNITY_STANDALONE_WIN
            var res = FilePicker.OpenFile("Select PCD", null, "pcd", false);
#else
    var res = new FilePickResult { OK = false, Error = "Unsupported platform" };
#endif
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
        }
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
        await pcdEntry.LoadPcdRuntimeAsync(path);
        SetStatus($"Loaded: {System.IO.Path.GetFileName(path)}");
    }

    void SetProgress(bool on) { if (progressSpinner) progressSpinner.SetActive(on); }
    void SetStatus(string msg) { if (statusText) statusText.text = msg; }
}
