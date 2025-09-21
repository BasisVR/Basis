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

using Meta.XR.Editor.Id;
using UnityEngine;

namespace Meta.XR.Editor.UserInterface
{
    internal class LinkLabel : IUserInterfaceItem
    {
        public bool Hide { get; set; }

        private readonly LinkDescription _linkDescription;
        private readonly GUILayoutOption[] _options;

        public LinkLabel(GUIContent label, string url, IIdentified originData, params GUILayoutOption[] options) :
            this(new UrlLinkDescription()
            {
                Content = label,
                URL = url,
                Underline = true,
                Origin = Origins.GuidedSetup,
                OriginData = originData
            }, options)
        { }

        public LinkLabel(LinkDescription description, params GUILayoutOption[] options)
        {
            _linkDescription = description;
            _options = options;
        }

        public virtual void Draw()
        {
            _linkDescription.Draw();
        }
    }
}
