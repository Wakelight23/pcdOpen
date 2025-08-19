using System;
using System.IO;

public sealed class PcdHeader
{
    public string Data;
    public int Width, Height;
    public string[] Fields;
    public int[] Size;
    public char[] Type;
    public int[] Count;
    public int PointStep;
    public int OffsetX = -1, OffsetY = -1, OffsetZ = -1;
}

public static class PcdUtil
{
    public static PcdHeader ParseHeader(StreamReader sr)
    {
        var h = new PcdHeader();
        string line;
        int offset = 0;
        while ((line = sr.ReadLine()) != null)
        {
            if (line.StartsWith("#")) continue;
            if (line.StartsWith("FIELDS"))
                h.Fields = line.Substring(6).Trim().Split();
            else if (line.StartsWith("SIZE"))
                h.Size = Array.ConvertAll(line.Substring(4).Trim().Split(), int.Parse);
            else if (line.StartsWith("TYPE"))
                h.Type = Array.ConvertAll(line.Substring(4).Trim().Split(), s => s[0]);
            else if (line.StartsWith("COUNT"))
                h.Count = Array.ConvertAll(line.Substring(5).Trim().Split(), int.Parse);
            else if (line.StartsWith("WIDTH"))
                h.Width = int.Parse(line.Substring(5).Trim());
            else if (line.StartsWith("HEIGHT"))
                h.Height = int.Parse(line.Substring(6).Trim());
            else if (line.StartsWith("DATA"))
            {
                h.Data = line.Substring(4).Trim();
                break;
            }
        }
        h.PointStep = 0;
        for (int i = 0; i < h.Fields.Length; i++)
            h.PointStep += h.Size[i] * ((h.Count != null && h.Count.Length > i) ? h.Count[i] : 1);

        int running = 0;
        for (int i = 0; i < h.Fields.Length; i++)
        {
            var f = h.Fields[i].ToLowerInvariant();
            if (f == "x") h.OffsetX = running;
            else if (f == "y") h.OffsetY = running;
            else if (f == "z") h.OffsetZ = running;
            running += h.Size[i] * ((h.Count != null && h.Count.Length > i) ? h.Count[i] : 1);
        }
        return h;
    }
}
