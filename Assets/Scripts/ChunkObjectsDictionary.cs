using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

// Attribute that adds a context menu item to create an asset of this type in the Unity Editor
[CreateAssetMenu(fileName = "ChunkObjectDictionary", menuName = "Chunk Object Dictionary")]
public class ChunkObjectsDictionary : ScriptableObject
{
    // Singleton instance of the ChunkObjectsDictionary
    private static ChunkObjectsDictionary _instance;

    // Property to get the singleton instance of the ChunkObjectsDictionary
    public static ChunkObjectsDictionary Instance => _instance ??= Submarine.Instance.ChunkDictionary;

    // Event handler for the OnGameReload event, which is fired when the game is reloaded
    static ChunkObjectsDictionary()
    {
        Submarine.OnGameReload += Submarine_OnGameReload;
    }

    // Method called when the game is reloaded
    private static void Submarine_OnGameReload()
    {
        // Set the singleton instance and IDDict to null, so that they are recreated when they are next needed
        _instance = null;
        IDDict = null;
    }

    // Struct that represents a dictionary entry for a chunk object
    [Serializable]
    public struct Entry
    {
        public int ID;               // Unique ID for the chunk object
        public GameObject Prefab;    // Prefab for the chunk object
        public float Rarity;         // Rarity of the chunk object
        public float HeightLimit;    // Maximum height at which the chunk object can spawn
        public float FoundObjectMinimum;    // Minimum number of objects that must be found before the chunk object can spawn
    }

    [SerializeField]
    private List<Entry> _chunkObjects = new List<Entry>();    // List of chunk objects

    // Property to get the list of chunk objects
    public static List<Entry> ChunkObjects => Instance._chunkObjects;

    // Dictionary that maps chunk object IDs to their corresponding entries
    public static Dictionary<int, Entry> IDDict;

    // Method to initialize the IDDict dictionary
    public static void InitDictionary()
    {
        IDDict = ChunkObjects.ToDictionary(e => e.ID);
    }

    // Method to randomly select a chunk object based on its rarity, height limit, and the number of objects already found
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
}

