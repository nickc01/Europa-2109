// These are the required using statements for the code
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

public class CaveGenerator : MonoBehaviour
{
    [SerializeField]
    private Map map; // Serialized field for the Map object

    [SerializeField]
    private int3 roomSizeMin; // Serialized field for the minimum size of a room

    [SerializeField]
    private int3 roomSizeMax; // Serialized field for the maximum size of a room

    [SerializeField]
    private float minDistanceBetweenRooms = 6; // Serialized field for the minimum distance between rooms

    [SerializeField]
    private float perlinSize = 10f; // Serialized field for the size of the Perlin noise

    [SerializeField]
    private float perlinThreshold = 0.5f; // Serialized field for the Perlin threshold
    private ConcurrentDictionary<Chunk, bool> loadedChunks = new ConcurrentDictionary<Chunk, bool>(); // Concurrent dictionary to keep track of loaded chunks
    private List<RegionCheck> RegionChecks = new List<RegionCheck>(); // List of region checks
    private ReaderWriterLockSlim regionLock = new ReaderWriterLockSlim(); // Reader writer lock slim object for synchronization

    // A class for storing region checks
    public class RegionCheck
    {
        public List<int3> chunksRequired; // List of required chunks
        public List<Chunk> chunksLoaded; // List of loaded chunks

        public Func<List<Chunk>, Task> callback; // Callback function
    }

    // Function for generating the cave
    private float CaveGenerationFunction(int3 chunkPosition)
    {
        if (chunkPosition.y > 5)
        {
            return 0;
        }
        else if (chunkPosition.y < 0)
        {
            return 0;
        }
        else if (all(chunkPosition == new int3(0, 5, 0)))
        {
            return 1;
        }
        else
        {
            return RandomFloatGenerator(chunkPosition);
        }
    }

    private void AddRegionCheck(int3 chunkPos, int3 bl, int3 tr, Func<List<Chunk>, Task> callback)
    {
        // Compute the bottom-left and top-right corners of the region to be checked.
        int3 bottomLeft = chunkPos - bl;
        int3 topRight = chunkPos + tr;

        // Create a list of all the chunks that are required in the region.
        List<int3> requiredChunks = new List<int3>();
        for (int x = bottomLeft.x; x <= topRight.x; x++)
        {
            for (int y = bottomLeft.y; y <= topRight.y; y++)
            {
                for (int z = bottomLeft.z; z <= topRight.z; z++)
                {
                    requiredChunks.Add(new int3(x, y, z));
                }
            }
        }

        // Find all the chunks that are currently loaded and required for this region check.
        List<Chunk> loadedChunksInRegion = loadedChunks.Where(kv => requiredChunks.Contains(kv.Key.Position)).Select(kv => kv.Key).ToList();

        // If all the required chunks are already loaded, invoke the callback with the loaded chunks.
        if (loadedChunksInRegion.Count == requiredChunks.Count)
        {
            callback(loadedChunksInRegion);
        }
        else
        {
            // If not all required chunks are loaded, add this region check to the list of pending checks.
            try
            {
                regionLock.EnterWriteLock();
                RegionChecks.Add(new RegionCheck
                {
                    callback = callback,
                    chunksLoaded = loadedChunksInRegion,
                    chunksRequired = requiredChunks
                });
            }
            finally
            {
                regionLock.ExitWriteLock();
            }
        }
    }

    private void CheckRegionAddition(Chunk newChunk)
    {
        // Add the new chunk to the dictionary of loaded chunks.
        loadedChunks.TryAdd(newChunk, true);
        try
        {
            List<RegionCheck> regionsToRemove = new List<RegionCheck>();

            // Acquire an upgradeable read lock on the list of pending region checks.
            regionLock.EnterUpgradeableReadLock();

            // Iterate over all pending region checks.
            for (int i = RegionChecks.Count - 1; i >= 0; i--)
            {
                RegionCheck region = RegionChecks[i];

                // If the new chunk is part of a region that is being checked, add it to the list of loaded chunks for that region.
                if (region.chunksRequired.Contains(newChunk.Position))
                {
                    region.chunksLoaded.Add(newChunk);

                    // If all the required chunks for the region are now loaded, remove the region check from the list and invoke the callback.
                    if (region.chunksRequired.Count == region.chunksLoaded.Count)
                    {
                        regionsToRemove.Add(region);
                        Task.Run(async () => await region.callback(region.chunksLoaded));
                    }
                }
            }

            // Remove all completed region checks from the list.
            if (regionsToRemove.Count > 0)
            {
                try
                {
                    regionLock.EnterWriteLock();
                    foreach (RegionCheck region in regionsToRemove)
                    {
                        RegionChecks.Remove(region);
                    }
                }
                finally
                {
                    regionLock.ExitWriteLock();
                }
            }
        }
        finally
        {
            regionLock.ExitUpgradeableReadLock();
        }
    }

