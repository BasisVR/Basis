namespace BattlePhaze.SettingsManager
{
    public class SettingsManagerEnums
    {
        public enum CultureType
        {
            InvariantCulture, CurrentCulture, CurrentUICulture, InstalledUICulture
        }
        public enum SupportedRenderPipelines
        {
            HighDefinitionRenderPipeline =0, BuiltIn= 3, UniversalRenderPipeline =4
        }
        public enum DestroyOnLoadSettings
        {
            DontDestroy, Destroyable
        }
        public enum State
        {
            Default, NewValuePresent, LoadFromSave
        }
        public enum TextReturn
        {
            SliderPercentage, SliderRawValue
        }
        public enum IsType
        {
            Toggle, DropDown, Dynamic, Slider, Disabled
        }
        public enum IsTypeInterpreter
        {
            Toggle, DropDown, Slider, Text,Management
        }
        public enum ItemParse
        {
            intValue, NormalValue
        }
        public enum GraphicsVendor
        {
            NVIDIA, ATI, INTEL, ALL
        }
        public enum MasterQualityState
        {
            WontEffectThis, WillEffectThis, MasterQualityOption
        }
        public enum captialisation
        {
            Normal, ToLower
        }
    }
}