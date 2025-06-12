namespace BattlePhaze.SettingsManager
{
    using System.Collections.Generic;
    using UnityEngine;
    using static BattlePhaze.SettingsManager.SettingsManagerEnums;

    [System.Serializable]
    public partial class SettingsMenuInput
    {
#if UNITY_EDITOR
        [SerializeField]
        public EditorToggles EditorBasedUIToggles = new EditorToggles();
#endif
        [SerializeField]
        public List<SupportedRenderPipelines> SupportedRenderPipeline = new List<SupportedRenderPipelines>() { SupportedRenderPipelines.BuiltIn, SupportedRenderPipelines.UniversalRenderPipeline, SupportedRenderPipelines.HighDefinitionRenderPipeline };
        [SerializeField]
        public bool Round = false;
        [SerializeField]
        public int RoundTo = 2;
        [SerializeField]
        public float MaxPercentage = 100;
        [SerializeField]
        public float MinPercentage = 0;
        [SerializeField]
        public string SliderMaxValue = "100";
        [SerializeField]
        public string SliderMinValue = "0";
        [SerializeField]
        public string Name = string.Empty;
        [SerializeField]
        public string ValueDescriptor = string.Empty;
        [SerializeField]
        public string SelectedValueDefault = string.Empty;
        [SerializeField]
        public string ValueDefault = string.Empty;
        [SerializeField]
        public string Explanation = string.Empty;
        [SerializeField]
        private string selectedValue = string.Empty;
        [SerializeField]
        public int OptionIndex
        {
            get
            {
                List<SettingsMenuInput> options = SettingsManager.Instance.Options;
                if (options != null)
                {
                    int hash = this.GetHashCode();
                    for (int Index = 0; Index < options.Count; Index++)
                    {
                        if (hash == options[Index].GetHashCode())
                        {
                            return Index;
                        }
                    }
                }
                return -1;
            }
        }
        public string SelectedValue
        {
            get
            {
                return selectedValue;
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    selectedValue = string.Empty;
                }
                else
                {
                    selectedValue = value.ToLower();
                }
            }
        }

        [SerializeField]
        public IsType Type;
        [SerializeField]
        public Object TextDescription;
        [SerializeField]
        public Object ObjectInput;
        [SerializeField]
        public Object ApplyInput;
        [SerializeField]
        public UnityEngine.Events.UnityAction<float> FloatAction;
        [SerializeField]
        public UnityEngine.Events.UnityAction<int> IntAction;
        [SerializeField]
        public UnityEngine.Events.UnityAction<bool> BoolAction;
        [SerializeField]
        public UnityEngine.Events.UnityAction ResetAction;
        [SerializeField]
        public UnityEngine.Events.UnityAction ApplyAction;
        /// <summary>
        /// this will most likely be a button
        /// this will simply reset the selected value back to the default value
        /// </summary>
        [SerializeField]
        public Object ResetToDefault;
        [SerializeField]
        public List<SMPlatFormDefault> PlatFormDefaultState = new List<SMPlatFormDefault>();
        [SerializeField]
        public List<SMExcludeFromPlatforms> ExcludeFromThesePlatforms = new List<SMExcludeFromPlatforms>();
        [SerializeField]
        public MasterQualityState MasterQualityState;
        [SerializeField]
        public ItemParse ParseController;
        [SerializeField]
        public TextReturn ReturnedValueTextType;
        [SerializeField]
        public List<SMSelectableValues> SelectableValueList = new List<SMSelectableValues>();
        public List<string> UserValues
        {
            get
            {
                List<string> values = new List<string>();
                for (int RealValueIndex = 0; RealValueIndex < SelectableValueList.Count; RealValueIndex++)
                {
                    values.Add(SelectableValueList[RealValueIndex].UserValue);
                }
                return values;
            }
        }
        public List<string> RealValues
        {
            get
            {
                List<string> values = new List<string>();
                for (int RealValueIndex = 0; RealValueIndex < SelectableValueList.Count; RealValueIndex++)
                {
                    values.Add(SelectableValueList[RealValueIndex].RealValue);
                }
                return values;
            }
        }
    }
}