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

using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Meta.XR.Editor.ToolingSupport
{
    internal static class ToolRegistry
    {
        private static readonly List<ToolDescriptor> _registry = new();
        private static readonly List<ToolDescriptor> _toInitialize = new();

        public static IReadOnlyList<ToolDescriptor> Registry => _registry;

        public static void Register(ToolDescriptor descriptor)
        {
            _registry.Add(descriptor);

            // Register a delayed callback to initialize descriptors
            if (!_toInitialize.Any())
            {
                EditorApplication.update += InitializeDescriptors;
            }

            _toInitialize.Add(descriptor);
        }

        private static void InitializeDescriptors()
        {
            foreach (var descriptor in _toInitialize)
            {
                descriptor.Initialize();
            }

            _toInitialize.Clear();
            EditorApplication.update -= InitializeDescriptors;
        }

    }
}
