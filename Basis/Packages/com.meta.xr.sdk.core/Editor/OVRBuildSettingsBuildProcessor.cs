/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor;

public class OVRBuildSettingsBuildProcessor : IPreprocessBuildWithReport

{
    public int callbackOrder => 0;

    // Spaces were added here to make this string unique, so we do not overwrite what devs set themselves.
    private const string LTOArgs = "     -flto=thin";
    private const string il2cppLTOArgs = "--compiler-flags=\"" + LTOArgs + "\""; // --compiler-flags="     -flto=thin"

    private string AddLTOFlag(string args)
    {
        var compilerFlagsMatch = Regex.Match(args, @"--compiler-flags\s*?=\s*?\"".*?\""");
        if (compilerFlagsMatch.Success)
        {
            string compilerFlags = compilerFlagsMatch.Value;
            string restArgs = args.Replace(compilerFlags, "");
            compilerFlags = compilerFlags.Trim('\"') + " " + LTOArgs + "\"";
            args = restArgs + " " + compilerFlags;
        }
        else
        {   // No --compiler-flags exists there, add the full string
            args = args + " " + il2cppLTOArgs;
        }
        return args;
    }

    private string RemoveLTOFlag(string args)
    {
        args = args.Replace(" " + il2cppLTOArgs, "");
        args = args.Replace(il2cppLTOArgs, "");
        args = args.Replace(" " + LTOArgs, "");
        return args;
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        var projectConfig = OVRProjectConfig.CachedProjectConfig;
        if (projectConfig == null)
        {
            return;
        }

        string IL2CPPArgs = PlayerSettings.GetAdditionalIl2CppArgs();
        string envIL2CPPArgs = Environment.GetEnvironmentVariable("IL2CPP_ADDITIONAL_ARGS");

        // Only add the flag when the enableIL2CPPLTo is checked and the build is Android, release and il2cpp
#pragma warning disable CS0618 // Type or member is obsolete
        if (!projectConfig.enableIL2CPPLTO || EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android || EditorUserBuildSettings.androidBuildType != AndroidBuildType.Release || PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android) != UnityEditor.ScriptingImplementation.IL2CPP)
#pragma warning restore CS0618 // Type or member is obsolete
        {
            string IL2CPPArgsRemoveLTO = RemoveLTOFlag(IL2CPPArgs);
            if (IL2CPPArgs != IL2CPPArgsRemoveLTO)
            {
                PlayerSettings.SetAdditionalIl2CppArgs(IL2CPPArgsRemoveLTO);
            }
            return;
        }

        if (String.IsNullOrEmpty(IL2CPPArgs) && string.IsNullOrEmpty(envIL2CPPArgs))
        {
            Debug.Log("Set AdditionalIL2CPPArgs: --compiler-flags=\"-flto=thin\"");
            PlayerSettings.SetAdditionalIl2CppArgs(il2cppLTOArgs);
            CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
        }
        else
        {
            // Check if lto is already enabled
            var ltoPattern = @"--compiler-flags\s*?=\s*?\"".*?-flto.*?\""";
            if ((!String.IsNullOrEmpty(IL2CPPArgs) && Regex.Match(IL2CPPArgs, ltoPattern).Success) ||
                (!String.IsNullOrEmpty(envIL2CPPArgs) && Regex.Match(envIL2CPPArgs, ltoPattern).Success))
            {
                return;
            }
            else
            {
                Debug.Log("Add compiler flags in AdditionalIL2CPPArgs: \"-flto=thin\"");
                string newIL2CPPArgs = AddLTOFlag(IL2CPPArgs);
                PlayerSettings.SetAdditionalIl2CppArgs(newIL2CPPArgs);
                CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
            }
        }
    }
}
