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
using UnityEngine.EventSystems;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for the slider UI element.
    /// It's used for the float/int debug data's tweaking in the Inspector panel of Immersive Debugger.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class Slider : Button, IDragHandler, IInitializePotentialDragHandler
    {
        private Background _emptyBackground;
        private Background _fillBackground;
        private Icon _pill;

        internal Tweak Tweak { get; set; }

        private ImageStyle _emptyBackgroundStyle;
        /// <summary>
        /// The background style when the slider is not filled (normally on the right hand side of the slider)
        /// </summary>
        public ImageStyle EmptyBackgroundStyle
        {
            set
            {
                _emptyBackgroundStyle = value;
                _emptyBackground.Sprite = value.sprite;
                _emptyBackground.Color = value.color;
                _emptyBackground.PixelDensityMultiplier = value.pixelDensityMultiplier;
            }
        }

        private ImageStyle _fillBackgroundStyle;
        /// <summary>
        /// The background style when the slider is filled (normally on the left hand side of the slider)
        /// </summary>
        public ImageStyle FillBackgroundStyle
        {
            set
            {
                _fillBackgroundStyle = value;
                _fillBackground.Sprite = value.sprite;
                _fillBackground.Color = value.color;
                _fillBackground.PixelDensityMultiplier = value.pixelDensityMultiplier;
            }
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            var raycastBackground = Append<Background>("raycast_background");
            raycastBackground.LayoutStyle = Style.Load<LayoutStyle>("Fill");
            raycastBackground.Color = new Color(0, 0, 0, 0);
            raycastBackground.Sprite = null;
            _emptyBackground = Append<Background>("empty_background");
            _emptyBackground.LayoutStyle = Style.Load<LayoutStyle>("SliderBackground");
            _emptyBackground.RaycastTarget = true;

            _fillBackground = Append<Background>("fill_background");
            _fillBackground.LayoutStyle = Style.Load<LayoutStyle>("SliderFill");
            _pill = Append<Icon>("pill");
            _pill.LayoutStyle = Style.Load<LayoutStyle>("SliderPill");
            _pill.Texture = Resources.Load<Texture2D>("Textures/icon_background_02");
            _pill.Color = Color.white;
            _pill.RaycastTarget = true;
        }

        private void UpdatePillPosition()
        {
            if (Tweak is not { Valid: true }) return;

            var width = RectTransform.rect.width;
            var idealPosition = Tweak.Tween * width;
            _pill.RectTransform.anchoredPosition = new Vector2(idealPosition, 0.0f);
            _fillBackground.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, idealPosition);
        }

        private void Update()
        {
            UpdatePillPosition();
        }

        /// <summary>
        /// The callback implementation for the <see cref="OnDrag"/> event.
        /// </summary>
        /// <param name="eventData">The pointer event data for the drag gesture</param>
        public void OnDrag(PointerEventData eventData)
        {
            if (!MayDrag(eventData)) return;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(RectTransform, eventData.position, eventData.enterEventCamera, out var position)) return;

            var rect = RectTransform.rect;
            Tweak.Tween = Mathf.InverseLerp(rect.min.x, rect.max.x, position.x);
        }

        private bool MayDrag(PointerEventData eventData)
        {
            return Tweak != null && eventData.button == PointerEventData.InputButton.Left;
        }

        /// <summary>
        /// The callback implementation for the <see cref="OnInitializePotentialDrag"/> event.
        /// </summary>
        /// <param name="eventData">The pointer event data for the potential drag gesture</param>
        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            eventData.useDragThreshold = false;
        }
    }
}

