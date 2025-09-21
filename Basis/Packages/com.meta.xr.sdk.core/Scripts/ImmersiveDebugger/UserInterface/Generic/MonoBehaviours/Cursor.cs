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


using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for the cursor UI element that's used on all panels of Immersive Debugger.
    /// It's inheriting from the <see cref="OVRCursor"/> and will be ray casting the "PanelLayer" specified in the settings.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class Cursor : OVRCursor
    {
        private const float _pressedScale = 0.8f;
        private const float _releasedScale = 1f;

        private Vector3 _forward;
        private Vector3 _endPoint;
        private Vector3 _normal;
        private bool _hit;
        private PointerEventData.FramePressState _pressState = PointerEventData.FramePressState.Released;
        private Canvas _canvas;

        internal GameObject GameObject { get; private set; }
        private Transform Transform { get; set; }

        private void Awake()
        {
            GameObject = gameObject;

            GameObject.layer = RuntimeSettings.Instance.PanelLayer;
            _canvas = GameObject.AddComponent<Canvas>();
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = Utils.CursorSortOrder;
            var canvasGroup = GameObject.AddComponent<CanvasGroup>();
            canvasGroup.blocksRaycasts = false;
            canvasGroup.interactable = false;
            var image = GameObject.AddComponent<RawImage>();
            image.texture = Resources.Load<Texture2D>("Textures/pointer");
            image.rectTransform.sizeDelta = (new Vector2(20, 20));
            image.raycastTarget = false;

            Transform = transform;
        }

        /// <summary>
        /// Overriding the <see cref="SetCursorStartDest"/> from <see cref="OVRCursor"/>, setting the starting point
        /// and the destination point of the cursor.
        /// </summary>
        /// <param name="start"><see cref="Vector3"/> of the starting point position</param>
        /// <param name="dest"><see cref="Vector3"/> of the destination point position</param>
        /// <param name="normal"><see cref="Vector3"/> representing the normal of the cursor</param>
        public override void SetCursorStartDest(Vector3 start, Vector3 dest, Vector3 normal)
        {
            _endPoint = dest;
            _normal = normal;
            _hit = true;
        }

        /// <summary>
        /// Overriding the <see cref="SetCursorRay"/> from <see cref="OVRCursor"/>, setting the transform of the cursor ray.
        /// </summary>
        /// <param name="t"><see cref="Transform"/> that's used to set the cursor's starting point and forward direction</param>
        public override void SetCursorRay(Transform t)
        {
            _forward = t.forward;
            _normal = _forward;
            _hit = false;
        }

        public void SetClickState(PointerEventData.FramePressState state)
        {
            // This method handles the stateful memory of whether or not it was pressed before
            if (state == PointerEventData.FramePressState.NotChanged)
            {
                if (_pressState == PointerEventData.FramePressState.PressedAndReleased)
                {
                    _pressState = PointerEventData.FramePressState.Released;
                }

                return;
            }

            _pressState = state;
        }

        private void LateUpdate()
        {
            if (_hit)
            {
                Transform.position = _endPoint;
                Transform.rotation = Quaternion.LookRotation(_normal, Vector3.up);
                var clicked = _pressState is PointerEventData.FramePressState.Pressed
                    or PointerEventData.FramePressState.PressedAndReleased;

                Transform.localScale = Vector3.one * (clicked ? _pressedScale : _releasedScale);
            }
            else
            {
                GameObject.SetActive(false);
            }
        }

        internal void Attach(Panel panel)
        {
            if (panel == null)
            {
                return;
            }

            GameObject.SetActive(true);
            Transform.SetParent(panel.Transform, false);
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = Utils.CursorSortOrder;
        }
    }
}

