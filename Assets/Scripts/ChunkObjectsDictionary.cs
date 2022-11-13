using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;


[CreateAssetMenu(fileName = "ChunkObjectDictionary", menuName = "Chunk Object Dictionary")]
public class ChunkObjectsDictionary : ScriptableObject
{
    private static ChunkObjectsDictionary _instance;
    public static ChunkObjectsDictionary Instance => _instance ??= Submarine.Instance.ChunkDictionary;


    static ChunkObjectsDictionary()
    {
        Submarine.OnGameReload += Submarine_OnGameReload;
    }

    private static void Submarine_OnGameReload()
    {
        _instance = null;
        IDDict = null;
    }

    [Serializable]
    public struct Entry
    {
        public int ID;
        public GameObject Prefab;
        public float Rarity;
        public float HeightLimit;
        public float FoundObjectMinimum;
    }

    [SerializeField]
    private List<Entry> _chunkObjects = new List<Entry>();

    public static List<Entry> ChunkObjects => Instance._chunkObjects;

    public static void InitDictionary()
    {
        IDDict = ChunkObjects.ToDictionary(e => e.ID);
    }

    public static bool PickRandomEntry(out Entry foundEntry, int3 chunkPosition)
    {
        int alreadyFoundObjects = Submarine.Instance.ObjectsFound;
        float height = Submarine.Instance.SubHeight;
        float total = ChunkObjects.Where(e => e.HeightLimit < height && e.FoundObjectMinimum <= alreadyFoundObjects).Sum(e => e.Rarity);
        Unity.Mathematics.Random randomizer = new Unity.Mathematics.Random((uint)abs(chunkPosition.GetHashCode()));
        float number = randomizer.NextFloat(0f, total);

        foreach (Entry entry in ChunkObjects)
        {
            if (number <= entry.Rarity)
            {
                foundEntry = entry;
                return true;
            }
            else
            {
                number -= entry.Rarity;
            }
        }
        foundEntry = default;
        return false;
    }

    public static Dictionary<int, Entry> IDDict;

}

