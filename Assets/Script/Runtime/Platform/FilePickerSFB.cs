#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX
using SFB;
using System;

public static class FilePicker
{
    public static FilePickResult OpenFile(string title, string initialDir, string extNoDot, bool multiselect = false)
    {
        try
        {
            var filters = new[] { new ExtensionFilter($"{extNoDot.ToUpper()} files", extNoDot) };
            var paths = StandaloneFileBrowser.OpenFilePanel(title, initialDir ?? "", filters, multiselect);
            return new FilePickResult { OK = paths != null && paths.Length > 0, Paths = paths ?? Array.Empty<string>() };
        }
        catch (Exception e)
        {
            return new FilePickResult { OK = false, Error = e.Message, Paths = Array.Empty<string>() };
        }
    }
}
#endif
