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


using Meta.XR.ImmersiveDebugger.UserInterface;
using Meta.XR.ImmersiveDebugger.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using Meta.XR.ImmersiveDebugger.Hierarchy;

namespace Meta.XR.ImmersiveDebugger.Manager
{
    internal class ActionManager : IDebugManager
    {
        internal readonly Dictionary<Type, List<(MethodInfo, DebugMember)>> ActionsDict = new();
        private IDebugUIPanel _uiPanel;
        private InstanceCache _instanceCache;

        public void Setup(IDebugUIPanel uiPanel, InstanceCache instanceCache)
        {
            _uiPanel = uiPanel;
            _instanceCache = instanceCache;
        }

        public void ProcessType(Type type)
        {
            ActionsDict.Remove(type);

            var actionList = new List<(MethodInfo, DebugMember)>();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            foreach (var method in methods)
            {
                var actionAttribute = method.GetCustomAttribute<DebugMember>();
                if (actionAttribute != null)
                {
                    actionList.Add((method, actionAttribute));
                }
            }

            actionList.AddRange(InspectedDataRegistry.GetMembersForType<MethodInfo>(type));

            ActionsDict[type] = actionList;
            ManagerUtils.RebuildInspectorForType(_uiPanel, _instanceCache, type, actionList, (memberController, member, attribute, instance) =>
            {
                var action = memberController.GetAction();
                if (!action?.Matches(member, instance) ?? true)
                {
                    memberController.RegisterAction(new ActionHook(member, instance, attribute));
                }
            });
        }

        public void ProcessTypeFromInspector(Type type, InstanceHandle handle, MemberInfo memberInfo, DebugMember memberAttribute)
        {
            throw new NotImplementedException();
        }

        public void ProcessTypeFromHierarchy(Item item, MemberInfo memberInfo)
        {
            throw new NotImplementedException();
        }

        public string TelemetryAnnotation => Telemetry.AnnotationType.Actions;
        public int GetCountPerType(Type type)
        {
            ActionsDict.TryGetValue(type, out var actions);
            return actions?.Count ?? 0;
        }
    }
}

