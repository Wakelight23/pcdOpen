using UnityEngine;

[ExecuteAlways]
public class CameraControl: MonoBehaviour
 {
    GameObject CameraParent;

    Vector3 defaultPosition;
    Quaternion defaultRotation;
    float defaultZoom;

    void Start()
    {
        init();
    }
    void Update()
    {
        cameraMove();
        cameraRotate();
        cameraZoomInOut();
        cameraInit();
    }
    private void init()
    {
        CameraParent = GameObject.Find("CameraManager");

        defaultPosition = Camera.main.transform.position;
        defaultRotation = CameraParent.transform.rotation;
        defaultZoom = Camera.main.fieldOfView;
    }
    private void cameraMove()
    {
        if (Input.GetMouseButton(0))
        {
            Camera.main.transform.Translate(Input.GetAxisRaw("Mouse X") / 10, Input.GetAxisRaw("Mouse Y") / 10, 0);
        }
    }
    private void cameraRotate()
    {
        if (Input.GetMouseButton(1))
        {
            CameraParent.transform.Rotate(Input.GetAxisRaw("Mouse Y") * 10, Input.GetAxisRaw("Mouse X") * 10, 0);
        }
    }
    private void cameraZoomInOut()
    {
        Camera.main.fieldOfView += 20 * Input.GetAxis("Mouse ScrollWheel");

        if (Camera.main.fieldOfView < 10) Camera.main.fieldOfView = 10;
    }
    private void cameraInit()
    {
        if (Input.GetMouseButton(2))
        {
            Camera.main.transform.position = defaultPosition;
            CameraParent.transform.rotation = defaultRotation;
            Camera.main.fieldOfView = defaultZoom;
        }
    }
}
