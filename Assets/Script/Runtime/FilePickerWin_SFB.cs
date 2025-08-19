#if UNITY_STANDALONE_WIN
using SFB;
using System;

public static class FilePickerWin
{
    public class PickResult
    {
        public bool OK;
        public string[] Paths = Array.Empty<string>();
        public string Error;
    }

    public static PickResult OpenFile(string title, string initialDir, string extNoDot, bool multiselect = false)
    {
        try
        {
            var extensions = new[] { new ExtensionFilter($"{extNoDot.ToUpper()} files", extNoDot) };
            var paths = StandaloneFileBrowser.OpenFilePanel(title, initialDir ?? "", extensions, multiselect);
            return new PickResult { OK = paths != null && paths.Length > 0, Paths = paths ?? Array.Empty<string>() };
        }
        catch (Exception e)
        {
            return new PickResult { OK = false, Error = e.Message };
        }
    }
}
#endif
