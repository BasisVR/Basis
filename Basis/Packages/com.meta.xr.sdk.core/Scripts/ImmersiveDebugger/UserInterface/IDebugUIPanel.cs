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


using Meta.XR.ImmersiveDebugger.Manager;
using Meta.XR.ImmersiveDebugger.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    internal interface IDebugUIPanel
    {
        public IInspector RegisterInspector(InstanceHandle instance, Category category);
        public void UnregisterInspector(InstanceHandle instance, Category category, bool allCategories);
        public IInspector GetInspector(InstanceHandle instance, Category category);
    }

    internal interface IInspector
    {
        public IMember RegisterMember(MemberInfo memberInfo, DebugMember attribute);
        public IMember GetMember(MemberInfo memberInfo);
    }

    internal interface IMember
    {
        public GizmoHook GetGizmo();
        public void RegisterGizmo(GizmoHook gizmo);

        public ActionHook GetAction();
        public void RegisterAction(ActionHook action);

        public Tweak GetTweak();
        public void RegisterTweak(Tweak tweak);

        public Watch GetWatch();
        public void RegisterWatch(Watch watch);
        public void RegisterEnum(TweakEnum tweak);
        public void RegisterTexture(WatchTexture watch);
    }

}

