using UnityEngine;
using UnityEngine.UI;

public class InstructionalProgressBar : MonoBehaviour
{
    private static InstructionalProgressBar _instance;
    public static InstructionalProgressBar Instance => _instance ??= GameObject.FindObjectOfType<InstructionalProgressBar>();


    static InstructionalProgressBar()
    {
        Submarine.OnGameReload += Submarine_OnGameReload;
    }

    private static void Submarine_OnGameReload()
    {
        _instance = null;
    }

    private Slider _slider;

    public Slider Slider => _slider ??= GetComponent<Slider>();

    private GameObject _objectLock = null;

    public static GameObject ObjectLock
    {
        get => Instance._objectLock;
        set
        {
            if (value != Instance._objectLock)
            {
                Instance._objectLock = value;
                Progress = 0;
            }
        }
    }

    public static float Progress
    {
        get => Instance.Slider.value;
        set => Instance.Slider.value = value;
    }
}
