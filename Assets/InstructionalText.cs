using System.Collections;
using TMPro;
using UnityEngine;
using static Unity.Mathematics.math;

public class InstructionalText : MonoBehaviour
{
    private static InstructionalText _instance;

    public static InstructionalText Instance => _instance ??= FindObjectOfType<InstructionalText>();

    static InstructionalText()
    {
        Submarine.OnGameReload += Submarine_OnGameReload;
    }

    private static void Submarine_OnGameReload()
    {
        _instance = null;
    }

    private bool visible = false;
    private TextMeshProUGUI textComponent;

    [SerializeField]
    private float interpolationTime = 1f;

    public float timer = 0;

    private float Alpha
    {
        get => textComponent.color.a;
        set
        {
            Color oldColor = textComponent.color;
            textComponent.color = new Color(oldColor.r, oldColor.g, oldColor.b, value);
        }
    }

    private void Awake()
    {
        textComponent = GetComponent<TextMeshProUGUI>();
        Alpha = 0;
    }

    private void Update()
    {
        if (timer > 0)
        {
            timer -= Time.deltaTime;
            if (timer <= 0)
            {
                timer = 0;
                MakeInvisible();
            }
        }
    }

    public static void DisplayText(string text, float time = 7f)
    {
        Instance.textComponent.text = text;
        Instance.MakeVisible();
        Instance.timer = time;
    }

    private void MakeVisible()
    {
        if (visible)
        {
            return;
        }
        visible = true;
        StopAllCoroutines();
        StartCoroutine(FadeAlpha(1f, interpolationTime));
    }

    private void MakeInvisible()
    {
        if (!visible)
        {
            return;
        }
        visible = false;
        StopAllCoroutines();
        StartCoroutine(FadeAlpha(0f, interpolationTime));
    }

    private IEnumerator FadeAlpha(float destAlpha, float time)
    {
        float oldA = Alpha;
        float newA = destAlpha;

        for (float t = 0; t < time; t += Time.deltaTime)
        {
            Alpha = lerp(oldA, newA, t / time);
            yield return null;
        }
        Alpha = newA;
    }

    /*IEnumerator MakeVisibleRoutine()
    {
        var oldA = Alpha;
        var newA = 1f;

        for (float t = 0; t < interpolationTime; t++)
        {
            Alpha = lerp(oldA,newA,t / interpolationTime);
            yield return null;
        }
    }

    IEnumerator MakeInvisibleRoutine()
    {
        var oldA = Alpha;
        var newA = 0f;

        for (float t = 0; t < interpolationTime; t++)
        {
            Alpha = lerp(oldA, newA, t / interpolationTime);
            yield return null;
        }
    }*/


}
