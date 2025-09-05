using UnityEngine;

[ExecuteAlways]
public class PcdViewerOrbitControllerRaycast : MonoBehaviour
{
    [Header("Move (Screen-to-World pan)")]
    public float movePixelToWorld = 1.0f;
    [Tooltip("Shift/Alt�� �д� ���� ����(Shift=���, Alt=����)")]
    public float panShiftMultiplier = 2.0f;
    public float panAltMultiplier = 0.5f;

    [Header("Rotate (Orbit with accurate pick pivot)")]
    public float rotateSpeed = 0.2f;
    public bool yawWithCameraUp = true;   // true: ī�޶� Up, false: ���� Up
    public float pitchClamp = 85f;        // ���� ��ġ ����(��), 0�̸� ���� ����

    [Tooltip("���� ȸ�� �⺻ �߽�(��ŷ ���� �� ����). ���� transform.position ���")]
    public Vector3 rotationPivot = Vector3.zero;

    [Header("Zoom (Cursor-centric)")]
    public float zoomSpeed = 0.25f;
    public float maxZoomStep = 0.2f;
    [Tooltip("Shift/Alt�� �� ���� ����(Shift=���, Alt=����)")]
    public float zoomShiftMultiplier = 2.0f;
    public float zoomAltMultiplier = 0.5f;

    [Header("Framing")]
    public float framePadding = 1.2f;

    [Header("Picking (Raycast options)")]
    [Tooltip("��ŷ ��� ���̾��ũ")]
    public LayerMask pickLayerMask = ~0;
    [Tooltip("����ĳ��Ʈ �ִ� �Ÿ�")]
    public float pickMaxDistance = 5000f;
    [Tooltip("��ŷ ���� �� ������(A��)���� ���� ���")]
    public bool fallbackToUnprojectWhenMiss = true;
    [Tooltip("��Ʈ �켱����: true=ù ��Ʈ(�ݶ��̴� �켱), false=���� ����� ��Ʈ")]
    public bool useFirstHit = true;

    [Header("Mouse Direction")]
    [Tooltip("���� �巡�� �� Yaw ��ȣ(+1=������ �巡�װ� +Yaw)")]
    public float yawSign = 1f; // 1 or -1
    [Tooltip("���� �巡�� �� Pitch ��ȣ(+1=���� �巡�װ� +Pitch)")]
    public float pitchSign = -1f; // �⺻ UI ����: ���� �巡��=ī�޶� �������� ���� �� -1
    [Tooltip("�� �� ����(+1=�� �� �� Ȯ��, -1=�� �� �� ���)")]
    public float wheelSign = 1f;
    [Tooltip("�д� ��ü ���� ��ȣ(+1/-1)")]
    public float panSign = 1f;

    // ���� ����
    Vector3 lastMouse;
    bool isPanning;
    bool isOrbiting;

    float panScreenZ;               // �д׿� ���� Z
    Vector3 clickPivotWorld;        // ȸ�� �ǹ�(��ŷ/�������� ����)
    float orbitRefScreenZ;          // ���� �������� ��ũ�� Z
    float accumulatedPitch;         // ��ġ ����(Ŭ������)

    // ������ �ٿ��(�����̹�/FŰ��). �ε� �� PcdEntry���� ���� ����
    public Vector3 boundsCenter;
    public Vector3 boundsSize;

