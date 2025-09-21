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
using Meta.XR.ImmersiveDebugger.Manager;

namespace Meta.XR.ImmersiveDebugger
{
    internal class DebugInspectorManager : DebugManagerAddon<DebugInspectorManager>
    {
        private readonly List<DebugInspector> _inspectors = new();

        public void RegisterInspector(DebugInspector inspector)
        {
            _inspectors.Add(inspector);
            ProcessInspector(inspector);
        }

        public void UnregisterInspector(DebugInspector inspector)
        {
            UnprocessInspector(inspector);
            _inspectors.Remove(inspector);
        }

        protected override Telemetry.Method Method => Telemetry.Method.DebugInspector;

        protected override void OnReadyInternal()
        {
            foreach (var inspector in _inspectors)
            {
                ProcessInspector(inspector);
            }
        }

        private void ProcessInspector(DebugInspector inspector)
        {
            if (_uiPanel == null) return;

            foreach (var entry in inspector.Registry.Handles)
            {
                if (!entry.Visible) continue;

                var handle = entry.InstanceHandle;
                _instanceCache.RegisterHandle(handle);
                foreach (var memberEntry in entry.inspectedMembers)
                {
                    if (!memberEntry.Visible) continue;

                    var memberInfo = memberEntry.MemberInfo;
                    if (memberInfo == null) continue;

                    var attribute = memberEntry.attribute;
                    if (attribute == null) continue;

                    UpdateCategory(attribute, inspector);

                    _uiPanel.RegisterInspector(handle, FetchCategory(attribute));

                    foreach (var manager in _subDebugManagers)
                    {
                        manager.ProcessTypeFromInspector(handle.Type, handle, memberInfo, attribute);
                    }
                }
            }
        }

        private void UnprocessInspector(DebugInspector inspector)
        {
            if (_uiPanel == null) return;

            foreach (var entry in inspector.Registry.Handles)
            {
                var handle = entry.InstanceHandle;
                foreach (var memberEntry in entry.inspectedMembers)
                {
                    var attribute = memberEntry.attribute;
                    if (attribute == null) continue;

                    _uiPanel.UnregisterInspector(handle, FetchCategory(attribute), false);
                }
                _instanceCache.UnregisterHandle(handle);
            }
        }

        private void UpdateCategory(DebugMember attribute, DebugInspector inspector)
        {
            if (string.IsNullOrEmpty(attribute.Category))
            {
                attribute.Category = inspector.Category;
            }
        }

        private static Category FetchCategory(DebugMember attribute)
        {
            return new Category() { Id = attribute.Category };
        }
    }
}

