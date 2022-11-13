using System.Collections.Generic;
using UnityEngine;

namespace Assets
{
    public class MainPool : MonoBehaviour
    {
        private static MainPool _instance;

        public static MainPool Instance => _instance ??= new GameObject("MAIN_POOL").AddComponent<MainPool>();

        private Dictionary<GameObject, Queue<GameObject>> PoolDict = new Dictionary<GameObject, Queue<GameObject>>();
        private Dictionary<GameObject, GameObject> PrefabDict = new Dictionary<GameObject, GameObject>();

        static MainPool()
        {
            Submarine.OnGameReload += Submarine_OnGameReload;
        }

        private static void Submarine_OnGameReload()
        {
            _instance = null;
        }


        public static GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (!Instance.PoolDict.TryGetValue(prefab, out Queue<GameObject> queue))
            {
                queue = new Queue<GameObject>();
                Instance.PoolDict.Add(prefab, queue);
            }

            /*HashSet<GameObject> spawnedSet;

            if (!Instance.Prefab.TryGetValue(prefab, out spawnedSet))
            {
                spawnedSet = new HashSet<GameObject>();
                Instance.Prefab.Add(prefab, spawnedSet);
            }*/

            if (queue.TryDequeue(out GameObject instance))
            {
                instance.SetActive(true);
                instance.transform.SetPositionAndRotation(position, rotation);
            }
            else
            {
                instance = GameObject.Instantiate(prefab, position, rotation);
            }
            Instance.PrefabDict.Add(instance, prefab);
            return instance;
        }

        public static void Return(GameObject instance)
        {
            if (Instance.PrefabDict.Remove(instance, out GameObject prefab))
            {
                if (!Instance.PoolDict.TryGetValue(prefab, out Queue<GameObject> queue))
                {
                    queue = new Queue<GameObject>();
                    Instance.PoolDict.Add(prefab, queue);
                }
                instance.SetActive(false);
                queue.Enqueue(instance);
            }
            else
            {
                Destroy(instance);
            }
        }
    }
}
