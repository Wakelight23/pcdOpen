using UnityEngine;

[ExecuteAlways]
public class MouseCameraController : MonoBehaviour
{
    [Header("Move (RMB)")]
    public float moveSpeed = 100.0f;          // 이동 기본 속도(초당 미터)
    public float moveSpeedBoost = 200.0f;    // Shift 가속 사용 시 배수(옵션)
    public bool enableShiftBoost = true;

    [Header("Drag Rotate (LMB)")]
    public float rotateSensitivity = 180f;  // 마우스 1.0에 대한 도/초 스케일
    public float minPitch = -89f;
    public float maxPitch = 89f;

    [Header("Zoom (Wheel)")]
    public float zoomSpeed = 500.0f;          // 휠 한 틱당 전진/후퇴 속도
    public float minDistance = 0.01f;       // 너무 가까워지는 것 방지(옵션)

    [Header("Reset (R)")]
    public Vector3 initialPosition = new Vector3(0, 0, -5);
    public Vector3 initialEulerAngles = new Vector3(0, 0, 0);

    // 내부 상태
    private Vector2 yawPitch;               // yaw=Y, pitch=X
    private Transform cam;

    void OnEnable()
    {
        cam = transform;
        // 초기 오일러에서 피치/요 추출
        var e = cam.eulerAngles;
        yawPitch = new Vector2(NormalizePitch(e.x), e.y);
        // 초기값 적용
        if (Application.isPlaying)
        {
            cam.position = initialPosition;
            cam.rotation = Quaternion.Euler(initialEulerAngles);
            yawPitch = new Vector2(NormalizePitch(initialEulerAngles.x), initialEulerAngles.y);
        }
    }

    void Update()
    {
        HandleInput();
    }

    void HandleInput()
    {
        // 입력 상태
        bool lmb = Input.GetMouseButton(0);
        bool rmb = Input.GetMouseButton(1);
        bool wmb = Input.GetMouseButton(2);
        float mx = Input.GetAxis("Mouse X");
        float my = Input.GetAxis("Mouse Y");
        float wheel = Input.mouseScrollDelta.y;

        // 카메라 드래그(회전): RMB 단독 드래그
        if (lmb && !rmb)
        {
            // 마우스 드래그에 비례해 yaw/pitch 변경
            yawPitch.y += mx * rotateSensitivity * Time.deltaTime;   // yaw (Y)
            yawPitch.x -= my * rotateSensitivity * Time.deltaTime;   // pitch (X)
            yawPitch.x = Mathf.Clamp(yawPitch.x, minPitch, maxPitch);

            Quaternion rot = Quaternion.Euler(yawPitch.x, yawPitch.y, 0f);
            cam.rotation = rot;
        }

        // 카메라 이동: RMB 누르고 있어야 함
        if (rmb && !lmb)
        {
            // 화면 평면 상에서의 이동을 마우스 드래그로 구현
            // 마우스 X는 카메라 오른쪽/왼쪽, 마우스 Y는 카메라 위/아래로 이동
            Vector3 right = cam.right;
            Vector3 up = cam.up;

            float boost = (enableShiftBoost && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                        ? moveSpeedBoost : 1f;

            Vector3 delta =
                (right * (mx) +   // 오른쪽/왼쪽
                  up * (my))   // 위/아래
                * moveSpeed * boost * Time.deltaTime;

            cam.position += delta;
        }

        // 줌: 마우스 휠(카메라 전방으로 전진/후퇴)
        if (Mathf.Abs(wheel) > 1e-6f)
        {
            Vector3 forward = cam.forward;
            float boost = (enableShiftBoost && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                        ? moveSpeedBoost : 1f;

            Vector3 delta = forward * (wheel * zoomSpeed * boost * Time.deltaTime);
            // 너무 가까워지는 것 방지(옵션)
            if ((cam.position + delta).sqrMagnitude > minDistance * minDistance)
                cam.position += delta;
        }

        if (wmb && !lmb && !rmb)
        {
            float boost = (enableShiftBoost && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) ? moveSpeedBoost : 1f;

            // mx > 0 → 전진(확대), mx < 0 → 후퇴(축소)
            // 스케일 계수는 프로젝트 스케일에 맞게 조정
            float dragZoomScale = zoomSpeed; // 필요 시 별도 공개 변수로 분리 가능
            float amount = mx * dragZoomScale * boost * Time.deltaTime;

            Vector3 delta = transform.forward * amount;
            if ((transform.position + delta).sqrMagnitude > minDistance * minDistance)
                transform.position += delta;
        }

        // 초기 위치로 이동: R
        if (Input.GetKeyDown(KeyCode.R))
        {
            cam.position = initialPosition;
            cam.rotation = Quaternion.Euler(initialEulerAngles);
            yawPitch = new Vector2(NormalizePitch(initialEulerAngles.x), initialEulerAngles.y);
        }
    }

    // Unity의 오일러 X는 -180~180처럼 보일 수 있어, -89~89 범위로 정규화
    float NormalizePitch(float x)
    {
        if (x > 180f) x -= 360f;
        return Mathf.Clamp(x, -89f, 89f);
    }
}
