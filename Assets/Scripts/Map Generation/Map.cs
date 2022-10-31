using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static UnityEngine.EventSystems.EventTrigger;

using static Unity.Mathematics.math;
using HPCsharp;

public class Map : MonoBehaviour
{
    //const bool BRUSH_GPU_MODE = true;

    const int THREAD_GROUP_SIZE = 8;

    static Map _instance;
    public static Map Instance => _instance ??= GameObject.FindObjectOfType<Map>();

    public string WorldName { get; private set; } = "default";

    [field: SerializeField]
    public Camera MainCamera { get; private set; }

    [field: Header("General Settings")]
    [field: SerializeField]
    public MapGenerator DensityGenerator { get; private set; }

    public Transform viewer;
    [field: SerializeField]
    public float ViewDistance { get; private set; } = 30;

    [Space]
    //public bool autoUpdateInEditor = true;
    //public bool autoUpdateInGame = true;
    public ComputeShader generatorShader;
    [field: SerializeField]
    public Material ChunkMateral { get; private set; }
    public bool generateColliders;

    [Header("Voxel Settings")]
    public float isoLevel;
    public float boundsSize = 1;
    public Vector3 offset = Vector3.zero;

    [Range(2, 100)]
    public int numPointsPerAxis = 30;

    [Header("Gizmos")]
    public bool showBoundsGizmo = true;
    public Color boundsGizmoCol = Color.white;

    GameObject chunkHolder;
    const string chunkHolderName = "Chunks Holder";
    List<Chunk> chunks = new List<Chunk>();
    Dictionary<Vector3Int, Chunk> existingChunks = new Dictionary<Vector3Int, Chunk>();
    HashSet<Vector3Int> chunkCoordsUsed = new HashSet<Vector3Int>();
    ConcurrentQueue<Chunk> recycleableChunks = new ConcurrentQueue<Chunk>();

    [Header("Sphere Brush Setting")]
    public float sphereBrushSpeed = 1;
    [SerializeField]
    float sphereBrushSize = 1;
    [SerializeField]
    ComputeShader sphereBrushShader;

    [Header("Cube Brush Setting")]
    public Vector3 cubeBrushSize = new Vector3(5,5,5);
    public int cubeBrushSpeed = 1;

    public float SphereBrushSize
    {
        get => sphereBrushSize;
        set
        {
            sphereBrushSize = value;
            sphereBrushPointSize = Mathf.CeilToInt(sphereBrushSize / (boundsSize / (numPointsPerAxis - 1)));
        }
    }

    /*
             recycleableChunks = new Queue<Chunk> ();
        chunks = new List<Chunk> ();
        existingChunks = new Dictionary<Vector3Int, Chunk> ();
        chunkCoordsUsed = new HashSet<Vector3Int>();
     */

    bool runningUpdate = false;

    // Buffers

    //List<ComputeBuffer> unusedPointBuffers = new List<ComputeBuffer>();
    //List<ComputeBuffer> usedPointBuffers = new List<ComputeBuffer>();
    Queue<BufferSet> unusedBuffers = new Queue<BufferSet>();
    HashSet<BufferSet> usedBuffers = new HashSet<BufferSet>();

    public int PointsPerChunk => numPointsPerAxis * numPointsPerAxis * numPointsPerAxis;

    //int numVoxelPerAxis = numPointsPerAxis - 1;

    public int numPointsInChunk => numPointsPerAxis* numPointsPerAxis * numPointsPerAxis;

    List<Task> taskPool = new List<Task>();

    Chunk.DistanceSorter distanceSorter;

    class BufferSet
    {
        public ComputeBuffer triangleBuffer;
        public ComputeBuffer pointsBuffer;
        public ComputeBuffer triCountBuffer;
    }

    bool settingsUpdated;

    void Awake()
    {
        sphereBrushPointSize = Mathf.CeilToInt(sphereBrushSize / (boundsSize / (numPointsPerAxis - 1)));
        if (Application.isPlaying)
        {

            var oldChunks = FindObjectsOfType<Chunk>();
            for (int i = oldChunks.Length - 1; i >= 0; i--)
            {
                Destroy(oldChunks[i].gameObject);
            }
        }
    }

    


    List<Chunk> chunkMeshesToRefresh = new List<Chunk>();
    bool refreshListDirty = true;

    async void FixedUpdate()
    {
        // Update endless terrain
        if (!runningUpdate && Application.isPlaying)
        {
            runningUpdate = true;
            //Debug.Log("A");
            await Run();
            //Debug.Log("RUNNING UPDATE");
            //Task.Run(Run().ConfigureAwait(true));
        }

        if (settingsUpdated && !runningUpdate)
        {
            //RequestMeshUpdate ();
            runningUpdate = true;
            //Debug.Log("B");
            await Run();
            //Task.Run(Run().ConfigureAwait(true));
            settingsUpdated = false;
        }
    }

    private void Update()
    {
        UpdateChunkCollider();
    }

    void UpdateChunkCollider()
    {
        if (refreshListDirty)
        {
            refreshListDirty = false;

            if (distanceSorter == null)
            {
                distanceSorter = new Chunk.DistanceSorter();
                distanceSorter.Viewer = viewer;
            }

            chunkMeshesToRefresh.SortMerge(distanceSorter);
        }
        /*if (chunkMeshesToRefresh.Count != previousCount)
        {
            previousCount = chunkMeshesToRefresh.Count;
            if (distanceSorter == null)
            {
                distanceSorter = new Chunk.DistanceSorter();
                distanceSorter.Viewer = viewer;
            }
            chunkMeshesToRefresh.Sort(distanceSorter);
        }*/
        while (chunkMeshesToRefresh.Count > 0)
        {
            var chunk = chunkMeshesToRefresh[0];
            chunkMeshesToRefresh.RemoveAt(0);
            //previousCount--;
            if (chunk.gameObject.activeSelf)
            {
                chunk.MainCollider.sharedMesh = chunk.Mesh;
                break;
            }
        }
    }

