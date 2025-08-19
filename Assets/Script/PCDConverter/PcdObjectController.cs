using UnityEngine;

public class PcdObjectController : MonoBehaviour
{
    [Header("Speeds")]
    public float moveSpeed = 0.01f;   // 마우스 이동 -> 평면 이동 민감도
    public float rotateSpeed = 0.4f;     // 마우스 이동 -> 회전 민감도
    public float zoomSpeed = 0.5f;    // 휠 -> 스케일 민감도

    [Header("Axes")]
    public bool rotateYawInWorld = true; // Yaw를 월드 Y축 기준으로 회전할지 여부 (권장: true)
    public Vector3 rotationPivot = Vector3.zero; // 필요 시 회전/이동 참조 피벗(기본은 객체의 현재 위치 사용)

    Vector3 lastMousePos;
    bool isMoving = false; // 좌클릭 드래그: 평면 이동
    bool isRotating = false; // 우클릭 드래그: 회전

    void Update()
    {
        // 마우스 상태 갱신
        if (Input.GetMouseButtonDown(0)) { lastMousePos = Input.mousePosition; isMoving = true; }
        if (Input.GetMouseButtonUp(0)) { isMoving = false; }
        if (Input.GetMouseButtonDown(1)) { lastMousePos = Input.mousePosition; isRotating = true; }
        if (Input.GetMouseButtonUp(1)) { isRotating = false; }

        // 1) 좌클릭 드래그: 회전 축(현재 회전 결과)에 정렬된 평면에서 이동
        if (isMoving && Input.GetMouseButton(0))
        {
            Vector3 delta = Input.mousePosition - lastMousePos;

            // 화면 X -> 오브젝트의 '오른쪽' 축, 화면 Y -> 오브젝트의 '위' 축으로 매핑
            // 이렇게 하면 회전 후에도 같은 기준(오브젝트 로컬 기준 평면)으로 이동이 지속됨
            Vector3 right = transform.right;   // 로컬 기준의 우측
            Vector3 up = transform.up;      // 로컬 기준의 위
            Vector3 moveWS = (right * (delta.x * moveSpeed)) + (up * (delta.y * moveSpeed));

            // 피벗 기준 이동 유지: 피벗이 기본(0)이라면 transform.position 사용
            transform.position += moveWS;

            lastMousePos = Input.mousePosition;
        }

        // 2) 우클릭 드래그: 회전
        if (isRotating && Input.GetMouseButton(1))
        {
            Vector3 delta = Input.mousePosition - lastMousePos;

            // 회전 피벗 결정
            Vector3 pivot = (rotationPivot == Vector3.zero) ? transform.position : rotationPivot;

            // 마우스 X: Yaw(좌우 회전), 마우스 Y: Pitch(상하 회전)
            float yaw = delta.x * rotateSpeed;
            float pitch = -delta.y * rotateSpeed;

            // 회전을 피벗 기준으로 적용
            // 1) Yaw: 월드 Y축 기준으로 돌리면, 항상 '수직 축' 기준의 직관적 회전
            if (Mathf.Abs(yaw) > Mathf.Epsilon)
            {
                if (rotateYawInWorld)
                    RotateAroundPivot(pivot, Vector3.up, yaw);       // 월드 Y축
                else
                    RotateAroundPivot(pivot, transform.up, yaw);      // 로컬 Y축
            }

            // 2) Pitch: 로컬 X축 기준 회전(카메라 오빗 느낌)
            if (Mathf.Abs(pitch) > Mathf.Epsilon)
            {
                RotateAroundPivot(pivot, transform.right, pitch);      // 로컬 X축
            }

            lastMousePos = Input.mousePosition;
        }

        // 3) 마우스 휠: 스케일(피벗 유지)
        float wheel = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(wheel) > 0.0001f)
        {
            float scale = 1f + wheel * zoomSpeed;
            // 피벗 기준 스케일을 원한다면, 스케일과 함께 pivot-translate 보정이 필요
            Vector3 pivot = (rotationPivot == Vector3.zero) ? transform.position : rotationPivot;

            // 피벗 기준으로 스케일 적용
            Vector3 prevPos = transform.position;
            transform.localScale *= scale;

            // 스케일 시 피벗 고정: (현재 위치를 피벗 방향으로 보정)
            Vector3 dirFromPivot = prevPos - pivot;
            Vector3 newPos = pivot + dirFromPivot * scale;
            transform.position = newPos;
        }
    }

    // 피벗 기준 회전 유틸
    void RotateAroundPivot(Vector3 pivot, Vector3 axis, float angleDegrees)
    {
        // pivot 기준으로 회전하려면, 회전 전 pivot을 기준으로 평행이동 -> 회전 -> 원위치
        // Transform.RotateAround를 사용하면 간단
        transform.RotateAround(pivot, axis, angleDegrees);
    }
}
