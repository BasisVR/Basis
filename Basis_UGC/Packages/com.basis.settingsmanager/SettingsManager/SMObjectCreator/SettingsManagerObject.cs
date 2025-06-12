using BattlePhaze.SettingsManager;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public class SettingsManagerObject : MonoBehaviour
    {
        [SerializeField]
        public SettingsManagerEnums.IsType Type;
        [SerializeField]
        public UnityEngine.Object TxtDescription;
        [SerializeField]
        public UnityEngine.Object OptionReference;
        [SerializeField]
        public UnityEngine.Object ResetReference;
        [SerializeField]
        public UnityEngine.Object ApplyReference;
        public void Initalize(SettingsMenuInput Option)
        {
            Option.TextDescription = TxtDescription;
            Option.ObjectInput = OptionReference;
            Option.ResetToDefault = ResetReference;
            Option.ApplyInput = ApplyReference;
        }
    }
}