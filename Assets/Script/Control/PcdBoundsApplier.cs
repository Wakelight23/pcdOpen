using UnityEngine;

[RequireComponent(typeof(BoxCollider))]
public class PcdBoundsApplier : MonoBehaviour
{
    public Camera targetCamera; // 비우면 Camera.main
    public float framePadding = 1.2f;
    public bool autoFrameAfterLoad = true;

    // PCD 로드/스케일 변경 이후 호출
    public void RefreshColliderAndFraming()
    {
        if (!BoundsUtil.TryComputeWorldBounds(gameObject, out var worldB)) return;

        // BoxCollider를 로컬 Bounds로 맞춤
        BoundsUtil.WorldBoundsToLocal(transform, worldB, out var localCenter, out var localSize);

        var box = GetComponent<BoxCollider>();
        box.center = localCenter;
        box.size = localSize;

        // PcdViewerOrbitControllerRaycast의 bounds도 동기화(있다면)
        var orbit = GetComponent<PcdViewerOrbitControllerRaycast>();
        if (orbit != null)
        {
            orbit.boundsCenter = worldB.center;
            orbit.boundsSize = worldB.size;
        }

        // 원하는 경우 즉시 카메라 프레이밍
        if (autoFrameAfterLoad)
        {
            var cam = targetCamera != null ? targetCamera : Camera.main;
            if (cam != null)
            {
                CameraFocusUtil.FocusCameraOnBounds(
                    cam,
                    worldB.center,
                    worldB.size,
                    cam.fieldOfView,
                    framePadding,
                    cam.transform.forward
                );
            }
        }
    }
}
