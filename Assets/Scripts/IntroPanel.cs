using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using static Unity.Mathematics.math;

public class IntroPanel : MonoBehaviour
{
    [SerializeField]
    private TextMeshProUGUI Introduction;

    [SerializeField]
    private TextMeshProUGUI Instructions;

    [SerializeField]
    private float fadeDuration;

    [SerializeField]
    private Button continueButton;

    private bool onInstructions = false;


    [SerializeField]
    private List<Graphic> buttonGraphics;

    private void Awake()
    {
        Introduction.alpha = 0f;
        Instructions.alpha = 0f;
        Introduction.gameObject.SetActive(true);
        Instructions.gameObject.SetActive(true);

        foreach (Graphic graphic in buttonGraphics)
        {
            Color c = graphic.color;
            graphic.color = new Color(c.r, c.g, c.b, 0f);
        }
        continueButton.gameObject.SetActive(true);

        continueButton.enabled = false;
        StartCoroutine(AwakeRoutine());
    }

    private IEnumerator AwakeRoutine()
    {
        yield return new WaitForSeconds(0.25f);
        continueButton.enabled = true;
        foreach (Graphic graphic in buttonGraphics)
        {
            StartCoroutine(CrossFadeAlpha(graphic, 1f, fadeDuration));
        }
        StartCoroutine(CrossFadeAlpha(Introduction, 1f, fadeDuration));
    }

    public void NextPanel()
    {
        if (!onInstructions)
        {
            onInstructions = true;
            continueButton.enabled = false;
            StartCoroutine(FadeToInstructions());
        }
        else
        {
            continueButton.enabled = false;
            StartCoroutine(StartGame());
        }
    }

    private IEnumerator FadeToInstructions()
    {
        StartCoroutine(CrossFadeAlpha(Introduction, 0f, fadeDuration));
        yield return new WaitForSeconds(fadeDuration);
        StartCoroutine(CrossFadeAlpha(Instructions, 1f, fadeDuration));

        continueButton.enabled = true;
    }

    private IEnumerator StartGame()
    {
        StartCoroutine(CrossFadeAlpha(Instructions, 0f, fadeDuration));
        foreach (Graphic graphic in buttonGraphics)
        {
            StartCoroutine(CrossFadeAlpha(graphic, 0f, fadeDuration));
        }
        yield return new WaitForSeconds(fadeDuration * 2f);
        Submarine.Instance.StartGame();

        yield return new WaitForSeconds(0.75f);
        gameObject.SetActive(false);
    }

    private IEnumerator CrossFadeAlpha(Graphic graphic, float alpha, float time)
    {
        float oldAlpha = graphic.color.a;

        for (float t = 0; t < time; t += Time.deltaTime)
        {
            graphic.color = new Color(graphic.color.r, graphic.color.g, graphic.color.b, lerp(oldAlpha, alpha, t / time));
            yield return null;
        }

        graphic.color = new Color(graphic.color.r, graphic.color.g, graphic.color.b, alpha);
    }
}