    public async Task Run()
    {

        try
        {
            /*if (brushShape.Count == 0)
            {
                InitBrush();
            }*/
            CreateBuffers();

            await InitVisibleChunks();

            // Release buffers immediately in editor
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

    /*public async Task RequestMeshUpdate()
    {
        if ((Application.isPlaying && autoUpdateInGame) || (!Application.isPlaying && autoUpdateInEditor))
        {
            await Run();
        }
    }*/

    /*void InitVariableChunkStructures()
    {
        recycleableChunks = new ConcurrentQueue<Chunk>();
        chunks = new List<Chunk>();
        existingChunks = new Dictionary<Vector3Int, Chunk>();
        chunkCoordsUsed = new HashSet<Vector3Int>();
    }*/


    ConcurrentBag<Chunk> chunksToRender = new ConcurrentBag<Chunk>();

    async Task InitVisibleChunks()
    {

        if (chunks == null)
        {
            return;
        }
        CreateChunkHolder();

        Vector3 p = viewer.position;
        Vector3 ps = p / boundsSize;
        Vector3Int viewerCoord = new Vector3Int(Mathf.RoundToInt(ps.x), Mathf.RoundToInt(ps.y), Mathf.RoundToInt(ps.z));

        int maxChunksInView = Mathf.CeilToInt(ViewDistance / boundsSize);
        float sqrViewDistance = ViewDistance * ViewDistance;

        taskPool.Clear();

        // Go through all existing chunks and flag for recyling if outside of max view dst
        for (int i = chunks.Count - 1; i >= 0; i--)
        {
            Chunk chunk = chunks[i];
            Vector3 centre = CentreFromCoord(chunk.Position);
            Vector3 viewerOffset = p - centre;
            Vector3 o = new Vector3(Mathf.Abs(viewerOffset.x), Mathf.Abs(viewerOffset.y), Mathf.Abs(viewerOffset.z)) - Vector3.one * boundsSize / 2;
            float sqrDst = new Vector3(Mathf.Max(o.x, 0), Mathf.Max(o.y, 0), Mathf.Max(o.z, 0)).sqrMagnitude;
            if (sqrDst > sqrViewDistance)
            {
                existingChunks.Remove(chunk.Position);
                chunkCoordsUsed.Remove(chunk.Position);
                chunks.RemoveAt(i);

                taskPool.Add(ChunkEnd(chunk));

                async Task ChunkEnd(Chunk chunk)
                {
                    await chunk.Uninit();
                    recycleableChunks.Enqueue(chunk);
                }
            }
        }


        //await Task.WhenAll(taskPool);
        //taskPool.Clear();
        //Debug.Log("STARTING VISIBLE CHECKS");
        //int vectorSize = System.Numerics.Vector<float>.Count;
        //System.Numerics.Vector.
        chunksToRender.Clear();

        for (int x = -maxChunksInView; x <= maxChunksInView; x++)
        {
            for (int y = -maxChunksInView; y <= maxChunksInView; y++)
            {
                for (int z = -maxChunksInView; z <= maxChunksInView; z++)
                {
                    Vector3Int coord = new Vector3Int(x, y, z) + viewerCoord;

                    /*if (existingChunks.ContainsKey (coord)) {
                        continue;
                    }*/

                    //Debug.Log("chunkCoordsUsed = " + chunkCoordsUsed);
                    if (chunkCoordsUsed.Contains(coord))
                    {
                        continue;
                    }

                    Vector3 centre = CentreFromCoord(coord);
                    Vector3 viewerOffset = p - centre;
                    Vector3 o = new Vector3(Mathf.Abs(viewerOffset.x), Mathf.Abs(viewerOffset.y), Mathf.Abs(viewerOffset.z)) - Vector3.one * boundsSize / 2;
                    float sqrDst = new Vector3(Mathf.Max(o.x, 0), Mathf.Max(o.y, 0), Mathf.Max(o.z, 0)).sqrMagnitude;

                    // Chunk is within view distance and should be created (if it doesn't already exist)
                    if (sqrDst <= sqrViewDistance)
                    {

                        Bounds bounds = new Bounds(CentreFromCoord(coord), Vector3.one * boundsSize);
                        if (IsVisibleFrom(bounds, MainCamera))
                        {
                            /*if (recycleableChunks.Count > 0) {
                                Chunk chunk = recycleableChunks.Dequeue ();
                                chunk.coord = coord;
                                chunk.SetUp(mat, generateColliders, true);
                                existingChunks.Add (coord, chunk);
                                chunkCoordsUsed.Add(coord);
                                chunks.Add (chunk);
                                UpdateChunkMesh (chunk);
                            } else {
                                Chunk chunk = CreateChunk (coord);
                                chunk.coord = coord;
                                chunk.SetUp (mat, generateColliders, false);
                                existingChunks.Add (coord, chunk);
                                chunkCoordsUsed.Add(coord);
                                chunks.Add (chunk);
                                UpdateChunkMesh (chunk);
                            }*/

                            Chunk chunk;
                            bool cached = true;

                            if (!recycleableChunks.TryDequeue(out chunk))
                            {
                                cached = false;
                                chunk = CreateChunk(coord);
                            }

                            chunk.Position = coord;
                            existingChunks.Add(coord, chunk);
                            chunkCoordsUsed.Add(coord);
                            chunks.Add(chunk);

                            //Debug.Log("ADDING CHUNK");

                            taskPool.Add(setupAndUpdateChunk(chunk, cached));

                            async Task setupAndUpdateChunk(Chunk chunk, bool cached)
                            {
                                await chunk.Init(this, cached);
                                //Debug.Log("UPDATING MESH");
                                chunksToRender.Add(chunk);
                            }
                        }
                    }

                }
            }
        }

        await Task.WhenAll(taskPool);

        foreach (var chunk in chunksToRender)
        {
            UpdateChunkMesh(chunk);
        }

        chunksToRender.Clear();
    }

    Plane[] planeCache = new Plane[6];

    public bool IsVisibleFrom(Bounds bounds, Camera camera)
    {
        //Plane[] planes = GeometryUtility.CalculateFrustumPlanes (camera);
        GeometryUtility.CalculateFrustumPlanes(camera, planeCache);
        return GeometryUtility.TestPlanesAABB(planeCache, bounds);
    }

    Triangle[] triangleCache;
    float[] pointCache;

    float[] sampleBuffer;

    void UpdateChunkMesh(Chunk chunk, BufferSet bufferSet = null, bool pointsAlreadySet = false)
    {
        int numPoints = numPointsPerAxis * numPointsPerAxis * numPointsPerAxis;
        //Debug.Log("Updating Chunk Mesh");
        int numVoxelsPerAxis = numPointsPerAxis - 1;
        int numThreadsPerAxis = Mathf.CeilToInt(numVoxelsPerAxis / (float)THREAD_GROUP_SIZE);
        float pointSpacing = boundsSize / (numPointsPerAxis - 1);

        Vector3Int coord = chunk.Position;
        Vector3 centre = CentreFromCoord(coord);

        Vector3 worldBounds = new Vector3(boundsSize, boundsSize, boundsSize);

        if (bufferSet == null)
        {
            bufferSet = GetBufferSet();
        }

        if (!pointsAlreadySet)
        {
            if (chunk.PointsLoaded)
            {
                bufferSet.pointsBuffer.SetData(chunk.Points, 0, 0, numPoints);
                /*var writer = bufferSet.pointsBuffer.BeginWrite<float>(0, numPoints);
                writer.CopyFrom(chunk.Points);
                bufferSet.pointsBuffer.EndWrite<float>(numPoints);*/
            }
            else
            {
                DensityGenerator.Generate(bufferSet.pointsBuffer, numPointsPerAxis, boundsSize, worldBounds, centre, offset, pointSpacing);
            }
        }

        bufferSet.triangleBuffer.SetCounterValue(0);
        generatorShader.SetBuffer(0, "points", bufferSet.pointsBuffer);
        generatorShader.SetBuffer(0, "triangles", bufferSet.triangleBuffer);
        generatorShader.SetInt("numPointsPerAxis", numPointsPerAxis);

        generatorShader.SetFloat("boundsSize", boundsSize);
        generatorShader.SetVector("centre", new Vector4(centre.x, centre.y, centre.z));
        generatorShader.SetFloat("spacing", pointSpacing);

        generatorShader.SetFloat("isoLevel", isoLevel);

        generatorShader.Dispatch(0, numThreadsPerAxis, numThreadsPerAxis, numThreadsPerAxis);

        // Get number of triangles in the triangle buffer
        ComputeBuffer.CopyCount(bufferSet.triangleBuffer, bufferSet.triCountBuffer, 0);
        int[] triCountArray = { 0 };
        bufferSet.triCountBuffer.GetData(triCountArray);
        int numTris = triCountArray[0];

        // Get triangle data from shader
        //Triangle[] tris = new Triangle[numTris];

        //AsyncGPUReadback.Request(bufferSet.triangleBuffer)

        //AsyncGPUReadback.

        bufferSet.triangleBuffer.GetData(triangleCache, 0, 0, numTris);

        //Point[] points = new Point[numPoints];



        if (!chunk.PointsLoaded || pointsAlreadySet)
        {
            bufferSet.pointsBuffer.GetData(pointCache, 0, 0, numPoints);

            if (chunk.Points == null)
            {
                chunk.Points = new float[numPoints];
            }

            Array.Copy(pointCache, chunk.Points, numPoints);

            chunk.PointsLoaded = true;

            sampleBuffer = pointCache;
        }
        else
        {
            sampleBuffer = chunk.Points;
        }
        /*else
        {
            pointCache = chunk.Points;
        }*/
        //PointsLoaded


        Mesh mesh = chunk.Mesh;
        mesh.Clear();

        var vertices = new Vector3[numTris * 3];
        var meshTriangles = new int[numTris * 3];

        /*for (int i = 0; i < numTris; i++)
        {
            
        }*/

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

        //List<Vector3> normals = new List<Vector3>();

        for (int i = 0; i < vertices.Length; i++)
        {
            //Presumes the vertex is in local space where
            //the min value is 0 and max is width/height/depth.
            /*Vector3 p = vertices[i];

            float u = p.x / (numPointsPerAxis - 1.0f);
            float v = p.y / (numPointsPerAxis - 1.0f);
            float w = p.z / (numPointsPerAxis - 1.0f);

            Vector3 n = GetNormal(u, v, w);*/

            /*float step = 0.01; // or whatever
            Vector3 nrm = default;
            nrm.x = F(p + Vector3(step, 0, 0)) - F(p - Vector3(step, 0, 0));
            nrm.y = F(p + Vector3(0, step, 0)) - F(p - Vector3(0, step, 0));
            nrm.z = F(p + Vector3(0, 0, step)) - F(p - Vector3(0, 0, step));
            nrm = normalize(nrm);*/

            //normals.Add(n.normalized);
        }

        //mesh.normals = normals.ToArray();
        //mesh.normal

        mesh.RecalculateNormals();
        //mesh.Optimize();

        chunk.OnMeshUpdate();

        chunkMeshesToRefresh.Add(chunk);

        ReturnBufferSet(bufferSet);
    }

    /*float F()
    {

    }*/

    private static float Lerp(float v0, float v1, float t)
    {
        return v0 + (v1 - v0) * t;
    }

    private static float BLerp(float v00, float v10, float v01, float v11, float tx, float ty)
    {
        return Lerp(Lerp(v00, v10, tx), Lerp(v01, v11, tx), ty);
    }

    public float GetVoxel(int x, int y, int z)
    {
        x = Mathf.Clamp(x, 0, numPointsPerAxis - 1);
        y = Mathf.Clamp(y, 0, numPointsPerAxis - 1);
        z = Mathf.Clamp(z, 0, numPointsPerAxis - 1);
        return sampleBuffer[PointPosToIndex(x,y,z)];
    }

    public float GetVoxel(float u, float v, float w)
    {
        float x = u * (numPointsPerAxis - 1);
        float y = v * (numPointsPerAxis - 1);
        float z = w * (numPointsPerAxis - 1);

        int xi = (int)Mathf.Floor(x);
        int yi = (int)Mathf.Floor(y);
        int zi = (int)Mathf.Floor(z);

        float v000 = GetVoxel(xi, yi, zi);
        float v100 = GetVoxel(xi + 1, yi, zi);
        float v010 = GetVoxel(xi, yi + 1, zi);
        float v110 = GetVoxel(xi + 1, yi + 1, zi);

        float v001 = GetVoxel(xi, yi, zi + 1);
        float v101 = GetVoxel(xi + 1, yi, zi + 1);
        float v011 = GetVoxel(xi, yi + 1, zi + 1);
        float v111 = GetVoxel(xi + 1, yi + 1, zi + 1);

        float tx = Mathf.Clamp01(x - xi);
        float ty = Mathf.Clamp01(y - yi);
        float tz = Mathf.Clamp01(z - zi);

        //use bilinear interpolation the find these values.
        float v0 = BLerp(v000, v100, v010, v110, tx, ty);
        float v1 = BLerp(v001, v101, v011, v111, tx, ty);

        //Now lerp those values for the final trilinear interpolation.
        return Lerp(v0, v1, tz);
    }

    public Vector3 GetNormal(float u, float v, float w)
    {
        var n = GetFirstDerivative(u, v, w);

        return n.normalized * -1;
        /*if (FlipNormals)
            return n.normalized * -1;
        else
            return n.normalized;*/
    }

    public Vector3 GetFirstDerivative(float u, float v, float w)
    {
        const float h = 0.005f;
        const float hh = h * 0.5f;
        const float ih = 1.0f / h;

        float dx_p1 = GetVoxel(u + hh, v, w);
        float dy_p1 = GetVoxel(u, v + hh, w);
        float dz_p1 = GetVoxel(u, v, w + hh);

        float dx_m1 = GetVoxel(u - hh, v, w);
        float dy_m1 = GetVoxel(u, v - hh, w);
        float dz_m1 = GetVoxel(u, v, w - hh);

        float dx = (dx_p1 - dx_m1) * ih;
        float dy = (dy_p1 - dy_m1) * ih;
        float dz = (dz_p1 - dz_m1) * ih;

        return new Vector3(dx, dy, dz);
    }

    public void UpdateAllChunks()
    {

        // Create mesh for each chunk
        foreach (Chunk chunk in chunks)
        {
            UpdateChunkMesh(chunk);
        }

    }

    void OnDestroy()
    {
        if (Application.isPlaying)
        {
            ReleaseBuffers();

            foreach (var chunk in chunks)
            {
                if (!chunksToRender.Contains(chunk))
                {
                    chunk.SaveSynchronously();
                }
            }
        }
    }

    /*IEnumerator UseBrush(KeyCode inputKey, bool eraseMode)
    {
        Vector2 input = new Vector2(Screen.width / 2.0f, Screen.height / 2.0f);
        RaycastHit hit;
        while (Input.GetKey(inputKey))
        {
            Ray ray = MainCamera.ScreenPointToRay(input);

            if (Physics.Raycast(ray, out hit, 300))// && !hit.collider.tag.Equals("Untagged"))
            {
                //PointIndicator.position = hit.point;
                UseBrush(hit.point, eraseMode);
            }
            yield return null;
        }
    }*/

    /*Vector3Int PosToChunkIdx3D(Vector3Int p)
    {
        return PosToChunkIdx3D(p.x, p.y, p.z);
    }
    Vector3Int PosToChunkIdx3D(float x, float y, float z)
    {
        Vector3Int result = new Vector3Int(Mathf.FloorToInt(x / numPointsPerAxis), Mathf.FloorToInt(y / numPointsPerAxis), Mathf.FloorToInt(z / numPointsPerAxis));
        return result;
    }*/

    /*int PosToIndex(int x, int y, int z)
    {
        return z * numPointsPerAxis * numPointsPerAxis + y * numPointsPerAxis + x;
    }*/

    /*HashSet<Chunk> chunkUpdateList = new HashSet<Chunk>();
    ReaderWriterLock updateListLock = new ReaderWriterLock();

    void ClearChunkUpdateList()
    {
        chunkUpdateList.Clear();
    }

    bool AddChunkToBeUpdated(Chunk chunk)
    {
        try
        {
            updateListLock.AcquireReaderLock(1000);
            for (int i = 0; i < chunkUpdateList.Count; i++)
            {
                if (chunkUpdateList.Contains(chunk))
                {
                    return false;
                }
            }
        }
        finally
        {
            updateListLock.ReleaseReaderLock();
        }

        try
        {
            updateListLock.AcquireWriterLock(1000);
            chunkUpdateList.Add(chunk);
            return true;
        }
        finally
        {
            updateListLock.ReleaseWriterLock();
        }
    }*/

    Chunk[] chunkUpdateList = new Chunk[16];
    int chunkUpdateCount = 0;
    object chunkUpdateLock = new object();

    bool AddChunkToBeUpdated(Chunk chunk)
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

            /*if (chunkUpdateCount == 0 || (chunkUpdateCount > 0 && chunkUpdateList[chunkUpdateCount - 1] != chunk))
            {
                chunkUpdateList[chunkUpdateCount] = chunk;
                chunkUpdateCount++;
                return true;
            }
            return false;*/
        }
    }

