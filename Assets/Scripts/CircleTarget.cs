using System.Collections;
using UnityEngine;
using UnityEngine.UI;

using static Unity.Mathematics.math;

public class CircleTarget : MonoBehaviour
{
    [SerializeField]
    private float alphaChangeTime = 0.35f;

    [SerializeField]
    private float collectionTime = 2f;
    private Graphic graphic;

    public Canvas TargetCanvas;
    public Camera TargetCamera;
    public FindableObject TargetObject;
    private Vector3 targetPosition;
    private Vector2 targetSize;
    private RectTransform rt;
    private Vector2 canvasSize = default;

    public bool Selected = true;
    private bool destroyed = false;

    public float Alpha
    {
        get => graphic.color.a;
        set
        {
            Color oldColor = graphic.color;
            graphic.color = new Color(oldColor.r, oldColor.g, oldColor.b, value);
        }
    }

    private void Awake()
    {
        rt = GetComponent<RectTransform>();
        transform.SetAsFirstSibling();
        graphic = GetComponent<Graphic>();
        Alpha = 0;
        StartCoroutine(InterpolateAlphaTo(1f, alphaChangeTime));
    }

    public void DestroyTarget()
    {
        if (destroyed)
        {
            return;
        }
        destroyed = true;

        if (InstructionalProgressBar.ObjectLock == gameObject)
        {
            InstructionalProgressBar.Progress = 0f;
        }
        Destroy(gameObject, alphaChangeTime);
        StopAllCoroutines();
        StartCoroutine(InterpolateAlphaTo(0f, alphaChangeTime));
    }

    private IEnumerator InterpolateAlphaTo(float alpha, float time)
    {
        float oldAlpha = Alpha;
        for (float t = 0; t < time; t += Time.deltaTime)
        {
            Alpha = Mathf.Lerp(oldAlpha, alpha, t / time);
            yield return null;
        }
        Alpha = alpha;
    }

    private bool visibleToSub = true;

    private void Update()
    {
        if (TargetObject != null)
        {
            targetPosition = TargetObject.transform.TransformPoint(TargetObject.TargetOffset);
            targetSize = (Vector2)TargetObject.TargetSize * TargetObject.transform.localScale;

            bool hitSomething = Map.Instance.FireRayParallel(new Ray(Submarine.Instance.transform.position, normalize(targetPosition - Submarine.Instance.transform.position)), out Unity.Mathematics.float3 hit, length(targetPosition - Submarine.Instance.transform.position));

            if ((hitSomething && Vector3.Distance(targetPosition, hit) < 0.2f) || !hitSomething)
            {
                visibleToSub = true;
            }
            else
            {
                visibleToSub = false;
            }

            if (Vector3.Distance(Submarine.Instance.transform.position, targetPosition) <= 3 && visibleToSub)
            {
                InstructionalText.DisplayText("Hold C to Collect", 1f / 59f);
                graphic.color = new Color(1f, 1f, 1f, graphic.color.a);
            }
            else
            {
                graphic.color = new Color(1f, 0f, 0f, graphic.color.a);
            }
        }
        if (TargetCanvas != null)
        {
            if (canvasSize == default)
            {
                canvasSize = TargetCanvas.GetComponent<RectTransform>().sizeDelta;
            }

            rt.anchoredPosition = TargetCamera.WorldToViewportPoint(targetPosition) * canvasSize;
            rt.sizeDelta = new Vector2(150f, 150f) * (1f / length(targetPosition - Submarine.Instance.transform.position)) * targetSize;
        }

        if (!Submarine.GameOver && !Submarine.GameWin && Selected && TargetObject != null && Input.GetKey(KeyCode.C))
        {
            if (Vector3.Distance(Submarine.Instance.transform.position, targetPosition) <= 3 && visibleToSub)
            {
                InstructionalProgressBar.ObjectLock = gameObject;
                InstructionalProgressBar.Progress += (1f / collectionTime) * Time.deltaTime;
                if (InstructionalProgressBar.Progress >= 1f)
                {
                    Submarine.Instance.FoundAnObject(TargetObject);
                    InstructionalProgressBar.Progress = 0f;
                }
            }
            else if (InstructionalProgressBar.ObjectLock == gameObject)
            {
                InstructionalProgressBar.Progress = 0f;
            }
        }

        if (TargetObject == null)
        {
            DestroyTarget();
        }
    }
}
