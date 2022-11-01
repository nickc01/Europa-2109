using HPCsharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Serialization;
using static Unity.Mathematics.math;

public class Map : MonoBehaviour
{
    private const int THREAD_GROUP_SIZE = 8;
    static Thread MAIN_THREAD;


    private static Map _instance;
    public static Map Instance => _instance ??= GameObject.FindObjectOfType<Map>();

    [field: SerializeField]
    public string WorldName { get; private set; } = "default";

    [field: SerializeField]
    public Camera MainCamera { get; private set; }

    [field: Header("General Settings")]
    [field: SerializeField]
    public MapGenerator DensityGenerator { get; private set; }

    public Transform viewer;
    [field: SerializeField]
    public float ViewDistance { get; private set; } = 30;

    [field: Space]
    [field: SerializeField]
    [field: FormerlySerializedAs("generatorShader")]
    public ComputeShader GeneratorShader { get; private set; }
    [field: SerializeField]
    public Material ChunkMaterial { get; private set; }
    public bool generateColliders;

    [field: Header("Voxel Settings")]
    [field: SerializeField]
    [field: FormerlySerializedAs("isoLevel")]
    public float IsoLevel { get; private set; }

    [field: FormerlySerializedAs("boundsSize")]
    [field: SerializeField]
    public float BoundsSize { get; private set; }

    [field: Range(2, 100)]
    [field: SerializeField]
    [field: FormerlySerializedAs("numPointsPerAxis")]
    public int NumPointsPerAxis { get; private set; } = 30;

    [Header("Gizmos")]
    [SerializeField]
    private bool showBoundsGizmo = true;
    [SerializeField]
    private Color boundsGizmoCol = Color.white;

    [field: Header("Sphere Brush Setting")]
    [field: SerializeField]
    [field: FormerlySerializedAs("sphereBrushSpeed")]
    public float SphereBrushSpeed { get; private set; } = 1;
    [SerializeField]
    private float sphereBrushSize = 1;
    [SerializeField]
    private ComputeShader sphereBrushShader;

    [field: Header("Cube Brush Setting")]
    [field: SerializeField]
    [field: FormerlySerializedAs("cubeBrushSize")]
    public Vector3 CubeBrushSize { get; private set; } = new Vector3(5, 5, 5);

    [field: SerializeField]
    [field: FormerlySerializedAs("cubeBrushSpeed")]
    public int CubeBrushSpeed { get; private set; } = 1;

    /// <summary>
    /// Automatically loads chunks surrounding the camera
    /// </summary>
    public bool AutoLoadChunks { get; set; } = true;


    /// <summary>
    /// Automatically unloads chunks when they aren't nearby the camera
    /// </summary>
    public bool AutoUnloadChunks { get; set; } = true;

    public float SphereBrushSize
    {
        get => sphereBrushSize;
        set
        {
            sphereBrushSize = value;
            sphereBrushPointSize = Mathf.CeilToInt(sphereBrushSize / (BoundsSize / (NumPointsPerAxis - 1)));
        }
    }

    private bool runningUpdate = false;
    private GameObject chunkHolder;
    private const string chunkHolderName = "Chunks Holder";
    private List<Chunk> chunks = new List<Chunk>();
    private ConcurrentDictionary<Vector3Int, Chunk> existingChunks = new ConcurrentDictionary<Vector3Int, Chunk>();
    private HashSet<Vector3Int> chunkCoordsUsed = new HashSet<Vector3Int>();
    private ConcurrentQueue<Chunk> recycleableChunks = new ConcurrentQueue<Chunk>();
    private Queue<BufferSet> unusedBuffers = new Queue<BufferSet>();
    private HashSet<BufferSet> usedBuffers = new HashSet<BufferSet>();
    private List<Task> taskPool = new List<Task>();
    private Chunk.DistanceSorter distanceSorter;

    private bool settingsUpdated;

    private List<Chunk> chunkMeshesToRefresh = new List<Chunk>();
    private bool refreshListDirty = true;

    private ConcurrentBag<Chunk> chunksToRender = new ConcurrentBag<Chunk>();

    public int PointsPerChunk => NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;
    public int numPointsInChunk => NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;

    ConcurrentQueue<Chunk> reservedChunks = new ConcurrentQueue<Chunk>();
    ConcurrentQueue<ReservedChunkInfo> reservedChunkInfo = new ConcurrentQueue<ReservedChunkInfo>();

    struct ReservedChunkInfo
    {
        public Vector3Int Coord;
    }


    private class BufferSet
    {
        public ComputeBuffer triangleBuffer;
        public ComputeBuffer pointsBuffer;
        public ComputeBuffer triCountBuffer;
    }

    public struct ChunkGenerationParameters
    {
        public float IsoOffset;
        public float IsoOffsetByHeight;
    }


    private void Awake()
    {
        MAIN_THREAD = Thread.CurrentThread;
        var camPos = MainCamera.transform.position;
        Task.Run(async () => await Test(camPos));
        sphereBrushPointSize = Mathf.CeilToInt(sphereBrushSize / (BoundsSize / (NumPointsPerAxis - 1)));
        if (Application.isPlaying)
        {

            Chunk[] oldChunks = FindObjectsOfType<Chunk>();
            for (int i = oldChunks.Length - 1; i >= 0; i--)
            {
                Destroy(oldChunks[i].gameObject);
            }
        }
    }

