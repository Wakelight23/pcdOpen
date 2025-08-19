using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(ParticleSystem))]
public class PcdViewer : MonoBehaviour
{
    public float pointSize = 0.02f;
    public bool useColors = true;

    public ParticleSystem ps;
    ParticleSystem.Particle[] particles;

    void Awake()
    {
        if (ps == null) ps = GetComponent<ParticleSystem>();
        if (ps == null) ps = GetComponentInChildren<ParticleSystem>(true);
        if (ps == null) ps = GetComponentInParent<ParticleSystem>();
        if (ps == null) ps = gameObject.AddComponent<ParticleSystem>();

        var main = ps.main;
        main.startSize = pointSize;
        main.maxParticles = Mathf.Max(main.maxParticles, 5_000_000);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.playOnAwake = true;
        ps.Play();
    }

#if UNITY_EDITOR
    [ContextMenu("Load PCD (Editor)")]
    public void LoadPcdEditor()
    {
        string path = EditorUtility.OpenFilePanel("Select PCD file", "", "pcd");
        if (string.IsNullOrEmpty(path)) return;
        LoadAndShow(path);
    }
#endif

    public void LoadAndShow(string path)
    {
        try
        {
            var data = PcdLoader.LoadFromFile(path);
            if (data == null || data.positions == null || data.pointCount <= 0)
            {
                Debug.LogError("Invalid PCD.");
                return;
            }
            Show(data);

            var cam = Camera.main;
            if (cam != null)
                CameraFocusUtil.FocusCameraOnBounds(cam, data.Center, data.Size, cam.fieldOfView, 1.2f);
        }
        catch (System.Exception e)
        {
            Debug.LogError(e);
        }
    }

    void Show(PcdData data)
    {
        if (ps == null) { Debug.LogError("ParticleSystem missing"); return; }
        if (data == null || data.positions == null || data.pointCount <= 0)
        {
            Debug.LogError("Invalid PcdData");
            return;
        }

        int n = data.pointCount;
        if (particles == null || particles.Length != n)
            particles = new ParticleSystem.Particle[n];

        bool hasColor = useColors && data.colors != null && data.colors.Length == n;

        for (int i = 0; i < n; i++)
        {
            var p = new ParticleSystem.Particle
            {
                position = data.positions[i],
                startSize = pointSize,
                remainingLifetime = float.MaxValue,
                startColor = hasColor ? data.colors[i] : new Color32(255, 255, 255, 255)
            };
            particles[i] = p;
        }

        var main = ps.main;
        main.maxParticles = Mathf.Max(main.maxParticles, n);

        ps.SetParticles(particles, n);
    }
}