    // Function for removing a loaded chunk from region checks
    private void CheckRegionRemove(Chunk newChunk)
    {
        loadedChunks.TryRemove(newChunk, out _); // Remove chunk from loadedChunks dictionary
        try
        {
            regionLock.EnterReadLock(); // Acquire read lock on regionLock
            for (int i = RegionChecks.Count - 1; i >= 0; i--) // Loop through RegionChecks list in reverse order
            {
                RegionCheck region = RegionChecks[i]; // Get current region

                if (region.chunksLoaded.Contains(newChunk)) // If the region contains the removed chunk
                {
                    region.chunksLoaded.Remove(newChunk); // Remove the chunk from the region's loaded chunks list
                }
            }
        }
        finally
        {
            regionLock.ExitReadLock(); // Release read lock on regionLock
        }
    }

    // Start function for initializing the CaveGenerator
    private void Start()
    {
        map.OnChunkLoad += Map_OnChunkLoad; // Add Map_OnChunkLoad as a callback for OnChunkLoad event of the Map object
        map.OnChunkLoad += CheckRegionAddition; // Add CheckRegionAddition as a callback for OnChunkLoad event of the Map object
        map.OnChunkUnload += CheckRegionRemove; // Add CheckRegionRemove as a callback for OnChunkUnload event of the Map object
    }

    // Callback function for the OnChunkLoad event of the Map object
    private void Map_OnChunkLoad(Chunk obj)
    {
        if (obj.NewlyGenerated) // If the chunk is newly generated
        {
            float perlinValue = CaveGenerationFunction(obj.Position); // Generate Perlin noise value for the chunk
            if (perlinValue >= perlinThreshold) // If the Perlin noise value is greater than or equal to the threshold
            {
                obj.KeepLoaded = true; // Keep the chunk loaded
                Task.Run(() => GenerateRoom(obj)); // Generate a room asynchronously for the chunk
            }
        }
    }

    private void GenerateRoom(Chunk chunk)
    {
        int regionAddition = 0;

        // Increase the region size for the central chunk
        if (all(chunk.Position == new int3(0, 5, 0)))
        {
            regionAddition = 2;
        }

        // Add the region to the list of checks
        AddRegionCheck(chunk.Position, new int3(2 + regionAddition), new int3(1 + regionAddition), async chunks =>
        {
            List<int3> destinations = new List<int3>();

            // Check each chunk in the region and add it to the list of destinations if it meets the threshold
            for (int i = 0; i < chunks.Count; i++)
            {
                Chunk chunk = chunks[i];
                chunk.KeepLoaded = true;
                float perlinValue = CaveGenerationFunction(chunk.Position);
                if (perlinValue >= perlinThreshold)
                {
                    destinations.Add(chunk.Position);
                }
            }

            int destCount = 0;

            // Adjust the destination count for the central chunk
            if (all(chunk.Position == new int3(0, 5, 0)))
            {
                destCount = -2;
            }

            List<Task> tasks = new List<Task>();

            // Draw tunnels to the chosen destinations
            while (true)
            {
                int3 randomDest = destinations[LimitToRange(RandomIntGenerator(chunk.Position, destCount), 0, destinations.Count)];
                tasks.Add(DrawTunnel(chunk.Position, randomDest, 4 * (int)length(chunk.Position - randomDest)));
                destCount++;
                if (destCount >= 2)
                {
                    break;
                }
            }

            // Wait for all tunnels to be drawn
            await Task.WhenAll(tasks);

            // Generate objects for each chunk in the region
            for (int i = 0; i < chunks.Count; i++)
            {
                Chunk chunk = chunks[i];
                await chunk.GenerateChunkObjects();
                chunk.KeepLoaded = false;
            }

        });

        // Use a sphere brush to generate a room
        map.UseSphereBrush(chunk.GetCentre(map), true, 1f, LimitToRange(RandomInt3Generator(chunk.Position, map.WorldSeed), roomSizeMax - 1, roomSizeMax));

        chunk.KeepLoaded = false;
    }

    // Function for drawing a tunnel between two chunks
    private async Task DrawTunnel(int3 sourceChunk, int3 destinationChunk, int steps)
    {

        List<Task> tasks = new List<Task>();
        float3 source = map.ChunkPosToWorldPos(sourceChunk) + (map.BoundsSize / 2f);
        float3 destination = map.ChunkPosToWorldPos(destinationChunk) + (map.BoundsSize / 2f);

        for (float i = 0; i <= steps; i++)
        {
            // Generates a random brush size using the source and destination chunk positions
            float3 brushSize = RandomFloat3Generator(sourceChunk + destinationChunk, (int)i);
            brushSize = Float3IntoRange(brushSize, new float3(1), new float3(2));

            // Uses the sphere brush to draw a shape in the map at a point between the source and destination
            map.UseSphereBrush(lerp(source, destination, i / steps), true, 1f, brushSize);

            // Calculates the world position for the current point between the source and destination
            float3 worldPos = lerp(source, destination, i / steps);

            // Converts the world position to a chunk position and checks if the chunk is loaded
            int3 chunkPos = map.WorldPosToChunkPos(worldPos);
            if (map.TryGetChunkAtChunkPos(chunkPos, out Chunk brushChunk))
            {
                // Adds a task for generating chunk objects to the list of tasks
                tasks.Add(brushChunk.GenerateChunkObjects());
            }
        }

        // Waits for all the tasks to finish
        await Task.WhenAll(tasks);
    }