    void Update()
    {
        var cam = Camera.main;
        if (cam == null) return;

        // ��Ŭ��: �̵� ����/����
        if (Input.GetMouseButtonDown(1))
        {
            isPanning = true;
            lastMouse = Input.mousePosition;

            Vector3 objScreen = cam.WorldToScreenPoint(transform.position);
            panScreenZ = objScreen.z;
        }
        if (Input.GetMouseButtonUp(1)) isPanning = false;

        // ��Ŭ��: ȸ�� ����/����(+��ŷ)
        if (Input.GetMouseButtonDown(0))
        {
            isOrbiting = true;
            lastMouse = Input.mousePosition;

            // 1) Raycast�� ��Ȯ�� �ǹ� ����Ʈ �õ�
            if (TryPickWorldPoint(cam, Input.mousePosition, out var picked))
            {
                clickPivotWorld = picked;
            }
            else
            {
                // 2) ���� �� ����(A��: ������Ʈ ��ũ�� Z ���� ������)
                Vector3 objScreen = cam.WorldToScreenPoint(transform.position);
                orbitRefScreenZ = objScreen.z;

                if (fallbackToUnprojectWhenMiss)
                {
                    Vector3 m = Input.mousePosition;
                    clickPivotWorld = cam.ScreenToWorldPoint(new Vector3(m.x, m.y, orbitRefScreenZ));
                }
                else
                {
                    clickPivotWorld = rotationPivot != Vector3.zero ? rotationPivot : transform.position;
                }
            }

            accumulatedPitch = 0f;
        }
        if (Input.GetMouseButtonUp(0)) isOrbiting = false;

        // �̵�(��Ŭ�� �巡��)
        if (isPanning && Input.GetMouseButton(1))
        {
            Vector3 cur = Input.mousePosition;
            Vector3 prev = lastMouse;

            Vector3 prevW = cam.ScreenToWorldPoint(new Vector3(prev.x, prev.y, panScreenZ));
            Vector3 curW = cam.ScreenToWorldPoint(new Vector3(cur.x, cur.y, panScreenZ));

            float panMultiplier = GetModifierMultiplier(panShiftMultiplier, panAltMultiplier);
            Vector3 deltaW = (curW - prevW) * movePixelToWorld * panMultiplier * Mathf.Sign(panSign);
            transform.position += deltaW;

            lastMouse = cur;
        }

        // ȸ��(��Ŭ�� �巡��, ��ŷ ����Ʈ ����)
        if (isOrbiting && Input.GetMouseButton(0))
        {
            Vector3 cur = Input.mousePosition;
            Vector3 delta = cur - lastMouse;

            // ȭ�� ���� �� ����ȭ
            Vector3 viewDir = (cam.transform.position - clickPivotWorld).normalized;
            Vector3 safeUp = Vector3.up;
            Vector3 screenRight = Vector3.Cross(safeUp, viewDir);
            if (screenRight.sqrMagnitude < 1e-6f) screenRight = cam.transform.right;
            screenRight.Normalize();

            Vector3 yawAxis = yawWithCameraUp ? cam.transform.up : safeUp; // ȭ�� ��� ���� ���� or ����Up
            Vector3 pitchAxis = screenRight;                                 // ȭ�� ���� �� ���� ��ġ

            // �巡�� �Է��� ��ȣ �ɼǿ� �°� ��ȯ
            float yawInput = delta.x * rotateSpeed * Mathf.Sign(yawSign);
            float pitchInput = delta.y * rotateSpeed * Mathf.Sign(pitchSign);

            // 1) ��ġ ������ ���� ����ϰ� clamp
            float clampedPitchStep = pitchInput;
            if (pitchClamp > 0f)
            {
                float targetAccum = Mathf.Clamp(accumulatedPitch + pitchInput, -pitchClamp, pitchClamp);
                clampedPitchStep = targetAccum - accumulatedPitch;
                accumulatedPitch = targetAccum;
            }

            // 2) ȸ�� ����(��ġ ����, �� ���� ��aw ������ �� �ְ� �ּ�ȭ)
            if (Mathf.Abs(clampedPitchStep) > Mathf.Epsilon)
                transform.RotateAround(clickPivotWorld, pitchAxis, clampedPitchStep);

            if (Mathf.Abs(yawInput) > Mathf.Epsilon)
                transform.RotateAround(clickPivotWorld, yawAxis, yawInput);

            lastMouse = cur;
        }


        // Ŀ�� ���� ��(��)
        float wheel = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(wheel) > 0.0001f)
        {
            float zoomMultiplier = GetModifierMultiplier(zoomShiftMultiplier, zoomAltMultiplier);
            float step = Mathf.Clamp(wheel * wheelSign * zoomSpeed * zoomMultiplier, -maxZoomStep, maxZoomStep);
            float scaleFactor = 1f + step;
            if (scaleFactor <= 0f) scaleFactor = 0.01f;

            // ���� ������Ʈ ��ġ ���� Z�� ������ ���� ��ǥ ���
            Vector3 objScreen = cam.WorldToScreenPoint(transform.position);
            float zForZoom = objScreen.z;

            Vector3 mouse = Input.mousePosition;
            Vector3 cursorWorld = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, zForZoom));

            Vector3 prevPos = transform.position;
            transform.localScale *= scaleFactor;
            transform.position = cursorWorld + (prevPos - cursorWorld) * scaleFactor;
        }

        // F Ű: �����̹�
        if (Input.GetKeyDown(KeyCode.F))
        {
            if (boundsSize.sqrMagnitude > 0.0f)
            {
                CameraFocusUtil.FocusCameraOnBounds(
                    cam,
                    boundsCenter != Vector3.zero ? boundsCenter : transform.position,
                    boundsSize,
                    cam.fieldOfView,
                    framePadding,
                    cam.transform.forward
                );
            }
        }
    }

    bool TryPickWorldPoint(Camera cam, Vector3 mousePos, out Vector3 picked)
    {
        picked = Vector3.zero;
        Ray ray = cam.ScreenPointToRay(mousePos);

        // 1) ��Ʈ �˻�
        if (useFirstHit)
        {
            if (Physics.Raycast(ray, out var hit, pickMaxDistance, pickLayerMask, QueryTriggerInteraction.Ignore))
            {
                picked = hit.point;
                return true;
            }
        }
        else
        {
            var hits = Physics.RaycastAll(ray, pickMaxDistance, pickLayerMask, QueryTriggerInteraction.Ignore);
            if (hits != null && hits.Length > 0)
            {
                float minDist = float.PositiveInfinity;
                int idx = -1;
                for (int i = 0; i < hits.Length; ++i)
                {
                    if (hits[i].distance < minDist)
                    {
                        minDist = hits[i].distance;
                        idx = i;
                    }
                }
                if (idx >= 0)
                {
                    picked = hits[idx].point;
                    return true;
                }
            }
        }

        // 2) ����
        return false;
    }

    float GetModifierMultiplier(float shiftMul, float altMul)
    {
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        if (shift && !alt) return shiftMul;
        if (alt && !shift) return altMul;
        return 1f;
    }
}
