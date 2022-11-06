using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using System.Collections.Concurrent;

public class Chunk : MonoBehaviour
{
    static string PersistentDataPath = null;

    public static ConcurrentDictionary<FileStream,bool> openStreams = new ConcurrentDictionary<FileStream,bool>();

    public enum LoadingState
    {
        Unloaded,
        UnloadedCached,
        Loading,
        Loaded,
        Unloading
    }

    /// <summary>
    /// Sorts chunks based on how close they are to the viewer
    /// </summary>
    public class DistanceSorter : IComparer<Chunk>
    {
        Comparer<float> floatComparer;

        //public Transform Viewer;
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
            return floatComparer.Compare(DistanceToViewer(y),DistanceToViewer(x));
        }

        float DistanceToViewer(Chunk chunk)
        {
            //return Vector3.Distance(Viewer.position,chunk.Position);
            return length(viewerChunkPos - chunk.Position);
        }
    }

    public class DistanceSorterInt3 : IComparer<int3>
    {
        Comparer<float> floatComparer;

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

        float DistanceToViewer(int3 chunk)
        {
            //return Vector3.Distance(Viewer.position,chunk.Position);
            return length(viewerChunkPos - chunk);
        }
    }

    //public LoadingState State { get; private set; } = LoadingState.Unloaded;

    /// <summary>
    /// Keeps this chunk loaded at all times.
    /// </summary>
    public bool KeepLoaded { get; set; } = false;

    public int3 Position;
    public Map SourceMap { get; set; }

    public Mesh Mesh { get; private set; }
    public MeshFilter MainFilter { get; private set; }
    public MeshRenderer MainRenderer { get; private set; }
    public MeshCollider MainCollider { get; private set; }

    public float[] Points { get; set; }

    //private bool generateCollider;

    public bool PointsLoaded = false;
    public bool NewlyGenerated = true;

    public float3 GetCentre(Map map)
    {
        return new float3(Position) * map.BoundsSize;
    }

    //object loadLock = new object();

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
            IsoOffsetByHeight = 0.1f,
            IsoOffset = 0
        };
    }

    // Add components/get references in case lost (references can be lost when working in the editor)
    //public async Task Init(Material mat, bool generateCollider, bool cached)
    public async Task<bool> Init(Map map, bool cached)
    {
        /*gameObject.SetActive(true);
        generateCollider = map.generateColliders;


        if (MainCollider == null && generateCollider)
        {
            MainCollider = gameObject.AddComponent<MeshCollider>();
        }
        if (MainCollider != null && !generateCollider)
        {
            DestroyImmediate(MainCollider);
        }*/

        /*MainRenderer.enabled = true;

        if (map.generateColliders && MainCollider.sharedMesh == null)
        {
            MainCollider.sharedMesh = Mesh;
        }

        MainRenderer.sharedMaterial = map.ChunkMaterial;*/

        return await ChunkStart(cached);
    }

    public async Task Uninit()
    {
        NewlyGenerated = true;
        await ChunkEnd();
        PointsLoaded = false;
    }

    private async Task<bool> ChunkStart(bool cached)
    {
        /*lock (loadLock)
        {
            if (!(State == LoadingState.Unloaded || State == LoadingState.UnloadedCached))
            {
                return false;
            }
            State = LoadingState.Loading;
        }*/


        //try
        //{
            //Monitor.Enter(loadLock);
            /*if (!(State == LoadingState.Unloaded || State == LoadingState.UnloadedCached))
            {
                return false;
            }*/
            //State = LoadingState.Loading;
            var folder = PersistentDataPath + $"/{SourceMap.WorldName}";
            await Task.Run(() => ReadPointData(folder));
            //State = LoadingState.Loaded;
        //}
        //finally
        //{
            //Monitor.Exit(loadLock);
        //}

        return true;

        //TODO - Generate Other Objects
    }

    private async Task<bool> ChunkEnd()
    {
        /*lock (loadLock)
        {
            if (State != LoadingState.Loaded)
            {
                return false;
            }
            State = LoadingState.Unloading;
        }*/
        /*try
        {
            Monitor.Enter(loadLock);
            if (State != LoadingState.Loaded)
            {
                return false;
            }
            State = LoadingState.Unloading;*/
            var folder = PersistentDataPath + $"/{SourceMap.WorldName}";
            await Task.Run(() => WritePointData(folder));
/*
            State = LoadingState.UnloadedCached;
        }
        finally
        {
            Monitor.Exit(loadLock);
        }*/

        return true;
        //TODO - Delete Other Objects
    }

    void WritePointData(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
        var bytes = MemoryMarshal.AsBytes(Points.AsSpan());

        //Debug.Log($"Writing {Position}");
        using var file = System.IO.File.OpenWrite(folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt");
        //using var file = System.IO.File.Open(folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt", FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        //openStreams.TryAdd(file, true);
        using var gzipWriter = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Compress);
        gzipWriter.Write(bytes);
        gzipWriter.Close();
        file.Close();
        //openStreams.TryRemove(file,out _);
        //Debug.Log($"Writing {Position} DONE");
    }

    void ReadPointData(string folder)
    {
        if (Points == null)
        {
            Points = new float[SourceMap.PointsPerChunk];
        }

        var bytes = MemoryMarshal.AsBytes(Points.AsSpan());

        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var filePath = folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt";

        if (!System.IO.File.Exists(filePath))
        {
            return;
        }

        //Debug.Log($"READING {Position}");
        using var file = System.IO.File.OpenRead(folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt");
        //using var file = System.IO.File.Open(folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt",FileMode.OpenOrCreate,FileAccess.Read,FileShare.ReadWrite);
        //openStreams.TryAdd(file, true);
        using var gzipReader = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        gzipReader.Read(bytes);
        gzipReader.Close();
        file.Close();
        //openStreams.TryRemove(file, out _);

        PointsLoaded = true;
        NewlyGenerated = false;
        //Debug.Log($"READING {Position} DONE");
    }

    public void SaveSynchronously(string folder)
    {
        WritePointData(folder + $"/{SourceMap.WorldName}");
    }


    public void OnMeshUpdate()
    {
        MainRenderer.enabled = true;
        //Debug.Log("Mesh Updated");
    }
}