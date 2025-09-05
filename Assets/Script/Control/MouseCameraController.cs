using UnityEngine;

[ExecuteAlways]
public class MouseCameraController : MonoBehaviour
{
    [Header("Move (RMB)")]
    public float moveSpeed = 100.0f;          // �̵� �⺻ �ӵ�(�ʴ� ����)
    public float moveSpeedBoost = 200.0f;    // Shift ���� ��� �� ���(�ɼ�)
    public bool enableShiftBoost = true;

    [Header("Drag Rotate (LMB)")]
    public float rotateSensitivity = 180f;  // ���콺 1.0�� ���� ��/�� ������
    public float minPitch = -89f;
    public float maxPitch = 89f;

    [Header("Zoom (Wheel)")]
    public float zoomSpeed = 500.0f;          // �� �� ƽ�� ����/���� �ӵ�
    public float minDistance = 0.01f;       // �ʹ� ��������� �� ����(�ɼ�)

    [Header("Reset (R)")]
    public Vector3 initialPosition = new Vector3(0, 0, -5);
    public Vector3 initialEulerAngles = new Vector3(0, 0, 0);

    // ���� ����
    private Vector2 yawPitch;               // yaw=Y, pitch=X
    private Transform cam;

    void OnEnable()
    {
        cam = transform;
        // �ʱ� ���Ϸ����� ��ġ/�� ����
        var e = cam.eulerAngles;
        yawPitch = new Vector2(NormalizePitch(e.x), e.y);
        // �ʱⰪ ����
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
        // �Է� ����
        bool lmb = Input.GetMouseButton(0);
        bool rmb = Input.GetMouseButton(1);
        bool wmb = Input.GetMouseButton(2);
        float mx = Input.GetAxis("Mouse X");
        float my = Input.GetAxis("Mouse Y");
        float wheel = Input.mouseScrollDelta.y;

        // ī�޶� �巡��(ȸ��): RMB �ܵ� �巡��
        if (lmb && !rmb)
        {
            // ���콺 �巡�׿� ����� yaw/pitch ����
            yawPitch.y += mx * rotateSensitivity * Time.deltaTime;   // yaw (Y)
            yawPitch.x -= my * rotateSensitivity * Time.deltaTime;   // pitch (X)
            yawPitch.x = Mathf.Clamp(yawPitch.x, minPitch, maxPitch);

            Quaternion rot = Quaternion.Euler(yawPitch.x, yawPitch.y, 0f);
            cam.rotation = rot;
        }

        // ī�޶� �̵�: RMB ������ �־�� ��
        if (rmb && !lmb)
        {
            // ȭ�� ��� �󿡼��� �̵��� ���콺 �巡�׷� ����
            // ���콺 X�� ī�޶� ������/����, ���콺 Y�� ī�޶� ��/�Ʒ��� �̵�
            Vector3 right = cam.right;
            Vector3 up = cam.up;

            float boost = (enableShiftBoost && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                        ? moveSpeedBoost : 1f;

            Vector3 delta =
                (right * (mx) +   // ������/����
                  up * (my))   // ��/�Ʒ�
                * moveSpeed * boost * Time.deltaTime;

            cam.position += delta;
        }

        // ��: ���콺 ��(ī�޶� �������� ����/����)
        if (Mathf.Abs(wheel) > 1e-6f)
        {
            Vector3 forward = cam.forward;
            float boost = (enableShiftBoost && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                        ? moveSpeedBoost : 1f;

            Vector3 delta = forward * (wheel * zoomSpeed * boost * Time.deltaTime);
            // �ʹ� ��������� �� ����(�ɼ�)
            if ((cam.position + delta).sqrMagnitude > minDistance * minDistance)
                cam.position += delta;
        }

        if (wmb && !lmb && !rmb)
        {
            float boost = (enableShiftBoost && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) ? moveSpeedBoost : 1f;

            // mx > 0 �� ����(Ȯ��), mx < 0 �� ����(���)
            // ������ ����� ������Ʈ �����Ͽ� �°� ����
            float dragZoomScale = zoomSpeed; // �ʿ� �� ���� ���� ������ �и� ����
            float amount = mx * dragZoomScale * boost * Time.deltaTime;

            Vector3 delta = transform.forward * amount;
            if ((transform.position + delta).sqrMagnitude > minDistance * minDistance)
                transform.position += delta;
        }

        // �ʱ� ��ġ�� �̵�: R
        if (Input.GetKeyDown(KeyCode.R))
        {
            cam.position = initialPosition;
            cam.rotation = Quaternion.Euler(initialEulerAngles);
            yawPitch = new Vector2(NormalizePitch(initialEulerAngles.x), initialEulerAngles.y);
        }
    }

    // Unity�� ���Ϸ� X�� -180~180ó�� ���� �� �־�, -89~89 ������ ����ȭ
    float NormalizePitch(float x)
    {
        if (x > 180f) x -= 360f;
        return Mathf.Clamp(x, -89f, 89f);
    }
}
