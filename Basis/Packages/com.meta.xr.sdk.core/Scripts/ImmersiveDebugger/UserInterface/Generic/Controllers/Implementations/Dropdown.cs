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
using System.Collections;
using System.Reflection;
using Meta.XR.ImmersiveDebugger.Manager;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    public class Dropdown : Controller
    {
        private Flex _flex;
        private TweakEnum _tweak;
        private ButtonWithLabel _baseLabel;
        private Background _background;
        private bool _requestBackgroundUpdate;
        private LayoutStyle _rootLayoutStyle;
        private bool IsMenuVisible => _flex.Visibility;

        private float DefaultHeight => _baseLabel.RectTransform.rect.size.y;
        private InspectorPanel _inspectorPanel;
        private float _previousScrollPosition;
        private ImageStyle _backgroundImageStyle;

        public string Label
        {
            get => _baseLabel.Label;
            set
            {
                _baseLabel.Label = value;
                _tweak.Value = value;
            }
        }

        private ImageStyle BackgroundStyle
        {
            set
            {
                _backgroundImageStyle = value;
                _background.Sprite = value.sprite;
                _background.Color = value.color;
                _background.PixelDensityMultiplier = value.pixelDensityMultiplier;
            }
        }

        internal void SetupMenu(TweakEnum tweak)
        {
            _tweak = tweak;
            Label = _tweak.Value;
            SetupDropdownList();
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);
            _baseLabel = Append<ButtonWithLabel>("label");
            _baseLabel.LayoutStyle = Style.Instantiate<LayoutStyle>("DropdownValueItem");
            _baseLabel.TextStyle = Style.Load<TextStyle>("MemberValue");
            _baseLabel.BackgroundStyle = Style.Instantiate<ImageStyle>("DropdownValueBackgroundRoot");
            _baseLabel.Callback += OnDropdownClick;

            var icon = _baseLabel.Append<Icon>("icon");
            icon.LayoutStyle = Style.Load<LayoutStyle>("DropdownArrowIcon");
            var style = Style.Load<ImageStyle>("DownArrowIcon");
            icon.Texture = style.icon;
            icon.Color = style.color;

            _rootLayoutStyle = Owner.LayoutStyle;
            _inspectorPanel = gameObject.GetComponentInParent<InspectorPanel>();
        }

        private void OnDropdownClick() => SetDropdownMenuVisibility(!IsMenuVisible);

        internal void OnMenuItemClick(DropdownMenuItem menuItem)
        {
            Label = menuItem.Label;
            SetDropdownMenuVisibility(false);
        }

        private void SetDropdownMenuVisibility(bool visible)
        {
            if (visible)
            {
                // Need to set canvas sort order if canvas is not active. This gets reset on deactivation.
                _flex.Show();
            }
            else
            {
                _flex.Hide();
            }
            _requestBackgroundUpdate = true;
        }

        private void Update()
        {
            if (!_requestBackgroundUpdate) return;
            _requestBackgroundUpdate = false;
            var dropdownHeight = DefaultHeight + _flex.RectTransform.rect.size.y;
            _rootLayoutStyle.size.y = _flex.Visibility ? dropdownHeight : DefaultHeight;

            var rowHeight = DefaultHeight - 2; // single row height + extra spacing
            _background.RectTransform.sizeDelta = new Vector2(_background.RectTransform.sizeDelta.x, _rootLayoutStyle.size.y - rowHeight);
            RefreshLayout();
            StartCoroutine(UpdateScrollPosition(_flex.Visibility));
        }

        private IEnumerator UpdateScrollPosition(bool dropdownIsShowing)
        {
            if (!dropdownIsShowing)
            {
                yield return new WaitForEndOfFrame();
                _inspectorPanel.ScrollView.Progress = _previousScrollPosition;
                yield break;
            }

            _previousScrollPosition = _inspectorPanel.ScrollView.Progress;

            var scrollRect = _inspectorPanel.ScrollView.ScrollRect;
            var menuHeight = _flex.RectTransform.rect.size.y;

            yield return new WaitForEndOfFrame();

            var scrollableArea = Mathf.Abs(scrollRect.content.rect.size.y - _inspectorPanel.ScrollView.RectTransform.rect.size.y);
            var normalizedScrollAmount = menuHeight / scrollableArea;
            _inspectorPanel.ScrollView.Progress = Mathf.Clamp01(_inspectorPanel.ScrollView.Progress + normalizedScrollAmount);
        }

        private void HideDropdownItems() => _flex.Hide();

        private void SetupDropdownList()
        {
            _flex = Append<Flex>("list");
            _flex.LayoutStyle = Style.Load<LayoutStyle>("DropdownValuesFlex");

            _background = _flex.Append<Background>("background");
            _background.LayoutStyle = Style.Instantiate<LayoutStyle>("DropdownBackground");
            BackgroundStyle = Style.Load<ImageStyle>("DropdownBackground");

            Array values = null;
            var fieldType = (_tweak.Member as FieldInfo)?.FieldType;
            var propertyType = (_tweak.Member as PropertyInfo)?.PropertyType;
            if (fieldType != null)
            {
                values = Enum.GetValues(fieldType);
            }
            else if (propertyType != null)
            {
                values = Enum.GetValues(propertyType);
            }

            foreach (var value in values)
            {
                AppendValue(value.ToString());
            }

            HideDropdownItems();
        }

        private void AppendValue(string data)
        {
            var value = _flex.Append<DropdownMenuItem>($"menu_item_{data}");
            value.Label = data;
            value.RegisterDropdownSourceMenu(this);
        }

        protected override void OnTransparencyChanged()
        {
            base.OnTransparencyChanged();
            _backgroundImageStyle.colorHover.a = Transparent ? 0.6f : 1f;
            _background.Color = Transparent ? _backgroundImageStyle.colorOff : _backgroundImageStyle.color;
        }
    }
}
