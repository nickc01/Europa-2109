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


        //RegionChecks.Add()
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
            //List<RegionCheck> regionsToAdd = new List<RegionCheck>();
            regionLock.EnterReadLock();
            for (int i = RegionChecks.Count - 1; i >= 0; i--)
            {
                RegionCheck region = RegionChecks[i];

                if (region.chunksLoaded.Contains(newChunk))
                {
                    region.chunksLoaded.Remove(newChunk);
                    /*if (region.chunksRequired.Count == region.chunksLoaded.Count)
                    {
                        regionsToRemove.Add(region);
                        region.callback(region.chunksLoaded);
                    }*/
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
        //Task.Run(Generator);
    }

    private void Map_OnChunkLoad(Chunk obj)
    {
        //Debug.Log("Chunk Loaded = " + obj.Position);
        if (obj.NewlyGenerated)
        {
            //var perlinValue = Perlin.Noise((float3)obj.Position / perlinSize);
            //var perlinValue = RandomFloatGenerator(obj.Position,map.WorldSeed);
            float perlinValue = CaveGenerationFunction(obj.Position);
            //Debug.Log($"Perlin Value {obj.Position} = " + perlinValue);
            if (perlinValue >= perlinThreshold)
            {
                obj.KeepLoaded = true;
                Task.Run(async () => await GenerateRoom(obj, perlinValue));
            }
            else
            {
                Task.Run(obj.GenerateChunkObjects);
            }
        }
    }

    private async Task GenerateRoom(Chunk chunk, float perlinValue)
    {
        //List<Chunk> loadedChunks = new List<Chunk>();
        //await map.LoadChunksInArea(chunk.GetCentre(map),new Unity.Mathematics.int3(1),keepChunksLoaded: true,out_chunksInArea: loadedChunks);

        AddRegionCheck(chunk.Position, new int3(2), new int3(1), async chunks =>
        {
            //Debug.Log($"Region Loaded for {chunk.Position}");

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

            //List<Task> tasks = new List<Task>();

            int destCount = 0;

            while (true)
            {
                int3 randomDest = destinations[LimitToRange(RandomIntGenerator(chunk.Position, destCount), 0, destinations.Count)];
                //tasks.AddRange();
                DrawTunnel(chunk.Position, randomDest, 4 * (int)length(chunk.Position - randomDest));
                destCount++;
                if (destCount >= 2)
                {
                    break;
                }
            }

            //Debug.Log("Drawn Tunnels");
            /*foreach (var destination in destinations)
            {
                tasks.AddRange(DrawTunnel(chunk.Position, destination, 10));
                destCount++;
                if (destCount >= 2)
                {
                    break;
                }
            }*/

            //await Task.WhenAll(tasks);

            for (int i = 0; i < chunks.Count; i++)
            {
                Chunk chunk = chunks[i];

                await chunk.GenerateChunkObjects();

                chunk.KeepLoaded = false;
            }

            /*List<Task> tasks = new List<Task>();
            foreach (var destination in destinations)
            {
                tasks.Add(DrawTunnel(chunk.Position,destination,10));
            }
            await Task.WhenAll(tasks);*/
        });

        map.UseSphereBrush(chunk.GetCentre(map), true, 1f, GenerateLimitedInt3(chunk.Position, map.WorldSeed));

        chunk.KeepLoaded = false;

        //var neighbors = GenerateNeighboringRoomLocations((int3)startingPos);

        //UnloadChunks(loadedChunks);
    }

    private void DrawTunnel(int3 sourceChunk, int3 destinationChunk, int steps)
    {
        float3 source = map.ChunkPosToWorldPos(sourceChunk) + (map.BoundsSize / 2f);
        float3 destination = map.ChunkPosToWorldPos(destinationChunk) + (map.BoundsSize / 2f);

        for (float i = 0; i <= steps; i++)
        {
            //Debug.DrawLine(source, lerp(source, destination, i / steps),Color.blue,10f);
            float3 brushSize = RandomFloat3Generator(sourceChunk + destinationChunk, (int)i);
            brushSize = Float3IntoRange(brushSize, new float3(1), new float3(2));
            map.UseSphereBrush(lerp(source, destination, i / steps), true, 1f, brushSize);
        }

        //map.UseSphereBrush(lerp(source, destination, i / steps), true, 1f, new float3(2));

        /*Parallel.For(0,steps,i =>
        {
            
        });*/
    }

    /*async Task Generator()
    {
        try
        {
            generatedRoomPositions.Add((int3)startingPos);
            Debug.Log("Drawing");
            await DrawRoom(startingPos, loadedChunks);

            Debug.Log("Drawing Done");
            var testRooms = GenerateNeighboringRoomLocations((int3)startingPos);

            Debug.Log("TEST ROOMS MADE = " + testRooms.Count);
            for (int i = 0; i < testRooms.Count; i++)
            {
                Debug.Log($"Room {i} = {testRooms[i]}");
                Debug.DrawLine(startingPos, (float3)testRooms[i], Color.red, 10f);
            }

        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }

        //Debug.Log("Random Float = " + RandomFloatGenerator(startingPos,0));
    }*/

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

        /*var randomInt = RandomIntGenerator(position, seedOffset);
        return ((float)randomInt) / int.MaxValue;*/
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

    /*
     * double fRand(double fMin, double fMax)
        {
            double f = (double)rand() / RAND_MAX;
            return fMin + f * (fMax - fMin);
        }
     */

    /*public List<int3> GenerateNeighboringRoomLocations(int3 sourcePosition)
    {
        var roomCount = LimitToRange(RandomIntGenerator(sourcePosition), neighboringRoomCountRange.x, neighboringRoomCountRange.y);

        Debug.Log("Room Count = " + roomCount);

        List<int3> newRooms = new List<int3>();

        //int seedOffset = 0;

        for (int i = 0; i < roomCount; i++)
        {
            //Debug.Log("Generating Room = " + i);
            for (int seedOffset = 0; seedOffset < 5; seedOffset++)
            {
                //Debug.Log("Seed = " + seedOffset);
                var roomDistance = FloatIntoRange(RandomFloatGenerator(sourcePosition, i * seedOffset), neighboringRoomDistanceRange.x, neighboringRoomDistanceRange.y);

                var direction = normalize(RandomFloat3Generator(sourcePosition, i * seedOffset));

                var newPosition = (int3)(sourcePosition + (direction * roomDistance));

                //Debug.Log("A");
                Debug.Log($"{i}:{seedOffset} = {newPosition}");

                float nearestDistance;
                if (newRooms.Count > 0)
                {
                    nearestDistance = min(generatedRoomPositions.AsParallel().Min(r => distance(r, newPosition)), newRooms.AsParallel().Min(r => distance(r, newPosition)));
                }
                else
                {
                    nearestDistance = generatedRoomPositions.AsParallel().Min(r => distance(r, newPosition));
                }
                //HPCsharp.ParallelAlgorithm.MinSsePar();
                //var nearestDistance = generatedRoomPositions.Min(r => distance(r, newPosition));
                //Debug.Log("B");
                //Debug.Log("Nearest Distance = " + nearestDistance);
                if (nearestDistance >= minDistanceBetweenRooms)
                {
                    newRooms.Add(newPosition);
                    break;
                }
            }
            //HPCsharp.Algorithm.MinHpc(generatedRoomPositions,)
            //HPCsharp.Algorithm.SortRadix(generatedRoomPositions,room => (uint)ceil(distance(room, newPosition)));

            //HPCsharp.Algorithm.SortRadix(new int[4], i => 1);

            //TODO - Create a Random Generator for floats and create a function for generating a random direction vector

            //TODO - Use the random direction vector and the random distance value to get the position of the new room

            //TODO - Check to make sure that the room doesn't collide with any other rooms already generated. If it does, increase the seedoffset and try again. If if fails 5 times, then maybe skip that room.

        }


        return newRooms;
    }*/
}
