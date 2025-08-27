using UnityEngine;

[ExecuteAlways]
public class PcdViewerController : MonoBehaviour
{
    [Header("Camera")]
    public Camera targetCamera;

    [Header("Rotate (LMB)")]
    public float rotateSpeed = 0.2f;
    [Tooltip("Yaw 기준: true=월드 Y, false=카메라 Up")]
    public bool yawWorldUp = true;
    [Tooltip("피치 누적 제한(도). 0이면 제한 없음")]
    public float pitchClamp = 85f;
    public float yawSign = 1f;
    public float pitchSign = -1f;

    [Header("Pan (RMB)")]
    [Tooltip("픽셀→월드 이동 배율(거리/FOV로 동적 보정됨)")]
    public float movePixelToWorld = 1.0f;
    public float panShiftMultiplier = 2.0f;
    public float panAltMultiplier = 0.5f;
    public float panSign = 1f;

    [Header("Zoom (Wheel - cursor-centric)")]
    [Tooltip("휠 줌 속도(비율)")]
    public float zoomSpeed = 0.25f;
    [Tooltip("단일 휠 스텝 최대 변화량")]
    public float maxZoomStep = 0.2f;
    public float zoomShiftMultiplier = 2.0f;
    public float zoomAltMultiplier = 0.5f;
    [Tooltip("휠 방향(+1=휠 업 확대, -1=휠 업 축소)")]
    public float wheelSign = 1f;

    [Header("Framing (optional)")]
    public Vector3 boundsCenter;
    public Vector3 boundsSize;
    public float framePadding = 1.2f;

    // 내부 상태
    Vector3 lastMouse;
    bool lmbHeld, rmbHeld;
    float accumulatedPitch;

    void Awake()
    {
        if (targetCamera == null) targetCamera = Camera.main;
    }

    void Update()
    {
        var cam = (targetCamera != null) ? targetCamera : Camera.main;
        if (cam == null) return;

        UpdateButtons();
        HandleRotate(cam);
        HandlePan(cam);
        HandleZoom(cam); // 항상 호출되어야 함(요구 5)
        HandleFrame(cam);
    }

    void UpdateButtons()
    {
        if (Input.GetMouseButtonDown(0)) { lmbHeld = true; lastMouse = Input.mousePosition; }
        if (Input.GetMouseButtonUp(0)) { lmbHeld = false; }
        if (Input.GetMouseButtonDown(1)) { rmbHeld = true; lastMouse = Input.mousePosition; }
        if (Input.GetMouseButtonUp(1)) { rmbHeld = false; }
    }

    void HandleRotate(Camera cam)
    {
        // 요구 4: LMB+RMB 동시에는 회전 금지
        if (!(lmbHeld && !rmbHeld)) return;
        if (!Input.GetMouseButton(0)) return;

        Vector3 cur = Input.mousePosition;
        Vector3 delta = cur - lastMouse;

        float yawInput = delta.x * rotateSpeed * Mathf.Sign(yawSign);
        float pitchInput = delta.y * rotateSpeed * Mathf.Sign(pitchSign);

        // 피치 누적 클램프
        float clampedPitch = pitchInput;
        if (pitchClamp > 0f)
        {
            float targetAccum = Mathf.Clamp(accumulatedPitch + pitchInput, -pitchClamp, pitchClamp);
            clampedPitch = targetAccum - accumulatedPitch;
            accumulatedPitch = targetAccum;
        }

        // 현재 카메라 위치 고정 → 회전만 적용
        Vector3 pos = cam.transform.position;

        // 회전 축
        Vector3 yawAxis = yawWorldUp ? Vector3.up : cam.transform.up;
        Vector3 right = cam.transform.right;

        // pitch 먼저, yaw 다음
        if (Mathf.Abs(clampedPitch) > Mathf.Epsilon)
            cam.transform.rotation = Quaternion.AngleAxis(clampedPitch, right) * cam.transform.rotation;
        if (Mathf.Abs(yawInput) > Mathf.Epsilon)
            cam.transform.rotation = Quaternion.AngleAxis(yawInput, yawAxis) * cam.transform.rotation;

        // 위치 원복(절대 이동 금지)
        cam.transform.position = pos;

        lastMouse = cur;
    }

