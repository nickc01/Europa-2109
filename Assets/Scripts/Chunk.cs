// import necessary libraries
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

// define the Chunk class that inherits from MonoBehaviour
public class Chunk : MonoBehaviour
{
    // declare a static string for storing the path of the persistent data
    private static string PersistentDataPath = null;

    // define an enum for representing the different loading states of a chunk
    public enum LoadingState
    {
        Unloaded,
        UnloadedCached,
        Loading,
        Loaded,
        Unloading
    }

    // define a struct for storing information about objects in the chunk
    [Serializable]
    public struct ChunkObjectInfo
    {
        public float3 worldpos;
        public float3 lookAtPoint;
        public float3 localScale;

        public int ObjectID;
    }

    // define a class for storing a generic value
    [Serializable]
    private class SaveHolder<T>
    {
        public T Value;
    }

    // define a class for sorting chunks by distance from a viewer
    public class DistanceSorter : IComparer<Chunk>
    {
        private Comparer<float> floatComparer;

        public int3 viewerChunkPos;

        // compare the distances of two chunks from the viewer and return the result
        public int Compare(Chunk x, Chunk y)
        {
            if (floatComparer == null)
            {
                floatComparer = Comparer<float>.Default;
            }

            if (x == null)
            {
                return 1;
            }
            else if (y == null)
            {
                return -1;
            }
            else if (x == null && y == null)
            {
                return 0;
            }

            return floatComparer.Compare(DistanceToViewer(y), DistanceToViewer(x));
        }

        // calculate the distance from the viewer to a chunk
        private float DistanceToViewer(Chunk chunk)
        {
            return length(viewerChunkPos - chunk.Position);
        }
    }

    // define a class for sorting int3 positions by distance from a viewer
    public class DistanceSorterInt3 : IComparer<int3>
    {
        private Comparer<float> floatComparer;

        public int3 viewerChunkPos;

        // compare the distances of two int3 positions from the viewer and return the result
        public int Compare(int3 x, int3 y)
        {
            if (floatComparer == null)
            {
                floatComparer = Comparer<float>.Default;
            }

            if (x.x == int.MaxValue)
            {
                return 1;
            }
            else if (y.y == int.MaxValue)
            {
                return -1;
            }
            else if (x.x == int.MaxValue && y.y == int.MaxValue)
            {
                return 0;
            }

            return floatComparer.Compare(DistanceToViewer(y), DistanceToViewer(x));
        }

        // calculate the distance from the viewer to an int3 position
        private float DistanceToViewer(int3 chunk)
        {
            return length(viewerChunkPos - chunk);
        }
    }

    // declare a public property for keeping the chunk loaded
    public bool KeepLoaded { get; set; } = false;

    // declare public variables for storing the chunk's position and source map
    public int3 Position;
    public Map SourceMap { get; set; }

    // declare public properties for accessing the chunk's mesh, mesh filter, mesh renderer, and mesh collider
    public Mesh Mesh { get; private set; }
    public MeshFilter MainFilter { get; private set; }
    public MeshRenderer MainRenderer { get; private set; }
    public MeshCollider MainCollider { get; private set; }

    //A list of all the points within the chunk
    public float[] Points { get; set; }

    // List of loaded chunk objects and chunk object info, with a flag to indicate if the objects have been generated
    public List<GameObject> loadedChunkObjects = new List<GameObject>();
    public List<ChunkObjectInfo> chunkObjectInfo;
    public bool chunkObjectsGenerated = false;

    // Flags to indicate if the points have been loaded and if the chunk has been newly generated
    public bool PointsLoaded = false;
    public bool NewlyGenerated = true;

    // Method to get the centre of the chunk, based on the map bounds size
    public float3 GetCentre(Map map)
    {
        return new float3(Position) * map.BoundsSize;
    }

