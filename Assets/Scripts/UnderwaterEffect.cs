using UnityEngine;

// This script is used to create an underwater effect in the scene view.
// The [ExecuteAlways] attribute allows the script to be executed both in play mode and in edit mode.
// The [ImageEffectAllowedInSceneView] attribute allows the effect to be visible in the scene view.
public class UnderwaterEffect : MonoBehaviour
{
    [SerializeField]
    private Material rippleMaterial; // The material used to render the ripple effect.

    // This function is called by the camera to render the image after all rendering is complete.
    // The function applies the ripple effect to the image.
    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        Graphics.Blit(source, destination, rippleMaterial);
    }
}