    void ClearChunkUpdateList()
    {
        chunkUpdateCount = 0;
    }

    public void UseSphereBrush(Vector3 worldPos, bool EraseMode, float intensity)
    {
        UseSphereBrushCPU(worldPos, EraseMode, intensity);
    }

    float CubicLength(Vector3Int value)
    {
        return Mathf.Sqrt((value.x * value.x) + (value.y * value.y) + (value.z * value.z));
    }

    public void UseSphereBrushGPU(Vector3 worldPos, bool EraseMode)
    {
        /*int numVoxelsPerAxis = numPointsPerAxis - 1;
        int numThreadsPerAxis = Mathf.CeilToInt(numVoxelsPerAxis / (float)THREAD_GROUP_SIZE);

        float brushValue = Time.deltaTime * sphereBrushSpeed * isoLevel;
        if (EraseMode) brushValue *= -1;

        //var sphereDist = PointPosToWorldPos(Vector3Int.zero,);


        var lowerCorner = worldPos + new Vector3(-CubeBrushPointSize.x, -CubeBrushPointSize.y, -CubeBrushPointSize.z) * (boundsSize / (numPointsPerAxis - 1));
        var upperCorner = worldPos + new Vector3(CubeBrushPointSize.x, CubeBrushPointSize.y, CubeBrushPointSize.z) * (boundsSize / (numPointsPerAxis - 1));

        var lowerChunkCorner = WorldPosToChunkPos(lowerCorner);
        var upperChunkCorner = WorldPosToChunkPos(upperCorner);

        var buffer = GetBufferSet();

        sphereBrushShader.SetBuffer(0, "points", buffer.pointsBuffer);
        sphereBrushShader.SetInt("numPointsPerAxis", numPointsPerAxis);
        sphereBrushShader.SetInt("numVoxelPerAxis", numVoxelPerAxis);
        sphereBrushShader.SetInt("sphereBrushPointSize", SphereBrushPointSize);
        sphereBrushShader.SetFloat("brushValue", brushValue);
        sphereBrushShader.SetFloat("boundsSize", boundsSize);

        Debug.Log("BRUSH VALUE = " + brushValue);
        Debug.Log("BRUSH SIZE = " + boundsSize);
        Debug.Log("Sphere Brush Point Size = " + SphereBrushPointSize);

        for (int x = lowerChunkCorner.x; x <= upperChunkCorner.x; x++)
        {
            for (int y = lowerChunkCorner.y; y <= upperChunkCorner.y; y++)
            {
                for (int z = lowerChunkCorner.z; z <= upperChunkCorner.z; z++)
                {
                    var chunkPos = new Vector3Int(x,y,z);
                    if (existingChunks.ContainsKey(chunkPos))
                    {
                        var chunk = existingChunks[chunkPos];

                        buffer.pointsBuffer.SetData(chunk.Points, 0, 0, chunk.Points.Length);

                        var relativeBrushPos = WorldPosToPointPos(chunkPos, worldPos);
                        sphereBrushShader.SetInts("chunkPosition", chunkPos.x, chunkPos.y, chunkPos.z);
                        sphereBrushShader.SetFloats("brushRelativePosition", relativeBrushPos.x, relativeBrushPos.y, relativeBrushPos.z);
                        Debug.Log("BRUSH RELATIVE POSITION = " + relativeBrushPos);
                        var demoPoint = new Vector3Int(5, 5, 5);
                        Debug.Log("DEMO POINT = " + demoPoint);
                        Debug.Log("DEMO Difference = " + (demoPoint - relativeBrushPos));
                        Debug.Log("DEMO DIFFERENCE Length = " + CubicLength(demoPoint - relativeBrushPos));
                        sphereBrushShader.Dispatch(0, numThreadsPerAxis, numThreadsPerAxis, numThreadsPerAxis);

                        //Debug.Log($"DISPATCHING IN CHUNK = {chunk.Position}");
                        //Debug.Log("BRUSH WORLD POS = " + worldPos);
                        //Debug.Log("BRUSH RELATIVE POS = " + relativeBrushPos);
                        Debug.DrawLine(MainCamera.transform.position, worldPos,Color.red,10f);

                        //buffer.pointsBuffer.GetData(chunk.Points);

                        UpdateChunkMesh(chunk,buffer, true);

                        Parallel.For(0, chunk.Points.Length, index =>
                        {
                            Debug.Log($"Value for index = {IndexToPointPos(index)} = {chunk.Points[index]}");
                        });
                        //chunkMeshesToRefresh.Add(chunk);
                    }
                }
            }
        }*/

        /*List<Chunk> GetAffectedChunks()
        {

        }

        */
    }



