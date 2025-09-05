using UnityEngine;

public static class CameraFocusUtil
{
    // ī�޶� boundsCenter/BoundsSize�� ȭ�鿡 padding ������ ���� �ְ� �㵵�� ��ġ/Ŭ������ ����
    // fovDeg: ī�޶� ���� FOV (�밳 cam.fieldOfView)
    // padding: ���� ����(1.2 = 20% ����)
    // viewDir: ī�޶� �ٶ� ����(���� �� ���� cam.forward ����)
    public static void FocusCameraOnBounds(Camera cam, Vector3 boundsCenter, Vector3 boundsSize, float fovDeg, float padding = 1.2f, Vector3? viewDir = null)
    {
        if (cam == null) return;

        // 1) ������ �ٻ�(�������� ����)
        float radius = boundsSize.magnitude * 0.5f;
        if (radius < 1e-5f) radius = 0.1f;

        // 2) �þ߰� ���
        float fovY = Mathf.Deg2Rad * Mathf.Max(1e-3f, fovDeg);
        float aspect = Mathf.Max(1e-3f, cam.aspect);
        float fovX = 2f * Mathf.Atan(Mathf.Tan(fovY * 0.5f) * aspect);

        // 3) �е� �ݿ�
        float rPadded = radius * Mathf.Max(1.0f, padding);

        // 4) ����/���� �� �� �� �������� �� �������� �Ÿ� ����(= �� �ڷ�)
        float distByY = rPadded / Mathf.Tan(fovY * 0.5f);
        float distByX = rPadded / Mathf.Tan(fovX * 0.5f);
        float distance = Mathf.Max(distByY, distByX);

        // 5) ī�޶� ���� ����
        Vector3 dir = (viewDir.HasValue ? viewDir.Value : cam.transform.forward).normalized;
        if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;

        // 6) ī�޶� ��ġ = �߽� - dir * distance
        Vector3 camPos = boundsCenter - dir * distance;

        // 7) ����
        cam.transform.position = camPos;
        cam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

        // 8) Ŭ���� ��� ����
        float nearTarget = Mathf.Max(0.01f, distance - rPadded * 1.2f);
        float farTarget = distance + rPadded * 1.5f;
        cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, nearTarget);
        cam.farClipPlane = Mathf.Max(cam.farClipPlane, farTarget);
    }

    // ���� ī�޶��(�ʿ� ��)
    public static void FocusOrthoCameraOnBounds(Camera cam, Vector3 boundsCenter, Vector3 boundsSize, float padding = 1.2f)
    {
        if (cam == null || !cam.orthographic) return;

        float halfW = boundsSize.x * 0.5f;
        float halfH = boundsSize.y * 0.5f;

        float sizeByHeight = halfH * padding;
        float sizeByWidth = (halfW / Mathf.Max(0.0001f, cam.aspect)) * padding;

        cam.orthographicSize = Mathf.Max(sizeByHeight, sizeByWidth);
        cam.transform.LookAt(boundsCenter, Vector3.up);
    }
}
