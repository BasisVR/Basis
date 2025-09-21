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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.MultiplayerBlocks.Fusion.Editor
{
    [InitializeOnLoad]
    internal class PhotonPackagesImportProcessor
    {
        private const string VoiceAssemblyName = "PhotonVoice";
        private const string PhysicsAddOnAssemblyName = "Fusion.Addons.Physics";
        // If you see compilation errors related to these symbols, remove them from the project's scripting definition
        // via PlayerSettings -> Script Compilation -> Scripting Define Symbols.
        // You can also do this for all the platform targets via editing the /ProjectSettings/ProjectSettings.asset in text editor.
        private const string VoiceDefineSymbol = "PHOTON_VOICE_DEFINED";
        private const string PhysicsAddOnDefineSymbol = "PHOTON_FUSION_PHYSICS_ADDON_DEFINED";

        static PhotonPackagesImportProcessor()
        {
            SetupDefineSymbolForAssembly(VoiceAssemblyName, VoiceDefineSymbol);
            SetupDefineSymbolForAssembly(PhysicsAddOnAssemblyName, PhysicsAddOnDefineSymbol);
        }

        private static void SetupDefineSymbolForAssembly(string assemblyName, string symbol)
        {
            try
            {
                Assembly.Load(assemblyName);
                ProcessSymbolsForAllBuildTargets(defineSymbols =>
                {
                    if (defineSymbols.Contains(symbol)) return false;
                    defineSymbols.Add(symbol);
                    return true;
                });
            }
            catch (Exception)
            {
                // Assembly not found, package is not installed or removed, remove the define symbol
                // Note: Practically this processor only guarantee project compilation when package is not installed,
                // but if it's installed then removed, it might not work well and requires user removing the symbols themselves.
                ProcessSymbolsForAllBuildTargets(defineSymbols => !defineSymbols.Contains(symbol) &&
                                                                  defineSymbols.Remove(symbol));
            }
        }

        private static void ProcessSymbolsForAllBuildTargets(Func<List<string>, bool> func)
        {
            foreach (BuildTarget target in Enum.GetValues(typeof(BuildTarget)))
            {
                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
                if (group == BuildTargetGroup.Unknown)
                {
                    continue;
                }
#pragma warning disable CS0618 // Type or member is obsolete
                var defineSymbols = PlayerSettings.GetScriptingDefineSymbolsForGroup(group).Split(';').Select(d => d.Trim()).ToList();
#pragma warning restore CS0618 // Type or member is obsolete
                if (!func(defineSymbols)) continue;
                try
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", defineSymbols.ToArray()));
#pragma warning restore CS0618 // Type or member is obsolete
                }
                catch (Exception e)
                {
                    Debug.LogErrorFormat("Could not remove Scripting Define Symbol for build target: {1} group: {2} {3}", target, group, e);
                }
            }
        }
    }
}