    public void UseSphereBrushCPU(Vector3 worldPos, bool EraseMode, float intensity)
    {
        float3 pos = worldPos;
        //float brushValue = Time.fixedDeltaTime * sphereBrushSpeed * isoLevel;
        float brushValue = intensity * sphereBrushSpeed * isoLevel;
        if (EraseMode) brushValue *= -1;

        var width = sphereBrushPointSize * 2;

        Parallel.For(0, width * width * width, i =>
        {
            int x = i % width;
            int y = (i / width) % width;
            int z = i / (width * width);


            float tx = lerp(-sphereBrushSize,sphereBrushSize,x / (float)width);
            float ty = lerp(-sphereBrushSize,sphereBrushSize,y / (float)width);
            float tz = lerp(-sphereBrushSize,sphereBrushSize,z / (float)width);

            //var samplePoint = pos + new Vector3(tx, ty, tz);
            //var samplePoint = pos + (new Vector3(x - SphereBrushPointSize, y - SphereBrushPointSize, z - SphereBrushPointSize) * (boundsSize / (numPointsPerAxis - 1)));

            var samplePoint = pos + ((new float3(x,y,z) - new float3(sphereBrushPointSize)) * (boundsSize / (numPointsPerAxis - 1)));

            //var brushSample = GetSphereBrushValueAtPoint(x - sphereBrushPointSize, y - sphereBrushPointSize, z - sphereBrushPointSize);
            var brushSample = GetSphereBrushValueAtPoint(new int3(x,y,z) - new int3(sphereBrushPointSize));

            PaintPointAdd(samplePoint, brushSample * brushSample * brushValue);
        });

        for (int i = 0; i < chunkUpdateCount; i++)
        {
            var chunk = chunkUpdateList[i];
            if (chunk != null)
            {
                UpdateChunkMesh(chunk);
                //chunk.MainCollider.sharedMesh = chunk.Mesh;
                //chunk.MainCollider.convex = true;
                //chunk.MainCollider.convex = false;

                for (int j = i; j < chunkUpdateCount; j++)
                {
                    if (chunkUpdateList[j] == chunk)
                    {
                        chunkUpdateList[j] = null;
                    }
                }
            }
        }

        /*var nearestPoint = Vector3.MoveTowards(worldPos, MainCamera.transform.position, Mathf.Min(sphereBrushSize / 2,Vector3.Distance(worldPos,MainCamera.transform.position)));

        var nearestChunk = WorldPosToChunkPos(nearestPoint);

        if (existingChunks.TryGetValue(new Vector3Int(nearestChunk.x, nearestChunk.y, nearestChunk.z), out var nearChunk))
        {
            nearChunk.MainCollider.sharedMesh = nearChunk.Mesh;
        }

        Debug.DrawLine(MainCamera.transform.position, nearestPoint, Color.yellow, 10f);*/

        ClearChunkUpdateList();
    }

