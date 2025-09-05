using UnityEngine;

public static class ColorSpaceUtils
{
    public static Vector3 RgbToHsl(Color32 rgb)
    {
        float r = rgb.r / 255f;
        float g = rgb.g / 255f;
        float b = rgb.b / 255f;

        float max = Mathf.Max(r, Mathf.Max(g, b));
        float min = Mathf.Min(r, Mathf.Min(g, b));
        float delta = max - min;

        float h = 0f;
        float s = 0f;
        float l = (max + min) / 2f;

        if (delta != 0f)
        {
            s = l > 0.5f ? delta / (2f - max - min) : delta / (max + min);

            if (max == r)
                h = ((g - b) / delta) % 6f;
            else if (max == g)
                h = (b - r) / delta + 2f;
            else
                h = (r - g) / delta + 4f;

            h /= 6f;
            if (h < 0f) h += 1f;
        }

        return new Vector3(h, s, l);
    }

    public static Color32 HslToRgb(Vector3 hsl)
    {
        float h = hsl.x;
        float s = hsl.y;
        float l = hsl.z;

        float c = (1f - Mathf.Abs(2f * l - 1f)) * s;
        float x = c * (1f - Mathf.Abs((h * 6f) % 2f - 1f));
        float m = l - c / 2f;

        float r = 0f, g = 0f, b = 0f;

        if (h < 1f / 6f) { r = c; g = x; b = 0f; }
        else if (h < 2f / 6f) { r = x; g = c; b = 0f; }
        else if (h < 3f / 6f) { r = 0f; g = c; b = x; }
        else if (h < 4f / 6f) { r = 0f; g = x; b = c; }
        else if (h < 5f / 6f) { r = x; g = 0f; b = c; }
        else { r = c; g = 0f; b = x; }

        return new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt((r + m) * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt((g + m) * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt((b + m) * 255f), 0, 255),
            255
        );
    }
}
