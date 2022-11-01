using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class Chunk : MonoBehaviour
{
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

        public Transform Viewer;
        public int Compare(Chunk x, Chunk y)
        {
            if (floatComparer == null)
            {
                floatComparer = Comparer<float>.Default;
            }
            return floatComparer.Compare(DistanceToViewer(x),DistanceToViewer(y));
        }

        float DistanceToViewer(Chunk chunk)
        {
            return Vector3.Distance(Viewer.position,chunk.Position);
        }
    }

    public LoadingState State { get; private set; } = LoadingState.Unloaded;

    /// <summary>
    /// Keeps this chunk loaded at all times.
    /// </summary>
    public bool KeepLoaded { get; set; } = false;

    public Vector3Int Position;
    public Map SourceMap { get; set; }

    public Mesh Mesh { get; private set; }
    public MeshFilter MainFilter { get; private set; }
    public MeshRenderer MainRenderer { get; private set; }
    public MeshCollider MainCollider { get; private set; }

    public float[] Points { get; set; }

    private bool generateCollider;

    public bool PointsLoaded = false;

    object loadLock = new object();

    private void Awake()
    {
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
        gameObject.SetActive(true);
        generateCollider = map.generateColliders;


        if (MainCollider == null && generateCollider)
        {
            MainCollider = gameObject.AddComponent<MeshCollider>();
        }
        if (MainCollider != null && !generateCollider)
        {
            DestroyImmediate(MainCollider);
        }

        MainRenderer.enabled = true;

        if (generateCollider && MainCollider.sharedMesh == null)
        {
            MainCollider.sharedMesh = Mesh;
        }

        MainRenderer.material = map.ChunkMaterial;

        return await ChunkStart(cached);
    }

    public async Task Uninit()
    {
        MainRenderer.enabled = false;
        await ChunkEnd();
        Mesh.Clear();
        gameObject.SetActive(false);
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


        try
        {
            Monitor.Enter(loadLock);
            if (!(State == LoadingState.Unloaded || State == LoadingState.UnloadedCached))
            {
                return false;
            }
            State = LoadingState.Loading;
            var folder = Application.persistentDataPath + $"/{SourceMap.WorldName}";
            await Task.Run(() => ReadPointData(folder));
            State = LoadingState.Loaded;
        }
        finally
        {
            Monitor.Exit(loadLock);
        }

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
        try
        {
            Monitor.Enter(loadLock);
            if (State != LoadingState.Loaded)
            {
                return false;
            }
            State = LoadingState.Unloading;
            var folder = Application.persistentDataPath + $"/{SourceMap.WorldName}";
            await Task.Run(() => WritePointData(folder));

            State = LoadingState.UnloadedCached;
        }
        finally
        {
            Monitor.Exit(loadLock);
        }

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
        using var file = System.IO.File.OpenWrite(folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt");
        using var gzipWriter = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Compress);
        gzipWriter.Write(bytes);
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

        using var file = System.IO.File.OpenRead(folder + $"/{SourceMap.WorldName}_{Position.x}_{Position.y}_{Position.z}.txt");
        using var gzipReader = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        gzipReader.Read(bytes);

        PointsLoaded = true;
    }

    public void SaveSynchronously()
    {
        var folder = Application.persistentDataPath + $"/{SourceMap.WorldName}";
        WritePointData(folder);
    }


    public void OnMeshUpdate()
    {
        MainRenderer.enabled = true;
        //Debug.Log("Mesh Updated");
    }
}