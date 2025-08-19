public sealed class SoALayout
{
    public int N;
    public int FieldCount;
    public int[] ElemBytes;     // per field: Size[i]*Count[i]
    public int[] SoAStart;      // per field: byte offset into uncompressed stream
    public int[] SoALength;     // per field: N * ElemBytes[i]
    public int XField = -1, YField = -1, ZField = -1; // indices in FIELDS
}

public static class PcdSoA
{
    public static SoALayout BuildSoALayout(PcdHeader h)
    {
        var lo = new SoALayout();
        lo.N = h.Width * h.Height;
        lo.FieldCount = h.Fields.Length;
        lo.ElemBytes = new int[lo.FieldCount];
        lo.SoAStart = new int[lo.FieldCount];
        lo.SoALength = new int[lo.FieldCount];

        int cursor = 0;
        for (int i = 0; i < lo.FieldCount; ++i)
        {
            int cnt = (h.Count != null && h.Count.Length > i) ? h.Count[i] : 1;
            int elemBytes = h.Size[i] * cnt;
            lo.ElemBytes[i] = elemBytes;
            lo.SoAStart[i] = cursor;
            lo.SoALength[i] = elemBytes * lo.N;
            cursor += lo.SoALength[i];

            string f = h.Fields[i].ToLowerInvariant();
            if (f == "x") lo.XField = i;
            if (f == "y") lo.YField = i;
            if (f == "z") lo.ZField = i;
        }
        return lo;
    }
}
