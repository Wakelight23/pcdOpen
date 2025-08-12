using UnityEngine;

[ExecuteAlways]
public class MouseOrbitPanZoom : MonoBehaviour
{
    [Header("Target")]
    public Transform target;
    public Vector3 fallbackTarget = Vector3.zero;

    [Header("Orbit (LMB)")]
    public float orbitSpeed = 180f; // deg/sec
    public float minPitch = -89f;
    public float maxPitch = 89f;

    [Header("Pan (RMB or MMB)")]
    public float panSpeed = 1.0f;

    [Header("Zoom (Wheel)")]
    public float distance = 5f;
    public float minDistance = 0.1f;
    public float maxDistance = 1000f;
    public float zoomSpeed = 2.0f;
    public bool zoomIsScaleByDistance = true;

    [Header("Smoothing")]
    public bool smooth = true;
    public float orbitDamp = 12f;
    public float panDamp = 12f;
    public float zoomDamp = 12f;

    [Header("Input")]
    public int orbitMouseButton = 0; // LMB
    public int panMouseButton = 1;   // MMB(1) 또는 RMB(2)
    public KeyCode resetKey = KeyCode.R;

    Vector2 orbitAngles = new Vector2(20f, 30f);
    Vector2 orbitAnglesTarget;
    float distanceTarget;
    Vector3 targetPoint;
    Vector3 targetPointVel;

    void Start()
    {
        if (target != null) targetPoint = target.position;
        else targetPoint = fallbackTarget;

        Vector3 dir = (transform.position - GetTargetPoint());
        if (dir.sqrMagnitude > 1e-6f)
        {
            distance = dir.magnitude;
            distanceTarget = Mathf.Clamp(distance, minDistance, maxDistance);
            Vector3 nd = dir.normalized;
            orbitAngles.x = Mathf.Asin(nd.y) * Mathf.Rad2Deg;
            orbitAngles.y = Mathf.Atan2(nd.x, nd.z) * Mathf.Rad2Deg;
        }
        else
        {
            distanceTarget = Mathf.Clamp(distance, minDistance, maxDistance);
        }
        orbitAnglesTarget = orbitAngles;
    }

    void Update()
    {
        HandleInput();
        UpdateCameraPose();
    }

    void HandleInput()
    {
        if (target != null) targetPoint = target.position;

        bool orbiting = Input.GetMouseButton(orbitMouseButton);
        bool panning = Input.GetMouseButton(panMouseButton);

        float mx = Input.GetAxis("Mouse X");
        float my = Input.GetAxis("Mouse Y");
        if (Mathf.Abs(mx) < 1e-4f) mx = 0f;
        if (Mathf.Abs(my) < 1e-4f) my = 0f;

        // Orbit
        if (orbiting)
        {
            orbitAnglesTarget.y += mx * orbitSpeed * Time.deltaTime; // yaw
            orbitAnglesTarget.x -= my * orbitSpeed * Time.deltaTime; // pitch
            orbitAnglesTarget.x = Mathf.Clamp(orbitAnglesTarget.x, minPitch, maxPitch);
        }

        // Pan
        if (panning)
        {
            var cam = transform;
            Vector3 right = cam.right;
            Vector3 up = cam.up;

            float scale = panSpeed * (distance * 0.2f + 0.1f);
            Vector3 delta = (-right * mx + -up * my) * scale;

            if (smooth)
                targetPoint += Vector3.SmoothDamp(Vector3.zero, delta, ref targetPointVel, 1f / Mathf.Max(1f, panDamp));
            else
                targetPoint += delta;

            if (target != null) target.position = targetPoint;
        }
        else
        {
            targetPointVel = Vector3.zero; // 드리프트 방지
        }

        // Zoom
        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > 1e-6f)
        {
            float z = scroll * zoomSpeed;
            if (zoomIsScaleByDistance) z *= Mathf.Max(0.1f, distanceTarget);
            distanceTarget = Mathf.Clamp(distanceTarget - z, minDistance, maxDistance);
        }

        // Reset
        if (Input.GetKeyDown(resetKey))
        {
            orbitAnglesTarget = new Vector2(20f, 30f);
            distanceTarget = Mathf.Clamp(5f, minDistance, maxDistance);
            targetPoint = (target != null) ? target.position : fallbackTarget;
            targetPointVel = Vector3.zero;
        }
    }

    void UpdateCameraPose()
    {
        if (smooth)
        {
            orbitAngles = Vector2.Lerp(orbitAngles, orbitAnglesTarget, 1f - Mathf.Exp(-orbitDamp * Time.deltaTime));
            distance = Mathf.Lerp(distance, distanceTarget, 1f - Mathf.Exp(-zoomDamp * Time.deltaTime));

            // 드리프트 잔류 제거 스냅
            if (Vector2.SqrMagnitude(orbitAnglesTarget - orbitAngles) < 1e-6f) orbitAngles = orbitAnglesTarget;
            if (Mathf.Abs(distanceTarget - distance) < 1e-6f) distance = distanceTarget;
        }
        else
        {
            orbitAngles = orbitAnglesTarget;
            distance = distanceTarget;
        }

        Quaternion rot = Quaternion.Euler(orbitAngles.x, orbitAngles.y, 0f);
        Vector3 focus = GetTargetPoint();
        Vector3 camPos = focus + rot * (Vector3.back * distance);

        transform.SetPositionAndRotation(camPos, rot);
    }

    Vector3 GetTargetPoint()
    {
        return target != null ? target.position : targetPoint;
    }

    void OnApplicationFocus(bool focus)
    {
        if (!focus)
        {
            targetPointVel = Vector3.zero; // 포커스 잃을 때 드리프트 방지
        }
    }
}
