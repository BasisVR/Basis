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
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.UserInterface
{
    internal class TextFieldWithButton : TextField
    {
        private readonly Button _button;
        private readonly Action<string> _textFieldCallback;

        public TextFieldWithButton(string buttonLabel, Action<string> buttonCallback) :
            this("", "", buttonLabel, buttonCallback)
        {
        }

        public TextFieldWithButton(
            string label,
            string text,
            string buttonLabel,
            Action<string> buttonCallback) : base(label, text)
        {
            _textFieldCallback = buttonCallback;
            _button = new Button(new ActionLinkDescription()
            {
                Content = new GUIContent(buttonLabel),
                Action = OnButtonPress
            });
        }

        private void OnButtonPress() => _textFieldCallback?.Invoke(Text);

        public override void Draw()
        {
            EditorGUILayout.BeginHorizontal();
            base.Draw();
            _button.Draw();
            EditorGUILayout.EndHorizontal();
        }
    }
}
