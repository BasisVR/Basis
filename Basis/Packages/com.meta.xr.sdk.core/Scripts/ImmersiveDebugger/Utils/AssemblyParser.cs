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
using System.Threading.Tasks;
using UnityEngine;
using Assembly = System.Reflection.Assembly;
#if UNITY_EDITOR
using Meta.XR.Editor.Callbacks;
using UnityEditor;
#endif // UNITY_EDITOR

namespace Meta.XR.ImmersiveDebugger.Utils
{
    internal static class AssemblyParser
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init() // reset static fields in case of domain reload disabled
        {
            _types?.Clear();
            _assembliesParsed = false;
            _prebakedRuntimeSettings = null;
#if UNITY_EDITOR
            RuntimeSettings.OnImmersiveDebuggerEnabledChanged -= OnSettingChanged;
#endif
        }

        private static List<Type> _types = new List<Type>();
        private static bool _assembliesParsed = false;
        private static event Action<List<Type>> OnAssemblyParsed;

        public static bool Ready => _assembliesParsed;
        private static bool GetImmersiveDebuggerEnabled() => RuntimeSettings.Instance.ImmersiveDebuggerEnabled;
        private static Func<bool> _enabledDelegate = GetImmersiveDebuggerEnabled;
        public static bool Enabled => _enabledDelegate.Invoke();

        private static Assembly[] GetAllAssemblies() => AppDomain.CurrentDomain.GetAssemblies();
        private static Func<Assembly[]> _assembliesDelegate = GetAllAssemblies;

        private static RuntimeSettings _prebakedRuntimeSettings;

#if UNITY_EDITOR
        static AssemblyParser()
        {
            RuntimeSettings.OnImmersiveDebuggerEnabledChanged += OnSettingChanged;
        }
#endif

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
#endif // UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod]
        private static void OnLoad()
        {
#if UNITY_EDITOR
            if (EditorApplication.isPlaying)
            {
                InitializeOnLoad.Register(RefreshWhenPlaying);
            }
#else
            Refresh();
#endif
        }


#if UNITY_EDITOR
        public static void OnSettingChanged()
        {
            if (!Application.isPlaying)
            {
                if (RuntimeSettings.Instance.ImmersiveDebuggerEnabled)
                {
                    AssemblyParser.RegisterAssemblyTypes(RuntimeSettings.UpdateAllDebugTypesForInstance);
                    AssemblyParser.Refresh(true); // re-bake the DebugType assets
                }
                else
                {
                    AssemblyParser.Unregister(RuntimeSettings.UpdateAllDebugTypesForInstance);
                }
            }
        }

#endif


        private static void RefreshWhenPlaying()
        {
            Refresh();
        }

        public static void Refresh(bool ignorePrebakedAsset = false)
        {
            if (Enabled)
            {
                _ = LoadAssembliesMainThread(ignorePrebakedAsset);
            }
        }

        private async static Task LoadAssembliesMainThread(bool ignorePrebakedAsset)
        {
            _assembliesParsed = false;
            _types.Clear();
            _prebakedRuntimeSettings = !ignorePrebakedAsset ? RuntimeSettings.Instance : null;
            await Task.Run(LoadAssembliesAsync);
            OnAssemblyParsed?.Invoke(_types);
            _assembliesParsed = true;
        }

        private static Task LoadAssembliesAsync()
        {
            var assemblies = _assembliesDelegate.Invoke();
            foreach (Assembly assembly in assemblies)
            {
                if (_prebakedRuntimeSettings != null)
                {
                    if (!_prebakedRuntimeSettings.debugTypesDict.ContainsKey(assembly.GetName().Name))
                    {
                        continue;
                    }
                    foreach (var debugType in _prebakedRuntimeSettings.debugTypesDict[assembly.GetName().Name])
                    {
                        try
                        {
                            _types.Add(assembly.GetType(debugType, true));
                        }
                        catch (Exception)
                        {
                            Debug.LogWarning($"Immersive Debugger cannot get {debugType} type from assembly {assembly.GetName().Name}, skipping");
                        }
                    }
                }
                else
                {
                    var types = assembly.GetTypes().Where(
                        t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance).Any(
                            m => m.GetCustomAttribute<DebugMember>() != null));
                    foreach (Type type in types)
                    {
                        _types.Add(type);
                    }
                }
            }

            return Task.CompletedTask;
        }

        public static void RegisterAssemblyTypes(Action<List<Type>> del)
        {
            if (Ready)
            {
                del?.Invoke(_types);
            }

            OnAssemblyParsed -= del;
            OnAssemblyParsed += del;
        }

        public static void Unregister(Action<List<Type>> del)
        {
            OnAssemblyParsed -= del;
        }
    }
}

