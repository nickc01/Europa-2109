using UnityEngine;
using UnityEngine.UI;

using static Unity.Mathematics.math;

public class InterpolatedBar : MonoBehaviour
{
    [SerializeField]
    private float interpolationSpeed = 7;

    [field: SerializeField]
    public Slider Slider { get; private set; }

    private float destinationValue = 0;

    [SerializeField]
    private bool colorGradient = false;

    [SerializeField]
    private Color startColor;

    [SerializeField]
    private Color endColor;

    [SerializeField]
    private Graphic colorTarget;


    private void Awake()
    {
        destinationValue = Slider.value;
    }

    private void Reset()
    {
        if (Slider == null)
        {
            Slider = GetComponent<Slider>();
        }
    }


    public void SetValue(float value)
    {
        UpdateValue(value, false);
    }

    public void DecreaseValue(float value)
    {
        UpdateValue(destinationValue - value, false);
    }

    public void SetValueRaw(float value)
    {
        UpdateValue(value, true);
    }

    public void DecreaseValueRaw(float value)
    {
        UpdateValue(destinationValue - value, true);
    }

    public void UpdateValue(float value, bool setRaw)
    {
        destinationValue = value;
        if (setRaw)
        {
            Slider.value = value;
        }
    }


    private void Update()
    {
        Slider.value = lerp(Slider.value, destinationValue, Time.deltaTime * interpolationSpeed);
        if (colorTarget != null)
        {
            colorTarget.color = HSVLerp(endColor, startColor, 1f - (Slider.value / Slider.maxValue));
        }
    }

    private Color HSVLerp(Color A, Color B, float t)
    {

        Color.RGBToHSV(A, out float AH, out float AS, out float AV);
        Color.RGBToHSV(B, out float BH, out float BS, out float BV);

        float CH = Mathf.Lerp(AH, BH, t);
        float CS = Mathf.Lerp(AS, BS, t);
        float CV = Mathf.Lerp(AV, BV, t);

        return Color.HSVToRGB(CH, CS, CV);
    }
}
