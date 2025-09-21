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
using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    internal class LogEntry
    {
        public static Action<LogEntry> OnDisplayDetails { get; set; }

        public void Setup(string label, string callstack, SeverityEntry severity)
        {
            Label = label;
            Callstack = callstack;
            Severity = severity;
            Line = null;
            Count = 1;
        }

        public string Label { get; private set; }
        public string Callstack { get; private set; }
        public SeverityEntry Severity { get; private set; }
        public int Count { get; set; }
        public ProxyConsoleLine Line { get; set; }
        public bool Shown => Line != null;

        public void DisplayDetails() => OnDisplayDetails?.Invoke(this);
    }

    internal class SeverityEntry
    {
        private readonly Console _owner;
        private readonly Toggle _button;
        private readonly Label _countLabel;
        private int _count = -1;

        internal Console Owner => _owner;

        public ImageStyle PillStyle { get; }

        public SeverityEntry(Console owner, string label, Texture2D icon, ImageStyle imageStyle, ImageStyle pillStyle)
        {
            _owner = owner;
            _countLabel = owner.RegisterCount();
            _button = owner.RegisterControl(label, icon, imageStyle,
                (() =>
                {
                    ShouldShow = !ShouldShow;
                    owner.Dirty = true;
                }));
            Count = 0;
            PillStyle = pillStyle;
        }

        public void Reset()
        {
            Count = 0;
        }

        public bool ShouldShow
        {
            get => _button.State;
            set
            {
                if (_button.State == value) return;
                _button.State = value;
                _owner.Dirty = true;
            }
        }

        public int Count
        {
            get => _count;
            set
            {
                if (_count == value) return;
                _count = value;
                _countLabel.Content = _count.ToString();
            }
        }
    }
}
