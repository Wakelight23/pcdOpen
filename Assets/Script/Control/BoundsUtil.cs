using UnityEngine;

public static class BoundsUtil
{
    // 루트 이하 모든 Renderer의 월드 Bounds를 합쳐서 반환
    public static bool TryComputeWorldBounds(GameObject root, out Bounds worldBounds)
    {
        worldBounds = new Bounds();
        var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
        bool found = false;
        foreach (var r in renderers)
        {
            // ParticleSystemRenderer 등도 포함될 수 있음. 필요시 필터링
            if (!found)
            {
                worldBounds = r.bounds; // r.bounds는 월드 공간
                found = true;
            }
            else
            {
                worldBounds.Encapsulate(r.bounds);
            }
        }
        return found;
    }

    // 월드 Bounds를 특정 Transform의 로컬 Bounds(center/size)로 변환
    public static void WorldBoundsToLocal(Transform target, Bounds worldBounds, out Vector3 localCenter, out Vector3 localSize)
    {
        // Bounds의 8개 코너를 로컬로 투영 후 다시 로컬 Bounds 구성
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
