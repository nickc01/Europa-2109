using UnityEngine;

[ExecuteAlways, ImageEffectAllowedInSceneView]
public class UnderwaterEffect : MonoBehaviour
{
    [SerializeField]
    private Material rippleMaterial;

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        Graphics.Blit(source, destination, rippleMaterial);
    }
}
