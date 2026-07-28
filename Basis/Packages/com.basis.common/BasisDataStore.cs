using System.Globalization;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Common
{
    public static class BasisDataStore
    {
        public static void SaveAvatar(string avatarName, byte avatarData, string fileNameAndExtension, string unlockPassword = "")
        {
            try
            {
                string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
                string json = JsonUtility.ToJson(new BasisSavedAvatar(avatarName, avatarData, unlockPassword));
                WriteAllTextAtomic(filePath, json);
                BasisDebug.Log("Avatar saved to " + filePath);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("SaveAvatar failed: " + e.Message);
            }
        }

        [System.Serializable]
        public class BasisSavedAvatar
        {
            public string UniqueID;
            public byte loadmode;
            public string Pass;

            public BasisSavedAvatar(string name, byte data, string pass = "")
            {
                UniqueID = name;
                loadmode = data;
                Pass = pass;
            }
        }

        private static void WriteAllTextAtomic(string filePath, string contents)
        {
            string tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, contents);
            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }
        }

        public static bool LoadAvatar(string fileNameAndExtension, string defaultName, byte defaultData, out BasisSavedAvatar BasisSavedAvatar)
        {
            try
            {
                string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    BasisSavedAvatar avatarWrapper = JsonUtility.FromJson<BasisSavedAvatar>(json);
                    if (string.IsNullOrEmpty(avatarWrapper.UniqueID))
                    {
                        avatarWrapper.UniqueID = defaultName;
                        avatarWrapper.loadmode = defaultData;
                    }
                    BasisDebug.Log("Avatar loaded from " + filePath);
                    BasisSavedAvatar = avatarWrapper;
                    return true;
                }
            }
            catch (System.Exception e)
            {
                BasisDebug.LogWarning("LoadAvatar failed: " + e.Message);
            }

            BasisSavedAvatar = new BasisSavedAvatar(defaultName, defaultData);
            return false;
        }

        public static void SaveString(string stringContents, string fileNameAndExtension)
        {
            try
            {
                string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
                string json = JsonUtility.ToJson(new BasisSavedString(stringContents));
                WriteAllTextAtomic(filePath, json);
                BasisDebug.Log("String saved to " + filePath);
            }
            catch (System.Exception e)
            {
                BasisDebug.LogWarning("SaveString failed: " + e.Message);
            }
        }

        public static string LoadString(string fileNameAndExtension, string defaultValue)
        {
            try
            {
                string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    BasisSavedString stringWrapper = JsonUtility.FromJson<BasisSavedString>(json);
                    BasisDebug.Log("String loaded from " + filePath);
                    return stringWrapper.ToValue();
                }
            }
            catch (System.Exception e)
            {
                BasisDebug.LogWarning("LoadString failed: " + e.Message);
            }

            return defaultValue;
        }

        public static void SaveInt(int intValue, string fileNameAndExtension)
        {
            try
            {
                string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
                string json = JsonUtility.ToJson(new BasisSavedInt(intValue));
                WriteAllTextAtomic(filePath, json);
                BasisDebug.Log("Int saved to " + filePath);
            }
            catch (System.Exception e)
            {
                BasisDebug.LogWarning("SaveInt failed: " + e.Message);
            }
        }

        public static int LoadInt(string fileNameAndExtension, int defaultValue)
        {
            try
            {
                string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    BasisSavedInt intWrapper = JsonUtility.FromJson<BasisSavedInt>(json);
                    BasisDebug.Log("Int loaded from " + filePath);
                    return intWrapper.ToValue();
                }
            }
            catch (System.Exception e)
            {
                BasisDebug.LogWarning("LoadInt failed: " + e.Message);
            }

            return defaultValue;
        }

        public static void SaveFloat(float floatValue, string fileNameAndExtension)
        {
            try
            {
                string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
                string json = JsonUtility.ToJson(new BasisSavedFloat(floatValue.ToString(CultureInfo.InvariantCulture)));
                WriteAllTextAtomic(filePath, json);
                BasisDebug.Log("Float saved to " + filePath);
            }
            catch (System.Exception e)
            {
                BasisDebug.LogWarning("SaveFloat failed: " + e.Message);
            }
        }

        public static bool LoadFloat(string fileNameAndExtension, float defaultValue, out float returningValue)
        {
            try
            {
                string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    BasisSavedFloat floatWrapper = JsonUtility.FromJson<BasisSavedFloat>(json);
                    if (float.TryParse(floatWrapper.ToValue(), NumberStyles.Float, CultureInfo.InvariantCulture, out float loadedFloat))
                    {
                        BasisDebug.Log("Float loaded from " + filePath);
                        returningValue = loadedFloat;
                        return true;
                    }
                }
            }
            catch (System.Exception e)
            {
                BasisDebug.LogWarning("LoadFloat failed: " + e.Message);
            }

            returningValue = defaultValue;
            return false;
        }

        [System.Serializable]
        private class BasisSavedString
        {
            public string String;

            public BasisSavedString(string saveString)
            {
                String = saveString;
            }

            public string ToValue()
            {
                return String;
            }
        }

        [System.Serializable]
        private class BasisSavedInt
        {
            public int Value;

            public BasisSavedInt(int value)
            {
                Value = value;
            }

            public int ToValue()
            {
                return Value;
            }
        }

        [System.Serializable]
        private class BasisSavedFloat
        {
            public string Value;

            public BasisSavedFloat(string value)
            {
                Value = value;
            }

            public string ToValue()
            {
                return Value;
            }
        }

        [System.Serializable]
        private class BasisSavedUrlList
        {
            public List<string> UrlList;

            public BasisSavedUrlList(List<string> bundleURL)
            {
                UrlList = bundleURL;
            }

            public List<string> ToValue()
            {
                return UrlList;
            }
        }
    }
}
