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
using Meta.XR.ImmersiveDebugger.Hierarchy;
using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for the Immersive Debugger UIs.
    /// It represents an object from the Hierarchy tree in the Inspector Panel and handles its bespoke behaviour.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    internal class HierarchyItemButton : Flex
    {
        private Item _item;
        private int _counter;
        private ToggleWithLabel _label;
        private Toggle _foldout;

        private bool _previousEnabledState;

        internal Item Item
        {
            get => _item;
            set
            {
                _item = value;
                _label.Label = _item.Label;
                if (_item.ComputeNumberOfChildren() > 0)
                {
                    _foldout.IconStyle = Style.Load<ImageStyle>("FoldoutIcon");
                }
                else
                {
                    _foldout.IconStyle = Style.Load<ImageStyle>("None");
                }

                UpdateGameObjectState(true);
            }
        }

        internal int Counter
        {
            get => _counter;
            set
            {
                _counter = value;
                _counter = Math.Max(0, _counter);
            }
        }

        internal Toggle Foldout => _foldout;
        internal ToggleWithLabel Label => _label;

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            // Foldout
            _foldout = Append<Toggle>("foldout");
            _foldout.LayoutStyle = Style.Load<LayoutStyle>("Foldout");
            _foldout.Icon = Resources.Load<Texture2D>("Textures/caret_right_icon");
            _foldout.IconStyle = Style.Load<ImageStyle>("FoldoutIcon");
            _foldout.StateChanged = OnStateChanged;

            // Label
            _label = Append<ToggleWithLabel>("label");
            _label.LayoutStyle = Style.Load<LayoutStyle>("HierarchyItemLabel");
            _label.TextStyle = Style.Load<TextStyle>("HierarchyItemLabel");
            _label.BackgroundStyle = Style.Instantiate<ImageStyle>("HierarchyItemBackground");
            _label.LabelLayoutStyle = Style.Load<LayoutStyle>("HierarchyItemLabelInner");
        }

        private void OnStateChanged(bool state)
        {
            _foldout.Icon = Resources.Load<Texture2D>(state ? "Textures/caret_down_icon" : "Textures/caret_right_icon");
            if (state)
            {
                Item.BuildChildren();
            }
            else
            {
                Item.ClearChildren();
            }
        }

        private void Update()
        {
            if (!Item.Valid)
            {
                Item.Clear();
                return;
            }

            UpdateGameObjectState();

            if (_foldout.State)
            {
                // Check if children have changed
                if (Item.ComputeNeedsRefresh())
                {
                    Item.BuildChildren();
                }
            }
        }

        private void UpdateGameObjectState(bool force = false)
        {
            if (Item.Owner is GameObject go)
            {
                UpdateGameObjectState(go.activeSelf, force);
            }
            else
            {
                UpdateGameObjectState(true, force);
            }
        }

        private void UpdateGameObjectState(bool state, bool force = false)
        {
            if (_previousEnabledState == state && !force) return;

            _label.TextStyle = Style.Load<TextStyle>(state ? "HierarchyItemLabel" : "HierarchyItemLabelDeactivated");

            _previousEnabledState = state;
        }
    }
}
