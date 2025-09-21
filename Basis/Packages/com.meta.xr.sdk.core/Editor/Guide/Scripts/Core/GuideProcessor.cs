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
using System.Text.RegularExpressions;
using Meta.XR.Editor.UserInterface;
using UnityEngine;

namespace Meta.XR.Guides.Editor
{
    internal static class GuideProcessor
    {
        internal static Dictionary<string, MethodInfo> _guideItemsMap = new();
        internal static Dictionary<string, MethodInfo> _initMap = new();
        private static bool _initialized;

        private static string[] _guidedSetupClasses;

        internal static string[] GuidedSetupClasses
        {
            get
            {
                if (_guidedSetupClasses != null) return _guidedSetupClasses;
                var types = Assemblies.SelectMany(t => t.GetTypes())
                    .Where(t => t.IsSubclassOf(typeof(GuidedSetup)))
                    .Select(t => t.AssemblyQualifiedName).ToArray();
                _guidedSetupClasses = new string[types.Length + 1];
                _guidedSetupClasses[0] = "None"; // Default selection in BlockData
                for (var i = 0; i < types.Length; i++)
                {
                    _guidedSetupClasses[i + 1] = types[i];
                }

                return _guidedSetupClasses;
            }
        }


        private static List<Assembly> _assemblies;
        private static List<Assembly> Assemblies => _assemblies ??= AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => Regex.IsMatch(a.FullName, RegexMatchPattern, RegexOptions.IgnoreCase)).ToList();

        private const string RegexMatchPattern = @"\b(Oculus|Meta)\b";

        internal static void Initialize()
        {
            if (_initialized) return;

            // Search for Types
            var types = Assemblies.SelectMany(a => a.GetTypes())
                .Where(t => t.GetCustomAttribute<GuideItemsAttribute>() != null);

            // Scan GetItems methods
            var methods = types
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<GuideItemsAttribute>(false) != null);

            foreach (var methodInfo in methods)
            {
                var id = $"{methodInfo.DeclaringType}";
                _guideItemsMap[id] = methodInfo;
            }

            // Search Init methods
            methods = types
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<InitAttribute>(false) != null);

            foreach (var methodInfo in methods)
            {
                var id = $"{methodInfo.DeclaringType}";
                _initMap[id] = methodInfo;
            }

            _initialized = true;
        }

        public static List<IUserInterfaceItem> GetItems(string populatorId)
        {
            Initialize();

            if (populatorId == null)
            {
                return null;
            }

            if (_guideItemsMap.TryGetValue(populatorId, out var methodInfo))
            {
                var obj = methodInfo.IsStatic ? null : Activator.CreateInstance(methodInfo.DeclaringType);
                return (List<IUserInterfaceItem>)methodInfo.Invoke(obj, null);
            }
            return null;
        }

        public static void InitializeWindow(string populatorId, GuideWindow guideWindow)
        {
            Initialize();

            if (populatorId == null)
            {
                return;
            }

            if (_initMap.TryGetValue(populatorId, out var methodInfo))
            {
                var obj = methodInfo.IsStatic ? null : Activator.CreateInstance(methodInfo.DeclaringType);
                methodInfo.Invoke(obj, new object[] { guideWindow });
            }
        }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    sealed class GuideItemsAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    sealed class InitAttribute : Attribute
    {
    }
}
