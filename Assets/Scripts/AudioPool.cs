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

        [NonSerialized]
        private Queue<AudioSource> audioPool = new Queue<AudioSource>();

        public static AudioPool Instance => _instance ??= new GameObject("Audio Pool").AddComponent<AudioPool>();

        private static bool reloaded = true;

        static AudioPool()
        {
            Submarine.OnGameReload += Submarine_OnGameReload;
        }

        private static void Submarine_OnGameReload()
        {
            reloaded = true;
            _instance = null;
        }

        public static AudioSource PlayAtPoint(Vector3 position, AudioClip clip, AudioMixerGroup mixerGroup = null)
        {
            if (reloaded)
            {
                Instance.audioPool.Clear();
                reloaded = false;
            }
            AudioSource instance;
            while (Instance.audioPool.TryDequeue(out instance))
            {
                if (instance != null)
                {
                    break;
                }
            }

            if (instance == null)
            {
                instance = new GameObject("Audio Source").AddComponent<AudioSource>();
            }

            instance.clip = clip;
            instance.transform.position = position;
            instance.gameObject.SetActive(true);
            instance.outputAudioMixerGroup = mixerGroup;
            Instance.StartCoroutine(DestroyAudioSource(instance, clip.length));
            instance.Play();
            return instance;
        }

        private static IEnumerator DestroyAudioSource(AudioSource source, float time)
        {
            yield return new WaitForSeconds(time);
            source.volume = 1;
            source.pitch = 1;
            source.clip = null;
            source.outputAudioMixerGroup = null;
            source.gameObject.SetActive(false);
            Instance.audioPool.Enqueue(source);
        }
    }
}
