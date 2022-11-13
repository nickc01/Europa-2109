using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

public class Chunk : MonoBehaviour
{
    private static string PersistentDataPath = null;

    public enum LoadingState
    {
        Unloaded,
        UnloadedCached,
        Loading,
        Loaded,
        Unloading
    }

    [Serializable]
    public struct ChunkObjectInfo
    {
        public float3 worldpos;
        public float3 lookAtPoint;
        public float3 localScale;

        public int ObjectID;
    }

    [Serializable]
    private class SaveHolder<T>
    {
        public T Value;
    }


    public class DistanceSorter : IComparer<Chunk>
    {
        private Comparer<float> floatComparer;

        public int3 viewerChunkPos;
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

        private float DistanceToViewer(Chunk chunk)
        {
            return length(viewerChunkPos - chunk.Position);
        }
    }

    public class DistanceSorterInt3 : IComparer<int3>
    {
        private Comparer<float> floatComparer;

        public int3 viewerChunkPos;
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

        private float DistanceToViewer(int3 chunk)
        {
            return length(viewerChunkPos - chunk);
        }
    }

    public bool KeepLoaded { get; set; } = false;

    public int3 Position;
    public Map SourceMap { get; set; }

    public Mesh Mesh { get; private set; }
    public MeshFilter MainFilter { get; private set; }
    public MeshRenderer MainRenderer { get; private set; }
    public MeshCollider MainCollider { get; private set; }

    public float[] Points { get; set; }

    public List<GameObject> loadedChunkObjects = new List<GameObject>();
    public List<ChunkObjectInfo> chunkObjectInfo;
    public bool chunkObjectsGenerated = false;

    public bool PointsLoaded = false;
    public bool NewlyGenerated = true;
    public float3 GetCentre(Map map)
    {
        return new float3(Position) * map.BoundsSize;
    }

    private void Awake()
    {
        if (PersistentDataPath == null)
        {
            PersistentDataPath = Application.persistentDataPath;
        }
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

        Mesh = MainFilter.sharedMesh;
        if (Mesh == null)
        {
            Mesh = new Mesh
            {
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            };
            MainFilter.sharedMesh = Mesh;
        }

        MainCollider = GetComponent<MeshCollider>();

        if (MainCollider == null)
        {
            MainCollider = gameObject.AddComponent<MeshCollider>();
        }
    }

    public Map.ChunkGenerationParameters ChunkGenerationParameters()
    {
        return new Map.ChunkGenerationParameters
        {
            IsoOffsetByHeight = -0.2f,
            IsoOffset = -2f,
            IsoOffsetByHeightAbs = 0.4f
        };

    }

    public async Task<bool> Init(Map map, bool cached)
    {
        return await ChunkStart(cached);
    }

    public async Task Uninit()
    {
        SourceMap.objectsToDestroy.Enqueue(loadedChunkObjects);
        loadedChunkObjects = new List<GameObject>();
        NewlyGenerated = true;
        await ChunkEnd();
        PointsLoaded = false;
    }

    private async Task<bool> ChunkStart(bool cached)
    {

        string folder = PersistentDataPath + $"/{SourceMap.WorldName}";
        await Task.Run(() => ReadPointData(folder));
        return true;

    }

    private async Task<bool> ChunkEnd()
    {
        string folder = PersistentDataPath + $"/{SourceMap.WorldName}";
        await Task.Run(() => WritePointData(folder));
        return true;
    }

    private void WritePointData(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
        Span<byte> bytes = MemoryMarshal.AsBytes(Points.AsSpan());

        using FileStream file = System.IO.File.OpenWrite(folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt");
        using System.IO.Compression.GZipStream gzipWriter = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Compress);
        gzipWriter.Write(bytes);
        gzipWriter.Close();
        file.Close();
    }

    private void ReadPointData(string folder)
    {
        if (Points == null)
        {
            Points = new float[SourceMap.PointsPerChunk];
        }

        Span<byte> bytes = MemoryMarshal.AsBytes(Points.AsSpan());

        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string filePath = folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt";

        if (!System.IO.File.Exists(filePath))
        {
            return;
        }

        using FileStream file = System.IO.File.OpenRead(folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt");
        using System.IO.Compression.GZipStream gzipReader = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        gzipReader.Read(bytes);
        gzipReader.Close();
        file.Close();
        PointsLoaded = true;
        NewlyGenerated = false;
    }

    public void SaveSynchronously(string folder)
    {
        WritePointData(folder + $"/{SourceMap.WorldName}");
    }


    public void OnMeshUpdate()
    {
        MainRenderer.enabled = true;
    }

    private float3 randomInsideUnitSphere(ref Unity.Mathematics.Random randomizer)
    {
        return forward(randomizer.NextQuaternionRotation());
    }

    public async Task GenerateChunkObjects()
    {
        try
        {
            if (chunkObjectsGenerated)
            {
                return;
            }

            //Debug.Log("GENERATING CHUNK OBJECTS");

            string folder = PersistentDataPath + $"/{SourceMap.WorldName}";

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            string filePath = $"{folder}/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}_EXTRA_DATA.txt";

            if (File.Exists(filePath))
            {
                string stringData = await File.ReadAllTextAsync(filePath);

                chunkObjectInfo = JsonUtility.FromJson<SaveHolder<List<ChunkObjectInfo>>>(stringData).Value;
            }
            else
            {
                if (chunkObjectInfo == null)
                {
                    chunkObjectInfo = new List<ChunkObjectInfo>();
                }
                else
                {
                    chunkObjectInfo.Clear();
                }

                float3 center = GetCentre(SourceMap);

                Unity.Mathematics.Random randomizer = Unity.Mathematics.Random.CreateFromIndex((uint)abs(Position.GetHashCode()));

                int times;
                if (all(Position == new int3(0, 5, 0)))
                {
                    times = 0;
                }
                else if (Position.y <= 0f)
                {
                    times = randomizer.NextInt(3, 20);
                }
                else
                {
                    times = randomizer.NextInt(3, 4);
                }

                for (int i = 0; i < times; i++)
                {
                    float3 randomDirection = randomInsideUnitSphere(ref randomizer);

                    if (ChunkObjectsDictionary.PickRandomEntry(out ChunkObjectsDictionary.Entry entry, Position))
                    {
                        if (SourceMap.FireRayParallel(new Ray(center, randomDirection), out float3 hit, 15f) && lengthsq(hit - center) > 2f)
                        {
                            chunkObjectInfo.Add(new ChunkObjectInfo
                            {
                                localScale = float3(float.PositiveInfinity),
                                ObjectID = entry.ID,
                                worldpos = hit,
                                lookAtPoint = center
                            });
                        }
                    }
                }
            }

            chunkObjectsGenerated = true;
            SourceMap.chunksWithObjectsToSpawn.Enqueue(this);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }
}