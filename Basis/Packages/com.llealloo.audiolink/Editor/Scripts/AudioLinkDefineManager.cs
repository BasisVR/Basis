#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

#if UNITY_6000_0_OR_NEWER
using UnityEditor.Build;
using UnityEditor.Build.Profile;
#endif

namespace AudioLink.Editor
{
    [InitializeOnLoad]
    public class AudioLinkDefineManager
    {
        static AudioLinkDefineManager()
        {
            AddDefinesIfMissing(EditorUserBuildSettings.selectedBuildTargetGroup, new string[] { "AUDIOLINK", "AUDIOLINK_V1" });
            Shader.EnableKeyword("AUDIOLINK_IMPORTED");
        }

        private static void AddDefinesIfMissing(BuildTargetGroup buildGroup, params string[] newDefines)
        {
            bool definesChanged = false;
#if UNITY_6000_0_OR_NEWER
            var profile = BuildProfile.GetActiveBuildProfile();
            string[] defines = profile != null
                ? profile.scriptingDefines
                : PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(buildGroup)).Split(';');
#else
            string[] defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(buildGroup).Split(';');
#endif
            HashSet<string> defineSet = new HashSet<string>(defines);

            foreach (string newDefine in newDefines)
                definesChanged |= defineSet.Add(newDefine);

            if (definesChanged)
            {
#if UNITY_6000_0_OR_NEWER
                if (profile != null)
                {
                    profile.scriptingDefines = defineSet.ToArray();
                    Debug.LogFormat("Set Scripting Define Symbols for selected build profile ({0}) to: {1}", profile.name, string.Join(";", defineSet));
                }
                else
                {
                    PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(buildGroup), defines);
                    Debug.LogFormat("Set Scripting Define Symbols for selected build group ({0}) to: {1}", buildGroup.ToString(), string.Join(";", defineSet));
                }
#else
                string finalDefineString = string.Join(";", defineSet.ToArray());
                PlayerSettings.SetScriptingDefineSymbolsForGroup(buildGroup, finalDefineString);
                Debug.LogFormat("Set Scripting Define Symbols for selected build target ({0}) to: {1}", buildGroup.ToString(), finalDefineString);
#endif
            }
        }
    }
}
#endif
