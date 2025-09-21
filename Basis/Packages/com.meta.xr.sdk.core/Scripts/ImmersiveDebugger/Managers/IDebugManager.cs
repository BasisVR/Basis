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
    internal interface IDebugManager
    {
        public void Setup(IDebugUIPanel panel, InstanceCache cache);
        public void ProcessType(Type type);

        public void ProcessTypeFromInspector(Type type, InstanceHandle handle, MemberInfo memberInfo,
            DebugMember memberAttribute);

        public void ProcessTypeFromHierarchy(Item item, MemberInfo memberInfo);

        // Telemetry
        public string TelemetryAnnotation { get; }
        public int GetCountPerType(Type type);
    }

    internal static class ManagerUtils
    {
        public delegate void RegisterMember<in T>(IMember memberController, T member, DebugMember attribute, InstanceHandle instanceHandle);
        public static void RebuildInspectorForType<T>(IDebugUIPanel panel, InstanceCache cache, Type type, List<(T, DebugMember)> memberPairs, RegisterMember<T> memberRegistration) where T : MemberInfo
        {
            foreach (var (member, attribute) in memberPairs)
            {
                if (member.IsStatic())
                {
                    var instanceHandle = InstanceHandle.Static(type);
                    var inspector = panel.RegisterInspector(instanceHandle, new Category { Id = attribute.Category });
                    var memberController = inspector?.RegisterMember(member, attribute);
                    if (memberController != null)
                    {
                        memberRegistration.Invoke(memberController, member, attribute, instanceHandle);
                    }
                }
                else
                {
                    var instances = cache.GetCacheDataForClass(type);
                    foreach (var instance in instances)
                    {
                        var inspector = panel.RegisterInspector(instance, new Category { Id = attribute.Category });
                        var memberController = inspector?.RegisterMember(member, attribute);
                        if (memberController != null)
                        {
                            memberRegistration.Invoke(memberController, member, attribute, instance);
                        }
                    }
                }
            }
        }
    }
}

