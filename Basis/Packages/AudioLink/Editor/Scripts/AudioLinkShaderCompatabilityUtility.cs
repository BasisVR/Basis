using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AudioLink.Editor
{
    public class AudioLinkShaderCompatabilityUtility
    {
        private const string OldAbsolutePath = "Assets/AudioLink/Shaders/AudioLink.cginc";
        private const string NewAbsolutePath = "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc";
        private const string MenuItemPath = "Tools/AudioLink/Update AudioLink Compatible Shaders";

        private const string DialogText =
            "Do you want to check all shaders in this project for AudioLink 0.3.x compatibility and update them if necessary? This is useful for upgrading projects using AudioLink 0.2.x or below."
            + "\n" + "\n" +
            "If you choose 'Go Ahead', shader files in this project which include references to 'Assets/AudioLink/Shaders/AudioLink.cginc' will be edited to use the new path introduced by AudioLink 0.3.x. Shaders which already use the new or custom path will be unaffected. You should make a backup before proceeding."
            + "\n" + "\n" +
            "(You can always run this utility manually via the " + MenuItemPath + " menu command)";

        private const string DialogTitle = "Upgrade AudioLink compatible shaders";
        private const string DialogOkButton = "I Made a Backup, Go Ahead!";
        private const string DialogCancelButton = "No Thanks";


        [MenuItem(MenuItemPath)]
        public static void UpgradeShaders()
        {
            if (EditorUtility.DisplayDialog(DialogTitle, DialogText, DialogOkButton, DialogCancelButton))
            {
                UpgradeShaderFiles();
                UpgradeCgincFiles();
                AssetDatabase.Refresh();
            }
        }

        private static void UpgradeShaderFiles()
        {
            ShaderInfo[] shaders = ShaderUtil.GetAllShaderInfo();

            foreach (ShaderInfo shaderinfo in shaders)
            {
                Shader shader = Shader.Find(shaderinfo.name);
                string path = AssetDatabase.GetAssetPath(shader);
                // we want to avoid built-in shaders so we check the Path
                if (path.StartsWith("Assets") || path.StartsWith("Packages"))
                {
                    ReplaceInFile(path);
                }
            }
        }

        private static void UpgradeCgincFiles()
        {
            List<string> cgincs = FindCgincFiles(Application.dataPath);
            cgincs.AddRange(FindCgincFiles(Path.GetDirectoryName(Application.dataPath) + "/Packages"));

            foreach (string cginc in cgincs)
            {
                ReplaceInFile(cginc);
            }
        }

        private static List<string> FindCgincFiles(string path)
        {
            List<string> cgincList = new List<string>();

            string[] files = Directory.GetFiles(path);
            foreach (string file in files)
            {
                if (file.EndsWith(".cginc") || file.EndsWith(".hlsl"))
                {
                    cgincList.Add(file);
                }
            }

            string[] folders = Directory.GetDirectories(path);
            foreach (string folder in folders)
            {
                List<string> cgincs = FindCgincFiles(folder);
                cgincList.AddRange(cgincs);
            }

            return cgincList;
        }

        private static void ReplaceInFile(string path)
        {
            string[] shaderSource = File.ReadAllLines(path);
            bool shouldWrite = false;
            for (int i = 0; i < shaderSource.Length; i++)
            {
                string line = shaderSource[i];
                if (line.Contains(OldAbsolutePath))
                {
                    line = line.Replace(OldAbsolutePath, NewAbsolutePath);
                    while (line.Contains("/Packages"))
                    {
                        line = line.Replace("./Packages", "Packages");
                        line = line.Replace("/Packages", "Packages");
                    }
                    shaderSource[i] = line;
                    shouldWrite = true;
                }
                else if (!line.Contains(NewAbsolutePath) && line.Contains("AudioLink.cginc"))
                {
                    string trimmedLine = line.Replace("#include", "").Trim().Trim('"');
                    string fullPath = Path.GetFullPath(Path.GetDirectoryName(path) + '/' + trimmedLine.TrimStart('/')).Replace('\\', '/');
                    if (fullPath.EndsWith(OldAbsolutePath))
                    {
                        int index = line.IndexOf("#include");
                        string whitespace = line.Remove(index, line.Length - index);
                        shaderSource[i] = whitespace + "#include \"" + NewAbsolutePath + "\"";
                        shouldWrite = true;
                    }
                }
            }

            if (shouldWrite)
            {
                File.WriteAllLines(path, shaderSource);
            }
        }
    }
}
