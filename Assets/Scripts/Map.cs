// These are the required using statements for the code to function properly
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
    // Events that can be subscribed to when a chunk is loaded or unloaded
    public event Action<Chunk> OnChunkLoad;
    public event Action<Chunk> OnChunkUnload;

    // Constants used throughout the code
    private const int THREAD_GROUP_SIZE = 8;
    private const string chunkHolderName = "Chunks Holder";
    private static Thread MAIN_THREAD;
    private const int MAX_BRUSH_CALLS_PER_FRAME = 1;
    private const int MAX_MESH_UPDATE_CALLS_PER_FRAME = 5;
    private const int CHUNK_OBJECT_SPAWN_LIMIT = 5;

    // Singleton instance of the Map class
    private static Map _instance;
    public static Map Instance => _instance ??= GameObject.FindObjectOfType<Map>();

    // Static constructor that subscribes to an event
    static Map()
    {
        Submarine.OnGameReload += Submarine_OnGameReload;
    }

    // Static method that sets the instance to null when an event is called
    private static void Submarine_OnGameReload()
    {
        _instance = null;
    }

    // Serialized properties that can be edited in the inspector
    [field: SerializeField]
    public string WorldName { get; private set; } = "default"; // The name of the world file created

    [field: SerializeField]
    public int WorldSeed { get; private set; } = 1; //The seed used for generating the world

    [field: SerializeField]
    public Camera MainCamera { get; private set; }  //A reference to the main camera in the scene

    [field: Header("General Settings")]
    [field: SerializeField]
    public MapGenerator DensityGenerator { get; private set; } //The Map Generator that will be used for generating chunks

    public Transform viewer;
    [field: SerializeField]
    public float ViewDistance { get; private set; } = 30; //How far the player is able to see

    [field: Space]
    [field: SerializeField]
    [field: FormerlySerializedAs("generatorShader")]
    public ComputeShader GeneratorShader { get; private set; } //The shader that will be used with the Map Generator to generate chunks on the GPU
    [field: SerializeField]
    public Material ChunkMaterial { get; private set; } //The material the GeneratorShader will be applied to
    public bool generateColliders; //Should the Map Generator also generate colliders?

    [field: Header("Voxel Settings")]
    [field: SerializeField]
    [field: FormerlySerializedAs("isoLevel")]
    public float IsoLevel { get; private set; } //The number that's used for determining if a certain point is within a chunk or not. If a chunk point is greater than the IsoLevel, then it is inside of ground

    [field: FormerlySerializedAs("boundsSize")]
    [field: SerializeField]
    public float BoundsSize { get; private set; } //The width, height, and depth of each chunk

    [field: Range(2, 100)]
    [field: SerializeField]
    [field: FormerlySerializedAs("numPointsPerAxis")]
    public int NumPointsPerAxis { get; private set; } = 30; //How many points make up the width, height, and depth of a chunk. This means each chunk will have a total of NumPointsPerAxis ^ 3 points

    [Header("Gizmos")]
    [SerializeField]
    private bool showBoundsGizmo = true; // Determines whether the bounds gizmo is shown or not
    [SerializeField]
    private Color boundsGizmoCol = Color.white; // The color of the bounds gizmo

    [field: SerializeField]
    public float FloorHeight { get; private set; } = -40; // The height of the floor
    [field: SerializeField]
    public float CeilingHeight { get; private set; } = 50; // The height of the ceiling

    public bool AutoLoadChunks { get; set; } = true; // Determines whether chunks are automatically loaded or not

    public bool AutoUnloadChunks { get; set; } = true; // Determines whether chunks are automatically unloaded or not

    private bool runningUpdate = false; // Whether the Map is currently running an update
    private GameObject chunkHolder; // The GameObject that holds all of the chunk objects

    private List<Chunk> loadedChunks = new List<Chunk>(); // A list of all currently loaded chunks
    private ConcurrentDictionary<int3, Chunk> loadedCoordinates = new ConcurrentDictionary<int3, Chunk>(); // A dictionary mapping chunk coordinates to chunk objects
    private Queue<Chunk> unloadedChunkPool = new Queue<Chunk>(); // A pool of unloaded chunks that can be reused

    private Queue<BufferSet> unusedBuffers = new Queue<BufferSet>(); // A queue of unused ComputeBuffers
    private HashSet<BufferSet> usedBuffers = new HashSet<BufferSet>(); // A set of ComputeBuffers that are currently being used
    private List<Task> taskPool = new List<Task>(); // A pool of background tasks

    private Chunk.DistanceSorter distanceSorter; // A sorter for sorting chunks by their distance to the player
    private Chunk.DistanceSorterInt3 distanceInt3Sorter; // A sorter for sorting chunk coordinates by their distance to the player

    private bool settingsUpdated; // Whether the Map's settings have been updated

    private List<Chunk> chunkMeshesToRefresh; // A list of chunks whose meshes need to be refreshed
    private bool refreshListDirty = true; // Whether the list of chunks that need to be refreshed has changed

    private ConcurrentBag<Chunk> chunksToRender = new ConcurrentBag<Chunk>(); // A bag of chunks that need to be rendered

    private ConcurrentQueue<AreaGenParameters> areaGenerationQueue = new ConcurrentQueue<AreaGenParameters>(); // A queue of area generation parameters
    private List<int3> meshesToUpdate; // A list of chunk coordinates whose meshes need to be updated
    private ConcurrentBag<int3> meshesTEMP = new ConcurrentBag<int3>(); // A temporary bag for holding chunk coordinates whose meshes need to be updated
    private object meshUpdateLock = new object(); // A lock for accessing the meshesToUpdate list
    private bool meshListDirty = false; // Whether the list of chunk coordinates that need to be updated has changed

    private ConcurrentQueue<BrushCall> brushCalls = new ConcurrentQueue<BrushCall>(); // A queue of brush calls

    public int PointsPerChunk => NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis; // The total number of voxels in a single chunk
    public int numPointsInChunk => NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis; // The total number of voxels in a single chunk

    public ConcurrentQueue<Chunk> chunksWithObjectsToSpawn = new ConcurrentQueue<Chunk>(); // A queue of chunks that have objects to spawn
    public ConcurrentQueue<List<GameObject>> objectsToDestroy = new ConcurrentQueue<List<GameObject>>(); // A queue of lists of objects to destroy

    public CancellationToken MainCancelToken = new CancellationToken(); // The cancellation token for the Map


    // A private inner class used to hold references to the compute buffers used in mesh generation.
    private class BufferSet
    {
        public ComputeBuffer triangleBuffer;
        public ComputeBuffer pointsBuffer;
        public ComputeBuffer triCountBuffer;
    }

    // A struct that holds parameters for chunk generation.
    public struct ChunkGenerationParameters
    {
        public float IsoOffset; // The iso offset value.
        public float IsoOffsetByHeight; // The iso offset value adjusted by height.
        public float IsoOffsetByHeightAbs; // The absolute value of the iso offset value adjusted by height.
    }

    // A private inner class used to hold data for brush operations.
    private class BrushCall
    {
        public enum BrushType
        {
            Sphere,
            Cube
        }
        public Vector3 worldPos; // The world position of the brush.
        public bool EraseMode; // Whether the brush is in erase mode.
        public float intensity; // The intensity of the brush.
        public float3 brushSize; // The size of the brush.
        public BrushType brushType; // The type of brush.
        public TaskCompletionSource<bool> completer; // A TaskCompletionSource that completes when the brush operation finishes.
    }

    // A private inner class used to hold data for area generation operations.
    private class AreaGenParameters
    {
        public float3 viewerPosition; // The position of the viewer.
        public int3 area; // The area to generate.
        public float viewDistance; // The view distance.
        public bool keepChunksLoaded; // Whether to keep the generated chunks loaded.
        public List<Chunk> out_ChunksInArea; // The list of chunks in the area.
        public TaskCompletionSource<bool> completer; // A TaskCompletionSource that completes when the area generation finishes.
    }

    private void Awake()
    {
        WorldSeed = UnityEngine.Random.Range(-99999, 99999); // Set the world seed to a random value.
        meshesToUpdate = Unity.VisualScripting.ListPool<int3>.New(); // Create a new ListPool for the meshes that need to be updated.
        chunkMeshesToRefresh = Unity.VisualScripting.ListPool<Chunk>.New(); // Create a new ListPool for the chunks that need their meshes refreshed.
        if (Directory.Exists(Application.persistentDataPath + "/default"))
        {
            Directory.Delete(Application.persistentDataPath + "/default", true); // Delete the default directory if it exists.
        }
        CreateBuffers(); // Create the compute buffers used in mesh generation.
        MAIN_THREAD = Thread.CurrentThread; // Set the MAIN_THREAD to the current thread.
        CreateChunkHolder(); // Create the holder for the chunks.
        if (Application.isPlaying)
        {
            Chunk[] oldChunks = FindObjectsOfType<Chunk>(); // Find all the chunks in the scene.
            for (int i = oldChunks.Length - 1; i >= 0; i--)
            {
                Destroy(oldChunks[i].gameObject); // Destroy all the chunks in the scene.
            }
        }
    }


    // Unloads the given list of chunks by setting their KeepLoaded flag to false
    private static void UnloadChunks(List<Chunk> chunks)
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].KeepLoaded = false;
        }
    }

    // FixedUpdate is called multiple times per second, and is used here to update the map
    private async void FixedUpdate()
    {
        // If an update is not already running and the game is playing, start a new update
        if (!runningUpdate && Application.isPlaying)
        {
            runningUpdate = true;
            await Run();
        }

        // If the map settings have been updated and an update is not currently running, start a new update
        if (settingsUpdated && !runningUpdate)
        {
            runningUpdate = true;
            await Run();
            settingsUpdated = false;
        }
    }

    // Removes duplicates from a given list using a HashSet
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

    // Method that updates the map's meshes, chunk objects, and brush calls
    private void Update()
    {
        int meshUpdateCount = 0;

        // Lock the mesh update lock to ensure thread safety
        lock (meshUpdateLock)
        {
            // Check if the distanceInt3Sorter is null
            if (distanceInt3Sorter == null)
            {
                // Create a new Chunk.DistanceSorterInt3 object
                distanceInt3Sorter = new Chunk.DistanceSorterInt3
                {
                };
            }

            // Set the viewer chunk position based on the viewer's position
            distanceInt3Sorter.viewerChunkPos = WorldPosToChunkPos(viewer.transform.position);

            // Remove duplicate elements in the meshesToUpdate list
            DistinctInList(meshesToUpdate);

            // Sort the meshesToUpdate list based on distance from the viewer
            CustomAlgorithms.SortMergeAdaptivePar(ref meshesToUpdate, 0, meshesToUpdate.Count, distanceInt3Sorter);

            // Set meshListDirty to false
            meshListDirty = false;
        }

        int3 lastCoordinate = new int3(int.MaxValue);

        // Update the mesh for each coordinate in meshesToUpdate
        while (meshUpdateCount < MAX_MESH_UPDATE_CALLS_PER_FRAME)
        {
            if (meshesToUpdate.Count > 0)
            {
                int3 coordinate = meshesToUpdate[meshesToUpdate.Count - 1];

                // Only update the chunk mesh if it's not the same as the last one and it's already loaded
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

        // Add any meshes in meshesTEMP to meshesToUpdate and set meshListDirty to true if there are any meshes
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

        // Spawn objects for each chunk in chunksWithObjectsToSpawn
        for (int i = 0; i < CHUNK_OBJECT_SPAWN_LIMIT; i++)
        {
            if (chunksWithObjectsToSpawn.TryDequeue(out Chunk chunk) && chunk != null)
            {
                foreach (ChunkObjectInfo entry in chunk.chunkObjectInfo)
                {
                    // Instantiate the chunk object's prefab and set its position and rotation
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

        // Destroy objects in objectsToDestroy list
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

        //Loop through each of the brush calls to draw in the world
        while (brushCalls.TryDequeue(out BrushCall call))
        {
            try
            {
                if (call.brushType == BrushCall.BrushType.Sphere)
                {
                    //Draw a sphere at the specified world position
                    UseSphereBrush(call.worldPos, call.EraseMode, call.intensity, call.brushSize);
                }
                else
                {
                    //Draw a cube at the specified world position
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

        //Update any chunks that need to have their collider's updated
        UpdateChunkCollider();
    }

    private void UpdateChunkCollider()
    {
        // Check if the refresh list needs to be updated
        if (refreshListDirty)
        {
            refreshListDirty = false;

            // Create a new DistanceSorter if one does not exist
            if (distanceSorter == null)
            {
                distanceSorter = new Chunk.DistanceSorter
                {

                };
            }

            // Set the viewer's chunk position in the DistanceSorter
            distanceSorter.viewerChunkPos = WorldPosToChunkPos(viewer.transform.position);

            // Remove duplicates from the list of chunks to refresh
            DistinctInList(chunkMeshesToRefresh);

            // Sort the list of chunks to refresh based on their distance from the viewer
            CustomAlgorithms.SortMergeAdaptivePar(ref chunkMeshesToRefresh, 0, chunkMeshesToRefresh.Count, distanceSorter);
        }

        // Keep refreshing the colliders of chunks until the list is empty or the maximum number of refreshes has been reached
        int3 lastCoordinate = new int3(int.MaxValue);
        while (chunkMeshesToRefresh.Count > 0)
        {
            Chunk chunk = chunkMeshesToRefresh[chunkMeshesToRefresh.Count - 1];
            chunkMeshesToRefresh.RemoveAt(chunkMeshesToRefresh.Count - 1);

            // Only refresh the collider if the chunk is not null, has not been refreshed before, and is active in the scene
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
            // Process the areaGenerationQueue until it is empty
            while (areaGenerationQueue.TryDequeue(out AreaGenParameters areaGenParams))
            {
                try
                {
                    // Load chunks in the specified area
                    await LoadChunksInArea(areaGenParams.viewerPosition, areaGenParams.area, areaGenParams.viewDistance, areaGenParams.keepChunksLoaded, areaGenParams.out_ChunksInArea);

                    // Signal that the area generation is complete
                    areaGenParams.completer.TrySetResult(true);
                }
                catch (Exception e)
                {
                    // If an error occurs, signal that the area generation has failed
                    areaGenParams.completer.TrySetException(e);
                }
            }

            // Load visible chunks
            await LoadVisibleChunks();
        }
        catch (Exception e)
        {
            // Log any errors that occur
            Debug.LogException(e);
        }
        finally
        {
            // Set runningUpdate to false when the task is complete
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
        // Check if the current thread is not the main thread and enqueue the parameters for area generation.
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

        // Create two lists to store chunks that need to be created and destroyed.
        List<int3> chunksToCreate = new List<int3>();
        List<Chunk> destroyedChunks = new List<Chunk>();

        await Task.Run(async () =>
        {
            // Calculate the viewer's position relative to the bounds of the chunk.
            float3 p = viewerPosition;
            float3 ps = p / BoundsSize;
            int3 viewerCoord = new int3(round(ps));

            float sqrViewDistance = viewDistance * viewDistance;
            if (!float.IsNaN(viewDistance))
            {
                // Iterate through the list of loaded chunks and unload any that are outside the view distance.
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

                            // Add a task to the task pool to unload the chunk.
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

                // Wait for all tasks in the task pool to finish before clearing it.
                await Task.WhenAll(taskPool);
                taskPool.Clear();
            }

            // Clear the list of chunks to render.
            chunksToRender.Clear();

            // Calculate the half width, height and depth of the area.
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

                        if (loadedCoordinates.TryGetValue(coord, out Chunk loadedChunk))
                        {
                            // If the chunk is already loaded, add it to the list of chunks in the area
                            out_chunksInArea?.Add(loadedChunk);

                            // If the 'keepChunksLoaded' flag is set, mark the chunk to be kept loaded
                            if (keepChunksLoaded)
                            {
                                loadedChunk.KeepLoaded = true;
                            }

                            // Skip to the next chunk in the loop
                            continue;
                        }

                        float sqrDst = 0;
                        if (!float.IsNaN(viewDistance))
                        {
                            // Calculate the squared distance between the viewer and the centre of the chunk
                            float3 centre = CentreFromCoord(coord);
                            float3 viewerOffset = p - centre;
                            Vector3 o = abs(viewerOffset) - new float3(1f) * BoundsSize / 2f;
                            sqrDst = lengthsq(max(o, new float3(0f)));
                        }

                        if (float.IsNaN(viewDistance) || sqrDst <= sqrViewDistance)
                        {
                            bool usingCached = false;

                            // Try to dequeue a chunk from the unloaded chunk pool
                            while (unloadedChunkPool.TryDequeue(out Chunk chunk))
                            {
                                if (chunk == null)
                                {
                                    continue;
                                }
                                usingCached = true;

                                // Initialize the chunk with the new coordinates and mark it as kept loaded if necessary
                                chunk.Position = coord;
                                if (keepChunksLoaded)
                                {
                                    chunk.KeepLoaded = true;
                                }

                                // Add the chunk to the list of chunks in the area and to the list of chunks to be rendered
                                out_chunksInArea?.Add(chunk);
                                chunksToRender.Add(chunk);

                                // Add the chunk initialization task to the task pool
                                taskPool.Add(chunk.Init(this, true));
                                break;
                            }

                            // If no chunk was dequeued from the pool, mark the chunk for creation
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
            // Create a new chunk at the specified coordinate
            Chunk chunk = CreateChunk(coord);

            // If keepChunksLoaded is true, mark the chunk as keep loaded
            if (keepChunksLoaded)
            {
                chunk.KeepLoaded = true;
            }

            // Add the chunk to the out_chunksInArea list if it's not null
            out_chunksInArea?.Add(chunk);

            // Add the chunk to the chunksToRender list
            chunksToRender.Add(chunk);

            // Add the chunk initialization task to the task pool
            taskPool.Add(chunk.Init(this, false));
        }

        // Wait for all initialization tasks to complete
        await Task.WhenAll(taskPool);

        // Clear the task pool
        taskPool.Clear();

        foreach (Chunk chunk in chunksToRender)
        {
            // Enable the chunk's main renderer
            chunk.MainRenderer.enabled = true;

            // If generateColliders is true and the chunk's main collider shared mesh is null, set it to the chunk's mesh
            if (generateColliders && chunk.MainCollider.sharedMesh == null)
            {
                chunk.MainCollider.sharedMesh = chunk.Mesh;
            }

            // Set the chunk's main renderer shared material to the ChunkMaterial
            chunk.MainRenderer.sharedMaterial = ChunkMaterial;

            // Add the chunk to the loadedCoordinates dictionary
            loadedCoordinates.TryAdd(chunk.Position, chunk);

            // Add the chunk to the loadedChunks list
            loadedChunks.Add(chunk);

            // Update the chunk's mesh
            UpdateChunkMesh(chunk);

            // Invoke the OnChunkLoad event with the loaded chunk
            OnChunkLoad?.Invoke(chunk);
        }

        foreach (Chunk chunk in destroyedChunks)
        {
            // Disable the chunk's main renderer
            chunk.MainRenderer.enabled = false;

            // Clear the chunk's mesh
            chunk.Mesh.Clear();
        }

        // Clear the chunksToRender list
        chunksToRender.Clear();

    }

    // Array to hold the planes for frustum culling.
    private static Plane[] planeCache = new Plane[6];

    // Determines if the bounds are visible from the given camera using frustum culling.
    private static bool IsVisibleFrom(Bounds bounds, Camera camera)
    {
        // Calculate the frustum planes.
        GeometryUtility.CalculateFrustumPlanes(camera, planeCache);

        // Test if the bounds intersect with the frustum.
        return GeometryUtility.TestPlanesAABB(planeCache, bounds);
    }

    // Caches the mesh triangles and vertices for the chunk being generated.
    private Triangle[] triangleCache;
    private float[] pointCache;

    // The array of vertices for the mesh.
    private Vector3[] meshVertices;

    // The array of triangles for the mesh.
    private int[] meshTriangles;

    // Adds the chunk meshes to the list of meshes to be updated.
    private void AddChunkMeshesToBeUpdated(int3 bl, int3 tr)
    {
        // Boolean flag to check if the mesh update lock is obtained.
        bool locked = false;
        try
        {
            // Attempt to obtain the mesh update lock.
            if (Monitor.TryEnter(meshUpdateLock))
            {
                locked = true;

                // Add all chunks within the given bounds to the list of meshes to be updated.
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

                // Set the mesh list as dirty.
                meshListDirty = true;
            }
            else
            {
                // If unable to obtain the mesh update lock, add the chunks to a temporary list.
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
            // Release the mesh update lock.
            if (locked)
            {
                Monitor.Exit(meshUpdateLock);
            }
        }
    }

    // This method updates the mesh of a chunk using a compute shader to generate the vertices and triangles
    private void UpdateChunkMesh(Chunk chunk, BufferSet bufferSet = null, bool pointsAlreadySet = false)
    {
        // Check if the current thread is not the main thread
        if (Thread.CurrentThread != MAIN_THREAD)
        {
            bool locked = false;
            try
            {
                // Try to enter a critical section using a lock
                if (Monitor.TryEnter(meshUpdateLock))
                {
                    locked = true;
                    // Add the chunk position to the list of meshes to update
                    meshesToUpdate.Add(chunk.Position);
                    // Mark the mesh list as dirty to indicate that there are changes to be processed
                    meshListDirty = true;
                }
                else
                {
                    // If the lock cannot be acquired, add the chunk position to a temporary list
                    meshesTEMP.Add(chunk.Position);
                }
            }
            finally
            {
                // Release the lock if it was acquired
                if (locked)
                {
                    Monitor.Exit(meshUpdateLock);
                }
            }
            // Exit the method if the current thread is not the main thread
            return;
        }

        // Calculate the number of points, voxels, and threads per axis, and the point spacing
        int numPoints = NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;
        int numVoxelsPerAxis = NumPointsPerAxis - 1;
        int numThreadsPerAxis = Mathf.CeilToInt(numVoxelsPerAxis / (float)THREAD_GROUP_SIZE);
        float pointSpacing = BoundsSize / (NumPointsPerAxis - 1);

        // Get the position and centre of the chunk
        int3 coord = chunk.Position;
        float3 centre = CentreFromCoord(coord);

        // Create a vector representing the world bounds
        float3 worldBounds = new float3(BoundsSize);

        // If no buffer set is provided, get a new buffer set
        if (bufferSet == null)
        {
            bufferSet = GetBufferSet();
        }

        // If the points have not already been set, generate them
        if (!pointsAlreadySet)
        {
            if (chunk.PointsLoaded)
            {
                // If the points are already loaded, set them in the buffer
                bufferSet.pointsBuffer.SetData(chunk.Points, 0, 0, numPoints);
            }
            else
            {
                // Otherwise, generate the points using the density generator
                DensityGenerator.Generate(bufferSet.pointsBuffer, NumPointsPerAxis, BoundsSize, worldBounds, centre, Vector3.one, pointSpacing, this, chunk);
            }
        }

        // Set the counter value of the triangle buffer to 0
        bufferSet.triangleBuffer.SetCounterValue(0);

        // Set the buffers and parameters of the generator shader
        GeneratorShader.SetBuffer(0, "points", bufferSet.pointsBuffer);
        GeneratorShader.SetBuffer(0, "triangles", bufferSet.triangleBuffer);
        GeneratorShader.SetInt("numPointsPerAxis", NumPointsPerAxis);
        GeneratorShader.SetFloat("boundsSize", BoundsSize);
        GeneratorShader.SetVector("centre", new Vector4(centre.x, centre.y, centre.z));
        GeneratorShader.SetFloat("spacing", pointSpacing);
        GeneratorShader.SetFloat("isoLevel", IsoLevel);

        // Dispatch the generator shader
        GeneratorShader.Dispatch(0, numThreadsPerAxis, numThreadsPerAxis, numThreadsPerAxis);

        // Copy the number of triangles generated to the triCountBuffer
        ComputeBuffer.CopyCount(bufferSet.triangleBuffer, bufferSet.triCountBuffer, 0);

        // Get the number of triangles generated from the triCountBuffer
        int[] triCountArray = { 0 };
        bufferSet.triCountBuffer.GetData(triCountArray);
        int numTris = triCountArray[0];

        // Copy the generated triangles to the triangleCache array
        bufferSet.triangleBuffer.GetData(triangleCache, 0, 0, numTris);

        // If the points in the chunk were not already set, copy the generated points to the chunk's Points array
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

        // Clear the mesh and initialize the meshVertices and meshTriangles arrays
        Mesh mesh = chunk.Mesh;
        mesh.Clear();
        if (meshVertices == null || meshVertices.Length < numTris * 3)
        {
            meshVertices = new Vector3[numTris * 3];
            meshTriangles = new int[numTris * 3];
        }

        // Use parallel for loop to generate the meshVertices and meshTriangles arrays
        Parallel.For(0, numTris, i =>
        {
            for (int j = 0; j < 3; j++)
            {
                meshTriangles[i * 3 + j] = i * 3 + j;
                meshVertices[i * 3 + j] = triangleCache[i][j];
            }
        });

        // Set the vertices, triangles, and normals of the mesh and update the chunk's state
        mesh.SetVertices(meshVertices, 0, numTris * 3, UnityEngine.Rendering.MeshUpdateFlags.Default);
        mesh.SetTriangles(meshTriangles, 0, numTris * 3, 0, false);
        mesh.RecalculateNormals();
        chunk.OnMeshUpdate();

        // Add the chunk to the list of chunks to be refreshed and set the refreshListDirty flag to true
        chunkMeshesToRefresh.Add(chunk);
        refreshListDirty = true;

        // Return the bufferSet to the bufferSetPool for later reuse
        ReturnBufferSet(bufferSet);
    }

    // Returns the normal of a given point in 3D space
    public float3 GetNormalOfPoint(float3 p)
    {
        // Calculate step size based on the bounds size and number of points per axis
        float step = BoundsSize / (NumPointsPerAxis + 1);

        // Initialize the normal vector to default values
        float3 nrm = default;

        // Calculate the x, y, and z components of the normal vector using finite differences
        nrm.x = (SamplePoint(p + float3(step, 0, 0)) / IsoLevel) - (SamplePoint(p - float3(step, 0, 0)) / IsoLevel);
        nrm.y = (SamplePoint(p + float3(0, step, 0)) / IsoLevel) - (SamplePoint(p - float3(0, step, 0)) / IsoLevel);
        nrm.z = (SamplePoint(p + float3(0, 0, step)) / IsoLevel) - (SamplePoint(p - float3(0, 0, step)) / IsoLevel);

        // Negate and normalize the normal vector
        nrm = -normalize(nrm);

        // Return the normal vector
        return nrm;
    }

    // Called when the Map object is destroyed
    private void OnDestroy()
    {
        // If the application is playing, release buffers and save loaded chunks to disk
        if (Application.isPlaying)
        {
            ReleaseBuffers();

            // Get the path to the persistent data folder
            string folder = Application.persistentDataPath;

            // Save all loaded chunks synchronously in parallel
            Parallel.For(0, loadedChunks.Count, i =>
            {
                loadedChunks[i].SaveSynchronously(folder);
            });
        }
    }

    // This method uses a sphere-shaped brush to modify the density values of the voxels within a given radius of a specified world position.
    // The brush can either add or subtract density based on the given erase mode.
    // The intensity parameter controls the strength of the brush, and sphereBrushSize determines the size of the brush.
    public void UseSphereBrush(float3 worldPos, bool EraseMode, float intensity, float3 sphereBrushSize)
    {
        // These magic intensity values are used to adjust the brush's falloff curve to produce a smoother effect.
        float3 magicIntensity = (sphereBrushSize / 5f) * 6f;

        // Convert the world position to a chunk position and calculate the brush value based on the intensity and iso level.
        float3 pos = worldPos;
        float brushValue = intensity * IsoLevel;
        if (EraseMode) brushValue *= -1;

        // Calculate the size of the brush in voxel space.
        int3 sphereBrushPointSize = (int3)ceil(sphereBrushSize / (BoundsSize / (NumPointsPerAxis)));
        int width = sphereBrushPointSize.x * 2;
        int height = sphereBrushPointSize.y * 2;
        int depth = sphereBrushPointSize.z * 2;

        // Calculate the chunk positions that need to be updated based on the brush size and position.
        int3 bl = WorldPosToChunkPos(worldPos - new float3(1) - new float3(sphereBrushSize));
        int3 tr = WorldPosToChunkPos(worldPos + new float3(1) + new float3(sphereBrushSize));

        // Loop through each voxel in the brush area in parallel and modify its density value based on its distance from the center of the brush.
        Parallel.For(0, width * height * depth, i =>
        {
            // Calculate the 3D index of the current voxel.
            int x = i % width;
            int y = (i / width) % height;
            int z = i / (width * height);

            // Check if we're testing the center point of the brush.
            bool testing = x == sphereBrushPointSize.x && y == sphereBrushPointSize.y && z == sphereBrushPointSize.z;

            // Calculate the world-space position of the current voxel based on its index and the size of the brush.
            float tx = lerp(-sphereBrushSize.x, sphereBrushSize.x, x / (float)width);
            float ty = lerp(-sphereBrushSize.y, sphereBrushSize.y, y / (float)height);
            float tz = lerp(-sphereBrushSize.z, sphereBrushSize.z, z / (float)depth);
            float3 samplePoint = new float3(tx, ty, tz) + pos;

            // Check if the sample point is outside the valid height range.
            if (samplePoint.y > CeilingHeight || samplePoint.y < FloorHeight)
            {
                return;
            }

            // Calculate the relative coordinates of the current voxel within the brush.
            float3 relativeCoords = new float3(x, y, z) - sphereBrushPointSize;

            // Calculate the distance of the current voxel from the center of the brush using a falloff curve.
            float3 sample3D = (relativeCoords * relativeCoords) / (sphereBrushPointSize * sphereBrushPointSize);
            float brushSample = -csum((sample3D - (1f / 3f)) * magicIntensity);

            // Ensure that the brush sample value is non-negative.
            if (brushSample < 0)
            {
                brushSample = 0;
            }

            // Add the painted point with the brush sample multiplied by the brush value.
            PaintPointAdd(samplePoint, brushSample * brushValue);
        });

        // Add the chunk meshes that need to be updated to the queue.
        AddChunkMeshesToBeUpdated(bl, tr);
    }

    // Asynchronously use a cube brush on the map, returning a task that completes when the brush is finished.
    public Task UseCubeBrushAsync(float3 worldPos, bool EraseMode, float intensity, float3 cubeBrushSize)
    {
        if (Thread.CurrentThread != MAIN_THREAD)
        {
            // If this method was called on a thread other than the main thread, enqueue the brush call and return a task that completes when the brush is finished.
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
            // Otherwise, use the cube brush on the main thread and immediately return a completed task.
            UseCubeBrush(worldPos, EraseMode, intensity, cubeBrushSize);
            return Task.CompletedTask;
        }
    }

    // Use a cube brush on the map.
    public void UseCubeBrush(float3 worldPos, bool EraseMode, float intensity, float3 cubeBrushSize)
    {
        if (Thread.CurrentThread != MAIN_THREAD)
        {
            // If this method was called on a thread other than the main thread, enqueue the brush call and return.
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

        // Calculate the brush value based on the intensity and ISO level.
        float3 pos = worldPos;
        float brushValue = intensity * IsoLevel;
        if (EraseMode) brushValue *= -1;

        // Calculate the size of the cube brush in terms of voxel points.
        int3 cubeBrushPointSize = (int3)ceil(cubeBrushSize / (BoundsSize / NumPointsPerAxis));

        // Calculate the dimensions of the cube brush in terms of voxel points.
        int width = cubeBrushPointSize.x * 2;
        int height = cubeBrushPointSize.y * 2;
        int depth = cubeBrushPointSize.z * 2;

        // Get the position of the camera.
        Vector3 camPos = MainCamera.transform.position;

        // Calculate the chunk positions of the corners of the brush.
        int3 bl = WorldPosToChunkPos(worldPos - new float3(1) - new float3(cubeBrushSize));
        int3 tr = WorldPosToChunkPos(worldPos + new float3(1) + new float3(cubeBrushSize));

        // Iterate over each voxel point in the cube brush.
        Parallel.For(0, width * height * depth, i =>
        {
            int x = i % width;
            int y = (i / width) % height;
            int z = i / (width * height);

            int3 point = new int3(x, y, z);

            // Calculate the position of the current voxel point in world space.
            float3 samplePoint = pos + ((float3)point - cubeBrushPointSize) * (BoundsSize / (NumPointsPerAxis));

            // Get the value of the cube brush at the current voxel point.
            float brushSample = GetCubeBrushValueAtPoint(point - cubeBrushPointSize, cubeBrushPointSize);

            // Paint the current voxel point with the brush value.
            PaintPointAdd(samplePoint, brushSample * brushValue);
        });

        // Add the chunk meshes that need to be updated to the queue.
        AddChunkMeshesToBeUpdated(bl, tr);
    }


    public void PaintPoint(float3 worldPosition, float value)
    {
        // Convert the world position to chunk position.
        int3 chunkPos = WorldPosToChunkPos(worldPosition);

        // Check if a chunk exists at the chunk position.
        if (TryGetChunkAtChunkPos(chunkPos, out Chunk chunk))
        {
            // If a chunk exists, paint the point.
            PaintPoint(chunk, WorldPosToPointPos(chunkPos, worldPosition), value);
        }
    }

    public void PaintPoint(Chunk chunk, int3 pointPosition, float value)
    {
        // Convert the point position to an index in the chunk's points array.
        int index = PointPosToIndex(pointPosition);

        // Check if the point already has the same value as the new value to be painted.
        if (chunk.Points[index] != value)
        {
            // If the point has a different value, update the point value and clamp it within a range.
            chunk.Points[index] = value;
            chunk.Points[index] = clamp(chunk.Points[index], IsoLevel - 4, IsoLevel + 4);

            // Update the neighboring points of the painted point.
            SetNeighboringPoints(chunk, pointPosition, value);
        }
    }


    public void PaintPointAdd(float3 worldPosition, float value)
    {
        // Convert the world position to chunk position.
        int3 chunkPos = WorldPosToChunkPos(worldPosition);

        // Check if a chunk exists at the chunk position.
        if (TryGetChunkAtChunkPos(chunkPos, out Chunk chunk))
        {
            // If a chunk exists, add the value to the point.
            PaintPointAdd(chunk, WorldPosToPointPos(chunkPos, worldPosition), value);
        }
    }

    public void PaintPointAdd(Chunk chunk, int3 pointPosition, float value)
    {
        // Convert the point position to an index in the chunk's points array.
        int index = PointPosToIndex(pointPosition);

        // Check if the value to be added is not zero.
        if (value != 0)
        {
            // If the value is not zero, add it to the point value and clamp it within a range.
            chunk.Points[index] += value;
            chunk.Points[index] = clamp(chunk.Points[index], IsoLevel - 4, IsoLevel + 4);

            // Update the neighboring points of the painted point.
            SetNeighboringPoints(chunk, pointPosition, chunk.Points[index]);
        }
    }

    // This method fires a synchronous ray at a given direction and returns whether it hit anything or not
    // It also returns the position of the hit point in case there was a hit
    public bool FireRaySync(Ray ray, out float3 hit, float maxDistance = 20f, float stepAmount = 0.25f)
    {
        // Call the other overload of this method with the ray's origin and direction
        return FireRaySync(ray.origin, ray.direction, out hit, maxDistance, stepAmount);
    }

    // This is the main method that fires the synchronous ray
    // It takes in a source position and a direction, and returns whether the ray hit anything or not
    // It also returns the position of the hit point in case there was a hit
    public bool FireRaySync(float3 source, float3 direction, out float3 hit, float maxDistance = 20f, float stepAmount = 0.25f)
    {
        // Initialize variables
        float3 hitPosition = source;
        float3 travelDirection = normalize(direction) * stepAmount;
        float value = 0;
        float distanceTravelled = 0f;

        // Loop through the ray until it reaches max distance or hits something
        do
        {
            // Sample the point at the current position
            value = SamplePoint(hitPosition);

            // If the value is NaN, stop the ray
            if (float.IsNaN(value))
            {
                break;
            }

            // If the value is greater than or equal to the iso level, there was a hit
            if (value >= IsoLevel)
            {
                hit = hitPosition;
                return true;
            }

            // Move the ray to the next position
            hitPosition = hitPosition + travelDirection;
            distanceTravelled += stepAmount;

        } while (distanceTravelled <= maxDistance);

        // If there was no hit, set the hit position to the default value and return false
        hit = default;
        return false;
    }

    // This method fires a parallel ray at a given direction and returns whether it hit anything or not
    // It also returns the position of the hit point in case there was a hit
    public bool FireRayParallel(Ray ray, out float3 hit, float maxDistance = 20f, float stepAmount = 0.25f)
    {
        // Call the other overload of this method with the ray's origin and direction
        return FireRayParallel(ray.origin, ray.direction, out hit, maxDistance, stepAmount);
    }

    // This is a cache for sampling the point along a ray
    private static float[] raySampleCache;

    // This method fires a parallel ray at a given direction and returns whether it hit anything or not
    // It also returns the position of the hit point in case there was a hit
    public bool FireRayParallel(float3 source, float3 direction, out float3 hit, float maxDistance = 20f, float stepAmount = 0.25f)
    {
        // Calculate the direction and distance to step in each iteration of the raycast
        float3 travelDirection = normalize(direction) * stepAmount;
        int increments = (int)(maxDistance / stepAmount);

        // Create an array to cache the voxel values sampled along the raycast path
        // The array size is based on the number of iterations needed to reach the maximum distance
        if (raySampleCache == null || raySampleCache.Length < increments)
        {
            raySampleCache = new float[increments];
        }

        // Sample the voxel values along the raycast path in parallel using multiple threads
        Parallel.For(0, increments, i =>
        {
            raySampleCache[i] = SamplePoint(source + (travelDirection * i));
        });

        // Check the sampled voxel values along the raycast path to find where the ray intersects the isosurface
        for (int i = 0; i < increments; i++)
        {
            if (float.IsNaN(raySampleCache[i]))  // If the voxel value is NaN, the ray has left the map and there is no intersection
            {
                break;
            }
            else if (raySampleCache[i] > IsoLevel)  // If the voxel value is greater than the isolevel, the ray has intersected the isosurface
            {
                // Interpolate the exact intersection point using the previous and current voxel samples
                if (i == 0)
                {
                    hit = source + (travelDirection * i);  // If the ray intersects the isosurface at the first sampled point, return that point
                }
                else
                {
                    float3 previousHit = source + (travelDirection * (i - 1));
                    float3 nextHit = source + (travelDirection * i);

                    float previousValue = raySampleCache[i - 1];
                    float nextValue = raySampleCache[i];

                    float tValue = unlerp(previousValue, nextValue, IsoLevel);  // Calculate the interpolation factor between the previous and current voxel samples

                    hit = lerp(previousHit, nextHit, tValue);  // Interpolate the exact intersection point using the interpolation factor
                    if (float.IsInfinity(hit.x) || float.IsNaN(hit.x))
                    {
                        hit = nextHit;
                    }
                }

                return true;
            }
        }

        // If the ray doesn't intersect the isosurface within the maximum distance, return false and a default hit position
        hit = default;
        return false;
    }

    private void SetNeighboringPoints(Chunk chunk, int3 pointPosition, float value)
    {
        // Get the number of voxels per axis (subtracting 1 since the number of points is always one more than the number of voxels).
        int numVoxelPerAxis = NumPointsPerAxis - 1;

        // Get the chunk position as an int3.
        int3 chunkPos = int3(chunk.Position.x, chunk.Position.y, chunk.Position.z);

        // Create a new int3 object called DI and initialize it to (0, 0, 0).
        int3 DI = new int3(0);

        // Declare a temporary int3 called chunkPosTemp.
        int3 chunkPosTemp;

        // Initialize cnt to 0.
        int cnt = 0;

        // Initialize pointIndex to 0.
        int pointIndex = 0;

        // Define a method called SetData, which checks if a chunk exists at chunkPosTemp, and if so, sets the point at pointIndex to the given value.
        void SetData()
        {
            if (loadedCoordinates.TryGetValue(chunkPosTemp, out Chunk chunk) && pointIndex < numPointsInChunk && pointIndex >= 0)
            {
                chunk.Points[pointIndex] = value;
            }
        }

        // If the point is on the positive X boundary, increment the DI.x by 1, set chunkPosTemp to the chunk to the right, set pointIndex to the index of the point on the opposite boundary, and call SetData.
        if (pointPosition.x == numVoxelPerAxis)
        {
            cnt++;
            DI.x++;
            chunkPosTemp = chunkPos;
            chunkPosTemp.x++;
            pointIndex = PointPosToIndex(0, pointPosition.y, pointPosition.z);
            SetData();
        }

        // If the point is on the positive Y boundary, increment the DI.y by 1, set chunkPosTemp to the chunk above, set pointIndex to the index of the point on the opposite boundary, and call SetData.
        if (pointPosition.y == numVoxelPerAxis)
        {
            cnt++;
            DI.y++;
            chunkPosTemp = chunkPos;
            chunkPosTemp.y++;
            pointIndex = PointPosToIndex(pointPosition.x, 0, pointPosition.z);
            SetData();
        }

        // If the point is on the positive Z boundary, increment the DI.z by 1, set chunkPosTemp to the chunk in front, set pointIndex to the index of the point on the opposite boundary, and call SetData.
        if (pointPosition.z == numVoxelPerAxis)
        {
            cnt++;
            DI.z++;
            chunkPosTemp = chunkPos;
            chunkPosTemp.z++;
            pointIndex = PointPosToIndex(pointPosition.x, pointPosition.y, 0);
            SetData();
        }

        // If the point is on the negative X boundary, decrement the DI.x by -1, set chunkPosTemp to the chunk to the left, set pointIndex to the index of the point on the opposite boundary, and call SetData.
        if (pointPosition.x == 0)
        {
            cnt++;
            DI.x--;
            chunkPosTemp = chunkPos;
            chunkPosTemp.x--;
            pointIndex = PointPosToIndex(numVoxelPerAxis, pointPosition.y, pointPosition.z);
            SetData();
        }

        // If the point is on the negative Y boundary, decrement the DI.x by -1, set chunkPosTemp to the chunk below, set pointIndex to the index of the point on the opposite boundary, and call SetData.
        if (pointPosition.y == 0)
        {
            cnt++;
            DI.y--;
            chunkPosTemp = chunkPos;
            chunkPosTemp.y--;
            pointIndex = PointPosToIndex(pointPosition.x, numVoxelPerAxis, pointPosition.z);
            SetData();
        }

        // If the point is on the negative Z boundary, decrement the DI.x by -1, set chunkPosTemp to the chunk to the back, set pointIndex to the index of the point on the opposite boundary, and call SetData.
        if (pointPosition.z == 0)
        {
            cnt++;
            DI.z--;
            chunkPosTemp = chunkPos;
            chunkPosTemp.z--;
            pointIndex = PointPosToIndex(pointPosition.x, pointPosition.y, numVoxelPerAxis);

            SetData();
        }

        // If there are at least 2 neighboring points, then an edge chunk needs to be updated
        if (cnt >= 2)
        {
            chunkPosTemp = chunkPos + DI; // Update chunkPosTemp to get the chunk in the direction of DI
            int x = DI.x == 0 ? pointPosition.x : DI.x > 0 ? 0 : numVoxelPerAxis; // Calculate the x-axis position of the neighboring point
            int y = DI.y == 0 ? pointPosition.y : DI.y > 0 ? 0 : numVoxelPerAxis; // Calculate the y-axis position of the neighboring point
            int z = DI.z == 0 ? pointPosition.z : DI.z > 0 ? 0 : numVoxelPerAxis; // Calculate the z-axis position of the neighboring point
            pointIndex = PointPosToIndex(x, y, z); // Calculate the index of the neighboring point

            SetData(); // Set the value of the neighboring point if the chunk containing the point is loaded

            // If there are three neighboring points, then corners chunk needs to be updated
            if (cnt == 3)
            {
                chunkPosTemp.z = chunkPos.z; // Set the z-axis component of chunkPosTemp to be the same as chunkPos to get the neighboring chunk in the z-axis direction
                pointIndex = PointPosToIndex(x, y, pointPosition.z); // Calculate the index of the neighboring point in the z-axis direction

                SetData();

                chunkPosTemp.z = chunkPos.z + DI.z; // Update the z-axis component of chunkPosTemp to get the chunk in the direction of DI
                chunkPosTemp.x = chunkPos.x; // Set the x-axis component of chunkPosTemp to be the same as chunkPos to get the neighboring chunk in the x-axis direction
                pointIndex = PointPosToIndex(pointPosition.x, y, z); // Calculate the index of the neighboring point in the x-axis direction

                SetData();

                chunkPosTemp.x = chunkPos.x + DI.x; // Update the x-axis component of chunkPosTemp to get the chunk in the direction of DI
                chunkPosTemp.y = chunkPos.y; // Set the y-axis component of chunkPosTemp to be the same as chunkPos to get the neighboring chunk in the y-axis direction
                pointIndex = PointPosToIndex(x, pointPosition.y, z); // Calculate the index of the neighboring point in the y-axis direction

                SetData();
            }
        }
    }

    //Samples a chunk point at the specified world position
    public float SamplePoint(Vector3 worldPosition)
    {
        // Convert world position to chunk position
        int3 chunkPos = WorldPosToChunkPos(worldPosition);

        // Get the chunk at the calculated chunk position
        Chunk chunk = GetChunkAtChunkPos(chunkPos);

        // If there is no chunk at the calculated chunk position, return NaN
        if (chunk == null)
        {
            return float.NaN;
        }
        // Otherwise, return the sample value at the point position in the chunk
        else
        {
            return SamplePoint(chunk, WorldPosToPointPos(chunkPos, worldPosition));
        }
    }

    // Returns the interpolation factor of a value within a range of values
    private static float InverseLerp(float a, float b, float value)
    {
        return (value - a) / (b - a);
    }

    // Returns the sample value at the given point position in the chunk
    public float SamplePoint(Chunk chunk, int3 pointPosition)
    {
        return chunk.Points[PointPosToIndex(pointPosition)];
    }

    // Converts chunk position to world position
    public float3 ChunkPosToWorldPos(int3 chunkPos)
    {
        return chunkPos * new float3(BoundsSize);
    }

    // Converts world position to chunk position
    public int3 WorldPosToChunkPos(float3 worldPos)
    {
        return new int3(round(worldPos / BoundsSize));
    }

    // Converts world position to point position in a chunk
    public int3 WorldPosToPointPos(int3 chunkPos, float3 worldPos)
    {
        // Calculate the normalized position of the point in the chunk
        float3 normalizedPos = ((worldPos - ((float3)chunkPos * BoundsSize)) / BoundsSize) + float3(0.5f);

        // Interpolate the normalized position to get the exact point position
        return int3(round(lerp(new float3(0), new float3(NumPointsPerAxis - 1), normalizedPos)));
    }

    // Converts point position in a chunk to world position
    public float3 PointPosToWorldPos(int3 chunkPos, int3 pointPos)
    {
        // Convert the point position to a normalized position
        float3 normalizedPos = unlerp(new float3(0), new float3(NumPointsPerAxis - 1), pointPos) - float3(0.5f);

        // Convert the normalized position to a world position
        return normalizedPos + ((float3)chunkPos * BoundsSize);
    }

    public Chunk GetChunkAtChunkPos(int3 chunkPos)
    {
        // Tries to retrieve a Chunk instance from the loadedCoordinates dictionary using the chunkPos as a key
        if (loadedCoordinates.TryGetValue(chunkPos, out Chunk chunk))
        {
            // Returns the Chunk instance if it is found
            return chunk;
        }
        else
        {
            // Returns null if the Chunk instance is not found
            return null;
        }
    }

    public bool TryGetChunkAtChunkPos(int3 chunkPos, out Chunk chunk)
    {
        // Tries to retrieve a Chunk instance from the loadedCoordinates dictionary using the chunkPos as a key
        if (loadedCoordinates.TryGetValue(chunkPos, out chunk))
        {
            // Returns true if the Chunk instance is found and sets the 'chunk' out parameter to the retrieved Chunk instance
            return true;
        }
        else
        {
            // Returns false if the Chunk instance is not found and sets the 'chunk' out parameter to null
            return false;
        }
    }

    public int PointPosToIndex(int3 pointPos)
    {
        // Calculates and returns the 1D array index for a voxel at the given 'pointPos' in the density array
        return PointPosToIndex(pointPos.x, pointPos.y, pointPos.z);
    }

    public int PointPosToIndex(int x, int y, int z)
    {
        // Calculates and returns the 1D array index for a voxel at the given (x, y, z) position in the density array
        return (z * NumPointsPerAxis * NumPointsPerAxis) + (y * NumPointsPerAxis) + x;
    }

    public int3 IndexToPointPos(int index)
    {
        // Calculates and returns the (x, y, z) position of a voxel at the given 1D array 'index' in the density array
        int x = index % NumPointsPerAxis;
        int y = (index / NumPointsPerAxis) % NumPointsPerAxis;
        int z = index / (NumPointsPerAxis * NumPointsPerAxis);

        return new int3(x, y, z);
    }

    private float GetCubeBrushValueAtPoint(int3 point, int3 cubeBrushPointSize)
    {
        float value;
        // Checks if the given 'point' is inside a cube brush with the given 'cubeBrushPointSize'
        if (point.x >= -cubeBrushPointSize.x && point.x < cubeBrushPointSize.x &&
            point.y >= -cubeBrushPointSize.y && point.y < cubeBrushPointSize.y &&
            point.z >= -cubeBrushPointSize.z && point.z < cubeBrushPointSize.z)
        {
            // Calculates and returns the value of the brush at the given 'point'
            value = length(cubeBrushPointSize);
        }
        else
        {
            // Returns 0 if the given 'point' is outside the cube brush
            value = 0;
        }

        return value;
    }

    private void CreateBuffers()
    {
        // Check if there are unused buffer sets available
        if (unusedBuffers.Count == 0)
        {
            // If there are no unused buffer sets, get a new one and return it to the pool
            ReturnBufferSet(GetBufferSet());
        }
    }

    private void ReleaseBuffers()
    {
        // Release all used buffer sets
        foreach (BufferSet buffer in usedBuffers)
        {
            buffer.triangleBuffer.Release();
            buffer.pointsBuffer.Release();
            buffer.triCountBuffer.Release();
        }

        usedBuffers.Clear();

        // Release all unused buffer sets
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
        // Calculate the center point of a chunk based on its coordinates
        return new float3(coord) * BoundsSize;
    }

    private void CreateChunkHolder()
    {
        if (chunkHolder == null)
        {
            // If there is no chunk holder object in the scene, create one
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
            // Log a warning if the method is called from a non-main thread in the Unity Editor
            Debug.Log($"{nameof(CreateChunk)} can only be called on the Main Thread");
        }
#endif
        // Create a new chunk object with the given coordinates
        GameObject chunk = new GameObject($"Chunk ({coord.x}, {coord.y}, {coord.z})");
        chunk.transform.parent = chunkHolder.transform;
        Chunk newChunk = chunk.AddComponent<Chunk>();
        newChunk.Position = coord;
        newChunk.SourceMap = this;
        return newChunk;
    }

    // This method is called when the script is loaded or values are changed in the inspector.
    // It sets a flag to indicate that the settings have been updated.
    private void OnValidate()
    {
        settingsUpdated = true;
    }

    // This method returns a BufferSet object from the unusedBuffers queue if available.
    // Otherwise, it creates a new BufferSet object and adds it to the usedBuffers list before returning it.
    // A BufferSet object contains ComputeBuffers used for generating and rendering voxel meshes.
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

    // This method creates a new BufferSet object with ComputeBuffers for generating and rendering voxel meshes.
    // A BufferSet object contains a triangleBuffer, a pointsBuffer, and a triCountBuffer.
    private BufferSet CreateBufferSet()
    {
        int numPoints = NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;
        int numVoxelsPerAxis = NumPointsPerAxis - 1;
        int numVoxels = numVoxelsPerAxis * numVoxelsPerAxis * numVoxelsPerAxis;
        int maxTriangleCount = numVoxels * 5;

        // If the triangleCache and pointCache arrays are null, create new ones with the appropriate sizes.
        if (triangleCache == null)
        {
            triangleCache = new Triangle[maxTriangleCount];
            pointCache = new float[numPoints];
        }

        // Create and return a new BufferSet object with the appropriate ComputeBuffers.
        return new BufferSet
        {
            triangleBuffer = new ComputeBuffer(maxTriangleCount, sizeof(float) * 3 * 3, ComputeBufferType.Append),
            pointsBuffer = new ComputeBuffer(numPoints, sizeof(float) * 4),
            triCountBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw)
        };
    }

    // This method removes a BufferSet object from the usedBuffers list and adds it to the unusedBuffers queue.
    private void ReturnBufferSet(BufferSet buffer)
    {
        usedBuffers.Remove(buffer);
        unusedBuffers.Enqueue(buffer);
    }

    private struct Triangle // Define a struct to represent a triangle
    {
#pragma warning disable 649  // Disable the warning about unassigned variables, because the variables will be assigned via the struct's indexer
        public Vector3 a; // The first vertex of the triangle
        public Vector3 b; // The second vertex of the triangle
        public Vector3 c; // The third vertex of the triangle

        public Vector3 this[int i] // Define an indexer to make it easy to access the vertices by index
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

    private void OnDrawGizmos() // This method is called by Unity to draw Gizmos in the Scene view
    {
        if (showBoundsGizmo) // Check if the "showBoundsGizmo" flag is set
        {
            Gizmos.color = boundsGizmoCol; // Set the color of the Gizmos to the "boundsGizmoCol"

            List<Chunk> chunks = (loadedChunks == null) ? new List<Chunk>(FindObjectsOfType<Chunk>()) : loadedChunks; // Get a list of all the loaded chunks
            for (int i = chunks.Count - 1; i >= 0; i--) // Loop through the chunks in reverse order
            {
                if (i >= chunks.Count) // If the index is out of range, skip this iteration
                {
                    continue;
                }
                Chunk chunk = chunks[i]; // Get the chunk at the current index
                Gizmos.color = boundsGizmoCol; // Set the color of the Gizmos to the "boundsGizmoCol"
                Gizmos.DrawWireCube(CentreFromCoord(chunk.Position), Vector3.one * BoundsSize); // Draw a wire cube at the center of the chunk with the size of the bounds
            }
        }
    }

}