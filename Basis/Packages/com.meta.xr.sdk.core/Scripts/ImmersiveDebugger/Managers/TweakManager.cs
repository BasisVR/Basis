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
    internal class TweakManager : IDebugManager
    {
        internal readonly Dictionary<Type, List<(MemberInfo, DebugMember)>> TweaksDict = new();
        private IDebugUIPanel _uiPanel;
        private InstanceCache _instanceCache;

        public void Setup(IDebugUIPanel panel, InstanceCache cache)
        {
            _uiPanel = panel;
            _instanceCache = cache;
        }

        public void ProcessType(Type type)
        {
            TweaksDict.Remove(type);

            var membersList = new List<(MemberInfo, DebugMember)>();
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.SetField | BindingFlags.SetProperty);
            foreach (MemberInfo member in members)
            {
                if (TweakUtils.IsMemberValidForTweak(member))
                {
                    var attribute = member.GetCustomAttribute<DebugMember>();
                    if (attribute != null && attribute.Tweakable)
                    {
                        membersList.Add((member, attribute));
                    }
                }
            }

            membersList.AddRange(InspectedDataRegistry.GetMembersForType<MemberInfo>(type,
                (info, attribute) => TweakUtils.IsMemberValidForTweak(info) && attribute.Tweakable));

            TweaksDict[type] = membersList;

            ManagerUtils.RebuildInspectorForType(_uiPanel, _instanceCache, type, membersList, (memberController, member, attribute, instance) =>
            {
                var tweak = memberController.GetTweak();
                if (!tweak?.Matches(member, instance) ?? true)
                {
                    if (member.IsBaseTypeEqual(typeof(Enum)))
                    {
                        memberController.RegisterEnum(TweakUtils.Create(member, attribute, instance, member.GetDataType()));
                    }
                    else
                    {
                        TweakUtils.ProcessMinMaxRange(member, attribute, instance);
                        memberController.RegisterTweak(TweakUtils.Create(member, attribute, instance));
                    }
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

        public string TelemetryAnnotation => Telemetry.AnnotationType.Tweaks;
        public int GetCountPerType(Type type)
        {
            TweaksDict.TryGetValue(type, out var actions);
            return actions?.Count ?? 0;
        }
    }
}
