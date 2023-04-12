using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Assets.Scripts
{
    public class AudioPool : MonoBehaviour
    {
        private static AudioPool _instance;

        // Queue to hold AudioSources that are not currently in use
        [NonSerialized]
        private Queue<AudioSource> audioPool = new Queue<AudioSource>();

        // Singleton instance of the AudioPool
        public static AudioPool Instance => _instance ??= new GameObject("Audio Pool").AddComponent<AudioPool>();

        // Flag indicating whether the game has been reloaded
        private static bool reloaded = true;

        // Subscribe to the OnGameReload event when the class is initialized
        static AudioPool()
        {
            Submarine.OnGameReload += Submarine_OnGameReload;
        }

        // Method to be called when the OnGameReload event is fired
        private static void Submarine_OnGameReload()
        {
            reloaded = true;
            _instance = null;
        }

        // Method for playing an AudioClip at a specific position
        public static AudioSource PlayAtPoint(Vector3 position, AudioClip clip, AudioMixerGroup mixerGroup = null)
        {
            // If the game has been reloaded, clear the audioPool
            if (reloaded)
            {
                Instance.audioPool.Clear();
                reloaded = false;
            }

            AudioSource instance;

            // Try to dequeue an AudioSource from the audioPool
            while (Instance.audioPool.TryDequeue(out instance))
            {
                // If the AudioSource is not null, break out of the loop
                if (instance != null)
                {
                    break;
                }
            }

            // If no AudioSource was dequeued, create a new one
            if (instance == null)
            {
                instance = new GameObject("Audio Source").AddComponent<AudioSource>();
            }

            // Set the AudioClip, position, mixer group, and activate the AudioSource
            instance.clip = clip;
            instance.transform.position = position;
            instance.gameObject.SetActive(true);
            instance.outputAudioMixerGroup = mixerGroup;

            // Start a coroutine to destroy the AudioSource after the length of the AudioClip
            Instance.StartCoroutine(DestroyAudioSource(instance, clip.length));

            // Play the AudioClip and return the AudioSource
            instance.Play();
            return instance;
        }

        // Coroutine to destroy an AudioSource after a certain amount of time
        private static IEnumerator DestroyAudioSource(AudioSource source, float time)
        {
            yield return new WaitForSeconds(time);

            // Reset the AudioSource properties and add it back to the audioPool
            source.volume = 1;
            source.pitch = 1;
            source.clip = null;
            source.outputAudioMixerGroup = null;
            source.gameObject.SetActive(false);
            Instance.audioPool.Enqueue(source);
        }
    }
}
