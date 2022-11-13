using System.Collections.Generic;
using UnityEngine;

public class NoiseDensity : MapGenerator
{

    [Header("Noise")]
    public int numOctaves = 4;
    public float lacunarity = 2;
    public float persistence = .5f;
    public float noiseScale = 1;
    public float noiseWeight = 1;
    public bool closeEdges;
    public float floorOffset = 1;
    public float weightMultiplier = 1;

    public float hardFloorHeight;
    public float hardFloorWeight;

    public Vector4 shaderParams;

    public override ComputeBuffer Generate(ComputeBuffer pointsBuffer, int numPointsPerAxis, float boundsSize, Vector3 worldBounds, Vector3 centre, Vector3 offset, float spacing, Map map, Chunk chunk)
    {
        buffersToRelease = new List<ComputeBuffer>();

        // Noise parameters
        System.Random prng = new System.Random(map.WorldSeed);
        Vector3[] offsets = new Vector3[numOctaves];
        float offsetRange = 1000;
        for (int i = 0; i < numOctaves; i++)
        {
            offsets[i] = new Vector3((float)prng.NextDouble() * 2 - 1, (float)prng.NextDouble() * 2 - 1, (float)prng.NextDouble() * 2 - 1) * offsetRange;
        }

        ComputeBuffer offsetsBuffer = new ComputeBuffer(offsets.Length, sizeof(float) * 3);
        offsetsBuffer.SetData(offsets);
        buffersToRelease.Add(offsetsBuffer);

        Map.ChunkGenerationParameters chunkParams = chunk.ChunkGenerationParameters();

        densityShader.SetVector("centre", new Vector4(centre.x, centre.y, centre.z));
        densityShader.SetInts("chunkPosition", chunk.Position.x, chunk.Position.y, chunk.Position.z);
        densityShader.SetInt("octaves", Mathf.Max(1, numOctaves));
        densityShader.SetFloat("lacunarity", lacunarity);
        densityShader.SetFloat("persistence", persistence);
        densityShader.SetFloat("noiseScale", noiseScale);
        densityShader.SetFloat("noiseWeight", noiseWeight);
        densityShader.SetBool("closeEdges", closeEdges);
        densityShader.SetBuffer(0, "offsets", offsetsBuffer);
        densityShader.SetFloat("floorOffset", floorOffset);
        densityShader.SetFloat("weightMultiplier", weightMultiplier);
        densityShader.SetFloat("hardFloor", hardFloorHeight);
        densityShader.SetFloat("hardFloorWeight", hardFloorWeight);
        densityShader.SetFloat("isoLevel", map.IsoLevel);
        densityShader.SetFloat("isoOffset", chunkParams.IsoOffset);
        densityShader.SetFloat("isoOffsetByHeight", chunkParams.IsoOffsetByHeight);
        densityShader.SetFloat("isoOffsetByHeightAbs", chunkParams.IsoOffsetByHeightAbs);

        densityShader.SetVector("params", shaderParams);

        return base.Generate(pointsBuffer, numPointsPerAxis, boundsSize, worldBounds, centre, offset, spacing, map, chunk);
    }
}