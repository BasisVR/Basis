using System.Globalization;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Common
{
    public static class BasisDataStore
    {
        public static void SaveAvatar(string avatarName, byte avatarData, string fileNameAndExtension)
        {
            try
            {
                string filePath = Path.Combine(Application.persistentDataPath, fileNameAndExtension);
                string json = JsonUtility.ToJson(new BasisSavedAvatar(avatarName, avatarData));
                File.WriteAllText(filePath, json);
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

            public BasisSavedAvatar(string name, byte data)
            {
                UniqueID = name;
                loadmode = data;
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
                File.WriteAllText(filePath, json);
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
                File.WriteAllText(filePath, json);
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
                File.WriteAllText(filePath, json);
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

        /// <summary>
        /// Saves an object (usually a <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>)
        /// directly as a JSON file. The output is properly formatted in a way that is ideal for cross-platform use:
        /// pretty-printed, tab indentation, LF line endings, and has the POSIX-compliant newline at the end of the file.
        /// </summary>
        /// <param name="filePath">The full path to the file where the JSON will be saved.</param>
        /// <param name="value">The object to serialize and save.</param>
        public static void SaveJson(string filePath, object value)
        {
            try
            {
                // First ensure that the directory exists.
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                // Then write the file. The using statements ensure that everything is disposed when the scope ends,
                // avoiding garbage collection, and that everything is disposed properly, even when exceptions occur.
                using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    System.Text.UTF8Encoding utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                    using (StreamWriter sw = new StreamWriter(fs, utf8NoBom))
                    {
                        sw.NewLine = "\n";
                        using (Newtonsoft.Json.JsonTextWriter jw = new Newtonsoft.Json.JsonTextWriter(sw))
                        {
                            jw.Formatting = Newtonsoft.Json.Formatting.Indented;
                            jw.IndentChar = '\t';
                            jw.Indentation = 1;
                            Newtonsoft.Json.JsonSerializer serializer = new Newtonsoft.Json.JsonSerializer();
                            serializer.Serialize(jw, value);
                            jw.WriteRaw("\n"); // POSIX-compliant newline at end of file.
                            jw.Close();
                        }
                        sw.Close();
                    }
                }
                BasisDebug.Log("Json saved to " + filePath);
            }
            catch (System.Exception e)
            {
                BasisDebug.LogWarning("SaveJson failed: " + e.Message);
            }
        }

        /// <summary>
        /// Loads an object (usually a <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>)
        /// directly from a JSON file. If loading fails, the provided default value is returned.
        /// </summary>
        /// <typeparam name="T">The type of object to load from JSON.</typeparam>
        /// <param name="filePath">The full path to the JSON file to load.</param>
        /// <param name="defaultValue">The default value to return if loading fails.</param>
        /// <returns>The loaded object, or the default value if loading fails.</returns>
        public static T LoadJson<T>(string filePath, T defaultValue)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    if (string.IsNullOrEmpty(json))
                    {
                        return defaultValue;
                    }
                    T value = Newtonsoft.Json.JsonConvert.DeserializeObject<T>(json);
                    BasisDebug.Log("Json loaded from " + filePath);
                    return value == null ? defaultValue : value;
                }
            }
            catch (System.Exception e)
            {
                BasisDebug.LogWarning("LoadJson failed: " + e.Message);
            }
            return defaultValue;
        }
    }
}
