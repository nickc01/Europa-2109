using Assets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using static Chunk;
using static Unity.Mathematics.math;

public class Map : MonoBehaviour
{
    public event Action<Chunk> OnChunkLoad;
    public event Action<Chunk> OnChunkUnload;

    private const int THREAD_GROUP_SIZE = 8;
    private const string chunkHolderName = "Chunks Holder";
    private static Thread MAIN_THREAD;
    private const int MAX_BRUSH_CALLS_PER_FRAME = 1;
    private const int MAX_MESH_UPDATE_CALLS_PER_FRAME = 5;
    private const int CHUNK_OBJECT_SPAWN_LIMIT = 5;


    private static Map _instance;
    public static Map Instance => _instance ??= GameObject.FindObjectOfType<Map>();

    static Map()
    {
        Submarine.OnGameReload += Submarine_OnGameReload;
    }

    private static void Submarine_OnGameReload()
    {
        _instance = null;
    }

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

    [field: SerializeField]
    public float FloorHeight { get; private set; } = -40;

    [field: SerializeField]
    public float CeilingHeight { get; private set; } = 50;

    public bool AutoLoadChunks { get; set; } = true;


    public bool AutoUnloadChunks { get; set; } = true;

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
    private List<int3> meshesToUpdate;
    private ConcurrentBag<int3> meshesTEMP = new ConcurrentBag<int3>();
    private object meshUpdateLock = new object();
    private bool meshListDirty = false;

    private ConcurrentQueue<BrushCall> brushCalls = new ConcurrentQueue<BrushCall>();

    public int PointsPerChunk => NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;
    public int numPointsInChunk => NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;

    public ConcurrentQueue<Chunk> chunksWithObjectsToSpawn = new ConcurrentQueue<Chunk>();
    public ConcurrentQueue<List<GameObject>> objectsToDestroy = new ConcurrentQueue<List<GameObject>>();

