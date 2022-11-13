using UnityEngine;

[ExecuteAlways, ImageEffectAllowedInSceneView]
public class UnderwaterEffect : MonoBehaviour
{
    [SerializeField]
    private Material rippleMaterial;

    // Start is called before the first frame update
    private void Start()
    {

    }

    // Update is called once per frame
    private void Update()
    {
        //rippleMaterial.SetFloat("_RotationY", -transform.eulerAngles.x * 3);
        //rippleMaterial.SetFloat("_RotationX", -transform.eulerAngles.y * 2);
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        Graphics.Blit(source, destination, rippleMaterial);
    }
}
