using System;
using System.IO;
using UnityEngine;

[Serializable]
public class PcdMetadata
{
    public string version = "PcdCache-0.1";
    public string name;
    public int points;
    public float[] boundingBox; // [minX,minY,minZ,maxX,maxY,maxZ]
    public float spacing; // root spacing
    public string[] attributes; // e.g. ["POSITION","RGB"]
    public float[] scale; // optional [sx,sy,sz]
    public float[] offset; // optional [ox,oy,oz]
}

public static class PcdCache
{
    public static string CacheRoot =>
    Path.Combine(Application.persistentDataPath, "PcdCache");
    public static string DatasetDirFor(string pcdPath)
    {
        var name = Path.GetFileNameWithoutExtension(pcdPath);
        return Path.Combine(CacheRoot, name);
    }
    public static string MetadataPath(string datasetDir) =>
        Path.Combine(datasetDir, "metadata.json");
    public static string HierarchyPath(string datasetDir) =>
        Path.Combine(datasetDir, "hierarchy.bin");
    public static string OctreePath(string datasetDir) =>
        Path.Combine(datasetDir, "octree.bin");

    public static bool Exists(string datasetDir)
    {
        return Directory.Exists(datasetDir)
            && File.Exists(MetadataPath(datasetDir))
            && File.Exists(HierarchyPath(datasetDir))
            && File.Exists(OctreePath(datasetDir));
    }
}