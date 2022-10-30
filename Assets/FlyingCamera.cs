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

        if (Input.GetKey(KeyCode.Mouse0))
        {
            UseBrush(false);
        }
        else if (Input.GetKey(KeyCode.Mouse1))
        {
            UseBrush(true);
        }
    }

    private void Update()
    {
        //if (Input.GetKeyDown(KeyCode.Mouse0)) StartCoroutine(UseBrush(KeyCode.Mouse0, false));
        //if (Input.GetKeyDown(KeyCode.Mouse1)) StartCoroutine(UseBrush(KeyCode.Mouse1, true));
    }

    void UseBrush(bool eraseMode)
    {
        var sample = map.SamplePoint(transform.position);
        if (sample < map.isoLevel)
        {
            //Vector2 input = new Vector2(Screen.width / 2.0f, Screen.height / 2.0f);
            Vector2 input = Input.mousePosition;
            Ray ray = map.MainCamera.ScreenPointToRay(input);

            //if (Physics.RaycastNonAlloc(ray, hits, 300) > 0)// && !hit.collider.tag.Equals("Untagged"))
            if (map.FireRayParallel(ray,out var hit,10f,map.boundsSize / map.numPointsPerAxis))
            {
                //Debug.DrawLine(transform.position, hit, Color.red, 10f);
                var distance = Vector3.Distance(transform.position, hit);
                if (distance >= map.SphereBrushSize / 2f && distance <= 10f)
                {
                    map.UseSphereBrush(hit, eraseMode);
                }
                //PointIndicator.position = hit.point;
            }
        }
    }

    RaycastHit[] hits = new RaycastHit[1];

    /*IEnumerator UseBrush(KeyCode inputKey, bool eraseMode)
    {
        var sample = map.SamplePoint(transform.position);
        Debug.Log("CURRENT POS SAMPLE = " + sample);
        if (sample <= map.isoLevel)
        {
            Vector2 input = new Vector2(Screen.width / 2.0f, Screen.height / 2.0f);
            while (Input.GetKey(inputKey))
            {
                Ray ray = map.MainCamera.ScreenPointToRay(input);

                if (Physics.RaycastNonAlloc(ray, hits, 300) > 0 && Vector3.Distance(transform.position, hits[0].point) >= map.SphereBrushSize / 2f)
                {
                    //PointIndicator.position = hit.point;
                    Debug.DrawLine(transform.position, hits[0].point, Color.red, 10f);
                    map.UseSphereBrush(hits[0].point, eraseMode);
                }
                yield return null;
            }
        }
    }*/
}
