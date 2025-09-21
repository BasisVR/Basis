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
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    public class Image : Icon
    {
        private WatchTexture _watchTexture;
        private float _defaultHeight;

        public override Texture2D Texture
        {
            internal get => base.Texture;
            set
            {
                base.Texture = value;
                if (value != null) UpdateSize();
                RefreshLayout();
            }
        }

        internal void Setup(WatchTexture watchTexture)
        {
            _watchTexture = watchTexture;
            _defaultHeight = LayoutStyle.size.y;
            Texture = watchTexture.Texture;
        }

        private void UpdateSize()
        {
            var width = Texture.width;
            var height = Texture.height;
            var ratio = width / height;
            var layoutHeight = Mathf.Min(height, _defaultHeight);
            var estimatedWidth = layoutHeight * ratio;
            LayoutStyle.size = new Vector2(estimatedWidth, layoutHeight);
            Owner.LayoutStyle.size.y = layoutHeight;
        }

        private void Update()
        {
            if (_watchTexture is not { Valid: true }) return;

            if (_watchTexture.Texture == null)
                return;

            Texture = _watchTexture.Texture;
        }
    }
}