    public CancellationToken MainCancelToken = new CancellationToken();


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
        public float IsoOffsetByHeightAbs;
    }

    private class BrushCall
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

    private class AreaGenParameters
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
        //Debug.Log("Target Frame Rate = " + Application.targetFrameRate);
        WorldSeed = UnityEngine.Random.Range(-99999, 99999);
        meshesToUpdate = Unity.VisualScripting.ListPool<int3>.New();
        chunkMeshesToRefresh = Unity.VisualScripting.ListPool<Chunk>.New();
        if (Directory.Exists(Application.persistentDataPath + "/default"))
        {
            Directory.Delete(Application.persistentDataPath + "/default", true);
        }
        CreateBuffers();
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

    private static void DistinctInList<T>(List<T> list)
    {
        HashSet<T> distinct = new HashSet<T>();
        for (int i = 0; i < list.Count; i++)
        {
            distinct.Add(list[i]);
        }

        list.Clear();

        list.AddRange(distinct);

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
                    };
                }
                distanceInt3Sorter.viewerChunkPos = WorldPosToChunkPos(viewer.transform.position);
                DistinctInList(meshesToUpdate);

                CustomAlgorithms.SortMergeAdaptivePar(ref meshesToUpdate, 0, meshesToUpdate.Count, distanceInt3Sorter);





                meshListDirty = false;

            }

            int3 lastCoordinate = new int3(int.MaxValue);
            while (meshUpdateCount < MAX_MESH_UPDATE_CALLS_PER_FRAME)
            {
                if (meshesToUpdate.Count > 0)
                {
                    int3 coordinate = meshesToUpdate[meshesToUpdate.Count - 1];
                    if (!all(coordinate == lastCoordinate) && loadedCoordinates.TryGetValue(coordinate, out Chunk chunk))
                    {
                        lastCoordinate = coordinate;
                        UpdateChunkMesh(chunk);
                        meshUpdateCount++;
                        meshesToUpdate.RemoveAt(meshesToUpdate.Count - 1);
                    }
                    else
                    {
                        meshesToUpdate.RemoveAt(meshesToUpdate.Count - 1);
                    }
                }
                else
                {
                    break;
                }
            }

            int tempMeshesCount = meshesTEMP.Count;

            for (int i = 0; i < tempMeshesCount; i++)
            {
                if (meshesTEMP.TryTake(out int3 coord))
                {
                    meshesToUpdate.Add(coord);
                }
            }
            if (tempMeshesCount > 0)
            {
                meshListDirty = true;
            }
        }


        for (int i = 0; i < CHUNK_OBJECT_SPAWN_LIMIT; i++)
        {
            if (chunksWithObjectsToSpawn.TryDequeue(out Chunk chunk) && chunk != null)
            {
                foreach (ChunkObjectInfo entry in chunk.chunkObjectInfo)
                {
                    GameObject instance = MainPool.Instantiate(ChunkObjectsDictionary.IDDict[entry.ObjectID].Prefab, entry.worldpos, Quaternion.identity);
                    instance.transform.LookAt(entry.lookAtPoint);
                    if (entry.localScale.x != float.PositiveInfinity)
                    {
                        instance.transform.localScale = entry.localScale;
                    }
                    chunk.loadedChunkObjects.Add(instance);
                }
            }
        }

        for (int i = 0; i < CHUNK_OBJECT_SPAWN_LIMIT; i++)
        {
            if (objectsToDestroy.TryDequeue(out List<GameObject> objects) && objects != null)
            {
                foreach (GameObject obj in objects)
                {
                    MainPool.Return(obj);
                }
            }
        }

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


            DistinctInList(chunkMeshesToRefresh);
            CustomAlgorithms.SortMergeAdaptivePar(ref chunkMeshesToRefresh, 0, chunkMeshesToRefresh.Count, distanceSorter);

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

            await completer.Task;
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

                        if (float.IsNaN(viewDistance) || sqrDst <= sqrViewDistance)
                        {
                            bool usingCached = false;
                            while (unloadedChunkPool.TryDequeue(out Chunk chunk))
                            {
                                if (chunk == null)
                                {
                                    continue;
                                }
                                usingCached = true;
                                chunk.Position = coord;
                                if (keepChunksLoaded)
                                {
                                    chunk.KeepLoaded = true;
                                }

                                out_chunksInArea?.Add(chunk);

                                chunksToRender.Add(chunk);
                                taskPool.Add(chunk.Init(this, true));
                                break;
                            }
                            if (!usingCached)
                            {
                                chunksToCreate.Add(coord);
                            }
                        }

                    }
                }
            }
        });

        foreach (int3 coord in chunksToCreate)
        {
            Chunk chunk = CreateChunk(coord);
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

        foreach (Chunk chunk in destroyedChunks)
        {
            chunk.MainRenderer.enabled = false;
            chunk.Mesh.Clear();
        }

        chunksToRender.Clear();

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
            }
        }
        finally
        {
            if (locked)
            {
                Monitor.Exit(meshUpdateLock);
            }
        }
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

        mesh.SetVertices(meshVertices, 0, numTris * 3, UnityEngine.Rendering.MeshUpdateFlags.Default);
        mesh.SetTriangles(meshTriangles, 0, numTris * 3, 0, false);
        mesh.RecalculateNormals();

        chunk.OnMeshUpdate();

        chunkMeshesToRefresh.Add(chunk);
        refreshListDirty = true;

        ReturnBufferSet(bufferSet);
    }

    public float3 GetNormalOfPoint(float3 p)
    {
        float step = BoundsSize / (NumPointsPerAxis + 1);
        float3 nrm = default;



        nrm.x = (SamplePoint(p + float3(step, 0, 0)) / IsoLevel) - (SamplePoint(p - float3(step, 0, 0)) / IsoLevel);
        nrm.y = (SamplePoint(p + float3(0, step, 0)) / IsoLevel) - (SamplePoint(p - float3(0, step, 0)) / IsoLevel);
        nrm.z = (SamplePoint(p + float3(0, 0, step)) / IsoLevel) - (SamplePoint(p - float3(0, 0, step)) / IsoLevel);


        nrm = -normalize(nrm);
        return nrm;
    }

    private void OnDestroy()
    {
        if (Application.isPlaying)
        {
            ReleaseBuffers();

            string folder = Application.persistentDataPath;
            Parallel.For(0, loadedChunks.Count, i =>
            {
                loadedChunks[i].SaveSynchronously(folder);
            });

        }
    }

    public void UseSphereBrush(float3 worldPos, bool EraseMode, float intensity, float3 sphereBrushSize)
    {
        float3 magicIntensity = (sphereBrushSize / 5f) * 6f;

        float3 pos = worldPos;
        float brushValue = intensity * IsoLevel;
        if (EraseMode) brushValue *= -1;
        int3 sphereBrushPointSize = (int3)ceil(sphereBrushSize / (BoundsSize / (NumPointsPerAxis)));
        int width = sphereBrushPointSize.x * 2;
        int height = sphereBrushPointSize.y * 2;
        int depth = sphereBrushPointSize.z * 2;

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

            float3 samplePoint = new float3(tx, ty, tz) + pos;

            if (samplePoint.y > CeilingHeight || samplePoint.y < FloorHeight)
            {
                return;
            }


            float3 relativeCoords = new float3(x, y, z) - sphereBrushPointSize;

            float3 sample3D = (relativeCoords * relativeCoords) / (sphereBrushPointSize * sphereBrushPointSize);
            float brushSample = -csum((sample3D - (1f / 3f)) * magicIntensity);

            if (brushSample < 0)
            {
                brushSample = 0;
            }

            PaintPointAdd(samplePoint, brushSample * brushValue);
        });

        AddChunkMeshesToBeUpdated(bl, tr);
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

        int3 cubeBrushPointSize = (int3)ceil(cubeBrushSize / (BoundsSize / NumPointsPerAxis));

        int width = cubeBrushPointSize.x * 2;
        int height = cubeBrushPointSize.y * 2;
        int depth = cubeBrushPointSize.z * 2;

        Vector3 camPos = MainCamera.transform.position;

        int3 bl = WorldPosToChunkPos(worldPos - new float3(1) - new float3(cubeBrushSize));
        int3 tr = WorldPosToChunkPos(worldPos + new float3(1) + new float3(cubeBrushSize));

        Parallel.For(0, width * height * depth, i =>
        {
            int x = i % width;
            int y = (i / width) % height;
            int z = i / (width * height);

            int3 point = new int3(x, y, z);


            float3 samplePoint = pos + ((float3)point - cubeBrushPointSize) * (BoundsSize / (NumPointsPerAxis));


            float brushSample = GetCubeBrushValueAtPoint(point - cubeBrushPointSize, cubeBrushPointSize);

            PaintPointAdd(samplePoint, brushSample * brushValue);
        });

        AddChunkMeshesToBeUpdated(bl, tr);



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
                if (i == 0)
                {
                    hit = source + (travelDirection * i);
                }
                else
                {
                    float3 previousHit = source + (travelDirection * (i - 1));
                    float3 nextHit = source + (travelDirection * i);

                    float previousValue = raySampleCache[i - 1];
                    float nextValue = raySampleCache[i];

                    float tValue = unlerp(previousValue, nextValue, IsoLevel);

                    hit = lerp(previousHit, nextHit, tValue);
                    if (float.IsInfinity(hit.x) || float.IsNaN(hit.x))
                    {
                        hit = nextHit;
                    }
                }

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
                chunkPosTemp.z = chunkPos.z;
                pointIndex = PointPosToIndex(x, y, pointPosition.z);
                SetData();

                chunkPosTemp.z = chunkPos.z + DI.z;
                chunkPosTemp.x = chunkPos.x;
                pointIndex = PointPosToIndex(pointPosition.x, y, z);
                SetData();

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
    }

    private void OnValidate()
    {
        settingsUpdated = true;
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

    private struct Triangle
    {
#pragma warning disable 649     
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

            List<Chunk> chunks = (loadedChunks == null) ? new List<Chunk>(FindObjectsOfType<Chunk>()) : loadedChunks;
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