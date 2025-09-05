using UnityEngine;

[ExecuteAlways]
public class PcdViewerOrbitControllerRaycast : MonoBehaviour
{
    [Header("Move (Screen-to-World pan)")]
    public float movePixelToWorld = 1.0f;
    [Tooltip("Shift/Alt로 패닝 감도 배율(Shift=고속, Alt=정밀)")]
    public float panShiftMultiplier = 2.0f;
    public float panAltMultiplier = 0.5f;

    [Header("Rotate (Orbit with accurate pick pivot)")]
    public float rotateSpeed = 0.2f;
    public bool yawWithCameraUp = true;   // true: 카메라 Up, false: 월드 Up
    public float pitchClamp = 85f;        // 누적 피치 제한(도), 0이면 제한 없음

    [Tooltip("오빗 회전 기본 중심(피킹 실패 시 폴백). 비우면 transform.position 사용")]
    public Vector3 rotationPivot = Vector3.zero;

    [Header("Zoom (Cursor-centric)")]
    public float zoomSpeed = 0.25f;
    public float maxZoomStep = 0.2f;
    [Tooltip("Shift/Alt로 줌 감도 배율(Shift=고속, Alt=정밀)")]
    public float zoomShiftMultiplier = 2.0f;
    public float zoomAltMultiplier = 0.5f;

    [Header("Framing")]
    public float framePadding = 1.2f;

    [Header("Picking (Raycast options)")]
    [Tooltip("피킹 대상 레이어마스크")]
    public LayerMask pickLayerMask = ~0;
    [Tooltip("레이캐스트 최대 거리")]
    public float pickMaxDistance = 5000f;
    [Tooltip("피킹 실패 시 역투영(A안)으로 폴백 사용")]
    public bool fallbackToUnprojectWhenMiss = true;
    [Tooltip("히트 우선순위: true=첫 히트(콜라이더 우선), false=가장 가까운 히트")]
    public bool useFirstHit = true;

    [Header("Mouse Direction")]
    [Tooltip("가로 드래그 → Yaw 부호(+1=오른쪽 드래그가 +Yaw)")]
    public float yawSign = 1f; // 1 or -1
    [Tooltip("세로 드래그 → Pitch 부호(+1=위로 드래그가 +Pitch)")]
    public float pitchSign = -1f; // 기본 UI 직관: 위로 드래그=카메라가 내려보는 느낌 → -1
    [Tooltip("휠 줌 방향(+1=휠 업 → 확대, -1=휠 업 → 축소)")]
    public float wheelSign = 1f;
    [Tooltip("패닝 전체 방향 부호(+1/-1)")]
    public float panSign = 1f;

    // 내부 상태
    Vector3 lastMouse;
    bool isPanning;
    bool isOrbiting;

    float panScreenZ;               // 패닝용 고정 Z
    Vector3 clickPivotWorld;        // 회전 피벗(피킹/폴백으로 구함)
    float orbitRefScreenZ;          // 폴백 역투영용 스크린 Z
    float accumulatedPitch;         // 피치 누적(클램프용)

    // 데이터 바운드(프레이밍/F키용). 로딩 후 PcdEntry에서 설정 권장
    public Vector3 boundsCenter;
    public Vector3 boundsSize;

    void Update()
    {
        var cam = Camera.main;
        if (cam == null) return;

        // 우클릭: 이동 시작/종료
        if (Input.GetMouseButtonDown(1))
        {
            isPanning = true;
            lastMouse = Input.mousePosition;

            Vector3 objScreen = cam.WorldToScreenPoint(transform.position);
            panScreenZ = objScreen.z;
        }
        if (Input.GetMouseButtonUp(1)) isPanning = false;

        // 좌클릭: 회전 시작/종료(+피킹)
        if (Input.GetMouseButtonDown(0))
        {
            isOrbiting = true;
            lastMouse = Input.mousePosition;

            // 1) Raycast로 정확한 피벗 포인트 시도
            if (TryPickWorldPoint(cam, Input.mousePosition, out var picked))
            {
                clickPivotWorld = picked;
            }
            else
            {
                // 2) 실패 시 폴백(A안: 오브젝트 스크린 Z 기준 역투영)
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

        // 이동(우클릭 드래그)
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

        // 회전(좌클릭 드래그, 피킹 포인트 기준)
        if (isOrbiting && Input.GetMouseButton(0))
        {
            Vector3 cur = Input.mousePosition;
            Vector3 delta = cur - lastMouse;

            // 화면 기준 축 안정화
            Vector3 viewDir = (cam.transform.position - clickPivotWorld).normalized;
            Vector3 safeUp = Vector3.up;
            Vector3 screenRight = Vector3.Cross(safeUp, viewDir);
            if (screenRight.sqrMagnitude < 1e-6f) screenRight = cam.transform.right;
            screenRight.Normalize();

            Vector3 yawAxis = yawWithCameraUp ? cam.transform.up : safeUp; // 화면 상단 방향 고정 or 월드Up
            Vector3 pitchAxis = screenRight;                                 // 화면 우측 축 기준 피치

            // 드래그 입력을 부호 옵션에 맞게 변환
            float yawInput = delta.x * rotateSpeed * Mathf.Sign(yawSign);
            float pitchInput = delta.y * rotateSpeed * Mathf.Sign(pitchSign);

            // 1) 피치 누적을 먼저 계산하고 clamp
            float clampedPitchStep = pitchInput;
            if (pitchClamp > 0f)
            {
                float targetAccum = Mathf.Clamp(accumulatedPitch + pitchInput, -pitchClamp, pitchClamp);
                clampedPitchStep = targetAccum - accumulatedPitch;
                accumulatedPitch = targetAccum;
            }

            // 2) 회전 적용(피치 먼저, 그 다음 요aw 적용해 축 왜곡 최소화)
            if (Mathf.Abs(clampedPitchStep) > Mathf.Epsilon)
                transform.RotateAround(clickPivotWorld, pitchAxis, clampedPitchStep);

            if (Mathf.Abs(yawInput) > Mathf.Epsilon)
                transform.RotateAround(clickPivotWorld, yawAxis, yawInput);

            lastMouse = cur;
        }


        // 커서 기준 줌(휠)
        float wheel = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(wheel) > 0.0001f)
        {
            float zoomMultiplier = GetModifierMultiplier(zoomShiftMultiplier, zoomAltMultiplier);
            float step = Mathf.Clamp(wheel * wheelSign * zoomSpeed * zoomMultiplier, -maxZoomStep, maxZoomStep);
            float scaleFactor = 1f + step;
            if (scaleFactor <= 0f) scaleFactor = 0.01f;

            // 현재 오브젝트 위치 기준 Z로 포인터 월드 좌표 계산
            Vector3 objScreen = cam.WorldToScreenPoint(transform.position);
            float zForZoom = objScreen.z;

            Vector3 mouse = Input.mousePosition;
            Vector3 cursorWorld = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, zForZoom));

            Vector3 prevPos = transform.position;
            transform.localScale *= scaleFactor;
            transform.position = cursorWorld + (prevPos - cursorWorld) * scaleFactor;
        }

        // F 키: 프레이밍
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

        // 1) 히트 검사
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

        // 2) 실패
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
