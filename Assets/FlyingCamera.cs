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
}
