using System.Collections;
using UnityEngine;
using static Unity.Mathematics.math;

public class AmbientMusic : MonoBehaviour
{
    [SerializeField]
    private float fadeInTime = 10f; // Time in seconds for audio to fade in

    private void Start()
    {
        AudioSource source = GetComponent<AudioSource>(); // Get AudioSource component from the game object
        StartCoroutine(FadeIn(source)); // Start the FadeIn coroutine
    }

    private IEnumerator FadeIn(AudioSource source)
    {
        for (float t = 0; t < fadeInTime; t += Time.deltaTime) // Loop until fade in time is reached
        {
            source.volume = lerp(0f, 1f, t / fadeInTime); // Linearly interpolate between volume 0 and 1
            yield return null; // Wait for next frame
        }
        source.volume = 1f; // Set the final volume to 1
    }

    public IEnumerator FadeOut(float time)
    {
        StopAllCoroutines(); // Stop all coroutines running on the game object
        AudioSource source = GetComponent<AudioSource>(); // Get AudioSource component from the game object

        float oldVolume = source.volume; // Store the current volume of the audio source

        for (float t = 0; t < time; t += Time.deltaTime) // Loop until fade out time is reached
        {
            source.volume = lerp(oldVolume, 0f, t / time); // Linearly interpolate between the current volume and 0
            yield return null; // Wait for next frame
        }
        source.volume = 0f; // Set the final volume to 0
    }
}