    // Awake method to set up the required components of the chunk game object
    private void Awake()
    {
        // Setting up the persistent data path
        if (PersistentDataPath == null)
        {
            PersistentDataPath = Application.persistentDataPath;
        }

        // Getting or adding the mesh filter and renderer components
        MainFilter = GetComponent<MeshFilter>();
        MainRenderer = GetComponent<MeshRenderer>();

        if (MainFilter == null)
        {
            MainFilter = gameObject.AddComponent<MeshFilter>();
        }

        if (MainRenderer == null)
        {
            MainRenderer = gameObject.AddComponent<MeshRenderer>();
        }

        // Setting up the mesh
        Mesh = MainFilter.sharedMesh;
        if (Mesh == null)
        {
            Mesh = new Mesh
            {
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            };
            MainFilter.sharedMesh = Mesh;
        }

        // Getting or adding the mesh collider component
        MainCollider = GetComponent<MeshCollider>();

        if (MainCollider == null)
        {
            MainCollider = gameObject.AddComponent<MeshCollider>();
        }
    }

    // Method to get the chunk generation parameters for the map
    public Map.ChunkGenerationParameters ChunkGenerationParameters()
    {
        return new Map.ChunkGenerationParameters
        {
            IsoOffsetByHeight = -0.2f,
            IsoOffset = -2f,
            IsoOffsetByHeightAbs = 0.4f
        };
    }

    // Initialization method for the Chunk class that loads point data for the chunk
    public async Task<bool> Init(Map map, bool cached)
    {
        // Calls the ChunkStart method to load point data asynchronously and return a bool indicating success or failure
        return await ChunkStart(cached);
    }

    // Method to unload the chunk and clean up any loaded objects
    public async Task Uninit()
    {
        // Enqueues loadedChunkObjects for destruction and resets it
        SourceMap.objectsToDestroy.Enqueue(loadedChunkObjects);
        loadedChunkObjects = new List<GameObject>();

        // Sets NewlyGenerated flag to true and calls ChunkEnd method to write point data asynchronously
        NewlyGenerated = true;
        await ChunkEnd();

        // Sets PointsLoaded flag to false
        PointsLoaded = false;
    }

    // Private method to load point data for the chunk asynchronously
    private async Task<bool> ChunkStart(bool cached)
    {
        // Constructs the path to the folder containing point data
        string folder = PersistentDataPath + $"/{SourceMap.WorldName}";

        // Loads point data asynchronously using the ReadPointData method and returns a bool indicating success or failure
        await Task.Run(() => ReadPointData(folder));
        return true;
    }

    // Private method to write point data for the chunk asynchronously
    private async Task<bool> ChunkEnd()
    {
        // Constructs the path to the folder containing point data
        string folder = PersistentDataPath + $"/{SourceMap.WorldName}";

        // Writes point data asynchronously using the WritePointData method and returns a bool indicating success or failure
        await Task.Run(() => WritePointData(folder));
        return true;
    }

    // Private method to write point data for the chunk to a file
    private void WritePointData(string folder)
    {
        // Creates the folder if it doesn't exist
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        // Converts the Points array to a span of bytes
        Span<byte> bytes = MemoryMarshal.AsBytes(Points.AsSpan());

        // Opens a file stream and a GZip stream for writing, then writes the bytes to the file
        using FileStream file = System.IO.File.OpenWrite(folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt");
        using System.IO.Compression.GZipStream gzipWriter = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Compress);
        gzipWriter.Write(bytes);

        // Closes the streams
        gzipWriter.Close();
        file.Close();
    }

    // Reads point data from a file in the specified folder
    private void ReadPointData(string folder)
    {
        // Initialize the Points array if it hasn't been already
        if (Points == null)
        {
            Points = new float[SourceMap.PointsPerChunk];
        }

        // Get a byte span from the Points array
        Span<byte> bytes = MemoryMarshal.AsBytes(Points.AsSpan());

        // Create the directory if it doesn't exist
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        // Construct the file path for the chunk
        string filePath = folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt";

        // If the file doesn't exist, return without doing anything
        if (!System.IO.File.Exists(filePath))
        {
            return;
        }

        // Open the file and create a GZipStream to decompress the data
        using FileStream file = System.IO.File.OpenRead(folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt");
        using System.IO.Compression.GZipStream gzipReader = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);

        // Read the decompressed bytes into the Points array
        gzipReader.Read(bytes);

        // Close the streams
        gzipReader.Close();
        file.Close();

        // Mark the points as loaded and newly generated as false
        PointsLoaded = true;
        NewlyGenerated = false;
    }

