using UnityEngine;

public class SphereDensity : MapGenerator
{

    public float radius = 1;

    public override ComputeBuffer Generate(ComputeBuffer pointsBuffer, int numPointsPerAxis, float boundsSize, Vector3 worldBounds, Vector3 centre, Vector3 offset, float spacing, Map map, Chunk chunk)
    {
        densityShader.SetFloat("radius", radius);
        return base.Generate(pointsBuffer, numPointsPerAxis, boundsSize, worldBounds, centre, offset, spacing, map, chunk);
    }
}