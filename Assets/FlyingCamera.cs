using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FlyingCamera : MonoBehaviour
{
    Rigidbody rb;

    [SerializeField]
    float rotationSpeed = 15f;

    [SerializeField]
    float acceleration = 20f;

    [SerializeField]
    float velocityLimit = 20f;

    [SerializeField]
    bool enableBrush;

    [SerializeField]
    Map map;

    // Start is called before the first frame update
    void Start()
    {
        rb = GetComponent<Rigidbody>();
    }

    // Update is called once per frame
    void FixedUpdate()
    {
        var rotation = transform.localRotation;

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
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Mouse0)) StartCoroutine(UseBrush(KeyCode.Mouse0, false));
        if (Input.GetKeyDown(KeyCode.Mouse1)) StartCoroutine(UseBrush(KeyCode.Mouse1, true));
    }

    IEnumerator UseBrush(KeyCode inputKey, bool eraseMode)
    {
        Vector2 input = new Vector2(Screen.width / 2.0f, Screen.height / 2.0f);
        RaycastHit hit;
        while (Input.GetKey(inputKey))
        {
            Ray ray = map.MainCamera.ScreenPointToRay(input);

            if (Physics.Raycast(ray, out hit, 300))// && !hit.collider.tag.Equals("Untagged"))
            {
                //PointIndicator.position = hit.point;
                Debug.DrawLine(transform.position,hit.point,Color.red,10f);
                map.UseBrush(hit.point, eraseMode);
            }
            yield return null;
        }
    }
}
