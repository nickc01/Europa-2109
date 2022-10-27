using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

public class Map : MonoBehaviour
{
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

    [Header("Brush Setting")]
    public float brushSpeed = 1;
    public int brushSize = 10;
    int _brushSize = 10;

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

    public int numVoxelPerAxis => numPointsPerAxis - 1;

    List<Task> taskPool = new List<Task>();

    class BufferSet
    {
        public ComputeBuffer triangleBuffer;
        public ComputeBuffer pointsBuffer;
        public ComputeBuffer triCountBuffer;
    }

    bool settingsUpdated;

    void Awake()
    {
        if (Application.isPlaying)
        {

            var oldChunks = FindObjectsOfType<Chunk>();
            for (int i = oldChunks.Length - 1; i >= 0; i--)
            {
                Destroy(oldChunks[i].gameObject);
            }
        }
    }


    Queue<Chunk> chunkMeshesToRefresh = new Queue<Chunk>();

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

        UpdateChunkCollider();
    }

    void UpdateChunkCollider()
    {
        if (chunkMeshesToRefresh.TryDequeue(out var chunk) && chunk.gameObject.activeSelf)
        {
            //chunk.meshCollider.sharedMesh = null;
            chunk.MainCollider.sharedMesh = chunk.Mesh;
            //Debug.Log("CHunk Updated");
        }
    }

    /*async Task ExecuteRun()
    {
        await Run().Confi
    }*/

    public async Task Run()
    {

        try
        {
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

    void InitVariableChunkStructures()
    {
        recycleableChunks = new ConcurrentQueue<Chunk>();
        chunks = new List<Chunk>();
        existingChunks = new Dictionary<Vector3Int, Chunk>();
        chunkCoordsUsed = new HashSet<Vector3Int>();
    }


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
    ChunkPoint[] pointCache;

    public void UpdateChunkMesh(Chunk chunk)
    {
        int numPoints = numPointsPerAxis * numPointsPerAxis * numPointsPerAxis;
        //Debug.Log("Updating Chunk Mesh");
        int numVoxelsPerAxis = numPointsPerAxis - 1;
        int numThreadsPerAxis = Mathf.CeilToInt(numVoxelsPerAxis / (float)THREAD_GROUP_SIZE);
        float pointSpacing = boundsSize / (numPointsPerAxis - 1);

        Vector3Int coord = chunk.Position;
        Vector3 centre = CentreFromCoord(coord);

        Vector3 worldBounds = new Vector3(boundsSize, boundsSize, boundsSize);

        var bufferSet = GetBufferSet();

        if (chunk.PointsLoaded)
        {
            bufferSet.pointsBuffer.SetData(chunk.Points, 0, 0, numPoints);
        }
        else
        {
            DensityGenerator.Generate(bufferSet.pointsBuffer, numPointsPerAxis, boundsSize, worldBounds, centre, offset, pointSpacing);
        }

        bufferSet.triangleBuffer.SetCounterValue(0);
        generatorShader.SetBuffer(0, "points", bufferSet.pointsBuffer);
        generatorShader.SetBuffer(0, "triangles", bufferSet.triangleBuffer);
        generatorShader.SetInt("numPointsPerAxis", numPointsPerAxis);
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



        if (!chunk.PointsLoaded)
        {
            bufferSet.pointsBuffer.GetData(pointCache, 0, 0, numPoints);

            if (chunk.Points == null)
            {
                chunk.Points = new ChunkPoint[numPoints];
            }

            Array.Copy(pointCache, chunk.Points, numPoints);
        }
        //PointsLoaded


        Mesh mesh = chunk.Mesh;
        mesh.Clear();

        var vertices = new Vector3[numTris * 3];
        var meshTriangles = new int[numTris * 3];

        for (int i = 0; i < numTris; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                meshTriangles[i * 3 + j] = i * 3 + j;
                vertices[i * 3 + j] = triangleCache[i][j];
            }
        }
        mesh.vertices = vertices;
        mesh.triangles = meshTriangles;

        mesh.RecalculateNormals();
        //mesh.Optimize();

        chunk.OnMeshUpdate();

        chunkMeshesToRefresh.Enqueue(chunk);

        ReturnBufferSet(bufferSet);
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

    Vector3Int PosToChunkIdx3D(Vector3Int p)
    {
        return PosToChunkIdx3D(p.x, p.y, p.z);
    }
    Vector3Int PosToChunkIdx3D(float x, float y, float z)
    {
        Vector3Int result = new Vector3Int(Mathf.FloorToInt(x / numPointsPerAxis), Mathf.FloorToInt(y / numPointsPerAxis), Mathf.FloorToInt(z / numPointsPerAxis));
        return result;
    }

    int PosToIndex(int x, int y, int z)
    {
        return z * numPointsPerAxis * numPointsPerAxis + y * numPointsPerAxis + x;
    }

    List<Chunk> updateList = new List<Chunk>();

    /*public void UseBrush(Vector3 point, bool EraseMode)
    {
        float brushValue = Time.deltaTime * brushSpeed * isoLevel;
        float max = 1;
        if (EraseMode) brushValue *= -1;
        point *= numPointsPerAxis / boundsSize; // spacing
        Vector3Int pos = new Vector3Int(Mathf.RoundToInt(point.x), Mathf.RoundToInt(point.y), Mathf.RoundToInt(point.z));

        Vector3Int pivotChunkIdx3d = PosToChunkIdx3D(pos); // 센터가 포함되어있는 청크의 인덱스.
                                                           //Task.Run (()=>
                                                           //{
        for (int i = 0; i < brushShape.Count; i++)
        {
            Vector3Int p = pos + brushShape[i];
            //Vector3Int chunkIdx3d = p / numVoxelPerAxis;

            Vector3Int chunkIdx3d = PosToChunkIdx3D(p);
            Vector3Int chunkIdx3d_modified = PosToChunkIdx3D(p + chunkIdx3d - pivotChunkIdx3d);

            //갭을 둔 후에 다른 청크로 가게 될 경우.
            while (chunkIdx3d != chunkIdx3d_modified)
            {
                chunkIdx3d = chunkIdx3d_modified;
                chunkIdx3d_modified = PosToChunkIdx3D(p + chunkIdx3d - pivotChunkIdx3d);
            }
            p += chunkIdx3d - pivotChunkIdx3d;


            if (!existingChunks.ContainsKey(chunkIdx3d)) continue;

            //Chunk chunk = chunks[chunkIdx3d];
            Chunk chunk = existingChunks[chunkIdx3d];
            if (!updateList.Contains(chunk))
            {
                updateList.Add(chunk);
            }
            Vector3Int localPos = p - chunkIdx3d * numPointsPerAxis;
            int localIndex = PosToIndex(localPos.x, localPos.y, localPos.z);
            float chunkValue = chunk.mapData[localIndex] + (max - chunk.mapData[localIndex]) * brushDist[i] * brushValue;
            chunk.mapData[localIndex] = chunkValue;
            SetNeighborChunk(localPos, chunkIdx3d, chunkValue);
        }
        foreach (Chunk chunk in updateList)
        {
            dataBuffer.SetData(chunk.mapData);
            meshGenerator.March(chunk);
        }
        updateList.Clear();
        //});
    }

    public void SetNeighborChunk(Vector3Int localPos, Vector3Int chunkIdx3d, float value)
    {
        Vector3Int DI = Vector3Int.zero; // 대각일 경우 처리.
        Vector3Int chunkIdx3d_Temp;
        int cnt = 0;
        int localIndex = 0;
        Chunk chunk;

        void SetData()
        {
            if (chunks.ContainsKey(chunkIdx3d_Temp) && localIndex < numPointsInChunk && localIndex >= 0)
            {
                chunk = chunks[chunkIdx3d_Temp];
                chunk.mapData[localIndex] = value;
                if (!updateList.Contains(chunk)) updateList.Add(chunk);
            }
        }
        if (localPos.x == numVoxelPerAxis)
        {
            cnt++;
            DI.x++;
            chunkIdx3d_Temp = chunkIdx3d;
            chunkIdx3d_Temp.x++;
            localIndex = PosToIndex(0, localPos.y, localPos.z);
            SetData();
        }
        if (localPos.y == numVoxelPerAxis)
        {
            cnt++;
            DI.y++;
            chunkIdx3d_Temp = chunkIdx3d;
            chunkIdx3d_Temp.y++;
            localIndex = PosToIndex(localPos.x, 0, localPos.z);

            SetData();
        }
        if (localPos.z == numVoxelPerAxis)
        {
            cnt++;
            DI.z++;
            chunkIdx3d_Temp = chunkIdx3d;
            chunkIdx3d_Temp.z++;
            localIndex = PosToIndex(localPos.x, localPos.y, 0);
            SetData();
        }

        if (localPos.x == 0)
        {
            cnt++;
            DI.x--;
            chunkIdx3d_Temp = chunkIdx3d;
            chunkIdx3d_Temp.x--;
            localIndex = PosToIndex(numVoxelPerAxis, localPos.y, localPos.z);
            SetData();
        }
        if (localPos.y == 0)
        {
            cnt++;
            DI.y--;
            chunkIdx3d_Temp = chunkIdx3d;
            chunkIdx3d_Temp.y--;
            localIndex = PosToIndex(localPos.x, numVoxelPerAxis, localPos.z);
            SetData();
        }
        if (localPos.z == 0)
        {
            cnt++;
            DI.z--;
            chunkIdx3d_Temp = chunkIdx3d;
            chunkIdx3d_Temp.z--;
            localIndex = PosToIndex(localPos.x, localPos.y, numVoxelPerAxis);
            SetData();
        }
        if (cnt >= 2)
        {
            chunkIdx3d_Temp = chunkIdx3d + DI;
            int x = DI.x == 0 ? localPos.x : DI.x > 0 ? 0 : numVoxelPerAxis;
            int y = DI.y == 0 ? localPos.y : DI.y > 0 ? 0 : numVoxelPerAxis;
            int z = DI.z == 0 ? localPos.z : DI.z > 0 ? 0 : numVoxelPerAxis;
            localIndex = PosToIndex(x, y, z);
            SetData();
            if (cnt == 3)
            {
                //xy
                chunkIdx3d_Temp.z = chunkIdx3d.z;
                localIndex = PosToIndex(x, y, localPos.z);
                SetData();

                //yz
                chunkIdx3d_Temp.z = chunkIdx3d.z + DI.z;
                chunkIdx3d_Temp.x = chunkIdx3d.x;
                localIndex = PosToIndex(localPos.x, y, z);
                SetData();

                //xz
                chunkIdx3d_Temp.x = chunkIdx3d.x + DI.x;
                chunkIdx3d_Temp.y = chunkIdx3d.y;
                localIndex = PosToIndex(x, localPos.y, z);
                SetData();
            }
        }
    }*/

    /*List<Vector3Int> brushShape = new List<Vector3Int>();
    List<float> brushDist = new List<float>();
    void InitBrush()
    {
        brushShape.Clear();
        brushDist.Clear();

        float spacing = boundsSize / (numPointsPerAxis - 1);
        int brushSize_relative = Mathf.CeilToInt(brushSize / spacing);
        for (int i = -brushSize_relative; i < brushSize_relative; i++)
            for (int j = -brushSize_relative; j < brushSize_relative; j++)
                for (int k = -brushSize_relative; k < brushSize_relative; k++)
                    if (Mathf.Sqrt(i * i + j * j + k * k) <= brushSize_relative)
                        brushShape.Add(new Vector3Int(i, j, k));

        brushShape.Sort((A, B) =>
        {
            return A.magnitude.CompareTo(B.magnitude);
        });
        for (int i = 0; i < brushShape.Count; i++)
        {
            brushDist.Add(brushSize_relative - brushShape[i].magnitude);
        }

        shader.SetInt(CSPARAM.brushSize, brushSize);
    }*/

    void CreateBuffers()
    {
        int numPoints = numPointsPerAxis * numPointsPerAxis * numPointsPerAxis;
        //int numVoxelsPerAxis = numPointsPerAxis - 1;
        //int numVoxels = numVoxelsPerAxis * numVoxelsPerAxis * numVoxelsPerAxis;
        //int maxTriangleCount = numVoxels * 5;

        // Always create buffers in editor (since buffers are released immediately to prevent memory leak)
        // Otherwise, only create if null or if size has changed
        if (unusedBuffers.Count == 0/* || numPoints != unusedBuffers.Peek().pointsBuffer.count*/)
        {
            //ReleaseBuffers();
            ReturnBufferSet(GetBufferSet());
        }
        //if (!Application.isPlaying || (unusedBuffers.Count == 0 || numPoints != unusedBuffers.Peek().pointsBuffer.count)) {
        //if (Application.isPlaying) {
        //    ReleaseBuffers ();
        //}
        //ReturnBufferSet(GetBufferSet());
        //triangleBuffer = new ComputeBuffer (maxTriangleCount, sizeof (float) * 3 * 3, ComputeBufferType.Append);
        //pointsBuffer = new ComputeBuffer (numPoints, sizeof (float) * 4);
        //triCountBuffer = new ComputeBuffer (1, sizeof (int), ComputeBufferType.Raw);

        //}
    }

    void ReleaseBuffers()
    {
        /*if (triangleBuffer != null) {
            triangleBuffer.Release ();
            pointsBuffer.Release ();
            triCountBuffer.Release ();
        }*/
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
        //triangleBuffer = new ComputeBuffer(maxTriangleCount, sizeof(float) * 3 * 3, ComputeBufferType.Append);
        //pointsBuffer = new ComputeBuffer(numPoints, sizeof(float) * 4);
        //triCountBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
        int numPoints = numPointsPerAxis * numPointsPerAxis * numPointsPerAxis;
        int numVoxelsPerAxis = numPointsPerAxis - 1;
        int numVoxels = numVoxelsPerAxis * numVoxelsPerAxis * numVoxelsPerAxis;
        int maxTriangleCount = numVoxels * 5;

        if (triangleCache == null)
        {
            triangleCache = new Triangle[maxTriangleCount];
            pointCache = new ChunkPoint[numPoints];
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

    static Queue<ChunkPoint[]> pointBufferCache = new Queue<ChunkPoint[]>();

    public static ChunkPoint[] GetChunkPointBuffer()
    {
        if (pointBufferCache.TryDequeue(out var buffer))
        {
            return buffer;
        }
        else
        {
            int numPoints = Instance.numPointsPerAxis * Instance.numPointsPerAxis * Instance.numPointsPerAxis;
            return new ChunkPoint[numPoints];
        }
    }

    public static void ReturnChunkPointBuffer(ChunkPoint[] buffer)
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