namespace BattlePhaze.SettingsManager
{
    using System.Collections.Generic;
    using UnityEngine;

    [System.Serializable]
    public class SMSelectableValues
    {
        [SerializeField]
        private string realValue;
        [SerializeField]
        public string UserValue;

        public string RealValue
        {
            get => realValue.ToLower(); // Use expression-bodied members for brevity and performance
            set => realValue = value.ToLower();
        }

        // Use List<T>.Capacity to preallocate memory, avoiding multiple resizes during Add
        public static List<string> GetRealValueArray(List<SMSelectableValues> selectableValues)
        {
            int count = selectableValues.Count;
            var listOfRealValues = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                listOfRealValues.Add(selectableValues[i].RealValue);
            }
            return listOfRealValues;
        }

        public static List<string> GetUserValueArray(List<SMSelectableValues> selectableValues)
        {
            int count = selectableValues.Count;
            var listOfUserValues = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                listOfUserValues.Add(selectableValues[i].UserValue);
            }
            return listOfUserValues;
        }

        // Replace ref with a return value to simplify method usage and improve performance
        public static List<SMSelectableValues> AddSelection(List<SMSelectableValues> selectableValues, string realValue, string userValue)
        {
            if (selectableValues == null)
            {
                selectableValues = new List<SMSelectableValues>();
            }

            // Use constructor initialization for clarity and performance
            selectableValues.Add(new SMSelectableValues
            {
                RealValue = realValue,
                UserValue = userValue
            });

            return selectableValues;
        }
    }
}