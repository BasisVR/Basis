using System.Collections.Generic;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    [System.Serializable]
    public class SMWorkAround
    {
        public string Name;
        public string DefaultValue;
        public string SelectedValue;
        public int Index;
        [SerializeField]
        public List<SMSelectableValues> SelectableValueList = new List<SMSelectableValues>();
        public float Min;
        public float Max;
    }
}