    public void UseCubeBrush(Vector3 worldPos, bool EraseMode)
    {
        Vector3 pos = worldPos;
        float brushValue = Time.deltaTime * cubeBrushSpeed * isoLevel;
        if (EraseMode) brushValue *= -1;

        var width = CubeBrushPointSize.x * 2;
        var height = CubeBrushPointSize.y * 2;
        var depth = CubeBrushPointSize.z * 2;

        var camPos = MainCamera.transform.position;

        Debug.DrawRay(camPos, pos,Color.blue,20f);

        Parallel.For(0, width * height * depth, i =>
        {
            int x = i % width;
            int y = (i / width) % height;
            int z = i / (width * height);


            var samplePoint = pos + (new Vector3(x - CubeBrushPointSize.x, y - CubeBrushPointSize.y, z - CubeBrushPointSize.z) * (boundsSize / (numPointsPerAxis - 1)));

            var brushSample = GetCubeBrushValueAtPoint(x - CubeBrushPointSize.x, y - CubeBrushPointSize.y, z - CubeBrushPointSize.z);

            Debug.DrawRay(camPos, samplePoint,Color.red,20f);

            PaintPointAdd(samplePoint, brushSample * brushSample * brushValue);
        });

        /*foreach (var chunk in chunkUpdateList)
        {
            UpdateChunkMesh(chunk);

            chunk.MainCollider.sharedMesh = chunk.Mesh;
        }*/
        for (int i = 0; i < chunkUpdateCount; i++)
        {
            var chunk = chunkUpdateList[i];
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
        var chunkPos = WorldPosToChunkPos(worldPosition);

        if (TryGetChunkAtChunkPos(chunkPos,out var chunk))
        {
            PaintPoint(chunk, WorldPosToPointPos(chunkPos, worldPosition), value);
        }
    }

    public void PaintPoint(Chunk chunk, int3 pointPosition, float value)
    {
        var index = PointPosToIndex(pointPosition);

        if (chunk.Points[index] != value)
        {
            chunk.Points[index] = value;

            chunk.Points[index] = clamp(chunk.Points[index], isoLevel - 2, isoLevel + 2);

            AddChunkToBeUpdated(chunk);

            SetNeighboringPoints(chunk, pointPosition, value);
        }
    }


    public void PaintPointAdd(float3 worldPosition, float value)
    {
        var chunkPos = WorldPosToChunkPos(worldPosition);

        if (TryGetChunkAtChunkPos(chunkPos, out var chunk))
        {
            PaintPointAdd(chunk, WorldPosToPointPos(chunkPos, worldPosition), value);
        }
    }

    public void PaintPointAdd(Chunk chunk, int3 pointPosition, float value)
    {
        var index = PointPosToIndex(pointPosition);

        if (value != 0)
        {
            chunk.Points[index] += value;

            chunk.Points[index] = clamp(chunk.Points[index],isoLevel - 2,isoLevel + 2);

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
            if (value >= isoLevel)
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

    static float[] sampleCache;

    public bool FireRayParallel(float3 source, float3 direction, out float3 hit, float maxDistance = 20f, float stepAmount = 0.25f)
    {
        float3 travelDirection = normalize(direction) * stepAmount;
        int increments = (int)(maxDistance / stepAmount);
        if (sampleCache == null || sampleCache.Length < increments)
        {
            sampleCache = new float[increments];
        }

        Parallel.For(0, increments, i =>
        {
            sampleCache[i] = SamplePoint(source + (travelDirection * i));
        });

        for (int i = 0; i < increments; i++)
        {
            if (float.IsNaN(sampleCache[i]))
            {
                break;
            }
            else if (sampleCache[i] > isoLevel)
            {
                hit = source + (travelDirection * i);
                return true;
            }
        }

        hit = default;
        return false;
    }

    public void SetNeighboringPoints(Chunk chunk, int3 pointPosition, float value)
    {
        //var chunkPos = ReinterpretCast<Vector3Int, int3>(chunk.Position);
        int numVoxelPerAxis = numPointsPerAxis - 1;
        int3 chunkPos = int3(chunk.Position.x, chunk.Position.y, chunk.Position.z);
        int3 DI = new int3(0); // 대각일 경우 처리.
        int3 chunkPosTemp;
        int cnt = 0;
        int pointIndex = 0;

        void SetData()
        {

            if (existingChunks.TryGetValue(new Vector3Int(chunkPosTemp.x, chunkPosTemp.y, chunkPosTemp.z),out var chunk) && pointIndex < numPointsInChunk && pointIndex >= 0)
            {
                chunk.Points[pointIndex] = value;
                //if (!updateList.Contains(chunk)) updateList.Add(chunk);
                AddChunkToBeUpdated(chunk);
            }
        }
        if (pointPosition.x == numVoxelPerAxis)
        {
            cnt++;
            DI.x++;
            chunkPosTemp = chunkPos;
            chunkPosTemp.x++;
            //Debug.Log($"X+ Neighbor from {chunkPos} to {chunkPosTemp}");
            pointIndex = PointPosToIndex(0, pointPosition.y, pointPosition.z);
            SetData();
        }
        if (pointPosition.y == numVoxelPerAxis)
        {
            cnt++;
            DI.y++;
            chunkPosTemp = chunkPos;
            chunkPosTemp.y++;
            //Debug.Log($"Y+ Neighbor from {chunkPos} to {chunkPosTemp}");
            pointIndex = PointPosToIndex(pointPosition.x, 0, pointPosition.z);

            SetData();
        }
        if (pointPosition.z == numVoxelPerAxis)
        {
            cnt++;
            DI.z++;
            chunkPosTemp = chunkPos;
            chunkPosTemp.z++;
            //Debug.Log($"Z+ Neighbor from {chunkPos} to {chunkPosTemp}");
            pointIndex = PointPosToIndex(pointPosition.x, pointPosition.y, 0);
            SetData();
        }

        if (pointPosition.x == 0)
        {
            //Debug.Log("Point Position = " + pointPosition);
            cnt++;
            DI.x--;
            chunkPosTemp = chunkPos;
            chunkPosTemp.x--;
            //Debug.Log($"X- Neighbor from {chunkPos} to {chunkPosTemp}");
            pointIndex = PointPosToIndex(numVoxelPerAxis, pointPosition.y, pointPosition.z);
            SetData();
        }
        if (pointPosition.y == 0)
        {
            cnt++;
            DI.y--;
            chunkPosTemp = chunkPos;
            chunkPosTemp.y--;
            //Debug.Log($"Y- Neighbor from {chunkPos} to {chunkPosTemp}");
            pointIndex = PointPosToIndex(pointPosition.x, numVoxelPerAxis, pointPosition.z);
            SetData();
        }
        if (pointPosition.z == 0)
        {
            cnt++;
            DI.z--;
            chunkPosTemp = chunkPos;
            chunkPosTemp.z--;
            //Debug.Log($"Z- Neighbor from {chunkPos} to {chunkPosTemp}");
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

            //Debug.Log($"Edge/Corner Neighbor from {chunkPos} to {chunkPosTemp}");
            SetData();
            if (cnt == 3)
            {
                //xy
                chunkPosTemp.z = chunkPos.z;
                pointIndex = PointPosToIndex(x, y, pointPosition.z);
                SetData();
                //Debug.Log($"XY Edge Neighbor from {chunkPos} to {chunkPosTemp}");

                //yz
                chunkPosTemp.z = chunkPos.z + DI.z;
                chunkPosTemp.x = chunkPos.x;
                pointIndex = PointPosToIndex(pointPosition.x, y, z);
                SetData();
                //Debug.Log($"YZ Edge Neighbor from {chunkPos} to {chunkPosTemp}");

                //xz
                chunkPosTemp.x = chunkPos.x + DI.x;
                chunkPosTemp.y = chunkPos.y;
                pointIndex = PointPosToIndex(x, pointPosition.y, z);
                SetData();
                //Debug.Log($"XZ Edge Neighbor from {chunkPos} to {chunkPosTemp}");
            }
        }
    }


    public float SamplePoint(Vector3 worldPosition)
    {
        var chunkPos = WorldPosToChunkPos(worldPosition);
        var chunk = GetChunkAtChunkPos(chunkPos);
        //Debug.Log("CHUNK Pos = " + chunkPos);
        //Debug.Log("POINT Pos = " + WorldPosToPointPos(chunkPos, worldPosition));
        if (chunk == null)
        {
            return float.NaN;
        }
        else
        {
            return SamplePoint(chunk,WorldPosToPointPos(chunkPos, worldPosition));
        }
    }

    public static float InverseLerp(float a, float b, float value)
    {
        return (value - a) / (b - a);
    }

    public float SamplePoint(Chunk chunk, int3 pointPosition)
    {
        return chunk.Points[PointPosToIndex(pointPosition)];
    }

    public float3 ChunkPosToWorldPos(int3 chunkPos)
    {
        //return new Vector3(chunkPos.x * boundsSize, chunkPos.y * boundsSize, chunkPos.z * boundsSize);
        return chunkPos * new float3(boundsSize);
    }

    public int3 WorldPosToChunkPos(float3 worldPos)
    {
        //return new Vector3Int(Mathf.RoundToInt(worldPos.x / boundsSize), Mathf.RoundToInt(worldPos.y / boundsSize), Mathf.RoundToInt(worldPos.z / boundsSize));\
        return new int3(round(worldPos / boundsSize));
    }

    public int3 WorldPosToPointPos(int3 chunkPos, float3 worldPos)
    {
        /*var relativePos = worldPos - ((Vector3)chunkPos * boundsSize);
        relativePos /= boundsSize;*/

        //var pointPos = new Vector3(Mathf.LerpUnclamped(0f, numVoxelPerAxis,relativePos.x + 0.5f), Mathf.LerpUnclamped(0f, numVoxelPerAxis, relativePos.y + 0.5f), Mathf.LerpUnclamped(0f, numVoxelPerAxis, relativePos.z + 0.5f));


        //var finalPos = new Vector3Int(Mathf.RoundToInt(pointPos.x), Mathf.RoundToInt(pointPos.y), Mathf.RoundToInt(pointPos.z));


        /*var relativePos = (worldPos - (chunkPos * new float3(boundsSize))) / boundsSize;
        var pointPos = lerp(new float3(0), new float3(numVoxelPerAxis), relativePos + float3(0.5f));
        var finalPos = int3(round(pointPos));
        return finalPos;*/

        return int3(round(lerp(new float3(0), new float3(numPointsPerAxis - 1), ((worldPos - ((float3)chunkPos * boundsSize)) / boundsSize) + float3(0.5f))));

    }

    public float3 PointPosToWorldPos(int3 chunkPos, int3 pointPos)
    {
        //var relativePos = new Vector3(InverseLerp(0f, numVoxelPerAxis, pointPos.x) - 0.5f, InverseLerp(0f, numVoxelPerAxis, pointPos.y) - 0.5f, InverseLerp(0f, numVoxelPerAxis, pointPos.z) - 0.5f) * boundsSize;

        //var worldPos = relativePos + ((Vector3)chunkPos * boundsSize);

        /*var relativePos = unlerp(new float3(0), new float3(numVoxelPerAxis), pointPos) - float3(0.5f);
        var worldPos = relativePos + ((float3)chunkPos * boundsSize);
        return worldPos;*/

        return unlerp(new float3(0), new float3(numPointsPerAxis - 1), pointPos) - float3(0.5f) + ((float3)chunkPos * boundsSize);
    }

    public Chunk GetChunkAtChunkPos(int3 chunkPos)
    {
        if (existingChunks.TryGetValue(new Vector3Int(chunkPos.x,chunkPos.y,chunkPos.z), out var chunk))
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
        return (z * numPointsPerAxis * numPointsPerAxis) + (y * numPointsPerAxis) + x;
    }

    public int3 IndexToPointPos(int index)
    {
        int x = index % numPointsPerAxis;
        int y = (index / numPointsPerAxis) % numPointsPerAxis;
        int z = index / (numPointsPerAxis * numPointsPerAxis);

        return new int3(x,y,z);
    }

    [NonSerialized]
    int sphereBrushPointSize;
    public Vector3Int CubeBrushPointSize
    {
        get
        {
            return new Vector3Int(
                Mathf.CeilToInt(cubeBrushSize.x / (boundsSize / (numPointsPerAxis - 1))),
                Mathf.CeilToInt(cubeBrushSize.y / (boundsSize / (numPointsPerAxis - 1))),
                Mathf.CeilToInt(cubeBrushSize.z / (boundsSize / (numPointsPerAxis - 1))));
        }
    }

    /// <summary>
    /// Gets the brush value for a certain point. Valid range for x, y and z is [-SphereBrushPointSize - SphereBrushPointSize - 1]
    /// </summary>
    /// <returns></returns>
    public float GetSphereBrushValueAtPoint(int3 point)
    {
        //return Mathf.Clamp(sphereBrushPointSize - new Vector3Int(point.x, point.y, point.z).magnitude,0f, sphereBrushPointSize * 2f);
        return clamp(sphereBrushPointSize - length(point), 0f, sphereBrushPointSize * 2f);
    }

    public float GetCubeBrushValueAtPoint(int x, int y, int z)
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

    void CreateBuffers()
    {
        int numPoints = numPointsPerAxis * numPointsPerAxis * numPointsPerAxis;
        if (unusedBuffers.Count == 0)
        {
            ReturnBufferSet(GetBufferSet());
        }
    }

    void ReleaseBuffers()
    {
        foreach (var buffer in usedBuffers)
        {
            buffer.triangleBuffer.Release();
            buffer.pointsBuffer.Release();
            buffer.triCountBuffer.Release();
        }

        usedBuffers.Clear();

        for (int i = 0; i < unusedBuffers.Count; i++)
        {
            var buffer = unusedBuffers.Dequeue();
            buffer.triangleBuffer.Release();
            buffer.pointsBuffer.Release();
            buffer.triCountBuffer.Release();
        }
    }

    Vector3 CentreFromCoord(Vector3Int coord)
    {
        // Centre entire map at origin
        return new Vector3(coord.x, coord.y, coord.z) * boundsSize;
    }

    void CreateChunkHolder()
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

    Chunk CreateChunk(Vector3Int coord)
    {
        GameObject chunk = new GameObject($"Chunk ({coord.x}, {coord.y}, {coord.z})");
        chunk.transform.parent = chunkHolder.transform;
        Chunk newChunk = chunk.AddComponent<Chunk>();
        newChunk.Position = coord;
        newChunk.SourceMap = this;
        return newChunk;
    }

    void OnValidate()
    {
        settingsUpdated = true;
        sphereBrushPointSize = Mathf.CeilToInt(sphereBrushSize / (boundsSize / (numPointsPerAxis - 1)));
    }

    BufferSet GetBufferSet()
    {
        if (unusedBuffers.TryDequeue(out var bufferSet))
        {
            usedBuffers.Add(bufferSet);
            return bufferSet;
        }
        else
        {
            var buffer = CreateBufferSet();
            usedBuffers.Add(buffer);
            return buffer;
        }
    }

    BufferSet CreateBufferSet()
    {
        int numPoints = numPointsPerAxis * numPointsPerAxis * numPointsPerAxis;
        int numVoxelsPerAxis = numPointsPerAxis - 1;
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

    void ReturnBufferSet(BufferSet buffer)
    {
        usedBuffers.Remove(buffer);
        unusedBuffers.Enqueue(buffer);
    }

    static Queue<float[]> pointBufferCache = new Queue<float[]>();

    public static float[] GetChunkPointBuffer()
    {
        if (pointBufferCache.TryDequeue(out var buffer))
        {
            return buffer;
        }
        else
        {
            int numPoints = Instance.numPointsPerAxis * Instance.numPointsPerAxis * Instance.numPointsPerAxis;
            return new float[numPoints];
        }
    }

    public static void ReturnChunkPointBuffer(float[] buffer)
    {
        pointBufferCache.Enqueue(buffer);
    }

    struct Triangle
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

    void OnDrawGizmos()
    {
        if (showBoundsGizmo)
        {
            Gizmos.color = boundsGizmoCol;

            List<Chunk> chunks = (this.chunks == null) ? new List<Chunk>(FindObjectsOfType<Chunk>()) : this.chunks;
            foreach (var chunk in chunks)
            {
                Bounds bounds = new Bounds(CentreFromCoord(chunk.Position), Vector3.one * boundsSize);
                Gizmos.color = boundsGizmoCol;
                Gizmos.DrawWireCube(CentreFromCoord(chunk.Position), Vector3.one * boundsSize);
            }
        }
    }

}