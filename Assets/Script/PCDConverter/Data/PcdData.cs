using UnityEngine;

public class PcdData
{
    public Vector3[] positions;
    public Color32[] colors;
    public float[] intensity;
    public int pointCount;

    // Bounds
    public Vector3 boundsMin;
    public Vector3 boundsMax;
    public Vector3 Center => 0.5f * (boundsMin + boundsMax);
    public Vector3 Size => boundsMax - boundsMin;
    public float Radius => 0.5f * Size.magnitude;
    public Bounds GetBounds()
    {
        var center = (boundsMin + boundsMax) * 0.5f;
        var size = boundsMax - boundsMin;
        return new Bounds(center, size);
    }
}
