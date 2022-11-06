using System.Collections;
using System.Collections.Generic;
using Unity.Burst.CompilerServices;
using Unity.Mathematics;
using UnityEngine;

public class FlyingCamera : MonoBehaviour
{
    Rigidbody rb;

    [SerializeField]
    float rotationSpeed = 7f;

    [SerializeField]
    float rotationInterpolationSpeed = 7f;

    [SerializeField]
    float acceleration = 10f;

    [SerializeField]
    float velocityLimit = 20f;

    [SerializeField]
    bool enableBrush;

    [SerializeField]
    float brushSpeed = 5f;

    [SerializeField]
    Map map;

    Quaternion targetRotation;

    Vector3 oldMousePosition;

    [field: SerializeField]
    public bool UseMouseControls { get; private set; } = true;

    // Start is called before the first frame update
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        targetRotation = transform.rotation;
        oldMousePosition = Input.mousePosition;
        Cursor.lockState = CursorLockMode.Locked;
        targetRotation = transform.rotation;
        //StartCoroutine(TestDraw());
    }

    /*IEnumerator TestDraw()
    {
        yield return new WaitForSeconds(2f);
        map.UseSphereBrush(transform.position, true, 1000f);
    }*/

    bool inFocus = false;

    // Update is called once per frame
    void Update()
    {
        if (inFocus)
        {
            var mousePos = Input.mousePosition;
            //var mouseDT = mousePos - oldMousePosition;
            oldMousePosition = mousePos;

            //Debug.Log("MousePos = " + new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")));

            //targetRotation = Quaternion.AngleAxis(mouseDT.x * rotationSpeed * Time.fixedDeltaTime, Vector3.up) * Quaternion.AngleAxis(-mouseDT.y * rotationSpeed * Time.fixedDeltaTime, Vector3.right) * targetRotation;
            //targetRotation.z = 0;
            //targetRotation = Quaternion.Euler(-mouseDT.y * rotationSpeed * Time.fixedDeltaTime, mouseDT.x * rotationSpeed * Time.fixedDeltaTime,0f) * targetRotation;

            //transform.rotation = targetRotation;
            //transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationInterpolationSpeed * Time.fixedDeltaTime);

            //transform.rotation = Quaternion.Euler(-Input.mousePosition.y * rotationSpeed * Time.fixedDeltaTime, -Input.mousePosition.x * rotationSpeed * Time.fixedDeltaTime, 0f);
            //targetRotation = Quaternion.Euler(Mathf.Clamp(targetRotation.eulerAngles.x + (-Input.GetAxis("Mouse Y") * rotationSpeed * Time.deltaTime),-89f,89f), targetRotation.eulerAngles.y + (Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime), 0f);

            var xRotation = targetRotation.eulerAngles.x + (-Input.GetAxis("Mouse Y") * rotationSpeed * Time.deltaTime);
            var yRotation = targetRotation.eulerAngles.y + (Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime);

            /*if (xRotation > 180f)
            {
                xRotation -= 360f;
            }
            else if (xRotation < 180f)
            {
                xRotation += 360f;
            }

            if (xRotation < -80f && xRotation < -89f)
            {
                xRotation = -89f;
                Debug.Log("Less Than");
            }
            else if (xRotation > 80f && xRotation > 89f)
            {
                Debug.Log("XRotation = " + xRotation);
                xRotation = 89f;
                Debug.Log("Greater Than");
            }*/

            targetRotation = Quaternion.Euler(xRotation, yRotation, 0f);
            transform.rotation = targetRotation;
            //transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationInterpolationSpeed * Time.deltaTime);

            rb.velocity += transform.TransformVector(Vector3.forward * acceleration * Input.GetAxis("Vertical") * Time.deltaTime);
            rb.velocity += transform.TransformVector(Vector3.right * acceleration * Input.GetAxis("Horizontal") * Time.deltaTime);

            if (rb.velocity.magnitude >= velocityLimit)
            {
                rb.velocity = rb.velocity.normalized * velocityLimit;
            }
        }

        /*var rotation = transform.localRotation;

        if (Input.GetKey(KeyCode.LeftShift))
        {
            rotation = Quaternion.AngleAxis(-Input.GetAxis("Vertical") * rotationSpeed * Time.fixedDeltaTime, transform.right) * rotation;
        }
        rotation = Quaternion.AngleAxis(Input.GetAxis("Horizontal") * rotationSpeed * Time.fixedDeltaTime, transform.up) * rotation;

        transform.localRotation = rotation;

        if (!Input.GetKey(KeyCode.LeftShift))
        {
            rb.velocity += transform.TransformVector(Vector3.forward * acceleration * Input.GetAxis("Vertical") * Time.fixedDeltaTime);
 
            if (rb.velocity.magnitude >= velocityLimit)
            {
                rb.velocity = rb.velocity.normalized * velocityLimit;
            }
        }*/
    }

    private void FixedUpdate()
    {
        if (inFocus && enableBrush)
        {
            if (Input.GetKey(KeyCode.Mouse0))
            {
                UseBrush(new Vector2(Screen.width / 2f, Screen.height / 2f), false);
            }
            else if (Input.GetKey(KeyCode.Mouse1))
            {
                UseBrush(new Vector2(Screen.width / 2f, Screen.height / 2f), true);
            }
        }
    }

    private void OnApplicationFocus(bool focus)
    {
        inFocus = focus;
    }

    void UseBrush(Vector2 screenPosition, bool eraseMode)
    {
        var sample = map.SamplePoint(transform.position);
        if (sample < map.IsoLevel)
        {
            Ray ray = map.MainCamera.ScreenPointToRay(screenPosition);

            if (map.FireRayParallel(ray,out var hit,10f,map.BoundsSize / map.NumPointsPerAxis))
            {
                var distance = Vector3.Distance(transform.position, hit);
                if (distance >= 1f && distance <= 10f)
                {
                    map.UseSphereBrush(hit, eraseMode, Time.fixedDeltaTime * (brushSpeed / 10f), new int3(1.5));
                    //map.UseCubeBrush(hit,eraseMode,10f,new int3(5,6,7));
                }
            }
        }
    }
}
