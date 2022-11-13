using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LaserAnimator : MonoBehaviour
{
    [SerializeField]
    private List<Sprite> sprites = new List<Sprite>();

    [SerializeField]
    private float fps = 30f;
    private SpriteRenderer sRenderer;

    private void Start()
    {
        StartCoroutine(AnimatorRoutine());
        sRenderer = GetComponent<SpriteRenderer>();
    }

    private IEnumerator AnimatorRoutine()
    {
        float frameTime = 1f / fps;
        while (true)
        {
            for (int i = 0; i < sprites.Count; i++)
            {
                sRenderer.sprite = sprites[i];
                yield return new WaitForSeconds(frameTime);
            }
        }
    }
}
