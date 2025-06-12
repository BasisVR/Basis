using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public class SettingsManagerOptionInputCreator : MonoBehaviour
    {
        public static void GetAllCreators(out List<string> paths, out List<SettingsManagerObject> objects)
        {
#if UNITY_EDITOR
            paths = new List<string>();
            objects = new List<SettingsManagerObject>();
            if (Application.isPlaying == false)
            {
                return;
            }
            string[] guids = AssetDatabase.FindAssets("t:GameObject");
            foreach (string guid in guids)
            {
                if (string.IsNullOrEmpty(guid))
                {
                    continue;
                }
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                GameObject obj = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                if (obj.TryGetComponent<SettingsManagerObject>(out var smObject))
                {
                    paths.Add(path);
                    objects.Add(smObject);
                }
            }
#else
        paths = new List<string>();
        objects = new List<SettingsManagerObject>();
#endif
        }

        public static GameObject LoadGameObject(string path)
        {
#if UNITY_EDITOR
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
#else
return null;
#endif
        }

        public static SettingsManagerObject GetSettingsManagerObject(GameObject obj)
        {
#if UNITY_EDITOR
            return obj.GetComponent<SettingsManagerObject>();
#else
return null;
#endif
        }

        public static SettingsManagerObject SpawnCreatedObject(string path, Transform parent)
        {
#if UNITY_EDITOR
            GameObject obj = LoadGameObject(path);
            GameObject spawnedObj = Instantiate(obj, parent);
            SettingsManagerObject smObject = GetSettingsManagerObject(spawnedObj);
            return smObject;
#else
return null;
#endif
        }
    }
}