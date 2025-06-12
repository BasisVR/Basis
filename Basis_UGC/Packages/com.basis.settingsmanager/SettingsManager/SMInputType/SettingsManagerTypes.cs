using UnityEngine;

namespace BattlePhaze.SettingsManager.Types
{
    public static class SettingsManagerTypesHelper
    {
        public static bool TypeCompare(object Type, System.Type TypeAgainst)
        {
            if (Type != null)
            {
                return Type.GetType() == TypeAgainst;
            }
            return false;
        }
    }
}