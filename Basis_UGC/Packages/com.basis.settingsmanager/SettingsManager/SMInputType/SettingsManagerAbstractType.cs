using BattlePhaze.SettingsManager;
using UnityEngine;
namespace BattlePhaze.SettingsManager.Types
{
    public abstract class SettingsManagerAbstractType : MonoBehaviour
    {
        public abstract SettingsManagerEnums.IsTypeInterpreter GetActiveType();
    }
}