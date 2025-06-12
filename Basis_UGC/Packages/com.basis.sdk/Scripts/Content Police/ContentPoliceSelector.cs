using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "ContentPoliceSelector", menuName = "Basis/ContentPoliceSelector")]
[System.Serializable]
public class ContentPoliceSelector : ScriptableObject
{
    [SerializeField]
    public List<string> selectedTypes;
}
