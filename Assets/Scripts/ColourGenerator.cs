using UnityEngine;

[ExecuteInEditMode]
public class ColourGenerator : MonoBehaviour
{
    public Material mat;
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

    private void Update()
    {
        Init();
        UpdateTexture();

        Map m = FindObjectOfType<Map>();

        float boundsY = m.BoundsSize;

        mat.SetFloat("boundsY", boundsY);
        mat.SetFloat("normalOffsetWeight", normalOffsetWeight);

        mat.SetTexture("ramp", texture);
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