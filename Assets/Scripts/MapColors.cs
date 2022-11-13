using UnityEngine;

//[ExecuteInEditMode]
public class MapColors : MonoBehaviour
{
    public Material mat;
    [Range(0, 1)]
    public float fogDstMultiplier = 1;

    public Vector4 shaderParams;
    private Map map;
    private Camera cam;

    public Gradient gradient;
    public float normalOffsetWeight;
    private Texture2D texture;
    private const int textureResolution = 50;

    private void Init()
    {
        if (texture == null || texture.width != textureResolution)
        {
            texture = new Texture2D(textureResolution, 1, TextureFormat.RGBA32, false);
        }
    }

    private void Awake()
    {
        Init();
        UpdateTexture();

        if (map == null)
        {
            map = FindObjectOfType<Map>();
        }
        if (cam == null)
        {
            cam = FindObjectOfType<Camera>();
        }

        mat.SetTexture("ramp", texture);
        mat.SetVector("params", shaderParams);

        RenderSettings.fogColor = cam.backgroundColor;
        RenderSettings.fogEndDistance = map.ViewDistance * fogDstMultiplier;
    }

    private void UpdateTexture()
    {
        if (gradient != null)
        {
            Color[] colours = new Color[texture.width];
            for (int i = 0; i < textureResolution; i++)
            {
                Color gradientCol = gradient.Evaluate(i / (textureResolution - 1f));
                colours[i] = gradientCol;
            }

            texture.SetPixels(colours);
            texture.Apply();
        }
    }
}