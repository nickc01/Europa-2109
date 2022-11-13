using System;
using TMPro;
using UnityEngine;

public class ObjectsFoundUI : MonoBehaviour
{
    [NonSerialized]
    private int maxObjects = 0;

    [NonSerialized]
    private int foundObjects = 0;

    [NonSerialized]
    private TextMeshProUGUI text;
    private string template;

    private void Awake()
    {
        text = GetComponent<TextMeshProUGUI>();
        template = text.text;
        UpdateText();
    }

    public void OnObjectsStart(int objectsToFind)
    {
        maxObjects = objectsToFind;
        UpdateText();
    }

    public void OnObjectFound(FindableObject foundObject)
    {
        foundObjects++;
        UpdateText();
    }

    private void UpdateText()
    {
        text.text = $"{template} {foundObjects}/{maxObjects}";
    }
}
