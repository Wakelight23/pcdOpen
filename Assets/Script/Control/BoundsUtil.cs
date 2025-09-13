using UnityEngine;

public static class BoundsUtil
{
    // ��Ʈ ���� ��� Renderer�� ���� Bounds�� ���ļ� ��ȯ
    public static bool TryComputeWorldBounds(GameObject root, out Bounds worldBounds)
    {
        worldBounds = new Bounds();
        var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
        bool found = false;
        foreach (var r in renderers)
        {
            // ParticleSystemRenderer � ���Ե� �� ����. �ʿ�� ���͸�
            if (!found)
            {
                worldBounds = r.bounds; // r.bounds�� ���� ����
                found = true;
            }
            else
            {
                worldBounds.Encapsulate(r.bounds);
            }
        }
        return found;
    }

    // ���� Bounds�� Ư�� Transform�� ���� Bounds(center/size)�� ��ȯ
    public static void WorldBoundsToLocal(Transform target, Bounds worldBounds, out Vector3 localCenter, out Vector3 localSize)
    {
        // Bounds�� 8�� �ڳʸ� ���÷� ���� �� �ٽ� ���� Bounds ����
        Vector3 c = worldBounds.center;
        Vector3 e = worldBounds.extents;

        Vector3[] corners =
        {
            new(c.x - e.x, c.y - e.y, c.z - e.z),
            new(c.x - e.x, c.y - e.y, c.z + e.z),
            new(c.x - e.x, c.y + e.y, c.z - e.z),
            new(c.x - e.x, c.y + e.y, c.z + e.z),
            new(c.x + e.x, c.y - e.y, c.z - e.z),
            new(c.x + e.x, c.y - e.y, c.z + e.z),
            new(c.x + e.x, c.y + e.y, c.z - e.z),
            new(c.x + e.x, c.y + e.y, c.z + e.z),
        };

        var t = target;
        var lb = new Bounds(t.InverseTransformPoint(corners[0]), Vector3.zero);
        for (int i = 1; i < corners.Length; i++)
            lb.Encapsulate(t.InverseTransformPoint(corners[i]));

        localCenter = lb.center;
        localSize = lb.size;
    }
}
