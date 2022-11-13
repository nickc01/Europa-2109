using System.Collections;
using UnityEngine;
using static Unity.Mathematics.math;

public class AmbientMusic : MonoBehaviour
{
    [SerializeField]
    private float fadeInTime = 10f;

    // Start is called before the first frame update
    private void Start()
    {
        AudioSource source = GetComponent<AudioSource>();
        StartCoroutine(FadeIn(source));
    }

    private IEnumerator FadeIn(AudioSource source)
    {
        for (float t = 0; t < fadeInTime; t += Time.deltaTime)
        {
            source.volume = lerp(0f, 1f, t / fadeInTime);
            yield return null;
        }
        source.volume = 1f;
    }

    public IEnumerator FadeOut(float time)
    {
        StopAllCoroutines();
        AudioSource source = GetComponent<AudioSource>();

        float oldVolume = source.volume;

        for (float t = 0; t < time; t += Time.deltaTime)
        {
            source.volume = lerp(oldVolume, 0f, t / time);
            yield return null;
        }
        source.volume = 0f;
    }
}
