#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

public class PcdEntry : MonoBehaviour
{
    public PcdGpuRenderer rendererTarget; // 동일 GameObject나 다른 오브젝트에 붙인 컴포넌트
    public Material pointMaterial;        // PcdPoint.shader 기반 머티리얼

#if UNITY_EDITOR
    [ContextMenu("Load PCD (Editor)")]
    public void LoadPcdEditor()
    {
        string path = EditorUtility.OpenFilePanel("Select PCD", "", "pcd");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var data = PcdLoader.LoadFromFile(path);
            if (data == null || data.positions == null || data.pointCount <= 0)
            {
                Debug.LogError("Invalid or empty PCD.");
                return;
            }

            if (rendererTarget == null)
                rendererTarget = GetComponent<PcdGpuRenderer>();

            if (rendererTarget == null)
                rendererTarget = gameObject.AddComponent<PcdGpuRenderer>();

            if (rendererTarget.pointMaterial == null && pointMaterial != null)
                rendererTarget.pointMaterial = pointMaterial;

            rendererTarget.useColors = data.colors != null && data.colors.Length == data.pointCount;
            rendererTarget.UploadData(data.positions, data.colors);

            Debug.Log($"PCD loaded: {data.pointCount} points (GPU)");
        }
        catch (System.Exception e)
        {
            Debug.LogError(e);
        }
    }
#endif
}
