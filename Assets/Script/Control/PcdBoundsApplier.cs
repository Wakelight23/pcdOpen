using UnityEngine;

[RequireComponent(typeof(BoxCollider))]
public class PcdBoundsApplier : MonoBehaviour
{
    public Camera targetCamera; // ���� Camera.main
    public float framePadding = 1.2f;
    public bool autoFrameAfterLoad = true;

    // PCD �ε�/������ ���� ���� ȣ��
    public void RefreshColliderAndFraming()
    {
        if (!BoundsUtil.TryComputeWorldBounds(gameObject, out var worldB)) return;

        // BoxCollider�� ���� Bounds�� ����
        BoundsUtil.WorldBoundsToLocal(transform, worldB, out var localCenter, out var localSize);

        var box = GetComponent<BoxCollider>();
        box.center = localCenter;
        box.size = localSize;

        // PcdViewerOrbitControllerRaycast�� bounds�� ����ȭ(�ִٸ�)
        var orbit = GetComponent<PcdViewerOrbitControllerRaycast>();
        if (orbit != null)
        {
            orbit.boundsCenter = worldB.center;
            orbit.boundsSize = worldB.size;
        }

        // ���ϴ� ��� ��� ī�޶� �����̹�
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
