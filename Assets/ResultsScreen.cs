using TMPro;
using UnityEngine;

public class ResultsScreen : MonoBehaviour
{
    [SerializeField]
    private TextMeshProUGUI resultsText;



    public void OnWin()
    {
        resultsText.text = "You Win!";
        gameObject.SetActive(true);
        Cursor.lockState = CursorLockMode.None;
    }

    public void OnLose()
    {
        resultsText.text = "Game Over";
        gameObject.SetActive(true);
        Cursor.lockState = CursorLockMode.None;
    }
}
