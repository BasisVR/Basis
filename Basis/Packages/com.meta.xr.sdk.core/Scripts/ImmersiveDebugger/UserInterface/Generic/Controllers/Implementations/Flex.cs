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
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> used for managing the layout for all the UI elements of Immersive Debugger.
    /// It contains helper functions to calculate and update layout.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class Flex : Controller
    {
        private Vector2 _sizeDelta;

        internal Vector2 SizeDelta => _sizeDelta;
        internal Vector2 SizeDeltaWithMargin => _sizeDelta + LayoutStyle.TopLeftMargin + LayoutStyle.BottomRightMargin;

        internal ScrollViewport ScrollViewport { get; set; }
        private Vector2? _previousAnchoredPosition;

        private void UpdateAnchoredPosition(Controller controller, ref Vector2 offset, Vector2 direction)
        {
            var margin = controller.LayoutStyle.margin;
            var anchoredPosition = new Vector2(margin.x, -margin.y);
            var size = controller.RectTransform.sizeDelta;
            controller.RectTransform.anchoredPosition = anchoredPosition + offset;

            offset += direction * size;
            offset += direction * _layoutStyle.spacing;
        }

        private void UpdateChildrenWidth()
        {
            if (_children == null) return;
            if (!_layoutStyle.autoFitChildren || _layoutStyle.size.x <= 0) return;

            var flexibleSpaceRemaining = RectTransform.sizeDelta.x;
            var fixedSizedChildCount = 0;
            foreach (var child in _children)
            {
                if (child.LayoutStyle.layout is LayoutStyle.Layout.Fixed)
                {
                    flexibleSpaceRemaining -= child.LayoutStyle.size.x + child.LayoutStyle.margin.x * 2 + _layoutStyle.spacing;
                    fixedSizedChildCount++;
                }
            }

            var remainingChild = _children.Count - fixedSizedChildCount;
            if (remainingChild == 0) return;

            foreach (var child in _children)
            {
                var estimatedCellWidth = Mathf.RoundToInt((flexibleSpaceRemaining / remainingChild)) - _layoutStyle.spacing;
                if (child.LayoutStyle.layout is not LayoutStyle.Layout.Fixed)
                {
                    child.SetWidth(estimatedCellWidth);
                }
            }
        }

        private void RefreshVisibilities(bool force = false)
        {
            if (ScrollViewport == null) return;
            if (_children == null) return;

            var newPosition = RectTransform.anchoredPosition;
            if (!force && newPosition == _previousAnchoredPosition) return;
            _previousAnchoredPosition = newPosition;

            var scroll = RectTransform.anchoredPosition;
            var viewportRect = new Rect(ScrollViewport.RectTransform.anchoredPosition, ScrollViewport.RectTransform.rect.size);

            var firstWasShown = false;
            var lastWasShown = false;

            foreach (var child in _children)
            {
                if (!lastWasShown && IsVerticallyInViewport(child, viewportRect, scroll))
                {
                    child.Show();
                    firstWasShown = true;
                }
                else
                {
                    child.Hide();
                    if (firstWasShown)
                    {
                        lastWasShown = true;
                    }
                }
            }
        }

        private static bool IsVerticallyInViewport(Controller controller, Rect viewportRect, Vector2 scroll)
        {
            var position = -controller.RectTransform.anchoredPosition - scroll;

            if (position.y >= viewportRect.yMin)
            {
                if (position.y < viewportRect.yMax)
                {
                    return true;
                }
            }
            else if (position.y + controller.RectTransform.sizeDelta.y >= viewportRect.yMin)
            {
                return true;
            }

            return false;
        }

        protected override void RefreshLayoutPreChildren()
        {
            base.RefreshLayoutPreChildren();

            UpdateChildrenWidth();
        }

        protected override void RefreshLayoutPostChildren()
        {
            if (!_hasRectTransform) return;

            // Not calling base purposefully

            if (_children != null)
            {
                var direction = _layoutStyle.flexDirection switch
                {
                    LayoutStyle.Direction.Left => Vector2.left,
                    LayoutStyle.Direction.Right => Vector2.right,
                    LayoutStyle.Direction.Down => Vector2.down,
                    LayoutStyle.Direction.Up => Vector2.up,
                    _ => throw new ArgumentOutOfRangeException()
                };

                var offset = Vector2.zero;
                foreach (var child in _children)
                {
                    UpdateAnchoredPosition(child, ref offset, direction);
                }

                // Reset Visibility previous position to enforce refreshing visibilities
                _previousAnchoredPosition = null;

                _sizeDelta = new Vector2(Mathf.Abs(offset.x), Mathf.Abs(offset.y));
            }

            if (LayoutStyle.adaptHeight)
            {
                RectTransform.sizeDelta = new Vector2(RectTransform.sizeDelta.x, Mathf.Abs(_sizeDelta.y));
            }
        }

        private void LateUpdate()
        {
            RefreshVisibilities();
        }

        internal void Forget(Controller controller)
        {
            Remove(controller, false);
            controller.Hide();
        }

        internal void Remember(Controller controller)
        {
            Append(controller);
            controller.Show();
        }

        internal void ForgetAll()
        {
            if (_children == null) return;

            foreach (var child in _children)
            {
                child.Hide();
            }
            Clear(false);
        }
    }
}

