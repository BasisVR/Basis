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


using Meta.XR.ImmersiveDebugger.Gizmo;
using Meta.XR.ImmersiveDebugger.UserInterface;
using Meta.XR.ImmersiveDebugger.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using Meta.XR.ImmersiveDebugger.Hierarchy;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Meta.XR.ImmersiveDebugger.Manager
{
    internal class GizmoManager : IDebugManager
    {
        internal readonly Dictionary<Type, List<(MemberInfo, GizmoRendererManager)>> GizmosDict = new Dictionary<Type, List<(MemberInfo, GizmoRendererManager)>>();
        private IDebugUIPanel _uiPanel;
        private InstanceCache _instanceCache;

        public void Setup(IDebugUIPanel panel, InstanceCache cache)
        {
            _uiPanel = panel;
            _instanceCache = cache;
        }

        public void ProcessType(Type type)
        {
            // sequence matters here, remove gizmos before remove the dict
            RemoveGizmosForType(type);
            GizmosDict.Remove(type);

            var gizmosList = new List<(MemberInfo, GizmoRendererManager)>();
            // tmp cache for convenience of building inspector
            var membersList = new List<(MemberInfo, DebugMember)>();
            var memberToGizmoRendererManagerDict = new Dictionary<MemberInfo, GizmoRendererManager>();

            var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.GetField | BindingFlags.GetProperty);
            foreach (var member in members)
            {
                var gizmoAttribute = member.GetCustomAttribute<DebugMember>();
                if (gizmoAttribute != null && gizmoAttribute.GizmoType != DebugGizmoType.None)
                {
                    if (AddGizmo(type, member, gizmoAttribute, _instanceCache, out var gizmoRendererManager))
                    {
                        gizmosList.Add((member, gizmoRendererManager));
                        membersList.Add((member, gizmoAttribute));
                        memberToGizmoRendererManagerDict[member] = gizmoRendererManager;
                    }
                }
            }

            InspectedDataRegistry.GetMembersForType<MemberInfo>(type, (info, attribute) =>
            {
                // process and add the lists instead of directly returning the list, always return false
                if (attribute.GizmoType == DebugGizmoType.None) return false;
                if (!AddGizmo(type, info, attribute, _instanceCache, out var gizmoRendererManager)) return false;
                gizmosList.Add((info, gizmoRendererManager));
                membersList.Add((info, attribute));
                memberToGizmoRendererManagerDict[info] = gizmoRendererManager;
                return false;
            });

            GizmosDict[type] = gizmosList;
            ManagerUtils.RebuildInspectorForType(_uiPanel, _instanceCache, type, membersList, (memberController, member, attribute, instance) =>
            {
                var gizmo = memberController.GetGizmo();
                if (!gizmo?.Matches(member, instance) ?? true)
                {
                    void OnStateChanged(bool state) => memberToGizmoRendererManagerDict[member].SetState(instance.Instance, state);
                    bool GetState() => memberToGizmoRendererManagerDict[member].GetState(instance.Instance);
                    memberController.RegisterGizmo(new GizmoHook(member, instance, attribute, OnStateChanged, GetState));
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

        internal static bool AddGizmo(Type type, MemberInfo member, DebugMember gizmoAttribute, InstanceCache instanceCache, out GizmoRendererManager gizmoRendererManager)
        {
            if (!GizmoTypesRegistry.IsValidDataTypeForGizmoType(member.GetDataType(), gizmoAttribute.GizmoType))
            {
                Debug.LogWarning($"Invalid registration of gizmo {member.Name}: type not matching gizmo type");
                gizmoRendererManager = null;
                return false;
            }

            var gizmo = new GameObject($"{member.Name}Gizmo");
            gizmoRendererManager = gizmo.AddComponent<GizmoRendererManager>();
            gizmoRendererManager.Setup(type, member, gizmoAttribute.GizmoType, gizmoAttribute.Color, instanceCache);

            if (Application.isPlaying)
            {
                // This method can only be called in play mode.
                // Overall, the Immersive Debugger should only be called during play mode
                // But it may be triggered during Unit Tests at some point,
                // so to avoid errors while calling this method, we guard it against isPlaying
                Object.DontDestroyOnLoad(gizmo);
            }

            return true;
        }

        private void RemoveGizmosForType(Type type)
        {
            if (GizmosDict.TryGetValue(type, out var value))
            {
                foreach (var member in value)
                {
                    UnityEngine.Object.Destroy(member.Item2.gameObject);
                }
                GizmosDict.Remove(type);
            }
        }

        public string TelemetryAnnotation => Telemetry.AnnotationType.Gizmos;
        public int GetCountPerType(Type type)
        {
            GizmosDict.TryGetValue(type, out var actions);
            return actions?.Count ?? 0;
        }
    }
}