    void HandlePan(Camera cam)
    {
        // 요구 4: LMB+RMB 동시에는 패닝 금지
        if (!rmbHeld || lmbHeld) return;
        if (!Input.GetMouseButton(1)) return;

        Vector3 cur = Input.mousePosition;
        Vector3 delta = cur - lastMouse;

        float mul = GetModifierMultiplier(panShiftMultiplier, panAltMultiplier);

        // 픽셀→월드 환산: 화면 높이 대비 시야 높이
        float dist = EstimateSceneDistance(cam);
        float pxToWorld = Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f) * (dist * 2f) / Mathf.Max(1, Screen.height);

        // 화면 기준 이동을 월드로: 카메라 Right/Up 반대로
        Vector3 moveW =
            (-cam.transform.right * delta.x + -cam.transform.up * delta.y) *
            pxToWorld * movePixelToWorld * mul * Mathf.Sign(panSign);

        // 회전값은 유지, Position만 변경
        cam.transform.position += moveW;

        lastMouse = cur;
    }

    void HandleZoom(Camera cam)
    {
        float wheel = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(wheel) <= 0.0001f) return;

        float mul = GetModifierMultiplier(zoomShiftMultiplier, zoomAltMultiplier);
        float step = Mathf.Clamp(wheel * wheelSign * zoomSpeed * mul, -maxZoomStep, maxZoomStep);
        float scaleFactor = 1f + step;
        if (scaleFactor <= 0f) scaleFactor = 0.01f;

        Vector3 mouse = Input.mousePosition;

        // 스크린 기준(Cursor-centric) 줌:
        // 1) 화면에서 카메라 전방 방향으로 참조 평면까지의 Z 계산
        //    - 바운즈가 있으면 중심까지의 거리, 없으면 헤우리스틱 거리 사용
        float z = ComputeScreenZForCursorZoom(cam);

        // 2) 현재 커서가 바라보는 월드 점
        Vector3 cursorWorld = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, z));

        // 3) 카메라를 cursorWorld 기준으로 전진/후진
        Vector3 prevPos = cam.transform.position;
        cam.transform.position = cursorWorld + (prevPos - cursorWorld) * (1f / scaleFactor);
        // 회전은 변경하지 않음
    }

    float ComputeScreenZForCursorZoom(Camera cam)
    {
        // 카메라에서 “기준 대상”까지의 거리 → 스크린 z로 사용
        float dist = 0f;
        if (boundsSize.sqrMagnitude > 0f)
        {
            dist = Vector3.Distance(cam.transform.position,
                                    (boundsCenter != Vector3.zero) ? boundsCenter : cam.transform.position + cam.transform.forward * 10f);
        }
        if (dist <= 0f) dist = EstimateSceneDistance(cam);
        if (dist <= 0f) dist = 10f;

        // 카메라 near보다 멀고 far보다 가까워야 함
        dist = Mathf.Clamp(dist, cam.nearClipPlane + 0.001f, cam.farClipPlane - 0.001f);
        return dist;
    }

    float EstimateSceneDistance(Camera cam)
    {
        // 바운즈가 있으면 대략적 거리 추정, 없으면 10
        if (boundsSize.sqrMagnitude > 0f)
        {
            float sizeMag = boundsSize.magnitude;
            // 화면에 적절히 차도록 하는 대략 값
            float fovY = Mathf.Deg2Rad * Mathf.Max(1e-3f, cam.fieldOfView);
            float aspect = Mathf.Max(1e-3f, cam.aspect);
            float fovX = 2f * Mathf.Atan(Mathf.Tan(fovY * 0.5f) * aspect);
            float r = sizeMag * 0.5f;
            float distByY = r / Mathf.Tan(fovY * 0.5f);
            float distByX = r / Mathf.Tan(fovX * 0.5f);
            return Mathf.Max(distByX, distByY);
        }
        return 10f;
    }

    void HandleFrame(Camera cam)
    {
        if (!Input.GetKeyDown(KeyCode.F)) return;
        if (boundsSize.sqrMagnitude <= 0f) return;

        CameraFocusUtil.FocusCameraOnBounds(
            cam,
            (boundsCenter != Vector3.zero) ? boundsCenter : Vector3.zero,
            boundsSize,
            cam.fieldOfView,
            framePadding,
            cam.transform.forward
        );

        // 프레이밍 후 회전 누적 피치 초기화(상하 제한 기준 재설정)
        accumulatedPitch = 0f;
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
