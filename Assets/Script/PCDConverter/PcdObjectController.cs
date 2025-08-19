using UnityEngine;

public class PcdObjectController : MonoBehaviour
{
    [Header("Speeds")]
    public float moveSpeed = 0.01f;   // ���콺 �̵� -> ��� �̵� �ΰ���
    public float rotateSpeed = 0.4f;     // ���콺 �̵� -> ȸ�� �ΰ���
    public float zoomSpeed = 0.5f;    // �� -> ������ �ΰ���

    [Header("Axes")]
    public bool rotateYawInWorld = true; // Yaw�� ���� Y�� �������� ȸ������ ���� (����: true)
    public Vector3 rotationPivot = Vector3.zero; // �ʿ� �� ȸ��/�̵� ���� �ǹ�(�⺻�� ��ü�� ���� ��ġ ���)

    Vector3 lastMousePos;
    bool isMoving = false; // ��Ŭ�� �巡��: ��� �̵�
    bool isRotating = false; // ��Ŭ�� �巡��: ȸ��

    void Update()
    {
        // ���콺 ���� ����
        if (Input.GetMouseButtonDown(0)) { lastMousePos = Input.mousePosition; isMoving = true; }
        if (Input.GetMouseButtonUp(0)) { isMoving = false; }
        if (Input.GetMouseButtonDown(1)) { lastMousePos = Input.mousePosition; isRotating = true; }
        if (Input.GetMouseButtonUp(1)) { isRotating = false; }

        // 1) ��Ŭ�� �巡��: ȸ�� ��(���� ȸ�� ���)�� ���ĵ� ��鿡�� �̵�
        if (isMoving && Input.GetMouseButton(0))
        {
            Vector3 delta = Input.mousePosition - lastMousePos;

            // ȭ�� X -> ������Ʈ�� '������' ��, ȭ�� Y -> ������Ʈ�� '��' ������ ����
            // �̷��� �ϸ� ȸ�� �Ŀ��� ���� ����(������Ʈ ���� ���� ���)���� �̵��� ���ӵ�
            Vector3 right = transform.right;   // ���� ������ ����
            Vector3 up = transform.up;      // ���� ������ ��
            Vector3 moveWS = (right * (delta.x * moveSpeed)) + (up * (delta.y * moveSpeed));

            // �ǹ� ���� �̵� ����: �ǹ��� �⺻(0)�̶�� transform.position ���
            transform.position += moveWS;

            lastMousePos = Input.mousePosition;
        }

        // 2) ��Ŭ�� �巡��: ȸ��
        if (isRotating && Input.GetMouseButton(1))
        {
            Vector3 delta = Input.mousePosition - lastMousePos;

            // ȸ�� �ǹ� ����
            Vector3 pivot = (rotationPivot == Vector3.zero) ? transform.position : rotationPivot;

            // ���콺 X: Yaw(�¿� ȸ��), ���콺 Y: Pitch(���� ȸ��)
            float yaw = delta.x * rotateSpeed;
            float pitch = -delta.y * rotateSpeed;

            // ȸ���� �ǹ� �������� ����
            // 1) Yaw: ���� Y�� �������� ������, �׻� '���� ��' ������ ������ ȸ��
            if (Mathf.Abs(yaw) > Mathf.Epsilon)
            {
                if (rotateYawInWorld)
                    RotateAroundPivot(pivot, Vector3.up, yaw);       // ���� Y��
                else
                    RotateAroundPivot(pivot, transform.up, yaw);      // ���� Y��
            }

            // 2) Pitch: ���� X�� ���� ȸ��(ī�޶� ���� ����)
            if (Mathf.Abs(pitch) > Mathf.Epsilon)
            {
                RotateAroundPivot(pivot, transform.right, pitch);      // ���� X��
            }

            lastMousePos = Input.mousePosition;
        }

        // 3) ���콺 ��: ������(�ǹ� ����)
        float wheel = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(wheel) > 0.0001f)
        {
            float scale = 1f + wheel * zoomSpeed;
            // �ǹ� ���� �������� ���Ѵٸ�, �����ϰ� �Բ� pivot-translate ������ �ʿ�
            Vector3 pivot = (rotationPivot == Vector3.zero) ? transform.position : rotationPivot;

            // �ǹ� �������� ������ ����
            Vector3 prevPos = transform.position;
            transform.localScale *= scale;

            // ������ �� �ǹ� ����: (���� ��ġ�� �ǹ� �������� ����)
            Vector3 dirFromPivot = prevPos - pivot;
            Vector3 newPos = pivot + dirFromPivot * scale;
            transform.position = newPos;
        }
    }

    // �ǹ� ���� ȸ�� ��ƿ
    void RotateAroundPivot(Vector3 pivot, Vector3 axis, float angleDegrees)
    {
        // pivot �������� ȸ���Ϸ���, ȸ�� �� pivot�� �������� �����̵� -> ȸ�� -> ����ġ
        // Transform.RotateAround�� ����ϸ� ����
        transform.RotateAround(pivot, axis, angleDegrees);
    }
}