    // Saves the chunk's data synchronously
    public void SaveSynchronously(string folder)
    {
        WritePointData(folder + $"/{SourceMap.WorldName}");
    }

    // Callback method for when the mesh is updated
    public void OnMeshUpdate()
    {
        MainRenderer.enabled = true;
    }

    // Returns a random point inside the unit sphere
    private float3 randomInsideUnitSphere(ref Unity.Mathematics.Random randomizer)
    {
        return forward(randomizer.NextQuaternionRotation());
    }

    //Generates objects inside of a chunk
    public async Task GenerateChunkObjects()
    {
        try
        {
            if (chunkObjectsGenerated) // Check if the chunk objects have already been generated
            {
                return;
            }

            string folder = PersistentDataPath + $"/{SourceMap.WorldName}"; // Create a folder with the world name

            if (!Directory.Exists(folder)) // If the folder doesn't exist
            {
                Directory.CreateDirectory(folder); // Create it
            }

            string filePath = $"{folder}/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}_EXTRA_DATA.txt"; // Create a file path for extra data

            if (File.Exists(filePath)) // If the file exists
            {
                string stringData = await File.ReadAllTextAsync(filePath); // Read the data from the file

                chunkObjectInfo = JsonUtility.FromJson<SaveHolder<List<ChunkObjectInfo>>>(stringData).Value; // Deserialize the data and set the chunk object info
            }
            else // If the file doesn't exist
            {
                if (chunkObjectInfo == null) // If the chunk object info is null
                {
                    chunkObjectInfo = new List<ChunkObjectInfo>(); // Create a new list
                }
                else // If the chunk object info is not null
                {
                    chunkObjectInfo.Clear(); // Clear the list
                }

                float3 center = GetCentre(SourceMap); // Get the center of the source map

                Unity.Mathematics.Random randomizer = Unity.Mathematics.Random.CreateFromIndex((uint)abs(Position.GetHashCode())); // Create a new randomizer based on the hash code of the position

                int times; // Define a variable for the number of times to loop
                if (all(Position == new int3(0, 5, 0))) // If the position is equal to (0, 5, 0)
                {
                    times = 0; // Set times to 0
                }
                else if (Position.y <= 0f) // If the y position is less than or equal to 0
                {
                    times = randomizer.NextInt(3, 20); // Set times to a random number between 3 and 20
                }
                else // If the y position is greater than 0
                {
                    times = randomizer.NextInt(2, 4); // Set times to a random number between 2 and 4
                }

                for (int i = 0; i < times; i++) // Loop times number of times
                {
                    float3 randomDirection = randomInsideUnitSphere(ref randomizer); // Generate a random direction within a unit sphere

                    if (ChunkObjectsDictionary.PickRandomEntry(out ChunkObjectsDictionary.Entry entry, Position)) // Pick a random chunk object from the dictionary for this chunk
                    {
                        if (SourceMap.FireRayParallel(new Ray(center, randomDirection), out float3 hit, 15f) && lengthsq(hit - center) > 2f) // Fire a ray from the center in the random direction, if it hits something and the distance is greater than 2
                        {
                            chunkObjectInfo.Add(new ChunkObjectInfo // Add the chunk object info to the list
                            {
                                localScale = float3(float.PositiveInfinity), // Set the local scale to infinity
                                ObjectID = entry.ID, // Set the object ID to the ID of the picked entry
                                worldpos = hit, // Set the world position to the hit point
                                lookAtPoint = center // Set the look at point to the center of the source map
                            });
                        }
                    }
                }
            }

            //The chunk objects have been generated for this chunk
            chunkObjectsGenerated = true;

            //Queue this chunk so that the objects can be spawned
            SourceMap.chunksWithObjectsToSpawn.Enqueue(this);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }
}