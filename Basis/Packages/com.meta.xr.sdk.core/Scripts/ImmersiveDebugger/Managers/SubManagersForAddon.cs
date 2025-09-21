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
using Meta.XR.ImmersiveDebugger.Gizmo;
using Meta.XR.ImmersiveDebugger.Hierarchy;
using Meta.XR.ImmersiveDebugger.UserInterface;
using Meta.XR.ImmersiveDebugger.Utils;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.Manager
{
    internal abstract class SubManagerForAddon : IDebugManager
    {
        private readonly Dictionary<Type, List<MemberInfo>> _dictionary = new();
        private IDebugUIPanel _uiPanel;
        protected InstanceCache InstanceCache;

        public void Setup(IDebugUIPanel panel, InstanceCache cache)
        {
            _uiPanel = panel;
            InstanceCache = cache;
        }

        public void ProcessType(Type type) => throw new NotImplementedException();

        public void ProcessTypeFromInspector(Type type, InstanceHandle handle, MemberInfo memberInfo, DebugMember memberAttribute)
        {
            var uiInspector = _uiPanel.RegisterInspector(handle, new Category() { Id = memberAttribute.Category });
            var member = uiInspector.RegisterMember(memberInfo, memberAttribute);

            if (RegisterSpecialisedWidget(member, memberInfo, memberAttribute, handle))
            {
                if (!_dictionary.TryGetValue(type, out var list))
                {
                    list = new List<MemberInfo>();
                    _dictionary.Add(type, list);
                }

                if (!list.Contains(memberInfo))
                {
                    list.Add(memberInfo);
                }
            }
        }

        public void ProcessTypeFromHierarchy(Item item, MemberInfo memberInfo)
        {
            var handle = item.Handle;
            var uiInspector = _uiPanel.RegisterInspector(handle, item.Category);
            var attribute = new DebugMember();
            var member = uiInspector.RegisterMember(memberInfo, attribute);

            if (RegisterSpecialisedWidget(member, memberInfo, attribute, handle))
            {
                if (!_dictionary.TryGetValue(handle.Type, out var list))
                {
                    list = new List<MemberInfo>();
                    _dictionary.Add(handle.Type, list);
                }

                if (!list.Contains(memberInfo))
                {
                    list.Add(memberInfo);
                }
            }
        }

        protected abstract bool RegisterSpecialisedWidget(IMember member, MemberInfo memberInfo, DebugMember memberAttribute, InstanceHandle handle);
        public abstract string TelemetryAnnotation { get; }

        public int GetCountPerType(Type type)
        {
            _dictionary.TryGetValue(type, out var actions);
            return actions?.Count ?? 0;
        }
    }

    internal class TweakManagerForAddon : SubManagerForAddon
    {
        protected override bool RegisterSpecialisedWidget(IMember member, MemberInfo memberInfo,
            DebugMember memberAttribute, InstanceHandle handle)
        {
            if (!memberAttribute.Tweakable || !TweakUtils.IsMemberValidForTweak(memberInfo))
            {
                return false;
            }

            var tweak = member.GetTweak();
            if (!tweak?.Matches(memberInfo, handle) ?? true)
            {
                if (memberInfo.IsBaseTypeEqual(typeof(Enum)))
                {
                    member.RegisterEnum(TweakUtils.Create(memberInfo, memberAttribute, handle, memberInfo.GetDataType()));
                }
                else
                {
                    TweakUtils.ProcessMinMaxRange(memberInfo, memberAttribute, handle);
                    member.RegisterTweak(TweakUtils.Create(memberInfo, memberAttribute, handle));
                }
            }
            return true;
        }

        public override string TelemetryAnnotation => Telemetry.AnnotationType.Tweaks;
    }

    internal class ActionManagerForAddon : SubManagerForAddon
    {
        protected override bool RegisterSpecialisedWidget(IMember member, MemberInfo memberInfo,
            DebugMember memberAttribute, InstanceHandle handle)
        {
            if (memberInfo.MemberType != MemberTypes.Method)
            {
                return false;
            }

            var method = memberInfo as MethodInfo;
            if (method == null || method.GetParameters().Length != 0 || method.ReturnType != typeof(void))
            {
                return false;
            }

            var action = member.GetAction();
            if (!action?.Matches(memberInfo, handle) ?? true)
            {
                member.RegisterAction(new ActionHook(memberInfo, handle, memberAttribute));
            }
            return true;

        }

        public override string TelemetryAnnotation => Telemetry.AnnotationType.Actions;
    }

    internal class WatchManagerForAddon : SubManagerForAddon
    {
        protected override bool RegisterSpecialisedWidget(IMember member, MemberInfo memberInfo,
            DebugMember memberAttribute, InstanceHandle handle)
        {
            if (!WatchManager.IsMemberValidForWatch(memberInfo))
            {
                return false;
            }

            var watch = member.GetWatch();
            if (!watch?.Matches(memberInfo, handle) ?? true)
            {
                if (memberInfo.IsTypeEqual(typeof(Texture2D)))
                {
                    member.RegisterTexture(WatchUtils.Create(memberInfo, handle, memberAttribute) as WatchTexture);
                }
                else
                {
                    member.RegisterWatch(WatchUtils.Create(memberInfo, handle, memberAttribute));
                }
            }
            return true;
        }

        public override string TelemetryAnnotation => Telemetry.AnnotationType.Watches;
    }

    internal class GizmoManagerForAddon : SubManagerForAddon
    {
        private readonly Dictionary<MemberInfo, GizmoRendererManager> _memberToGizmoRendererManagerDict = new();
        protected override bool RegisterSpecialisedWidget(IMember member, MemberInfo memberInfo,
            DebugMember memberAttribute, InstanceHandle handle)
        {
            if (memberInfo.IsTypeEqual(typeof(Pose))) memberAttribute.GizmoType = DebugGizmoType.Axis;
            if (memberInfo.IsTypeEqual(typeof(Vector3))) memberAttribute.GizmoType = DebugGizmoType.Point;
            if (memberInfo.IsTypeEqual(typeof(Tuple<Vector3, Vector3>))) memberAttribute.GizmoType = DebugGizmoType.Line;
            if (memberInfo.IsTypeEqual(typeof(Vector3[]))) memberAttribute.GizmoType = DebugGizmoType.Lines;
            if (memberInfo.IsTypeEqual(typeof(Tuple<Pose, float, float>))) memberAttribute.GizmoType = DebugGizmoType.Plane;
            if (memberInfo.IsTypeEqual(typeof(Tuple<Vector3, float>))) memberAttribute.GizmoType = DebugGizmoType.Cube;
            if (memberInfo.IsTypeEqual(typeof(Tuple<Pose, float, float, float>))) memberAttribute.GizmoType = DebugGizmoType.Box;

            if (memberAttribute.GizmoType == DebugGizmoType.None)
            {
                return false;
            }

            // Extreme edge case for convenience :
            if (memberInfo.DeclaringType == typeof(Transform) && memberInfo.Name == "position")
            {
                memberAttribute.ShowGizmoByDefault = true;
            }

            if (!_memberToGizmoRendererManagerDict.TryGetValue(memberInfo, out var gizmoRendererManager))
            {
                if (GizmoManager.AddGizmo(handle.Type, memberInfo, memberAttribute, InstanceCache, out gizmoRendererManager))
                {
                    _memberToGizmoRendererManagerDict[memberInfo] = gizmoRendererManager;
                }
            }

            if (gizmoRendererManager == null)
            {
                return false;
            }

            var gizmo = member.GetGizmo();
            if (!gizmo?.Matches(memberInfo, handle) ?? true)
            {
                member.RegisterGizmo(new GizmoHook(memberInfo, handle, memberAttribute, OnStateChanged, GetState));
            }
            return true;

            void OnStateChanged(bool state) => _memberToGizmoRendererManagerDict[memberInfo].SetState(handle.Instance, state);
            bool GetState() => _memberToGizmoRendererManagerDict[memberInfo].GetState(handle.Instance);
        }

        public override string TelemetryAnnotation => Telemetry.AnnotationType.Gizmos;
    }
}
