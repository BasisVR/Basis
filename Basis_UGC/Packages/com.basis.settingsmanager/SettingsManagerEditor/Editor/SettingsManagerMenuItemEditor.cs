using BattlePhaze.SettingsManager.DebugSystem;
using UnityEditor;
using UnityEngine;
namespace BattlePhaze.SettingsManager.SMUnityEditor
{
    /// <summary>
    /// Settings manager
    /// </summary>
    public class SettingsManagerMenuItemEditor : Editor
    {
        [MenuItem("Tools/BattlePhaze/Settings Manager/External Documentation")]
        public static void Documentation()
        {
            Application.OpenURL("https://docs.google.com/document/d/1oomvlVtnaTo82SvzwUdxJph2vhGh7Wbh_W64UPX3Ap4/edit?usp=sharing");
        }
        [MenuItem("Tools/BattlePhaze/Settings Manager/Add Settings Manager To Scene")]
        public static void AddSettingsManager()
        {
            GameObject Object = (GameObject)AssetDatabase.LoadAssetAtPath("Packages/com.battlephaze.settingsmanager/SettingsManagerDemo/Settings Manager Prefab/Settings Manager.prefab", typeof(GameObject));
            if (Object == null)
            {
                Object = (GameObject)AssetDatabase.LoadAssetAtPath("Packages/com.battlephaze.settingsmanager/BattlePhazeSettingsManager/SettingsManagerDemo/Settings Manager Prefab/Settings Manager.prefab", typeof(GameObject));
            }
            if (Object == null)
            {
                Object = (GameObject)AssetDatabase.LoadAssetAtPath("Assets/Settings Manager/SettingsManagerDemo/Settings Manager Prefab/Settings Manager.prefab", typeof(GameObject));
            }
            if (Object == null)
            {
                Object = (GameObject)AssetDatabase.LoadAssetAtPath("Assets/Settings Manager/BattlePhazeSettingsManager/SettingsManagerDemo/Settings Manager Prefab/Settings Manager.prefab", typeof(GameObject));
            }
            if (Object == null)
            {
                Object = (GameObject)AssetDatabase.LoadAssetAtPath("Assets/BattlePhazeSettingsManager/SettingsManagerDemo/Settings Manager Prefab/Settings Manager.prefab", typeof(GameObject));
            }
            if (Object == null)
            {
                SettingsManagerDebug.LogError("Cant instantiate Settings Manager Missing Gameobject");
            }
            else
            {
                PrefabUtility.InstantiatePrefab(Object);
            }
        }
    }
}