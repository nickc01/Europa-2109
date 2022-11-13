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
    private Map map;

    [SerializeField]
    private int3 roomSizeMin;

    [SerializeField]
    private int3 roomSizeMax;

    [SerializeField]
    private int2 neighboringRoomCountRange = new int2(1, 5);

    [SerializeField]
    private float2 neighboringRoomDistanceRange = new float2(10, 20);

    [SerializeField]
    private float minDistanceBetweenRooms = 6;

    [SerializeField]
    private float perlinSize = 10f;

    [SerializeField]
    private float perlinThreshold = 0.5f;
    private float3 startingPos;
    private ConcurrentDictionary<Chunk, bool> loadedChunks = new ConcurrentDictionary<Chunk, bool>();
    private List<int3> generatedRoomPositions = new List<int3>();
    private List<RegionCheck> RegionChecks = new List<RegionCheck>();
    private ReaderWriterLockSlim regionLock = new ReaderWriterLockSlim();



    public class RegionCheck
    {
        public List<int3> chunksRequired;
        public List<Chunk> chunksLoaded;

        public Func<List<Chunk>, Task> callback;
    }

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
        int3 bottomLeft = chunkPos - bl;
        int3 topRight = chunkPos + tr;

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

        List<Chunk> loadedChunksInRegion = loadedChunks.Where(kv => requiredChunks.Contains(kv.Key.Position)).Select(kv => kv.Key).ToList();

        if (loadedChunksInRegion.Count == requiredChunks.Count)
        {
            callback(loadedChunksInRegion);
        }
        else
        {
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
        loadedChunks.TryAdd(newChunk, true);
        try
        {
            List<RegionCheck> regionsToRemove = new List<RegionCheck>();
            regionLock.EnterUpgradeableReadLock();
            for (int i = RegionChecks.Count - 1; i >= 0; i--)
            {
                RegionCheck region = RegionChecks[i];

                if (region.chunksRequired.Contains(newChunk.Position))
                {
                    region.chunksLoaded.Add(newChunk);
                    if (region.chunksRequired.Count == region.chunksLoaded.Count)
                    {
                        regionsToRemove.Add(region);
                        Task.Run(async () => await region.callback(region.chunksLoaded));
                    }
                }
            }

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

    private void CheckRegionRemove(Chunk newChunk)
    {
        loadedChunks.TryRemove(newChunk, out _);
        try
        {
            regionLock.EnterReadLock();
            for (int i = RegionChecks.Count - 1; i >= 0; i--)
            {
                RegionCheck region = RegionChecks[i];

                if (region.chunksLoaded.Contains(newChunk))
                {
                    region.chunksLoaded.Remove(newChunk);
                }
            }
        }
        finally
        {
            regionLock.ExitReadLock();
        }
    }

    private void Start()
    {
        startingPos = map.viewer.transform.position;
        map.OnChunkLoad += Map_OnChunkLoad;
        map.OnChunkLoad += CheckRegionAddition;
        map.OnChunkUnload += CheckRegionRemove;
    }

    private void Map_OnChunkLoad(Chunk obj)
    {
        if (obj.NewlyGenerated)
        {
            float perlinValue = CaveGenerationFunction(obj.Position);
            if (perlinValue >= perlinThreshold)
            {
                obj.KeepLoaded = true;
                Task.Run(async () => await GenerateRoom(obj, perlinValue));
            }
            else
            {
                //Task.Run(obj.GenerateChunkObjects);
            }
        }
    }

    private async Task GenerateRoom(Chunk chunk, float perlinValue)
    {
        int regionAddition = 0;

        if (all(chunk.Position == new int3(0, 5, 0)))
        {
            regionAddition = 2;
        }


        AddRegionCheck(chunk.Position, new int3(2 + regionAddition), new int3(1 + regionAddition), async chunks =>
        {
            List<int3> destinations = new List<int3>();


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
            if (all(chunk.Position == new int3(0, 5, 0)))
            {
                destCount = -2;
            }

            List<Task> tasks = new List<Task>();

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


            await Task.WhenAll(tasks);

            for (int i = 0; i < chunks.Count; i++)
            {
                Chunk chunk = chunks[i];

                await chunk.GenerateChunkObjects();

                chunk.KeepLoaded = false;
            }

        });

        map.UseSphereBrush(chunk.GetCentre(map), true, 1f, LimitToRange(RandomInt3Generator(chunk.Position, map.WorldSeed), roomSizeMax - 1, roomSizeMax));

        chunk.KeepLoaded = false;

    }

    private async Task DrawTunnel(int3 sourceChunk, int3 destinationChunk, int steps)
    {

        List<Task> tasks = new List<Task>();
        float3 source = map.ChunkPosToWorldPos(sourceChunk) + (map.BoundsSize / 2f);
        float3 destination = map.ChunkPosToWorldPos(destinationChunk) + (map.BoundsSize / 2f);

        for (float i = 0; i <= steps; i++)
        {
            float3 brushSize = RandomFloat3Generator(sourceChunk + destinationChunk, (int)i);
            brushSize = Float3IntoRange(brushSize, new float3(1), new float3(2));
            map.UseSphereBrush(lerp(source, destination, i / steps), true, 1f, brushSize);

            float3 worldPos = lerp(source, destination, i / steps);

            int3 chunkPos = map.WorldPosToChunkPos(worldPos);

            if (map.TryGetChunkAtChunkPos(chunkPos, out Chunk brushChunk))
            {
                tasks.Add(brushChunk.GenerateChunkObjects());
            }

        }

        await Task.WhenAll(tasks);
    }

    private void UnloadChunks(List<Chunk> chunks)
    {
        chunks.AsParallel().ForAll(c => c.KeepLoaded = false);
        chunks.Clear();
    }

    private async Task DrawRoom(float3 position, List<Chunk> chunks = null)
    {
        await map.LoadChunksInArea(position, new int3(1), keepChunksLoaded: chunks != null, out_chunksInArea: chunks);
        int3 size = GenerateLimitedInt3(position);
        map.UseSphereBrush(position, true, 1f, size);
    }

    public float RandomFloatGenerator(float3 position, int seedOffset = 0)
    {
        return RandomFloatGenerator((int3)position, seedOffset);
    }

    public float RandomFloatGenerator(int3 position, int seedOffset = 0)
    {
        int randomInt = RandomIntGenerator(position, seedOffset);
        return ((float)randomInt) / int.MaxValue;
    }

    public float3 RandomFloat3Generator(float3 position, int seedOffset = 0)
    {
        return RandomFloat3Generator((int3)position, seedOffset);
    }

    public float3 RandomFloat3Generator(int3 position, int seedOffset = 0)
    {
        return new float3(RandomFloatGenerator(position, seedOffset), RandomFloatGenerator(position, seedOffset), RandomFloatGenerator(position, seedOffset));

    }


    public int RandomIntGenerator(float3 position, int seedOffset = 0)
    {
        return RandomIntGenerator((int3)position, seedOffset);
    }

    public int RandomIntGenerator(int3 position, int seedOffset = 0)
    {
        return abs(HashCode.Combine(position, map.WorldSeed + seedOffset));
    }

    public int3 RandomInt3Generator(float3 position, int seedOffset = 0)
    {
        return RandomInt3Generator((int3)position, seedOffset);
    }

    public int3 RandomInt3Generator(int3 position, int seedOffset = 0)
    {
        return abs(new int3(ConsistentHashGenerator.Combine(position.x, map.WorldSeed + seedOffset), ConsistentHashGenerator.Combine(position.y, map.WorldSeed + seedOffset), ConsistentHashGenerator.Combine(position.z, map.WorldSeed + seedOffset)));
    }

    public int3 LimitToRange(int3 value, int3 minInclusive, int3 maxExclusive)
    {
        return (value % (maxExclusive - minInclusive)) + minInclusive;
    }

    public int LimitToRange(int value, int minInclusive, int maxExclusive)
    {
        return (value % (maxExclusive - minInclusive)) + minInclusive;
    }

    public int3 GenerateLimitedInt3(float3 position, int seedOffset = 0)
    {
        return GenerateLimitedInt3((int3)position, seedOffset);
    }

    public int3 GenerateLimitedInt3(int3 position, int seedOffset = 0)
    {
        return LimitToRange(RandomInt3Generator(position, seedOffset), roomSizeMin, roomSizeMax);
    }

    public float FloatIntoRange(float value, float min, float max)
    {
        return (value * (max - min)) + min;
    }

    public float3 Float3IntoRange(float3 value, float3 min, float3 max)
    {
        return (value * (max - min)) + min;
    }

}
