namespace BattlePhaze.SettingsManager.Intergrations
{
    /// <summary>
    /// Settings Manager Option Utilitys
    /// </summary>
    public static class SettingsManagerOptionUtilitys
    {
        /// <summary>
        /// Option Compare
        /// </summary>
        /// <param name="InputOption"></param>
        /// <param name="OptionName"></param>
        /// <returns></returns>
        public static bool OptionCompare(SettingsMenuInput InputOption, string OptionName)
        {
            if (InputOption.Name == OptionName)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
        public static SettingsMenuInput InputCreator(string Name, string Explanation, string ValueDescriptor, SettingsManagerEnums.IsType Type, SettingsManagerEnums.ItemParse Parse)
        {
            SettingsMenuInput Input = new SettingsMenuInput();
            Input.Name = Name;
            Input.Explanation = Explanation;
            Input.ValueDescriptor = ValueDescriptor;
            Input.Type = Type;
            Input.ParseController = Parse;
            return Input;
        }
    }
}