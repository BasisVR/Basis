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
using System.Reflection;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger
{
    /// <summary>
    /// Storing inspected members separately as a file and get loaded by runtime,
    /// can be used when [DebugMember] attribute cannot be conveniently added to script.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    [CreateAssetMenu(fileName = "InspectedData", menuName = "Meta/ImmersiveDebugger/InspectedData", order = 100)]
    public class InspectedData : ScriptableObject
    {
        [Tooltip("The name of the InspectedData, used to manage this asset in Immersive Debugger settings")]
        [SerializeField] internal string DisplayName;
        [SerializeField] internal List<InspectedMember> InspectedMembers = new();

        internal IEnumerable<Type> ExtractTypesFromInspectedMembers()
        {
            var types = new HashSet<Type>();
            foreach (var inspectedMember in InspectedMembers)
            {
                inspectedMember.Initialize();
                if (!inspectedMember.Valid) continue;
                var type = inspectedMember.MemberInfo.DeclaringType;
                if (type == null) continue;
                types.Add(type);
                InspectedDataRegistry.Add(type, inspectedMember);
            }

            return types;
        }
    }

    internal static class InspectedDataRegistry
    {
        private static readonly Dictionary<Type, List<InspectedMember>> InspectedMembersRegistry = new();

        internal static void Add(Type type, InspectedMember inspectedMember)
        {
            if (!InspectedMembersRegistry.TryGetValue(type, out var inspectedMembers))
            {
                inspectedMembers = new List<InspectedMember>();
                InspectedMembersRegistry[type] = inspectedMembers;
            }

            inspectedMembers.Add(inspectedMember);
        }

        internal static void Reset()
        {
            InspectedMembersRegistry?.Clear();
        }

        internal static List<(T, DebugMember)> GetMembersForType<T>(Type type, Func<T, DebugMember, bool> filterCallback = null) where T : MemberInfo
        {
            var result = new List<(T, DebugMember)>();
            if (!InspectedMembersRegistry.TryGetValue(type, out var inspectedMembers))
            {
                return result;
            }

            foreach (var inspectedMember in inspectedMembers)
            {
                var castedMemberInfo = inspectedMember.MemberInfo as T;
                if (castedMemberInfo != null && (filterCallback == null || filterCallback(castedMemberInfo, inspectedMember.attribute)))
                {
                    result.Add((castedMemberInfo, inspectedMember.attribute));
                }
            }

            return result;
        }

    }
}