    // Function for unloading a list of chunks
    private void UnloadChunks(List<Chunk> chunks)
    {
        chunks.AsParallel().ForAll(c => c.KeepLoaded = false);
        chunks.Clear();
    }

    // Function for drawing a room at a given position
    private async Task DrawRoom(float3 position, List<Chunk> chunks = null)
    {
        // Loads the chunks in the area around the position and optionally keeps them loaded if a list is provided
        await map.LoadChunksInArea(position, new int3(1), keepChunksLoaded: chunks != null, out_chunksInArea: chunks);
        // Generates a limited int3 using the position and uses it to draw a sphere brush in the map
        int3 size = GenerateLimitedInt3(position);
        map.UseSphereBrush(position, true, 1f, size);
    }

    public float RandomFloatGenerator(float3 position, int seedOffset = 0)
    {
        // Generates a random float based on the position and seed offset
        return RandomFloatGenerator((int3)position, seedOffset);
    }

    public float RandomFloatGenerator(int3 position, int seedOffset = 0)
    {
        // Generates a random integer using the position and seed offset, 
        // and then converts it to a float and scales it to [0,1] range
        int randomInt = RandomIntGenerator(position, seedOffset);
        return ((float)randomInt) / int.MaxValue;
    }

    public float3 RandomFloat3Generator(float3 position, int seedOffset = 0)
    {
        // Generates a random float3 based on the position and seed offset,
        // where each component is generated using the same position and seed offset
        return RandomFloat3Generator((int3)position, seedOffset);
    }

    public float3 RandomFloat3Generator(int3 position, int seedOffset = 0)
    {
        // Generates a random float3 where each component is a random float 
        // generated using the same position and seed offset
        return new float3(RandomFloatGenerator(position, seedOffset), RandomFloatGenerator(position, seedOffset), RandomFloatGenerator(position, seedOffset));
    }

    public int RandomIntGenerator(float3 position, int seedOffset = 0)
    {
        // Generates a random integer based on the position and seed offset
        return RandomIntGenerator((int3)position, seedOffset);
    }

    public int RandomIntGenerator(int3 position, int seedOffset = 0)
    {
        // Generates a unique hash code using the position and seed offset,
        // and then takes the absolute value of the hash code
        return abs(HashCode.Combine(position, map.WorldSeed + seedOffset));
    }

    public int3 RandomInt3Generator(float3 position, int seedOffset = 0)
    {
        // Generates a random int3 based on the position and seed offset,
        // where each component is generated using the same position and seed offset
        return RandomInt3Generator((int3)position, seedOffset);
    }

    public int3 RandomInt3Generator(int3 position, int seedOffset = 0)
    {
        // Generates a unique int3 using the position and seed offset,
        // where each component is a hash code generated using the same position and seed offset
        return abs(new int3(ConsistentHashGenerator.Combine(position.x, map.WorldSeed + seedOffset), ConsistentHashGenerator.Combine(position.y, map.WorldSeed + seedOffset), ConsistentHashGenerator.Combine(position.z, map.WorldSeed + seedOffset)));
    }

    public int3 LimitToRange(int3 value, int3 minInclusive, int3 maxExclusive)
    {
        // Limits the value of int3 to be within the range of minInclusive and maxExclusive
        return (value % (maxExclusive - minInclusive)) + minInclusive;
    }

    public int LimitToRange(int value, int minInclusive, int maxExclusive)
    {
        // Limits the value of int to be within the range of minInclusive and maxExclusive
        return (value % (maxExclusive - minInclusive)) + minInclusive;
    }

    public int3 GenerateLimitedInt3(float3 position, int seedOffset = 0)
    {
        // Generates a limited int3 based on the position and seed offset,
        // where each component is a random integer within the range of roomSizeMin and roomSizeMax
        return GenerateLimitedInt3((int3)position, seedOffset);
    }

    // Function for generating a random int3 within the range of roomSizeMin and roomSizeMax
    // The generated int3 is based on the provided position and seedOffset
    public int3 GenerateLimitedInt3(int3 position, int seedOffset = 0)
    {
        return LimitToRange(RandomInt3Generator(position, seedOffset), roomSizeMin, roomSizeMax);
    }

    // Function for mapping a float value into a specified range
    public float FloatIntoRange(float value, float min, float max)
    {
        return (value * (max - min)) + min;
    }

    // Function for mapping a float3 value into a specified range
    public float3 Float3IntoRange(float3 value, float3 min, float3 max)
    {
        return (value * (max - min)) + min;
    }

}
