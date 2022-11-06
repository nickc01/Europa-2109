using HPCsharp;
using HPCsharp.ParallelAlgorithms;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
    public event Action<Chunk> OnChunkLoad;
    public event Action<Chunk> OnChunkUnload;

    private const int THREAD_GROUP_SIZE = 8;
    private const string chunkHolderName = "Chunks Holder";
    private static Thread MAIN_THREAD;
    const int MAX_BRUSH_CALLS_PER_FRAME = 1;
    const int MAX_MESH_UPDATE_CALLS_PER_FRAME = 5;


    private static Map _instance;
    public static Map Instance => _instance ??= GameObject.FindObjectOfType<Map>();

    [field: SerializeField]
    public string WorldName { get; private set; } = "default";

    [field: SerializeField]
    public int WorldSeed { get; private set; } = 1;

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

    /*[field: Header("Sphere Brush Setting")]
    [field: SerializeField]
    [field: FormerlySerializedAs("sphereBrushSpeed")]
    public float SphereBrushSpeed { get; private set; } = 1;
    [SerializeField]
    private float sphereBrushSize = 1;*/
    [SerializeField]
    private ComputeShader sphereBrushShader;

    /*[field: Header("Cube Brush Setting")]
    [field: SerializeField]
    [field: FormerlySerializedAs("cubeBrushSize")]
    public Vector3 CubeBrushSize { get; private set; } = new Vector3(5, 5, 5);

    [field: SerializeField]
    [field: FormerlySerializedAs("cubeBrushSpeed")]
    public int CubeBrushSpeed { get; private set; } = 1;*/

    /// <summary>
    /// Automatically loads chunks surrounding the camera
    /// </summary>
    public bool AutoLoadChunks { get; set; } = true;


    /// <summary>
    /// Automatically unloads chunks when they aren't nearby the camera
    /// </summary>
    public bool AutoUnloadChunks { get; set; } = true;

    /*public float SphereBrushSize
    {
        get => sphereBrushSize;
        set
        {
            sphereBrushSize = value;
            sphereBrushPointSize = Mathf.CeilToInt(sphereBrushSize / (BoundsSize / (NumPointsPerAxis - 1)));
        }
    }*/

    private bool runningUpdate = false;
    private GameObject chunkHolder;

    private List<Chunk> loadedChunks = new List<Chunk>();
    private ConcurrentDictionary<int3, Chunk> loadedCoordinates = new ConcurrentDictionary<int3, Chunk>();
    private Queue<Chunk> unloadedChunkPool = new Queue<Chunk>();

    private Queue<BufferSet> unusedBuffers = new Queue<BufferSet>();
    private HashSet<BufferSet> usedBuffers = new HashSet<BufferSet>();
    private List<Task> taskPool = new List<Task>();
    private Chunk.DistanceSorter distanceSorter;
    private Chunk.DistanceSorterInt3 distanceInt3Sorter;

    private bool settingsUpdated;

    private List<Chunk> chunkMeshesToRefresh;
    private bool refreshListDirty = true;

    private ConcurrentBag<Chunk> chunksToRender = new ConcurrentBag<Chunk>();

    private ConcurrentQueue<AreaGenParameters> areaGenerationQueue = new ConcurrentQueue<AreaGenParameters>();
    //private ConcurrentQueue<int3> meshesToUpdate = new ConcurrentQueue<int3>();

    private List<int3> meshesToUpdate;
    private ConcurrentBag<int3> meshesTEMP = new ConcurrentBag<int3>();
    private object meshUpdateLock = new object();
    private bool meshListDirty = false;

    private ConcurrentQueue<BrushCall> brushCalls = new ConcurrentQueue<BrushCall>();

    public int PointsPerChunk => NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;
    public int numPointsInChunk => NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;


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

    class BrushCall
    {
        public enum BrushType
        {
            Sphere,
            Cube
        }
        public Vector3 worldPos;
        public bool EraseMode;
        public float intensity;
        public float3 brushSize;
        public BrushType brushType;
        public TaskCompletionSource<bool> completer;
    }

    class AreaGenParameters
    {
        public float3 viewerPosition;
        public int3 area;
        public float viewDistance;
        public bool keepChunksLoaded;
        public List<Chunk> out_ChunksInArea;
        public TaskCompletionSource<bool> completer;
    }


    private void Awake()
    {
        meshesToUpdate = ListPool<int3>.New();
        chunkMeshesToRefresh = ListPool<Chunk>.New();
        //meshesToUpdate = new List<int3>();
        //chunkMeshesToRefresh = new List<Chunk>();
        if (Directory.Exists(Application.persistentDataPath + "/default"))
        {
            Directory.Delete(Application.persistentDataPath + "/default",true);
        }
        CreateBuffers();
        //Directory.Delete(Application.persistentDataPath, true);
        MAIN_THREAD = Thread.CurrentThread;
        CreateChunkHolder();
        Vector3 viewerPos = viewer.position;
        if (Application.isPlaying)
        {
            Chunk[] oldChunks = FindObjectsOfType<Chunk>();
            for (int i = oldChunks.Length - 1; i >= 0; i--)
            {
                Destroy(oldChunks[i].gameObject);
            }
        }
    }


    private static void UnloadChunks(List<Chunk> chunks)
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].KeepLoaded = false;
        }
    }

    //async Task TestGeneration(float3 sourcePosition)
    //{
    //Debug.Log("Main Thread Before = " + (Thread.CurrentThread == MAIN_THREAD));
    /*await Task.Delay(5000);
    var chunkList = new List<Chunk>();
    await InitChunksInArea(sourcePosition,new int3(8),keepChunksLoaded: true, out_chunksInArea: chunkList);
    //SphereBrushSize = 3;
    //SphereBrushSpeed = 1;
    //UseSphereBrush(sourcePosition, true, 1f,new int3(20,20,20));

    Debug.Log("Brushing");
    await UseSphereBrushAsync(sourcePosition, true, 1f, new int3(5, 5, 5));
    //await UseCubeBrushAsync(sourcePosition,true,10f,new int3(5,5,5));

    Debug.Log("Brush Done");*/

    //Debug.Log("Loaded Chunks = " + chunkList.Count);
    //Debug.Log("Main Thread After = " + (Thread.CurrentThread == MAIN_THREAD));

    //await Task.Delay(5000);
    //await InitChunksInArea(sourcePosition, new int3(2), keepChunksLoaded: false, out_chunksInArea: chunkList);
    //}

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

    static void DistinctInList<T>(List<T> list)
    {
        HashSet<T> distinct = new HashSet<T>();

        for (int i = 0; i < list.Count; i++)
        {
            distinct.Add(list[i]);
        }

        int index = 0;
        foreach (var e in distinct)
        {
            list[index] = e;
            index++;
        }
        list.RemoveRange(index, list.Count - index);
    }

    private void Update()
    {
        int meshUpdateCount = 0;
        lock (meshUpdateLock)
        {
            if (true)
            {
                if (distanceInt3Sorter == null)
                {
                    distanceInt3Sorter = new Chunk.DistanceSorterInt3
                    {
                        //viewerChunkPos = viewer.transform.position
                    };
                }
                distanceInt3Sorter.viewerChunkPos = WorldPosToChunkPos(viewer.transform.position);
                //DistinctInList(meshesToUpdate);
                //meshesToUpdate = meshesToUpdate.AsParallel().Distinct().ToList();

                meshesToUpdate = meshesToUpdate.AsParallel().Distinct().ToList();

                //CustomAlgorithms.SortMergeAdaptivePar(ref meshesToUpdate, 0, meshesToUpdate.Count, distanceInt3Sorter);
                meshesToUpdate = meshesToUpdate.SortMergePseudoInPlacePar(distanceInt3Sorter);

                //meshesToUpdate = meshesToUpdate.AsParallel().Distinct().ToList();
                //HPCsharp.ParallelAlgorithm.SortMergePseudoInPlaceAdaptivePar(ref meshesToUpdate, 0, chunkMeshesToRefresh.Count, distanceInt3Sorter);
                //Debug.Log("B1 Count = " + meshesToUpdate.Count);
                //CustomAlgorithms.SortMergeAdaptivePar(ref meshesToUpdate, 0, meshesToUpdate.Count, distanceInt3Sorter);
                //Debug.Log("B2 Count = " + meshesToUpdate.Count);



                //TODO - TODO - TODO - TRY MAKING THIS MORE PERFORMANT



                //meshesToUpdate = meshesToUpdate.AsParallel().Distinct().ToListPooled();
                meshListDirty = false;

                /*for (int i = 0; i < MAX_MESH_UPDATE_CALLS_PER_FRAME; i++)
                {
                    var mesh = meshesToUpdate[meshesToUpdate.Count - 1];
                    UpdateChunkMesh(mesh);
                }*/

                //meshesToUpdate.Sort
            }

            /*if (meshesToUpdate.Count > 0)
            {
                Debug.Log("Nearest Mesh = " + length(meshesToUpdate[meshesToUpdate.Count - 1] - distanceInt3Sorter.viewerChunkPos));
                Debug.Log("Nearest Mesh Coord = " + meshesToUpdate[meshesToUpdate.Count - 1]);
            }*/

            //Debug.Log("Count = " + meshesToUpdate.Count);
            int3 lastCoordinate = new int3(int.MaxValue);
            while (meshUpdateCount < MAX_MESH_UPDATE_CALLS_PER_FRAME)
            {
                if (meshesToUpdate.Count > 0)
                {
                    var coordinate = meshesToUpdate[meshesToUpdate.Count - 1];
                    if (!all(coordinate == lastCoordinate) && loadedCoordinates.TryGetValue(coordinate, out var chunk))
                    {
                        lastCoordinate = coordinate;
                        UpdateChunkMesh(chunk);
                        //Debug.Log("UPDATING MESH");
                        //Debug.Log($"Updating Mesh = {chunk.Position}");
                        meshUpdateCount++;
                        meshesToUpdate.RemoveAt(meshesToUpdate.Count - 1);
                    }
                    else
                    {
                        //Debug.Log("SKIP");
                        meshesToUpdate.RemoveAt(meshesToUpdate.Count - 1);
                    }
                }
                else
                {
                    break;
                }
            }

            //Debug.Log("After Count = " + meshesToUpdate.Count);

            int tempMeshesCount = meshesTEMP.Count;

            for (int i = 0; i < tempMeshesCount; i++)
            {
                if (meshesTEMP.TryTake(out var coord))
                {
                    meshesToUpdate.Add(coord);
                }
            }
            //Debug.Log("After 2 Count = " + meshesToUpdate.Count);

            if (tempMeshesCount > 0)
            {
                meshListDirty = true;
            }
        }
        /*int meshUpdateCount = 0;
        while (meshesToUpdate.TryDequeue(out int3 coordinates))
        {
            if (loadedCoordinates.TryGetValue(coordinates, out var chunk))
            {
                HPCsharp.ParallelAlgorithm.SortMergeInPlaceAdaptivePar()
            UpdateChunkMesh(coordinates);
                meshUpdateCount++;
                if (meshUpdateCount >= MAX_MESH_UPDATE_CALLS_PER_FRAME)
                {
                    break;
                }
            }
        }*/

        int brushCallCount = 0;
        while (brushCalls.TryDequeue(out BrushCall call))
        {
            try
            {
                if (call.brushType == BrushCall.BrushType.Sphere)
                {
                    UseSphereBrush(call.worldPos, call.EraseMode, call.intensity, call.brushSize);
                }
                else
                {
                    UseCubeBrush(call.worldPos, call.EraseMode, call.intensity, call.brushSize);
                }
                call.completer?.TrySetResult(true);

            }
            catch (Exception e)
            {
                call.completer?.TrySetException(e);
            }
            brushCallCount++;
            if (brushCallCount >= MAX_BRUSH_CALLS_PER_FRAME)
            {
                break;
            }
        }

        UpdateChunkCollider();
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
                    
                };
            }

            distanceSorter.viewerChunkPos = WorldPosToChunkPos(viewer.transform.position);

            //chunkMeshesToRefresh.SortMerge(distanceSorter);
            //HPCsharp.ParallelAlgorithm.SortMergePseudoInPlaceAdaptivePar(ref chunkMeshesToRefresh, 0, chunkMeshesToRefresh.Count, distanceSorter);
            //CustomAlgorithms.SortMergeAdaptivePar(ref chunkMeshesToRefresh, 0, chunkMeshesToRefresh.Count, distanceSorter);

            //chunkMeshesToRefresh = chunkMeshesToRefresh.SortMergePseudoInPlacePar(distanceSorter);
            //chunkMeshesToRefresh = chunkMeshesToRefresh.AsParallel().Distinct().ToList();

            //DistinctInList(chunkMeshesToRefresh);
            chunkMeshesToRefresh = chunkMeshesToRefresh.AsParallel().Distinct().ToList();
            chunkMeshesToRefresh = chunkMeshesToRefresh.SortMergePseudoInPlacePar(distanceSorter);
            //CustomAlgorithms.SortMergeAdaptivePar(ref chunkMeshesToRefresh, 0, chunkMeshesToRefresh.Count, distanceSorter);
        }

        int3 lastCoordinate = new int3(int.MaxValue);
        while (chunkMeshesToRefresh.Count > 0)
        {
            Chunk chunk = chunkMeshesToRefresh[chunkMeshesToRefresh.Count - 1];
            chunkMeshesToRefresh.RemoveAt(chunkMeshesToRefresh.Count - 1);
            if (chunk != null && !all(chunk.Position == lastCoordinate) && chunk.gameObject.activeSelf)
            {
                chunk.MainCollider.sharedMesh = chunk.Mesh;
                lastCoordinate = chunk.Position;
                break;
            }
        }
    }

    private async Task Run()
    {
        try
        {
            while (areaGenerationQueue.TryDequeue(out AreaGenParameters areaGenParams))
            {
                try
                {
                    await LoadChunksInArea(areaGenParams.viewerPosition, areaGenParams.area, areaGenParams.viewDistance, areaGenParams.keepChunksLoaded, areaGenParams.out_ChunksInArea);
                    areaGenParams.completer.TrySetResult(true);
                }
                catch (Exception e)
                {
                    areaGenParams.completer.TrySetException(e);
                }
            }

            await LoadVisibleChunks();

            /*if (!Application.isPlaying)
            {
                ReleaseBuffers();
            }*/
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

    private Task LoadVisibleChunks()
    {
        int maxChunksInView = (int)ceil(ViewDistance / BoundsSize) * 2;
        return LoadChunksInArea(viewer.position, new int3(maxChunksInView), ViewDistance);
    }

    public async Task LoadChunksInArea(float3 viewerPosition, int3 area, float viewDistance = float.NaN, bool keepChunksLoaded = false, List<Chunk> out_chunksInArea = null)
    {
        // Debug.Log("A");
        if (Thread.CurrentThread != MAIN_THREAD)
        {
            TaskCompletionSource<bool> completer = new TaskCompletionSource<bool>();
            areaGenerationQueue.Enqueue(new AreaGenParameters
            {
                viewerPosition = viewerPosition,
                area = area,
                viewDistance = viewDistance,
                keepChunksLoaded = keepChunksLoaded,
                out_ChunksInArea = out_chunksInArea,
                completer = completer
            });

            //Debug.Log("GENERATING CHUNKS IN AREA");
            await completer.Task;
            //Debug.Log("DONE GENERATING CHUNKS IN AREA");
            return;
        }

        List<int3> chunksToCreate = new List<int3>();
        List<Chunk> destroyedChunks = new List<Chunk>();

        await Task.Run(async () =>
        {
            float3 p = viewerPosition;
            float3 ps = p / BoundsSize;
            int3 viewerCoord = new int3(round(ps));

            float sqrViewDistance = viewDistance * viewDistance;
            // Go through all existing chunks and flag for recyling if outside of max view dst
            if (!float.IsNaN(viewDistance))
            {
                for (int i = loadedChunks.Count - 1; i >= 0; i--)
                {
                    Chunk chunk = loadedChunks[i];
                    if (!chunk.KeepLoaded)
                    {
                        float3 centre = CentreFromCoord(chunk.Position);
                        float3 viewerOffset = p - centre;
                        Vector3 o = abs(viewerOffset) - new float3(1f) * BoundsSize / 2f;
                        float sqrDst = lengthsq(max(o, new float3(0f)));
                        if (sqrDst > sqrViewDistance)
                        {
                            OnChunkUnload?.Invoke(chunk);
                            loadedCoordinates.TryRemove(chunk.Position, out _);
                            loadedChunks.RemoveAt(i);

                            taskPool.Add(ChunkEnd(chunk));

                            destroyedChunks.Add(chunk);

                            async Task ChunkEnd(Chunk chunk)
                            {
                                await chunk.Uninit();
                                unloadedChunkPool.Enqueue(chunk);
                            }
                        }
                    }
                }

                await Task.WhenAll(taskPool);
                taskPool.Clear();
            }

            chunksToRender.Clear();

            int halfWidth = area.x / 2;
            int halfHeight = area.y / 2;
            int halfDepth = area.z / 2;

            for (int x = -halfWidth; x <= area.x - halfWidth; x++)
            {
                for (int y = -halfHeight; y <= area.y - halfHeight; y++)
                {
                    for (int z = -halfDepth; z <= area.z - halfDepth; z++)
                    {
                        int3 coord = new int3(x, y, z) + viewerCoord;

                        {
                            if (loadedCoordinates.TryGetValue(coord, out Chunk loadedChunk))
                            {
                                out_chunksInArea?.Add(loadedChunk);
                                if (keepChunksLoaded)
                                {
                                    loadedChunk.KeepLoaded = true;
                                }
                                continue;
                            }
                        }

                        float sqrDst = 0;
                        if (!float.IsNaN(viewDistance))
                        {
                            float3 centre = CentreFromCoord(coord);
                            float3 viewerOffset = p - centre;
                            Vector3 o = abs(viewerOffset) - new float3(1f) * BoundsSize / 2f;
                            sqrDst = lengthsq(max(o, new float3(0f)));
                        }

                        // Chunk is within view distance and should be created (if it doesn't already exist)
                        if (float.IsNaN(viewDistance) || sqrDst <= sqrViewDistance)
                        {
                            if (unloadedChunkPool.TryDequeue(out Chunk chunk))
                            {
                                chunk.Position = coord;
                                if (keepChunksLoaded)
                                {
                                    chunk.KeepLoaded = true;
                                }

                                out_chunksInArea?.Add(chunk);

                                chunksToRender.Add(chunk);
                                taskPool.Add(chunk.Init(this, true));
                            }
                            else
                            {
                                chunksToCreate.Add(coord);
                            }
                        }

                    }
                }
            }
        });

        foreach (var coord in chunksToCreate)
        {
            var chunk = CreateChunk(coord);
            if (keepChunksLoaded)
            {
                chunk.KeepLoaded = true;
            }

            out_chunksInArea?.Add(chunk);

            chunksToRender.Add(chunk);
            taskPool.Add(chunk.Init(this, false));
        }
        await Task.WhenAll(taskPool);
        taskPool.Clear();

        //Debug.Log("C");

        foreach (Chunk chunk in chunksToRender)
        {
            chunk.MainRenderer.enabled = true;

            if (generateColliders && chunk.MainCollider.sharedMesh == null)
            {
                chunk.MainCollider.sharedMesh = chunk.Mesh;
            }

            chunk.MainRenderer.sharedMaterial = ChunkMaterial;

            loadedCoordinates.TryAdd(chunk.Position, chunk);
            loadedChunks.Add(chunk);
            UpdateChunkMesh(chunk);
            OnChunkLoad?.Invoke(chunk);
        }

        foreach (var chunk in destroyedChunks)
        {
            chunk.MainRenderer.enabled = false;
            chunk.Mesh.Clear();
            //chunk.gameObject.SetActive(false);
        }

        chunksToRender.Clear();

        //Debug.Log("D");
    }

    private static Plane[] planeCache = new Plane[6];

    private static bool IsVisibleFrom(Bounds bounds, Camera camera)
    {
        GeometryUtility.CalculateFrustumPlanes(camera, planeCache);
        return GeometryUtility.TestPlanesAABB(planeCache, bounds);
    }

    private Triangle[] triangleCache;
    private float[] pointCache;

    private Vector3[] meshVertices;
    private int[] meshTriangles;

    private void AddChunkMeshesToBeUpdated(int3 bl, int3 tr)
    {
        bool locked = false;
        try
        {
            if (Monitor.TryEnter(meshUpdateLock))
            {
                locked = true;
                for (int x = bl.x; x <= tr.x; x++)
                {
                    for (int y = bl.y; y <= tr.y; y++)
                    {
                        for (int z = bl.z; z <= tr.z; z++)
                        {
                            meshesToUpdate.Add(new int3(x, y, z));
                        }
                    }
                }
                //meshesToUpdate.AddRange(chunk.Position);
                meshListDirty = true;
            }
            else
            {
                for (int x = bl.x; x <= tr.x; x++)
                {
                    for (int y = bl.y; y <= tr.y; y++)
                    {
                        for (int z = bl.z; z <= tr.z; z++)
                        {
                            meshesTEMP.Add(new int3(x, y, z));
                        }
                    }
                }
                //meshesTEMP.Add(chunk.Position);
            }
        }
        finally
        {
            if (locked)
            {
                Monitor.Exit(meshUpdateLock);
            }
        }
        /*if (Thread.CurrentThread != MAIN_THREAD)
        {
            
        }*/
    }

    private void UpdateChunkMesh(Chunk chunk, BufferSet bufferSet = null, bool pointsAlreadySet = false)
    {
        if (Thread.CurrentThread != MAIN_THREAD)
        {
            bool locked = false;
            try
            {
                if (Monitor.TryEnter(meshUpdateLock))
                {
                    locked = true;
                    meshesToUpdate.Add(chunk.Position);
                    meshListDirty = true;
                }
                else
                {
                    meshesTEMP.Add(chunk.Position);
                }
            }
            finally
            {
                if (locked)
                {
                    Monitor.Exit(meshUpdateLock);
                }
            }
            /*lock (meshUpdateLock)
            {
                meshesToUpdate.Add(chunk.Position);
                meshListDirty = true;
            }*/
            //meshesToUpdate.Enqueue(chunk);
            return;
        }
        int numPoints = NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;
        int numVoxelsPerAxis = NumPointsPerAxis - 1;
        int numThreadsPerAxis = Mathf.CeilToInt(numVoxelsPerAxis / (float)THREAD_GROUP_SIZE);
        float pointSpacing = BoundsSize / (NumPointsPerAxis - 1);

        int3 coord = chunk.Position;
        float3 centre = CentreFromCoord(coord);

        float3 worldBounds = new float3(BoundsSize);

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

        if (meshVertices == null || meshVertices.Length < numTris * 3)
        {
            meshVertices = new Vector3[numTris * 3];
            meshTriangles = new int[numTris * 3];
        }



        Parallel.For(0, numTris, i =>
        {
            for (int j = 0; j < 3; j++)
            {
                meshTriangles[i * 3 + j] = i * 3 + j;
                meshVertices[i * 3 + j] = triangleCache[i][j];
            }
        });

        //mesh.vertices = meshVertices;
        //mesh.triangles = meshTriangles;
        mesh.SetVertices(meshVertices, 0, numTris * 3, UnityEngine.Rendering.MeshUpdateFlags.Default);
        mesh.SetTriangles(meshTriangles, 0, numTris * 3, 0, false);

        mesh.RecalculateNormals();

        chunk.OnMeshUpdate();

        chunkMeshesToRefresh.Add(chunk);

        ReturnBufferSet(bufferSet);
    }

    private void OnDestroy()
    {
        //Debug.Log("Open Streams = " + Chunk.openStreams.Count);
        //GC.Collect();
        if (Application.isPlaying)
        {
            ReleaseBuffers();

            string folder = Application.persistentDataPath;
            Parallel.For(0, loadedChunks.Count, i =>
            {
                //Debug.Log($"Saving Chunk {loadedChunks[i].Position}");
                loadedChunks[i].SaveSynchronously(folder);
            });

            /*for (int i = 0; i < loadedChunks.Count; i++)
            {
                try
                {
                    Debug.Log($"Saving Chunk {loadedChunks[i].Position}");
                    loadedChunks[i].SaveSynchronously(folder);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            };*/
        }
    }

    /*private ThreadLocal<Chunk[]> chunkUpdateList = new ThreadLocal<Chunk[]>(() => new Chunk[200]);
    private ThreadLocal<int> chunkUpdateCount = new ThreadLocal<int>(() => 0);
    private ThreadLocal<object> chunkUpdateLock = new ThreadLocal<object>(() => new object());

    private bool AddChunkToBeUpdated(Chunk chunk)
    {
        int currentCount = chunkUpdateCount.Value;
        for (int i = 0; i < currentCount; i++)
        {
            if (UnityUtilities.GetCachedPtr(chunkUpdateList.Value[i]) == UnityUtilities.GetCachedPtr(chunk))
            {
                return false;
            }
        }

        lock (chunkUpdateLock.Value)
        {
            for (int i = currentCount; i < chunkUpdateCount.Value; i++)
            {
                if (UnityUtilities.GetCachedPtr(chunkUpdateList.Value[i]) == UnityUtilities.GetCachedPtr(chunk))
                {
                    return false;
                }
            }

            chunkUpdateList.Value[chunkUpdateCount.Value] = chunk;
            chunkUpdateCount.Value++;
            //Debug.Log("Update Count = " + chunkUpdateCount);
            return true;
        }
    }

    private void ClearChunkUpdateList()
    {
        chunkUpdateCount.Value = 0;
    }*/

    /*public void UseSphereBrush(float3 worldPos, bool EraseMode, float intensity, int3 sphereBrushSize)
    {
        UseSphereBrushCPU(worldPos, EraseMode, intensity, sphereBrushSize);
    }*/

    /*public Task UseSphereBrushAsync(float3 worldPos, bool EraseMode, float intensity, float3 sphereBrushSize)
    {
        UseSphereBrush(worldPos, EraseMode, intensity, sphereBrushSize);
        return Task.CompletedTask;
    }*/

    public void UseSphereBrush(float3 worldPos, bool EraseMode, float intensity, float3 sphereBrushSize)
    {
        /*if (Thread.CurrentThread != MAIN_THREAD)
        {
            brushCalls.Enqueue(new BrushCall
            {
                worldPos = worldPos,
                EraseMode = EraseMode,
                intensity = intensity,
                brushSize = sphereBrushSize,
                brushType = BrushCall.BrushType.Sphere
            });
            return;
        }*/

        float3 magicIntensity = (sphereBrushSize / 5f) * 6f;

        /*Debug.Log("MAIN THREAD = " + (Thread.CurrentThread == MAIN_THREAD));
        Debug.Log("World Pos = " + worldPos);
        Debug.Log("EraseMode = " + EraseMode);
        Debug.Log("intensity = " + intensity);*/
        float3 pos = worldPos;
        float brushValue = intensity * IsoLevel;
        if (EraseMode) brushValue *= -1;
        //Debug.Log("Brush Value = " + brushValue);

        int3 sphereBrushPointSize = (int3)ceil(sphereBrushSize / (BoundsSize / (NumPointsPerAxis)));
        //var sphereBrushSize = sphereBrushSize

        int width = sphereBrushPointSize.x * 2;
        int height = sphereBrushPointSize.y * 2;
        int depth = sphereBrushPointSize.z * 2;

        //Debug.Log("Width = " + width);

        int3 bl = WorldPosToChunkPos(worldPos - new float3(1) - new float3(sphereBrushSize));
        int3 tr = WorldPosToChunkPos(worldPos + new float3(1) + new float3(sphereBrushSize));

        Parallel.For(0, width * height * depth, i =>
        {

            int x = i % width;
            int y = (i / width) % height;
            int z = i / (width * height);

            bool testing = x == sphereBrushPointSize.x && y == sphereBrushPointSize.y && z == sphereBrushPointSize.z;


            float tx = lerp(-sphereBrushSize.x, sphereBrushSize.x, x / (float)width);
            float ty = lerp(-sphereBrushSize.y, sphereBrushSize.y, y / (float)height);
            float tz = lerp(-sphereBrushSize.z, sphereBrushSize.z, z / (float)depth);

            /*if (testing)
            {
                Debug.Log("TX = " + tx);
                Debug.Log("TY = " + ty);
                Debug.Log("TZ = " + tz);
            }*/

            //float3 samplePoint = pos + ((new float3(x, y, z) - new float3(sphereBrushPointSize)) * (BoundsSize / (NumPointsPerAxis - 1)));
            float3 samplePoint = new float3(tx, ty, tz) + pos;

            /*if (testing)
            {
                Debug.Log("Sample Point = " + samplePoint);
                Debug.Log("SPhere Brush Point Size = " + sphereBrushPointSize);
            }*/


            //float brushSample = GetSphereBrushValueAtPoint(new int3(x, y, z) - new int3(sphereBrushPointSize));
            //float brushSample = clamp(sphereBrushPointSize - (length(new int3(x, y, z) - sphereBrushPointSize)), 0f, sphereBrushPointSize * 2f);

            //float samp = sphereBrushPointSize - (length(new int3(x, y, z) - sphereBrushPointSize);

            //var sample3D = (new float3(x, y, z) - sphereBrushPointSize) / sphereBrushPointSize;

            float3 relativeCoords = new float3(x, y, z) - sphereBrushPointSize;

            /*if (testing)
            {
                Debug.Log("relativeCoords = " + relativeCoords);
            }*/

            float3 sample3D = (relativeCoords * relativeCoords) / (sphereBrushPointSize * sphereBrushPointSize);
            //var brushSample = clamp(-(csum(sample3D) - 1),0,100f);
            float brushSample = -csum((sample3D - (1f / 3f)) * magicIntensity);

            if (brushSample < 0)
            {
                brushSample = 0;
            }

            //brushSample = IsoLevel * clamp(1f - (brushSample * brushSample),0f,1f);

            /*if (testing)
            {
                Debug.Log("Brush Sample = " + brushSample);
            }*/

            PaintPointAdd(samplePoint, brushSample * brushValue);
        });

        //Debug.Log("Writing");
        AddChunkMeshesToBeUpdated(bl,tr);
        //Debug.Log("DONE SPHERE");
        /*for (int i = 0; i < chunkUpdateCount.Value; i++)
        {
            Chunk chunk = chunkUpdateList.Value[i];
            if (chunk != null)
            {
                UpdateChunkMesh(chunk);
                for (int j = i; j < chunkUpdateCount.Value; j++)
                {
                    if (chunkUpdateList.Value[j] == chunk)
                    {
                        chunkUpdateList.Value[j] = null;
                    }
                }
            }
        }*/

        //ClearChunkUpdateList();
    }

    public Task UseCubeBrushAsync(float3 worldPos, bool EraseMode, float intensity, float3 cubeBrushSize)
    {
        if (Thread.CurrentThread != MAIN_THREAD)
        {
            TaskCompletionSource<bool> completer = new TaskCompletionSource<bool>();
            brushCalls.Enqueue(new BrushCall
            {
                worldPos = worldPos,
                EraseMode = EraseMode,
                intensity = intensity,
                brushSize = cubeBrushSize,
                brushType = BrushCall.BrushType.Cube,
                completer = completer
            });
            return completer.Task;
        }
        else
        {
            UseCubeBrush(worldPos, EraseMode, intensity, cubeBrushSize);
            return Task.CompletedTask;
        }
    }

    public void UseCubeBrush(float3 worldPos, bool EraseMode, float intensity, float3 cubeBrushSize)
    {
        //cubeBrushSize /= new float3(0.66628725f);
        if (Thread.CurrentThread != MAIN_THREAD)
        {
            brushCalls.Enqueue(new BrushCall
            {
                worldPos = worldPos,
                EraseMode = EraseMode,
                intensity = intensity,
                brushSize = cubeBrushSize,
                brushType = BrushCall.BrushType.Cube
            });
            return;
        }
        float3 pos = worldPos;
        float brushValue = intensity * IsoLevel;
        if (EraseMode) brushValue *= -1;

        /*new Vector3Int(
                Mathf.CeilToInt(CubeBrushSize.x / (BoundsSize / (NumPointsPerAxis - 1))),
                Mathf.CeilToInt(CubeBrushSize.y / (BoundsSize / (NumPointsPerAxis - 1))),
                Mathf.CeilToInt(CubeBrushSize.z / (BoundsSize / (NumPointsPerAxis - 1))));*/

        int3 cubeBrushPointSize = (int3)ceil(cubeBrushSize / (BoundsSize / NumPointsPerAxis));

        int width = cubeBrushPointSize.x * 2;
        int height = cubeBrushPointSize.y * 2;
        int depth = cubeBrushPointSize.z * 2;

        Vector3 camPos = MainCamera.transform.position;

        //Debug.DrawRay(camPos, pos, Color.blue, 20f);

        int3 bl = WorldPosToChunkPos(worldPos - new float3(1) - new float3(cubeBrushSize));
        int3 tr = WorldPosToChunkPos(worldPos + new float3(1) + new float3(cubeBrushSize));

        Parallel.For(0, width * height * depth, i =>
        {
            int x = i % width;
            int y = (i / width) % height;
            int z = i / (width * height);

            int3 point = new int3(x, y, z);


            //Vector3 samplePoint = pos + (new Vector3(x - cubeBrushSize.x, y - cubeBrushSize.y, z - cubeBrushSize.z) * (BoundsSize / (NumPointsPerAxis)));

            //float tx = lerp(-cubeBrushSize.x, cubeBrushSize.x, x / (float)width);
            //float ty = lerp(-cubeBrushSize.y, cubeBrushSize.y, y / (float)height);
            //float tz = lerp(-cubeBrushSize.z, cubeBrushSize.z, z / (float)depth);
            //float3 samplePoint = new float3(tx, ty, tz) + pos;

            //float3 samplePoint = pos + ((new float3(x, y, z) - new float3(sphereBrushPointSize)) * (BoundsSize / (NumPointsPerAxis - 1)));

            float3 samplePoint = pos + ((float3)point - cubeBrushPointSize) * (BoundsSize / (NumPointsPerAxis));


            float brushSample = GetCubeBrushValueAtPoint(point - cubeBrushPointSize, cubeBrushPointSize);

            //Debug.DrawRay(camPos, samplePoint, Color.red, 20f);

            PaintPointAdd(samplePoint, brushSample * brushValue);
        });

        AddChunkMeshesToBeUpdated(bl, tr);

        /*for (int i = 0; i < chunkUpdateCount.Value; i++)
        {
            Chunk chunk = chunkUpdateList.Value[i];
            if (chunk != null)
            {
                UpdateChunkMesh(chunk);

                for (int j = i; j < chunkUpdateCount.Value; j++)
                {
                    if (chunkUpdateList.Value[j] == chunk)
                    {
                        chunkUpdateList.Value[j] = null;
                    }
                }
            }
        }*/



        //ClearChunkUpdateList();
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

            chunk.Points[index] = clamp(chunk.Points[index], IsoLevel - 4, IsoLevel + 4);

            //AddChunkToBeUpdated(chunk);

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

            chunk.Points[index] = clamp(chunk.Points[index], IsoLevel - 4, IsoLevel + 4);

            //AddChunkToBeUpdated(chunk);

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

    private void SetNeighboringPoints(Chunk chunk, int3 pointPosition, float value)
    {
        int numVoxelPerAxis = NumPointsPerAxis - 1;
        int3 chunkPos = int3(chunk.Position.x, chunk.Position.y, chunk.Position.z);
        int3 DI = new int3(0);
        int3 chunkPosTemp;
        int cnt = 0;
        int pointIndex = 0;

        void SetData()
        {

            if (loadedCoordinates.TryGetValue(chunkPosTemp, out Chunk chunk) && pointIndex < numPointsInChunk && pointIndex >= 0)
            {
                chunk.Points[pointIndex] = value;
                //AddChunkToBeUpdated(chunk);
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

    private static float InverseLerp(float a, float b, float value)
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
        if (loadedCoordinates.TryGetValue(chunkPos, out Chunk chunk))
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
        if (loadedCoordinates.TryGetValue(chunkPos, out chunk))
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

    /*[NonSerialized]
    private int sphereBrushPointSize;
    Vector3Int CubeBrushPointSize => new Vector3Int(
                Mathf.CeilToInt(CubeBrushSize.x / (BoundsSize / (NumPointsPerAxis - 1))),
                Mathf.CeilToInt(CubeBrushSize.y / (BoundsSize / (NumPointsPerAxis - 1))),
                Mathf.CeilToInt(CubeBrushSize.z / (BoundsSize / (NumPointsPerAxis - 1))));
    */
    /*/// <summary>
    /// Gets the brush value for a certain point. Valid range for x, y and z is [-SphereBrushPointSize - SphereBrushPointSize - 1]
    /// </summary>
    /// <returns></returns>
    float GetSphereBrushValueAtPoint(int3 point)
    {
        //return clamp(sphereBrushPointSize - length(point), 0f, sphereBrushPointSize * 2f);
        return sphereBrushPointSize - new Vector3(point.x, point.y, point.z).magnitude;
    }*/

    private float GetCubeBrushValueAtPoint(int3 point, int3 cubeBrushPointSize)
    {
        float value;
        if (point.x >= -cubeBrushPointSize.x && point.x < cubeBrushPointSize.x &&
            point.y >= -cubeBrushPointSize.y && point.y < cubeBrushPointSize.y &&
            point.z >= -cubeBrushPointSize.z && point.z < cubeBrushPointSize.z)
        {
            value = length(cubeBrushPointSize);
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

    private float3 CentreFromCoord(int3 coord)
    {
        return new float3(coord) * BoundsSize;
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

    /*private async Task<Chunk> CreateChunkAsync(Vector3Int coord)
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
    }*/

    private Chunk CreateChunk(int3 coord)
    {
#if UNITY_EDITOR
        if (Thread.CurrentThread != MAIN_THREAD)
        {
            Debug.Log($"{nameof(CreateChunk)} can only be called on the Main Thread");
        }
#endif
        GameObject chunk = new GameObject($"Chunk ({coord.x}, {coord.y}, {coord.z})");
        chunk.transform.parent = chunkHolder.transform;
        Chunk newChunk = chunk.AddComponent<Chunk>();
        newChunk.Position = coord;
        newChunk.SourceMap = this;
        return newChunk;
        /*if (Thread.CurrentThread == MAIN_THREAD)
        {
            
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
        }*/
    }

    private void OnValidate()
    {
        settingsUpdated = true;
        //sphereBrushPointSize = Mathf.CeilToInt(sphereBrushSize / (BoundsSize / (NumPointsPerAxis - 1)));
        //sphereBrushPointSize = (int)ceil(sphereBrushSize / (BoundsSize / (NumPointsPerAxis - 1)));
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

            List<Chunk> chunks = (this.loadedChunks == null) ? new List<Chunk>(FindObjectsOfType<Chunk>()) : this.loadedChunks;
            //foreach (Chunk chunk in chunks)
            for (int i = chunks.Count - 1; i >= 0; i--)
            {
                if (i >= chunks.Count)
                {
                    continue;
                }
                Chunk chunk = chunks[i];
                Bounds bounds = new Bounds(CentreFromCoord(chunk.Position), Vector3.one * BoundsSize);
                Gizmos.color = boundsGizmoCol;
                Gizmos.DrawWireCube(CentreFromCoord(chunk.Position), Vector3.one * BoundsSize);
            }
        }
    }

}