    async Task Test(Vector3 position)
    {
        try
        {
            await Task.Delay(5000);
            Debug.Log("LOADING TEST REGION");
            var chunks = await LoadChunksInArea(position, 50, 50, 50);
            Debug.Log("REGION LOADED");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    private async void FixedUpdate()
    {
        if (!runningUpdate && Application.isPlaying)
        {
            runningUpdate = true;
            await Run();
        }

        if (settingsUpdated && !runningUpdate)
        {
            runningUpdate = true;
            await Run();
            settingsUpdated = false;
        }
    }

    private void Update()
    {
        UpdateChunkCollider();

        while (reservedChunkInfo.TryDequeue(out var info))
        {
            reservedChunks.Enqueue(CreateChunk(info.Coord));
        }
        /*int count = creationChunksReserved;

        for (int i = count - 1; i >= 0; i--)
        {
            reservedChunks.Enqueue(CreateChunk(Vector3Int.zero));
        }
        Interlocked.Add(ref creationChunksReserved, -count);*/
    }

    private void UpdateChunkCollider()
    {
        if (refreshListDirty)
        {
            refreshListDirty = false;

            if (distanceSorter == null)
            {
                distanceSorter = new Chunk.DistanceSorter
                {
                    Viewer = viewer
                };
            }

            chunkMeshesToRefresh.SortMerge(distanceSorter);
        }

        while (chunkMeshesToRefresh.Count > 0)
        {
            Chunk chunk = chunkMeshesToRefresh[0];
            chunkMeshesToRefresh.RemoveAt(0);
            if (chunk.gameObject.activeSelf)
            {
                chunk.MainCollider.sharedMesh = chunk.Mesh;
                break;
            }
        }
    }

    async Task Run()
    {

        try
        {
            CreateBuffers();

            await InitVisibleChunks();

            if (!Application.isPlaying)
            {
                ReleaseBuffers();
            }
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
        finally
        {
            runningUpdate = false;
        }
    }

    /*Task LoadChunk(Vector3Int coord)
    {
        if (existingChunks.TryAdd(coord, null))
        {
            bool cached = true;

            if (!recycleableChunks.TryDequeue(out Chunk chunk))
            {
                cached = false;
                chunk = CreateChunk(coord);
            }

            existingChunks[coord] = chunk;
            chunk.Position = coord;
            chunkCoordsUsed.Add(coord);
            chunks.Add(chunk);

            return setupAndUpdateChunk(chunk, cached);

            async Task setupAndUpdateChunk(Chunk chunk, bool cached)
            {
                await chunk.Init(this, cached);
                //chunksToRender.Add(chunk);
            }
        }
        return Task.CompletedTask;
    }*/

    Task LoadChunk(Chunk chunk, Vector3Int coord, bool cached)
    {
        if (existingChunks.TryAdd(coord,chunk))
        {
            chunk.Position = coord;
            chunkCoordsUsed.Add(coord);
            chunks.Add(chunk);

            return setupAndUpdateChunk(chunk, cached);

            async Task setupAndUpdateChunk(Chunk chunk, bool cached)
            {
                await chunk.Init(this, cached);
                //chunksToRender.Add(chunk);
            }
        }
        return Task.CompletedTask;
    }

    Task UnloadChunk(Chunk chunk)
    {
        if (existingChunks.TryRemove(chunk.Position,out var _))
        {
            chunkCoordsUsed.Remove(chunk.Position);
            chunks.Remove(chunk);

            async Task ChunkEnd(Chunk chunk)
            {
                await chunk.Uninit();
                recycleableChunks.Enqueue(chunk);
            }
            return ChunkEnd(chunk);
        }
        return Task.CompletedTask;
    }

    private async Task InitVisibleChunks()
    {
        if (chunks == null)
        {
            return;
        }
        CreateChunkHolder();

        Vector3 p = viewer.position;
        Vector3 ps = p / BoundsSize;
        Vector3Int viewerCoord = new Vector3Int(Mathf.RoundToInt(ps.x), Mathf.RoundToInt(ps.y), Mathf.RoundToInt(ps.z));

        int maxChunksInView = Mathf.CeilToInt(ViewDistance / BoundsSize);
        float sqrViewDistance = ViewDistance * ViewDistance;

        taskPool.Clear();

        // Go through all existing chunks and flag for recyling if outside of max view dst
        for (int i = chunks.Count - 1; i >= 0; i--)
        {
            Chunk chunk = chunks[i];
            if (!chunk.KeepLoaded)
            {
                Vector3 centre = CentreFromCoord(chunk.Position);
                Vector3 viewerOffset = p - centre;
                Vector3 o = new Vector3(Mathf.Abs(viewerOffset.x), Mathf.Abs(viewerOffset.y), Mathf.Abs(viewerOffset.z)) - Vector3.one * BoundsSize / 2;
                float sqrDst = new Vector3(Mathf.Max(o.x, 0), Mathf.Max(o.y, 0), Mathf.Max(o.z, 0)).sqrMagnitude;
                if (sqrDst > sqrViewDistance && chunk.State == Chunk.LoadingState.Loaded)
                {
                    taskPool.Add(UnloadChunk(chunk));
                    /*existingChunks.Remove(chunk.Position);
                    chunkCoordsUsed.Remove(chunk.Position);
                    chunks.Remove(chunk);

                    taskPool.Add(ChunkEnd(chunk));

                    async Task ChunkEnd(Chunk chunk)
                    {
                        await chunk.Uninit();
                        recycleableChunks.Enqueue(chunk);
                    }*/
                }
            }
        }

        chunksToRender.Clear();

        for (int x = -maxChunksInView; x <= maxChunksInView; x++)
        {
            for (int y = -maxChunksInView; y <= maxChunksInView; y++)
            {
                for (int z = -maxChunksInView; z <= maxChunksInView; z++)
                {
                    Vector3Int coord = new Vector3Int(x, y, z) + viewerCoord;
                    if (chunkCoordsUsed.Contains(coord))
                    {
                        continue;
                    }

                    Vector3 centre = CentreFromCoord(coord);
                    Vector3 viewerOffset = p - centre;
                    Vector3 o = new Vector3(Mathf.Abs(viewerOffset.x), Mathf.Abs(viewerOffset.y), Mathf.Abs(viewerOffset.z)) - Vector3.one * BoundsSize / 2;
                    float sqrDst = new Vector3(Mathf.Max(o.x, 0), Mathf.Max(o.y, 0), Mathf.Max(o.z, 0)).sqrMagnitude;

                    // Chunk is within view distance and should be created (if it doesn't already exist)
                    if (sqrDst <= sqrViewDistance)
                    {

                        Bounds bounds = new Bounds(CentreFromCoord(coord), Vector3.one * BoundsSize);
                        if (IsVisibleFrom(bounds, MainCamera))
                        {
                            //taskPool.Add(LoadChunk(coord));

                            bool cached = true;

                            if (!recycleableChunks.TryDequeue(out Chunk chunk))
                            {
                                cached = false;
                                chunk = CreateChunk(coord);
                            }

                            chunksToRender.Add(chunk);

                            taskPool.Add(LoadChunk(chunk,coord,cached));
                            /*chunk.Position = coord;
                            existingChunks.Add(coord, chunk);
                            chunkCoordsUsed.Add(coord);
                            chunks.Add(chunk);

                            taskPool.Add(setupAndUpdateChunk(chunk, cached));

                            async Task setupAndUpdateChunk(Chunk chunk, bool cached)
                            {
                                await chunk.Init(this, cached);
                                chunksToRender.Add(chunk);
                            }*/
                        }
                    }

                }
            }
        }

        await Task.WhenAll(taskPool);

        foreach (Chunk chunk in chunksToRender)
        {
            UpdateChunkMesh(chunk);
        }

        chunksToRender.Clear();
    }

    /// <summary>
    /// Loads all chunks in a certain region. 
    /// </summary>
    /// <param name="position"></param>
    /// <param name=""></param>
    /// <returns></returns>
    public async Task<List<Chunk>> LoadChunksInArea(Vector3 position, int width, int height, int depth)
    {
        AutoUnloadChunks = false;
        List<Chunk> chunks = new List<Chunk>();
        List<Task> tasks = new List<Task>();
        List<Chunk> chunksToRender = new List<Chunk>();

        Vector3 p = position;
        Vector3 ps = p / BoundsSize;
        Vector3Int viewerCoord = new Vector3Int(Mathf.RoundToInt(ps.x), Mathf.RoundToInt(ps.y), Mathf.RoundToInt(ps.z));

        int halfWidth = width / 2;
        int halfHeight = height / 2;
        int halfDepth = depth / 2;
        for (int x = -halfWidth; x <= width - halfWidth; x++)
        {
            for (int y = -halfHeight; y <= height - halfHeight; y++)
            {
                for (int z = -halfDepth; z <= depth - halfDepth; z++)
                {
                    Vector3Int coord = new Vector3Int(x, y, z) + viewerCoord;
                    Debug.Log("COORD = " + coord);
                    if (chunkCoordsUsed.Contains(coord))
                    {
                        chunks.Add(existingChunks[coord]);
                        existingChunks[coord].KeepLoaded = true;
                        continue;
                    }

                    Debug.Log("A");

                    //Bounds bounds = new Bounds(CentreFromCoord(coord), Vector3.one * BoundsSize);
                    //if (IsVisibleFrom(bounds, MainCamera))
                    //{
                    bool cached = true;

                    if (recycleableChunks.TryDequeue(out Chunk chunk))
                    {
                        chunk.KeepLoaded = true;
                        chunks.Add(chunk);
                        chunksToRender.Add(chunk);

                        Debug.Log($"Loading Chunk = {chunk.Position}");

                        tasks.Add(LoadChunk(chunk, coord, cached));
                    }
                    else
                    {
                        cached = false;
                        chunk = CreateChunk(coord);

                        chunk.KeepLoaded = true;
                        chunks.Add(chunk);
                        chunksToRender.Add(chunk);

                        Debug.Log($"Loading Chunk = {chunk.Position}");

                        tasks.Add(LoadChunk(chunk, coord, cached));
                    }

                    

                    

                    /*chunk.Position = coord;
                    existingChunks.Add(coord, chunk);
                    chunkCoordsUsed.Add(coord);
                    chunks.Add(chunk);

                    tasks.Add(setupAndUpdateChunk(chunk, cached));

                    async Task setupAndUpdateChunk(Chunk chunk, bool cached)
                    {
                        await chunk.Init(this, cached);
                        chunksToRender.Add(chunk);
                    }*/
                    //}

                    //Vector3 centre = CentreFromCoord(coord);
                    //Vector3 viewerOffset = p - centre;
                    //Vector3 o = new Vector3(Mathf.Abs(viewerOffset.x), Mathf.Abs(viewerOffset.y), Mathf.Abs(viewerOffset.z)) - Vector3.one * BoundsSize / 2;
                    //float sqrDst = new Vector3(Mathf.Max(o.x, 0), Mathf.Max(o.y, 0), Mathf.Max(o.z, 0)).sqrMagnitude;

                    // Chunk is within view distance and should be created (if it doesn't already exist)
                    /*if (sqrDst <= sqrViewDistance)
                    {

                        
                    }*/
                }
            }
        }

        await Task.WhenAll(tasks);

        Debug.Log("TASKS DONE");

        /*foreach (Chunk chunk in chunksToRender)
        {
            UpdateChunkMesh(chunk);
        }*/

        AutoUnloadChunks = true;

        return chunks;
    }

    private static Plane[] planeCache = new Plane[6];

    static bool IsVisibleFrom(Bounds bounds, Camera camera)
    {
        GeometryUtility.CalculateFrustumPlanes(camera, planeCache);
        return GeometryUtility.TestPlanesAABB(planeCache, bounds);
    }

    private Triangle[] triangleCache;
    private float[] pointCache;

    private void UpdateChunkMesh(Chunk chunk, BufferSet bufferSet = null, bool pointsAlreadySet = false)
    {
        int numPoints = NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;
        int numVoxelsPerAxis = NumPointsPerAxis - 1;
        int numThreadsPerAxis = Mathf.CeilToInt(numVoxelsPerAxis / (float)THREAD_GROUP_SIZE);
        float pointSpacing = BoundsSize / (NumPointsPerAxis - 1);

        Vector3Int coord = chunk.Position;
        Vector3 centre = CentreFromCoord(coord);

        Vector3 worldBounds = new Vector3(BoundsSize, BoundsSize, BoundsSize);

        if (bufferSet == null)
        {
            bufferSet = GetBufferSet();
        }

        if (!pointsAlreadySet)
        {
            if (chunk.PointsLoaded)
            {
                bufferSet.pointsBuffer.SetData(chunk.Points, 0, 0, numPoints);
            }
            else
            {
                DensityGenerator.Generate(bufferSet.pointsBuffer, NumPointsPerAxis, BoundsSize, worldBounds, centre, Vector3.one, pointSpacing, this, chunk);
            }
        }

        bufferSet.triangleBuffer.SetCounterValue(0);
        GeneratorShader.SetBuffer(0, "points", bufferSet.pointsBuffer);
        GeneratorShader.SetBuffer(0, "triangles", bufferSet.triangleBuffer);
        GeneratorShader.SetInt("numPointsPerAxis", NumPointsPerAxis);

        GeneratorShader.SetFloat("boundsSize", BoundsSize);
        GeneratorShader.SetVector("centre", new Vector4(centre.x, centre.y, centre.z));
        GeneratorShader.SetFloat("spacing", pointSpacing);

        GeneratorShader.SetFloat("isoLevel", IsoLevel);

        GeneratorShader.Dispatch(0, numThreadsPerAxis, numThreadsPerAxis, numThreadsPerAxis);

        // Get number of triangles in the triangle buffer
        ComputeBuffer.CopyCount(bufferSet.triangleBuffer, bufferSet.triCountBuffer, 0);
        int[] triCountArray = { 0 };
        bufferSet.triCountBuffer.GetData(triCountArray);
        int numTris = triCountArray[0];

        bufferSet.triangleBuffer.GetData(triangleCache, 0, 0, numTris);


        if (!chunk.PointsLoaded || pointsAlreadySet)
        {
            bufferSet.pointsBuffer.GetData(pointCache, 0, 0, numPoints);

            if (chunk.Points == null)
            {
                chunk.Points = new float[numPoints];
            }

            Array.Copy(pointCache, chunk.Points, numPoints);

            chunk.PointsLoaded = true;
        }


        Mesh mesh = chunk.Mesh;
        mesh.Clear();

        Vector3[] vertices = new Vector3[numTris * 3];
        int[] meshTriangles = new int[numTris * 3];

        Parallel.For(0, numTris, i =>
        {
            for (int j = 0; j < 3; j++)
            {
                meshTriangles[i * 3 + j] = i * 3 + j;
                vertices[i * 3 + j] = triangleCache[i][j];
            }
        });

        mesh.vertices = vertices;
        mesh.triangles = meshTriangles;

        mesh.RecalculateNormals();

        chunk.OnMeshUpdate();

        chunkMeshesToRefresh.Add(chunk);

        ReturnBufferSet(bufferSet);
    }

    private void OnDestroy()
    {
        if (Application.isPlaying)
        {
            ReleaseBuffers();

            foreach (Chunk chunk in chunks)
            {
                if (!chunksToRender.Contains(chunk))
                {
                    chunk.SaveSynchronously();
                }
            }
        }
    }

    private Chunk[] chunkUpdateList = new Chunk[16];
    private int chunkUpdateCount = 0;
    private object chunkUpdateLock = new object();

    private bool AddChunkToBeUpdated(Chunk chunk)
    {
        int currentCount = chunkUpdateCount;
        for (int i = 0; i < currentCount; i++)
        {
            if (UnityUtilities.GetCachedPtr(chunkUpdateList[i]) == UnityUtilities.GetCachedPtr(chunk))
            {
                return false;
            }
        }

        lock (chunkUpdateLock)
        {
            for (int i = currentCount; i < chunkUpdateCount; i++)
            {
                if (UnityUtilities.GetCachedPtr(chunkUpdateList[i]) == UnityUtilities.GetCachedPtr(chunk))
                {
                    return false;
                }
            }

            chunkUpdateList[chunkUpdateCount] = chunk;
            chunkUpdateCount++;
            return true;
        }
    }

    private void ClearChunkUpdateList()
    {
        chunkUpdateCount = 0;
    }

    public void UseSphereBrush(Vector3 worldPos, bool EraseMode, float intensity)
    {
        UseSphereBrushCPU(worldPos, EraseMode, intensity);
    }

    private void UseSphereBrushCPU(Vector3 worldPos, bool EraseMode, float intensity)
    {
        float3 pos = worldPos;
        float brushValue = intensity * SphereBrushSpeed * IsoLevel;
        if (EraseMode) brushValue *= -1;

        int width = sphereBrushPointSize * 2;

        Parallel.For(0, width * width * width, i =>
        {
            int x = i % width;
            int y = (i / width) % width;
            int z = i / (width * width);


            float tx = lerp(-sphereBrushSize, sphereBrushSize, x / (float)width);
            float ty = lerp(-sphereBrushSize, sphereBrushSize, y / (float)width);
            float tz = lerp(-sphereBrushSize, sphereBrushSize, z / (float)width);

            float3 samplePoint = pos + ((new float3(x, y, z) - new float3(sphereBrushPointSize)) * (BoundsSize / (NumPointsPerAxis - 1)));

            float brushSample = GetSphereBrushValueAtPoint(new int3(x, y, z) - new int3(sphereBrushPointSize));

            PaintPointAdd(samplePoint, brushSample * brushSample * brushValue);
        });

        for (int i = 0; i < chunkUpdateCount; i++)
        {
            Chunk chunk = chunkUpdateList[i];
            if (chunk != null)
            {
                UpdateChunkMesh(chunk);
                for (int j = i; j < chunkUpdateCount; j++)
                {
                    if (chunkUpdateList[j] == chunk)
                    {
                        chunkUpdateList[j] = null;
                    }
                }
            }
        }

        ClearChunkUpdateList();
    }

    public void UseCubeBrush(Vector3 worldPos, bool EraseMode)
    {
        Vector3 pos = worldPos;
        float brushValue = Time.deltaTime * CubeBrushSpeed * IsoLevel;
        if (EraseMode) brushValue *= -1;

        int width = CubeBrushPointSize.x * 2;
        int height = CubeBrushPointSize.y * 2;
        int depth = CubeBrushPointSize.z * 2;

        Vector3 camPos = MainCamera.transform.position;

        Debug.DrawRay(camPos, pos, Color.blue, 20f);

        Parallel.For(0, width * height * depth, i =>
        {
            int x = i % width;
            int y = (i / width) % height;
            int z = i / (width * height);


            Vector3 samplePoint = pos + (new Vector3(x - CubeBrushPointSize.x, y - CubeBrushPointSize.y, z - CubeBrushPointSize.z) * (BoundsSize / (NumPointsPerAxis - 1)));

            float brushSample = GetCubeBrushValueAtPoint(x - CubeBrushPointSize.x, y - CubeBrushPointSize.y, z - CubeBrushPointSize.z);

            Debug.DrawRay(camPos, samplePoint, Color.red, 20f);

            PaintPointAdd(samplePoint, brushSample * brushSample * brushValue);
        });

        for (int i = 0; i < chunkUpdateCount; i++)
        {
            Chunk chunk = chunkUpdateList[i];
            if (chunk != null)
            {
                UpdateChunkMesh(chunk);

                for (int j = i; j < chunkUpdateCount; j++)
                {
                    if (chunkUpdateList[j] == chunk)
                    {
                        chunkUpdateList[j] = null;
                    }
                }
            }
        }



        ClearChunkUpdateList();
    }


    public void PaintPoint(float3 worldPosition, float value)
    {
        int3 chunkPos = WorldPosToChunkPos(worldPosition);

        if (TryGetChunkAtChunkPos(chunkPos, out Chunk chunk))
        {
            PaintPoint(chunk, WorldPosToPointPos(chunkPos, worldPosition), value);
        }
    }

    public void PaintPoint(Chunk chunk, int3 pointPosition, float value)
    {
        int index = PointPosToIndex(pointPosition);

        if (chunk.Points[index] != value)
        {
            chunk.Points[index] = value;

            chunk.Points[index] = clamp(chunk.Points[index], IsoLevel - 2, IsoLevel + 2);

            AddChunkToBeUpdated(chunk);

            SetNeighboringPoints(chunk, pointPosition, value);
        }
    }


    public void PaintPointAdd(float3 worldPosition, float value)
    {
        int3 chunkPos = WorldPosToChunkPos(worldPosition);

        if (TryGetChunkAtChunkPos(chunkPos, out Chunk chunk))
        {
            PaintPointAdd(chunk, WorldPosToPointPos(chunkPos, worldPosition), value);
        }
    }

    public void PaintPointAdd(Chunk chunk, int3 pointPosition, float value)
    {
        int index = PointPosToIndex(pointPosition);

        if (value != 0)
        {
            chunk.Points[index] += value;

            chunk.Points[index] = clamp(chunk.Points[index], IsoLevel - 2, IsoLevel + 2);

            AddChunkToBeUpdated(chunk);

            SetNeighboringPoints(chunk, pointPosition, chunk.Points[index]);
        }
    }

    public bool FireRaySync(Ray ray, out float3 hit, float maxDistance = 20f, float stepAmount = 0.25f)
    {
        return FireRaySync(ray.origin, ray.direction, out hit, maxDistance, stepAmount);
    }

    public bool FireRaySync(float3 source, float3 direction, out float3 hit, float maxDistance = 20f, float stepAmount = 0.25f)
    {
        float3 hitPosition = source;
        float3 travelDirection = normalize(direction) * stepAmount;
        float value = 0;

        float distanceTravelled = 0f;

        do
        {
            value = SamplePoint(hitPosition);
            if (float.IsNaN(value))
            {
                break;
            }
            if (value >= IsoLevel)
            {
                hit = hitPosition;
                return true;
            }

            hitPosition = hitPosition + travelDirection;
            distanceTravelled += stepAmount;

        } while (distanceTravelled <= maxDistance);

        hit = default;
        return false;
    }

    public bool FireRayParallel(Ray ray, out float3 hit, float maxDistance = 20f, float stepAmount = 0.25f)
    {
        return FireRayParallel(ray.origin, ray.direction, out hit, maxDistance, stepAmount);
    }

    private static float[] raySampleCache;

    public bool FireRayParallel(float3 source, float3 direction, out float3 hit, float maxDistance = 20f, float stepAmount = 0.25f)
    {
        float3 travelDirection = normalize(direction) * stepAmount;
        int increments = (int)(maxDistance / stepAmount);
        if (raySampleCache == null || raySampleCache.Length < increments)
        {
            raySampleCache = new float[increments];
        }

        Parallel.For(0, increments, i =>
        {
            raySampleCache[i] = SamplePoint(source + (travelDirection * i));
        });

        for (int i = 0; i < increments; i++)
        {
            if (float.IsNaN(raySampleCache[i]))
            {
                break;
            }
            else if (raySampleCache[i] > IsoLevel)
            {
                hit = source + (travelDirection * i);
                return true;
            }
        }

        hit = default;
        return false;
    }

    void SetNeighboringPoints(Chunk chunk, int3 pointPosition, float value)
    {
        int numVoxelPerAxis = NumPointsPerAxis - 1;
        int3 chunkPos = int3(chunk.Position.x, chunk.Position.y, chunk.Position.z);
        int3 DI = new int3(0);
        int3 chunkPosTemp;
        int cnt = 0;
        int pointIndex = 0;

        void SetData()
        {

            if (existingChunks.TryGetValue(new Vector3Int(chunkPosTemp.x, chunkPosTemp.y, chunkPosTemp.z), out Chunk chunk) && pointIndex < numPointsInChunk && pointIndex >= 0)
            {
                chunk.Points[pointIndex] = value;
                AddChunkToBeUpdated(chunk);
            }
        }
        if (pointPosition.x == numVoxelPerAxis)
        {
            cnt++;
            DI.x++;
            chunkPosTemp = chunkPos;
            chunkPosTemp.x++;
            pointIndex = PointPosToIndex(0, pointPosition.y, pointPosition.z);
            SetData();
        }
        if (pointPosition.y == numVoxelPerAxis)
        {
            cnt++;
            DI.y++;
            chunkPosTemp = chunkPos;
            chunkPosTemp.y++;
            pointIndex = PointPosToIndex(pointPosition.x, 0, pointPosition.z);

            SetData();
        }
        if (pointPosition.z == numVoxelPerAxis)
        {
            cnt++;
            DI.z++;
            chunkPosTemp = chunkPos;
            chunkPosTemp.z++;
            pointIndex = PointPosToIndex(pointPosition.x, pointPosition.y, 0);
            SetData();
        }

        if (pointPosition.x == 0)
        {
            cnt++;
            DI.x--;
            chunkPosTemp = chunkPos;
            chunkPosTemp.x--;
            pointIndex = PointPosToIndex(numVoxelPerAxis, pointPosition.y, pointPosition.z);
            SetData();
        }
        if (pointPosition.y == 0)
        {
            cnt++;
            DI.y--;
            chunkPosTemp = chunkPos;
            chunkPosTemp.y--;
            pointIndex = PointPosToIndex(pointPosition.x, numVoxelPerAxis, pointPosition.z);
            SetData();
        }
        if (pointPosition.z == 0)
        {
            cnt++;
            DI.z--;
            chunkPosTemp = chunkPos;
            chunkPosTemp.z--;
            pointIndex = PointPosToIndex(pointPosition.x, pointPosition.y, numVoxelPerAxis);
            SetData();
        }
        if (cnt >= 2)
        {
            chunkPosTemp = chunkPos + DI;
            int x = DI.x == 0 ? pointPosition.x : DI.x > 0 ? 0 : numVoxelPerAxis;
            int y = DI.y == 0 ? pointPosition.y : DI.y > 0 ? 0 : numVoxelPerAxis;
            int z = DI.z == 0 ? pointPosition.z : DI.z > 0 ? 0 : numVoxelPerAxis;
            pointIndex = PointPosToIndex(x, y, z);

            SetData();
            if (cnt == 3)
            {
                //xy
                chunkPosTemp.z = chunkPos.z;
                pointIndex = PointPosToIndex(x, y, pointPosition.z);
                SetData();

                //yz
                chunkPosTemp.z = chunkPos.z + DI.z;
                chunkPosTemp.x = chunkPos.x;
                pointIndex = PointPosToIndex(pointPosition.x, y, z);
                SetData();

                //xz
                chunkPosTemp.x = chunkPos.x + DI.x;
                chunkPosTemp.y = chunkPos.y;
                pointIndex = PointPosToIndex(x, pointPosition.y, z);
                SetData();
            }
        }
    }


    public float SamplePoint(Vector3 worldPosition)
    {
        int3 chunkPos = WorldPosToChunkPos(worldPosition);
        Chunk chunk = GetChunkAtChunkPos(chunkPos);
        if (chunk == null)
        {
            return float.NaN;
        }
        else
        {
            return SamplePoint(chunk, WorldPosToPointPos(chunkPos, worldPosition));
        }
    }

    static float InverseLerp(float a, float b, float value)
    {
        return (value - a) / (b - a);
    }

    public float SamplePoint(Chunk chunk, int3 pointPosition)
    {
        return chunk.Points[PointPosToIndex(pointPosition)];
    }

    public float3 ChunkPosToWorldPos(int3 chunkPos)
    {
        return chunkPos * new float3(BoundsSize);
    }

    public int3 WorldPosToChunkPos(float3 worldPos)
    {
        return new int3(round(worldPos / BoundsSize));
    }

    public int3 WorldPosToPointPos(int3 chunkPos, float3 worldPos)
    {
        return int3(round(lerp(new float3(0), new float3(NumPointsPerAxis - 1), ((worldPos - ((float3)chunkPos * BoundsSize)) / BoundsSize) + float3(0.5f))));

    }

    public float3 PointPosToWorldPos(int3 chunkPos, int3 pointPos)
    {
        return unlerp(new float3(0), new float3(NumPointsPerAxis - 1), pointPos) - float3(0.5f) + ((float3)chunkPos * BoundsSize);
    }

    public Chunk GetChunkAtChunkPos(int3 chunkPos)
    {
        if (existingChunks.TryGetValue(new Vector3Int(chunkPos.x, chunkPos.y, chunkPos.z), out Chunk chunk))
        {
            return chunk;
        }
        else
        {
            return null;
        }
    }

    public bool TryGetChunkAtChunkPos(int3 chunkPos, out Chunk chunk)
    {
        if (existingChunks.TryGetValue(new Vector3Int(chunkPos.x, chunkPos.y, chunkPos.z), out chunk))
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    public int PointPosToIndex(int3 pointPos)
    {
        return PointPosToIndex(pointPos.x, pointPos.y, pointPos.z);
    }

    public int PointPosToIndex(int x, int y, int z)
    {
        return (z * NumPointsPerAxis * NumPointsPerAxis) + (y * NumPointsPerAxis) + x;
    }

    public int3 IndexToPointPos(int index)
    {
        int x = index % NumPointsPerAxis;
        int y = (index / NumPointsPerAxis) % NumPointsPerAxis;
        int z = index / (NumPointsPerAxis * NumPointsPerAxis);

        return new int3(x, y, z);
    }

    [NonSerialized]
    private int sphereBrushPointSize;
    Vector3Int CubeBrushPointSize => new Vector3Int(
                Mathf.CeilToInt(CubeBrushSize.x / (BoundsSize / (NumPointsPerAxis - 1))),
                Mathf.CeilToInt(CubeBrushSize.y / (BoundsSize / (NumPointsPerAxis - 1))),
                Mathf.CeilToInt(CubeBrushSize.z / (BoundsSize / (NumPointsPerAxis - 1))));

    /// <summary>
    /// Gets the brush value for a certain point. Valid range for x, y and z is [-SphereBrushPointSize - SphereBrushPointSize - 1]
    /// </summary>
    /// <returns></returns>
    float GetSphereBrushValueAtPoint(int3 point)
    {
        return clamp(sphereBrushPointSize - length(point), 0f, sphereBrushPointSize * 2f);
    }

    float GetCubeBrushValueAtPoint(int x, int y, int z)
    {
        float value;
        if (x >= -CubeBrushPointSize.x && x < CubeBrushPointSize.x &&
            y >= -CubeBrushPointSize.y && y < CubeBrushPointSize.y &&
            z >= -CubeBrushPointSize.z && z < CubeBrushPointSize.z)
        {
            value = CubeBrushPointSize.magnitude;
        }
        else
        {
            value = 0;
        }

        return value;
    }

    private void CreateBuffers()
    {
        int numPoints = NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;
        if (unusedBuffers.Count == 0)
        {
            ReturnBufferSet(GetBufferSet());
        }
    }

    private void ReleaseBuffers()
    {
        foreach (BufferSet buffer in usedBuffers)
        {
            buffer.triangleBuffer.Release();
            buffer.pointsBuffer.Release();
            buffer.triCountBuffer.Release();
        }

        usedBuffers.Clear();

        for (int i = 0; i < unusedBuffers.Count; i++)
        {
            BufferSet buffer = unusedBuffers.Dequeue();
            buffer.triangleBuffer.Release();
            buffer.pointsBuffer.Release();
            buffer.triCountBuffer.Release();
        }
    }

    private Vector3 CentreFromCoord(Vector3Int coord)
    {
        return new Vector3(coord.x, coord.y, coord.z) * BoundsSize;
    }

    private void CreateChunkHolder()
    {
        // Create/find mesh holder object for organizing chunks under in the hierarchy
        if (chunkHolder == null)
        {
            if (GameObject.Find(chunkHolderName))
            {
                chunkHolder = GameObject.Find(chunkHolderName);
            }
            else
            {
                chunkHolder = new GameObject(chunkHolderName);
            }
        }
    }

    private async Task<Chunk> CreateChunkAsync(Vector3Int coord)
    {
        if (Thread.CurrentThread == MAIN_THREAD)
        {
            GameObject chunk = new GameObject($"Chunk ({coord.x}, {coord.y}, {coord.z})");
            chunk.transform.parent = chunkHolder.transform;
            Chunk newChunk = chunk.AddComponent<Chunk>();
            newChunk.Position = coord;
            newChunk.SourceMap = this;
            return newChunk;
        }
        else
        {
            Chunk chunk;
            if (!reservedChunks.TryDequeue(out chunk))
            {
                //Interlocked.Increment(ref creationChunksReserved);
                reservedChunkInfo.Enqueue(new ReservedChunkInfo() { Coord = coord });

                while (!reservedChunks.TryDequeue(out chunk))
                {
                    await Task.Delay(100);
                }
            }

            //chunk.name = $"Chunk ({coord.x}, {coord.y}, {coord.z})";
            chunk.Position = coord;
            chunk.SourceMap = this;
            return chunk;
        }
    }

    private Chunk CreateChunk(Vector3Int coord)
    {
        if (Thread.CurrentThread == MAIN_THREAD)
        {
            GameObject chunk = new GameObject($"Chunk ({coord.x}, {coord.y}, {coord.z})");
            chunk.transform.parent = chunkHolder.transform;
            Chunk newChunk = chunk.AddComponent<Chunk>();
            newChunk.Position = coord;
            newChunk.SourceMap = this;
            return newChunk;
        }
        else
        {
            Chunk chunk;
            if (!reservedChunks.TryDequeue(out chunk))
            {
                //Interlocked.Increment(ref creationChunksReserved);
                reservedChunkInfo.Enqueue(new ReservedChunkInfo() { Coord = coord});

                while (!reservedChunks.TryDequeue(out chunk))
                {
                    Thread.Sleep(100);
                }
            }

            //chunk.name = $"Chunk ({coord.x}, {coord.y}, {coord.z})";
            chunk.Position = coord;
            chunk.SourceMap = this;
            return chunk;
        }
    }

    private void OnValidate()
    {
        settingsUpdated = true;
        sphereBrushPointSize = Mathf.CeilToInt(sphereBrushSize / (BoundsSize / (NumPointsPerAxis - 1)));
    }

    private BufferSet GetBufferSet()
    {
        if (unusedBuffers.TryDequeue(out BufferSet bufferSet))
        {
            usedBuffers.Add(bufferSet);
            return bufferSet;
        }
        else
        {
            BufferSet buffer = CreateBufferSet();
            usedBuffers.Add(buffer);
            return buffer;
        }
    }

    private BufferSet CreateBufferSet()
    {
        int numPoints = NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;
        int numVoxelsPerAxis = NumPointsPerAxis - 1;
        int numVoxels = numVoxelsPerAxis * numVoxelsPerAxis * numVoxelsPerAxis;
        int maxTriangleCount = numVoxels * 5;

        if (triangleCache == null)
        {
            triangleCache = new Triangle[maxTriangleCount];
            pointCache = new float[numPoints];
        }

        return new BufferSet
        {
            triangleBuffer = new ComputeBuffer(maxTriangleCount, sizeof(float) * 3 * 3, ComputeBufferType.Append),
            pointsBuffer = new ComputeBuffer(numPoints, sizeof(float) * 4),
            triCountBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw)
        };

    }

    private void ReturnBufferSet(BufferSet buffer)
    {
        usedBuffers.Remove(buffer);
        unusedBuffers.Enqueue(buffer);
    }

    private static Queue<float[]> pointBufferCache = new Queue<float[]>();

    /*static float[] GetChunkPointBuffer()
    {
        if (pointBufferCache.TryDequeue(out float[] buffer))
        {
            return buffer;
        }
        else
        {
            int numPoints = Instance.NumPointsPerAxis * Instance.NumPointsPerAxis * Instance.NumPointsPerAxis;
            return new float[numPoints];
        }
    }

    static void ReturnChunkPointBuffer(float[] buffer)
    {
        pointBufferCache.Enqueue(buffer);
    }*/

    private struct Triangle
    {
#pragma warning disable 649 // disable unassigned variable warning
        public Vector3 a;
        public Vector3 b;
        public Vector3 c;

        public Vector3 this[int i]
        {
            get
            {
                switch (i)
                {
                    case 0:
                        return a;
                    case 1:
                        return b;
                    default:
                        return c;
                }
            }
        }
    }

    private void OnDrawGizmos()
    {
        if (showBoundsGizmo)
        {
            Gizmos.color = boundsGizmoCol;

            List<Chunk> chunks = (this.chunks == null) ? new List<Chunk>(FindObjectsOfType<Chunk>()) : this.chunks;
            //foreach (Chunk chunk in chunks)
            for (int i = chunks.Count - 1; i >= 0; i--)
            {
                if (i >= chunks.Count)
                {
                    continue;
                }
                var chunk = chunks[i];
                Bounds bounds = new Bounds(CentreFromCoord(chunk.Position), Vector3.one * BoundsSize);
                Gizmos.color = boundsGizmoCol;
                Gizmos.DrawWireCube(CentreFromCoord(chunk.Position), Vector3.one * BoundsSize);
            }
        }
    }

}