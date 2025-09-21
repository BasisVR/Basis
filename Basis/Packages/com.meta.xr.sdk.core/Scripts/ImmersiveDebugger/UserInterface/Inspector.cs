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



using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using System.Collections.Generic;
using System.Reflection;
using Meta.XR.ImmersiveDebugger.Utils;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    internal class Inspector : Controller, IInspector
    {
        private InstanceHandle _instanceHandle;
        private ToggleWithLabel _title;
        private Flex _flex;
        private Background _background;
        private readonly Dictionary<MemberInfo, Member> _registry = new();
        private ImageStyle _backgroundImageStyle;
        private Toggle _foldout;

        private bool _previousEnabledState;

        public ImageStyle BackgroundStyle
        {
            set
            {
                _background.Sprite = value.sprite;
                _background.Color = value.color;
                _background.PixelDensityMultiplier = value.pixelDensityMultiplier;
            }
        }

        public string Title
        {
            get => _title.Label;
            set => _title.Label = value;
        }

        public InstanceHandle InstanceHandle
        {
            get => _instanceHandle;
            set
            {
                _instanceHandle = value;

                var instance = _instanceHandle.Instance;
                var inspectorTitle = instance != null ? $"{instance.name} - {_instanceHandle.Type.Name}" : $"{_instanceHandle.Type.Name}";
                Title = inspectorTitle;

                UpdateInstanceState();
            }
        }

        public Toggle Foldout => _foldout;

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            // Background
            _background = Append<Background>("background");
            _background.LayoutStyle = Style.Load<LayoutStyle>("Fill");
            _backgroundImageStyle = Style.Load<ImageStyle>("InspectorBackground");
            BackgroundStyle = _backgroundImageStyle;

            // Label
            _title = Append<ToggleWithLabel>("title");
            _title.LayoutStyle = Style.Load<LayoutStyle>("InspectorTitle");
            _title.Background.LayoutStyle = Style.Load<LayoutStyle>("InspectorTitleBackground");
            _title.BackgroundStyle = Style.Load<ImageStyle>("InspectorTitleBackground");

            // Foldout
            _foldout = Append<Toggle>("foldout");
            _foldout.LayoutStyle = Style.Load<LayoutStyle>("InspectorFoldout");
            _foldout.Icon = Resources.Load<Texture2D>("Textures/caret_right_icon");
            _foldout.IconStyle = Style.Load<ImageStyle>("InspectorFoldoutIcon");

            // Flex
            _flex = Append<Flex>("list");
            _flex.LayoutStyle = Style.Load<LayoutStyle>("InspectorFlex");

            // Toggling callbacks
            _foldout.StateChanged = OnStateChanged;
            _foldout.Callback = _foldout.ToggleState;
            _title.Callback = _foldout.ToggleState;
            _foldout.State = true;
        }

        protected override void OnTransparencyChanged()
        {
            base.OnTransparencyChanged();
            _background.Color = Transparent ? _backgroundImageStyle.colorOff : _backgroundImageStyle.color;
        }

        public void UpdateBackground(bool transparent)
        {
            Transparent = transparent;
            OnTransparencyChanged();
        }

        public IMember RegisterMember(MemberInfo memberInfo, DebugMember attribute)
        {
            if (!_registry.TryGetValue(memberInfo, out var member))
            {
                member = _flex.Append<Member>(memberInfo.Name);
                member.LayoutStyle = Style.Instantiate<LayoutStyle>("Member");
                member.Title = string.IsNullOrEmpty(attribute.DisplayName) ? $"{memberInfo.Name}" : attribute.DisplayName;

                if (!string.IsNullOrEmpty(attribute.Description))
                {
                    member.RegisterDescriptor();
                    member.Description = attribute.Description;
                }

                member.PillColor = attribute.Color;
                _registry.Add(memberInfo, member);

                if (!_foldout.State)
                {
                    _flex.Forget(member);
                }
            }

            return member;
        }

        public IMember GetMember(MemberInfo memberInfo)
        {
            _registry.TryGetValue(memberInfo, out var member);
            return member;
        }

        private void OnStateChanged(bool state)
        {
            _foldout.Icon = Resources.Load<Texture2D>(state ? "Textures/caret_down_icon" : "Textures/caret_right_icon");
            if (state)
            {
                foreach (var member in _registry)
                {
                    _flex.Remember(member.Value);
                }
                _flex.LayoutStyle = Style.Load<LayoutStyle>("InspectorFlex");
            }
            else
            {
                _flex.ForgetAll();
                _flex.LayoutStyle = Style.Load<LayoutStyle>("InspectorFlexFold");
            }
        }

        private void Update()
        {
            UpdateInstanceState();
        }

        private void UpdateInstanceState(bool force = false)
        {
            if (InstanceHandle.Instance is Behaviour component)
            {
                UpdateInstanceState(component != null && component.isActiveAndEnabled, force);
            }
            else
            {
                UpdateInstanceState(true, force);
            }
        }

        private void UpdateInstanceState(bool state, bool force = false)
        {
            if (_previousEnabledState == state && !force) return;

            _title.TextStyle = Style.Load<TextStyle>(state ? "InspectorTitle" : "InspectorTitleDeactivated");

            _previousEnabledState = state;
        }
    }
}
