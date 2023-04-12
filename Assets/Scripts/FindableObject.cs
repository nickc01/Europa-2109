using Assets;
using System.Collections.Generic;
using UnityEngine;
using static Unity.Mathematics.math;

public class FindableObject : MonoBehaviour
{
    [SerializeField]
    private float minimumFindDistance = 3f;

    [field: SerializeField]
    public Vector3 TargetOffset { get; private set; } = new Vector3(0f, 0.5f, 0f);

    [field: SerializeField]
    public Vector3 TargetSize { get; private set; } = new Vector3(1f, 1f, 1f);

    [SerializeField]
    private Vector3 anchorPoint = new Vector3(0f, 0f, 0f);

    public static HashSet<FindableObject> NearbyObjects = new HashSet<FindableObject>();

    private void OnEnable()
    {
        FixedUpdate();
    }

    private void Update()
    {
        if (lengthsq(Submarine.Instance.transform.position - transform.position) <= (minimumFindDistance * minimumFindDistance))
        {
            NearbyObjects.Add(this);
        }
        else
        {
            NearbyObjects.Remove(this);
        }
    }

    private void FixedUpdate()
    {
        if (Map.Instance.SamplePoint(transform.TransformPoint(anchorPoint)) < Map.Instance.IsoLevel)
        {
            MainPool.Return(gameObject);
        }
    }

    private void OnDisable()
    {
        if (NearbyObjects != null)
        {
            NearbyObjects.Remove(this);
        }
    }
}
