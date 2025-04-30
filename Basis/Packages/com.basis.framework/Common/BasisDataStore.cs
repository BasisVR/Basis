using System.Globalization;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using System;
namespace Basis.Scripts.Common
{
    public static class BasisDataStore
    {
        // Method to save the avatar(string and byte) to a file using JSON
        public static void SaveAvatar(string avatarName, byte avatarData, string fileNameAndExtension)
        {
            string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
            string json = JsonUtility.ToJson(new BasisSavedAvatar(avatarName, avatarData));
            File.WriteAllText(filePath, json);
            BasisDebug.Log("Avatar saved to " + filePath);
        }
        [System.Serializable]
        public class BasisSavedAvatar
        {
            public string UniqueID;//this is associated with a ID on Disc
            public byte loadmode;
            public BasisSavedAvatar(string name, byte data)
            {
                UniqueID = name;
                 loadmode = data;
            }
        }

        // Method to load the avatar (string and byte) from a file using JSON
        public static bool LoadAvatar(string fileNameAndExtension, string defaultName, byte defaultData,out BasisSavedAvatar BasisSavedAvatar)
        {
            string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
            if (File.Exists(filePath))
            {
                BasisSavedAvatar avatarWrapper = new BasisSavedAvatar(defaultName, defaultData);
                string json = File.ReadAllText(filePath);
                try {
                    avatarWrapper = JsonUtility.FromJson<BasisSavedAvatar>(json);
				}
                catch (Exception e)
                {
                    BasisDebug.LogError("BasisDataStore: JSON file cannot be loaded - switching to default.");
                }

				if (avatarWrapper == null || string.IsNullOrEmpty(avatarWrapper.UniqueID))
                {
                    avatarWrapper.UniqueID = defaultName;
                    avatarWrapper.loadmode = defaultData;
                }
                BasisDebug.Log("Avatar loaded from " + filePath);
                BasisSavedAvatar = avatarWrapper;
                return true;
            }
            else
            {
                BasisDebug.LogWarning("File not found at " + filePath);
                BasisSavedAvatar = null;
                return false;
            }
        }

        // Method to save the string to a file using JSON
        public static void SaveString(string stringContents, string fileNameAndExtension)
        {
            string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
            string json = JsonUtility.ToJson(new BasisSavedString(stringContents));
            File.WriteAllText(filePath, json);
            BasisDebug.Log("String saved to " + filePath);
        }

        // Method to load the string from a file using JSON
        public static string LoadString(string fileNameAndExtension, string defaultValue)
        {
            string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                BasisSavedString stringWrapper = JsonUtility.FromJson<BasisSavedString>(json);
                BasisDebug.Log("String loaded from " + filePath);
                return stringWrapper.ToValue();
            }
            else
            {
                Debug.LogWarning("File not found at " + filePath);
                return defaultValue;
            }
        }

        // Method to save an int value to a file
        public static void SaveInt(int intValue, string fileNameAndExtension)
        {
            string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
            string json = JsonUtility.ToJson(new BasisSavedInt(intValue));
            File.WriteAllText(filePath, json);
            BasisDebug.Log("Int saved to " + filePath);
        }

        // Method to load an int value from a file
        public static int LoadInt(string fileNameAndExtension, int defaultValue)
        {
            string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                BasisSavedInt intWrapper = JsonUtility.FromJson<BasisSavedInt>(json);
                BasisDebug.Log("Int loaded from " + filePath);
                return intWrapper.ToValue();
            }
            else
            {
                Debug.LogWarning("File not found at " + filePath);
                return defaultValue;
            }
        }

        // Method to save a float value to a file using invariant culture
        public static void SaveFloat(float floatValue, string fileNameAndExtension)
        {
            string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
            string json = JsonUtility.ToJson(new BasisSavedFloat(floatValue.ToString(CultureInfo.InvariantCulture)));
            File.WriteAllText(filePath, json);
            BasisDebug.Log("Float saved to " + filePath);
        }

        // Method to load a float value from a file using invariant culture
        public static bool LoadFloat(string fileNameAndExtension, float defaultValue, out float returningValue)
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
                else
                {
                    Debug.LogWarning("Failed to parse float, returning default value.");
                    returningValue = defaultValue;
                    return false;
                }
            }
            else
            {
                Debug.LogWarning("File not found at " + filePath);
                returningValue = defaultValue;
                return false;
            }
        }

        // Wrapper class for serializing and deserializing the string
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

        // Wrapper class for serializing and deserializing the int
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

        // Wrapper class for serializing and deserializing the float using a string representation (to handle culture)
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

        // Wrapper class for serializing and deserializing a list of URLs
        [System.Serializable]
        private class BasisSavedUrlList
        {
            public List<string> UrlList;

            public BasisSavedUrlList(List<string> BundleURL)
            {
                UrlList = BundleURL;
            }

            public List<string> ToValue()
            {
                return UrlList;
            }
        }
    }
}
