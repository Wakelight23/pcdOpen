using UnityEngine;

public static class CameraFocusUtil
{
    // 카메라가 boundsCenter/BoundsSize를 화면에 padding 배율로 여유 있게 담도록 위치/클리핑을 조정
    // fovDeg: 카메라 수직 FOV (대개 cam.fieldOfView)
    // padding: 여유 배율(1.2 = 20% 여유)
    // viewDir: 카메라가 바라볼 방향(생략 시 기존 cam.forward 유지)
    public static void FocusCameraOnBounds(Camera cam, Vector3 boundsCenter, Vector3 boundsSize, float fovDeg, float padding = 1.2f, Vector3? viewDir = null)
    {
        if (cam == null) return;

        // 1) 반지름 근사(구형으로 가정)
        float radius = boundsSize.magnitude * 0.5f;
        if (radius < 1e-5f) radius = 0.1f;

        // 2) 시야각 계산
        float fovY = Mathf.Deg2Rad * Mathf.Max(1e-3f, fovDeg);
        float aspect = Mathf.Max(1e-3f, cam.aspect);
        float fovX = 2f * Mathf.Atan(Mathf.Tan(fovY * 0.5f) * aspect);

        // 3) 패딩 반영
        float rPadded = radius * Mathf.Max(1.0f, padding);

        // 4) 수직/수평 축 중 더 제한적인 쪽 기준으로 거리 산출(= 더 뒤로)
        float distByY = rPadded / Mathf.Tan(fovY * 0.5f);
        float distByX = rPadded / Mathf.Tan(fovX * 0.5f);
        float distance = Mathf.Max(distByY, distByX);

        // 5) 카메라 방향 결정
        Vector3 dir = (viewDir.HasValue ? viewDir.Value : cam.transform.forward).normalized;
        if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;

        // 6) 카메라 위치 = 중심 - dir * distance
        Vector3 camPos = boundsCenter - dir * distance;

        // 7) 적용
        cam.transform.position = camPos;
        cam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

        // 8) 클리핑 평면 보정
        float nearTarget = Mathf.Max(0.01f, distance - rPadded * 1.2f);
        float farTarget = distance + rPadded * 1.5f;
        cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, nearTarget);
        cam.farClipPlane = Mathf.Max(cam.farClipPlane, farTarget);
    }

    // 직교 카메라용(필요 시)
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
