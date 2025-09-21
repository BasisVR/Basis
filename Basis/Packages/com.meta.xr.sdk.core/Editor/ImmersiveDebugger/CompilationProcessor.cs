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
using UnityEditor;
using UnityEditor.Compilation;
using Meta.XR.Editor.Callbacks;
using System.IO;
using System.Linq;
using System.Reflection;
using Meta.XR.Editor.Settings;
using UnityEditor.Callbacks;
using UnityEngine;
using Assembly = System.Reflection.Assembly;

namespace Meta.XR.ImmersiveDebugger.Editor
{
    /// <summary>
    /// If ImmersiveDebugger is enabled,
    /// listen for assembly compilation events and bake DebugMember
    /// requests from those changed assembly into ScriptableObject.
    /// Only runs in Editor and during compilation time.
    /// </summary>
    internal class CompilationProcessor
    {
        internal static List<string> CompiledAssemblies = new();
        [InitializeOnLoadMethod]
        private static void OnLoad()
        {
            InitializeOnLoad.Register(Init);
        }

        private static void Init()
        {
            if (RuntimeSettings.Instance.ImmersiveDebuggerEnabled)
            {
                // Only add debug types incrementally when ImmersiveDebugger is enabled
                CompilationPipeline.assemblyCompilationFinished += (s, _) =>
                {
                    if (!string.IsNullOrEmpty(s))
                    {
                        CompiledAssemblies.Add(s);
                    }
                };

                CompilationPipeline.compilationFinished += _ => OnCompilationEnded();
            }
        }

        internal static void OnCompilationEnded()
        {
            ClearCompiledAssemblies();
            SaveCompiledAssemblies(CompiledAssemblies);
            CompiledAssemblies.Clear();
        }

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            InitializeOnLoad.Register(ProcessCompiledAssemblies);
        }

        internal static void ProcessCompiledAssemblies()
        {
            if (!RuntimeSettings.Instance.ImmersiveDebuggerEnabled) // only process when ID is enabled
            {
                return;
            }

            var compiledAssemblies = LoadCompiledAssemblies();
            foreach (var s in compiledAssemblies)
            {
                string absolutePath = Path.Combine(Application.dataPath, "..", s);
                // not try-catch here because any compiler error would be intercepted as not surfaced well in console
                Assembly assembly = Assembly.LoadFile(absolutePath);
                var types = assembly.GetTypes().Where(
                    t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                      BindingFlags.Instance | BindingFlags.DeclaredOnly).Any(
                        m => m.GetCustomAttribute<DebugMember>() != null));
                RuntimeSettings.UpdateTypes(assembly.GetName().Name, types.ToList().ConvertAll(type => type.FullName));
            }
        }

        #region Persistence for Compiled Assemblies
        // persist compiled assemblies across compilation and scripting reload
        private static readonly Setting<string> CompiledAssembliesStorage = new UserString()
        {
            Owner = Utils.ToolDescriptor,
            Uid = "CompiledAssemblies",
            SendTelemetry = false,
            Default = string.Empty
        };

        private static List<string> LoadCompiledAssemblies()
        {
            var serializedList = CompiledAssembliesStorage.Value;
            return string.IsNullOrEmpty(serializedList)
                ? new List<string>()
                : serializedList.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private static void SaveCompiledAssemblies(List<string> compiledAssemblies)
        {
            var serializedList = string.Join(";", compiledAssemblies);
            CompiledAssembliesStorage.SetValue(serializedList);
        }

        private static void ClearCompiledAssemblies()
        {
            CompiledAssembliesStorage.Reset();
        }
        #endregion
    }
}
