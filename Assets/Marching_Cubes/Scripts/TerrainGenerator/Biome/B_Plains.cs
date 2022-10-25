using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class B_Plains : Biome
{
	[Tooltip("The max deep and height of the plains, low values")][Range(0, Constants.MAX_HEIGHT - 1)]
	public int maxHeightDifference = Constants.MAX_HEIGHT/5;

	public override byte[] GenerateChunkData(Vector2Int vecPos, float[] biomeMerge)
	{
		byte[] chunkData = new byte[Constants.CHUNK_BYTES];
		float[] noise = NoiseManager.GenerateNoiseMap(scale, octaves, persistance, lacunarity, vecPos);

        var offset = new Vector2Int(vecPos.x * Constants.CHUNK_VERTEX_SIZE, vecPos.y * Constants.CHUNK_VERTEX_SIZE);

		for (int z = 0; z < Constants.CHUNK_VERTEX_SIZE; z++)
		{
			for (int x = 0; x < Constants.CHUNK_VERTEX_SIZE; x++)
			{
                for (int y = 0; y < Constants.CHUNK_VERTEX_HEIGHT; y++)
                {
                    int index = (x + z * Constants.CHUNK_VERTEX_SIZE + y * Constants.CHUNK_VERTEX_AREA) * Constants.CHUNK_POINT_BYTE;
                    //var value = Perlin.Noise(x, z, y);
                    var value = SimplexNoise.Noise.CalcPixel3D(x + offset.x, y, z + offset.y, 1f);
                    Debug.Log("POS = " + new Vector3Int(x + offset.x, y, z + offset.y));
                    Debug.Log("VALUE = " + value);

                    //chunkData[index] = UnityEngine.Random.Range(0f, 1f) >= 0.5f ? (byte)0 : (byte)255;
                    /*if (value >= 0.5f)
                    {
                        chunkData[index] = 0;
                    }
                    else
                    {
                        chunkData[index] = 255;
                    }*/
                }


                // Get surface height of the x,z position 
                    /*float height = Mathf.Lerp(
                        NoiseManager.Instance.worldConfig.surfaceLevel,//Biome merge height
                        (((terrainHeightCurve.Evaluate(noise[x + z * Constants.CHUNK_VERTEX_SIZE]) * 2 - 1) * maxHeightDifference) + NoiseManager.Instance.worldConfig.surfaceLevel),//Desired biome height
                        biomeMerge[x + z * Constants.CHUNK_VERTEX_SIZE]);//Merge value,0 = full merge, 1 = no merge

                    int heightY = Mathf.CeilToInt(height);//Vertex Y where surface start
                    int lastVertexWeigh = (int)((255 - isoLevel) * (height % 1) + isoLevel);//Weigh of the last vertex

                    for (int y = 0; y < Constants.CHUNK_VERTEX_HEIGHT; y++)
                    {
                        int index = (x + z  * Constants.CHUNK_VERTEX_SIZE + y * Constants.CHUNK_VERTEX_AREA) * Constants.CHUNK_POINT_BYTE;
                        if (y < heightY - 5)
                        {
                            chunkData[index] = 255;
                            chunkData[index + 1] = 4;//Rock
                        }
                        else if (y < heightY)
                        {
                            chunkData[index] = 255;
                            chunkData[index + 1] = 1;//dirt
                        }
                        else if (y == heightY)
                        {
                            chunkData[index] = (byte)lastVertexWeigh;
                            chunkData[index + 1] = 0;//grass

                        }
                        else
                        {
                            chunkData[index] = 0;
                            chunkData[index + 1] = Constants.NUMBER_MATERIALS;
                        }
                    }*/
            }
		}
		return chunkData;
	}

    public byte[] GenerateChunkDataOLD(Vector2Int vecPos, float[] biomeMerge)
    {
        byte[] chunkData = new byte[Constants.CHUNK_BYTES];
        float[] noise = NoiseManager.GenerateNoiseMap(scale, octaves, persistance, lacunarity, vecPos);
        for (int z = 0; z < Constants.CHUNK_VERTEX_SIZE; z++)
        {
            for (int x = 0; x < Constants.CHUNK_VERTEX_SIZE; x++)
            {
                // Get surface height of the x,z position 
                float height = Mathf.Lerp(
                    NoiseManager.Instance.worldConfig.surfaceLevel,//Biome merge height
                    (((terrainHeightCurve.Evaluate(noise[x + z * Constants.CHUNK_VERTEX_SIZE]) * 2 - 1) * maxHeightDifference) + NoiseManager.Instance.worldConfig.surfaceLevel),//Desired biome height
                    biomeMerge[x + z * Constants.CHUNK_VERTEX_SIZE]);//Merge value,0 = full merge, 1 = no merge

                int heightY = Mathf.CeilToInt(height);//Vertex Y where surface start
                int lastVertexWeigh = (int)((255 - isoLevel) * (height % 1) + isoLevel);//Weigh of the last vertex

                for (int y = 0; y < Constants.CHUNK_VERTEX_HEIGHT; y++)
                {
                    int index = (x + z * Constants.CHUNK_VERTEX_SIZE + y * Constants.CHUNK_VERTEX_AREA) * Constants.CHUNK_POINT_BYTE;
                    if (y < heightY - 5)
                    {
                        chunkData[index] = 255;
                        chunkData[index + 1] = 4;//Rock
                    }
                    else if (y < heightY)
                    {
                        chunkData[index] = 255;
                        chunkData[index + 1] = 1;//dirt
                    }
                    else if (y == heightY)
                    {
                        chunkData[index] = (byte)lastVertexWeigh;
                        chunkData[index + 1] = 0;//grass

                    }
                    else
                    {
                        chunkData[index] = 0;
                        chunkData[index + 1] = Constants.NUMBER_MATERIALS;
                    }
                }
            }
        }
        return chunkData;
    }
}
