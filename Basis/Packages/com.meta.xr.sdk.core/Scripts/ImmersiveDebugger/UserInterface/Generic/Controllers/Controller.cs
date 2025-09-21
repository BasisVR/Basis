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
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    internal static class Extensions
    {
        public static void SetSizeOptimized(this RectTransform rectTransform, Vector2 offsetMin, Vector2 offsetMax, Vector2 fixedDimensions, bool setAnchoredPosition)
        {
            // This function is only the unwrapping and simple optimisation (as in, removing duplicated computing, and reusing proxy variables)
            // of .minOffset, followed by .maxOffset, followed by .sizeDelta from the Unity Source code
            var sizeDelta = rectTransform.sizeDelta;
            var anchoredPosition = rectTransform.anchoredPosition;
            var pivot = rectTransform.pivot;
            var vector = offsetMin - (anchoredPosition - new Vector2(sizeDelta.x * pivot.x, sizeDelta.y * pivot.y));
            sizeDelta.x = vector.x + sizeDelta.x;
            sizeDelta.y = vector.y + sizeDelta.y;
            anchoredPosition = new Vector2(offsetMin.x + sizeDelta.x * pivot.x, offsetMin.y + sizeDelta.y * pivot.y);

            vector = offsetMax - (anchoredPosition + new Vector2(sizeDelta.x * (1 - pivot.x), sizeDelta.y * (1 - pivot.y)));
            sizeDelta.x = vector.x + sizeDelta.x;
            sizeDelta.y = vector.y + sizeDelta.y;
            anchoredPosition = new Vector2(offsetMax.x - sizeDelta.x * (1 - pivot.x), offsetMax.y - sizeDelta.y * (1 - pivot.y));

            sizeDelta.x = fixedDimensions.x != 0.0f ? fixedDimensions.x : sizeDelta.x;
            sizeDelta.y = fixedDimensions.y != 0.0f ? fixedDimensions.y : sizeDelta.y;
            rectTransform.sizeDelta = sizeDelta;
            if (setAnchoredPosition)
            {
                rectTransform.anchoredPosition = anchoredPosition;
            }
        }
    }

    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> served as the base element of all UI elements of Immersive Debugger.
    /// Manages common User Interface properties like visibility, transform etc. and provide generic ways to manage hierarchy
    /// relationship and layouts.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class Controller : MonoBehaviour
    {
        private bool _visibility = true;
        private bool _refreshLayoutRequested;
        protected bool _hasRectTransform;
        private bool _layoutStyleHasChanged;


        [SerializeField]
        protected LayoutStyle _layoutStyle;
        protected List<Controller> _children;

        internal Controller Owner { get; set; }
        /// <summary>
        /// Transform of the object, if the <see cref="RectTransform"/> is not specified,
        /// it would be the <see cref="GameObject"/>'s transform. Otherwise it's the same with the <see cref="RectTransform"/>.
        /// </summary>
        public Transform Transform { get; protected set; }
        /// <summary>
        /// As most of the UI elements are 2D rectangles, this RectTransform is representing the element's actual transform.
        /// </summary>
        public RectTransform RectTransform { get; protected set; }
        protected GameObject GameObject { get; set; }
        /// <summary>
        /// All the children of this UI element.
        /// </summary>
        public List<Controller> Children => _children;

        private RectMask2D _mask = null;

        /// <summary>
        /// The layout style of this UI element, specify how UI elements within this container should be arranged.
        /// Upon setting this property, the UI will be refreshed to be reflected in the runtime.
        /// </summary>
        public LayoutStyle LayoutStyle
        {
            get => _layoutStyle;
            set
            {
                if (value == null) return;
                if (_layoutStyle == value) return;

                _layoutStyle = value;
                _layoutStyleHasChanged = true;
                RefreshLayout();

                if (_layoutStyle?.isOverlayCanvas ?? false)
                    UpdateRefreshLayout(false);
            }
        }

        /// <summary>
        /// Event that can be subscribed to when the visibility of this UI element is changed.
        /// </summary>
        public event Action<Controller> OnVisibilityChangedEvent;

        private bool _transparent;

        /// <summary>
        /// The boolean indicating the current panel is transparent or not.
        /// In Immersive Debugger this could be controlled by one of the mini buttons on Debug Bar panel.
        /// </summary>
        public bool Transparent
        {
            get => _transparent;
            set
            {
                if (_transparent == value)
                {
                    return;
                }

                _transparent = value;
                OnTransparencyChanged();
            }
        }

        protected virtual void OnTransparencyChanged()
        {
            // Place to setup transparency in child classes.
        }

        protected virtual void Setup(Controller owner)
        {
            Owner = owner;
            GameObject = gameObject;
            GameObject.layer = RuntimeSettings.Instance.PanelLayer;
            RectTransform = GameObject.AddComponent<RectTransform>() ?? GameObject.GetComponent<RectTransform>();
            Transform = RectTransform ? RectTransform : GameObject.transform;
            if (Owner != this && Owner != null)
            {
                Transform.SetParent(Owner.Transform, false);
            }

            LayoutStyle = Style.Default<LayoutStyle>();

            _hasRectTransform = RectTransform != null;
        }

        internal T Append<T>(string childName)
            where T : Controller, new()
        {
            var childController = SetupChildController<T>(childName);
            _children ??= new List<Controller>();
            _children.Add(childController);
            return childController;
        }

        internal T Prepend<T>(string childName)
            where T : Controller, new()
        {
            var childController = SetupChildController<T>(childName);
            _children ??= new List<Controller>();
            _children.Insert(0, childController);
            return childController;
        }

        internal T InsertAfter<T>(string childName, Controller previous)
            where T : Controller, new()
        {
            var childController = SetupChildController<T>(childName);
            _children ??= new List<Controller>();
            var index = _children.IndexOf(previous);
            _children.Insert(index + 1, childController);
            return childController;
        }

        internal T InsertBefore<T>(string childName, Controller next)
            where T : Controller, new()
        {
            var childController = SetupChildController<T>(childName);
            _children ??= new List<Controller>();
            var index = _children.IndexOf(next);
            _children.Insert(index, childController);
            return childController;
        }

        private T SetupChildController<T>(string childName)
            where T : Controller, new()
        {
            var childObject = new GameObject(childName);
            var childController = childObject.AddComponent<T>();
            childController.Setup(this);
            return childController;
        }

        protected void Append(Controller controller)
        {
            if (_children == null) return;
            if (_children.Contains(controller)) return;

            _children.Add(controller);
            controller.RefreshLayout();
        }

        internal void Remove(Controller controller, bool destroy)
        {
            if (_children == null) return;

            _children.Remove(controller);
            if (destroy)
            {
                if (Application.isPlaying)
                {
                    Destroy(controller.gameObject);
                }
                else
                {
                    DestroyImmediate(controller.gameObject);
                }
            }
            RefreshLayout();
        }

        protected void Clear(bool destroy)
        {
            while (_children.Count > 0)
            {
                Remove(_children[^1], destroy);
            }
        }

        /// <summary>
        /// Visibility of the UI component, when visibility changed it will automatically invoke the
        /// <see cref="OnVisibilityChanged"/> event, and the game object's active status is also changed based on it.
        /// </summary>
        public bool Visibility
        {
            get => _visibility;
            private set
            {
                if (_visibility != value)
                {
                    _visibility = value;
                    OnVisibilityChanged();
                }
            }
        }

        /// <summary>
        /// Hide the UI element, it will automatically invoke the <see cref="OnVisibilityChanged"/> event,
        /// and the game object's active status would be changed to false.
        /// </summary>
        public void Hide()
        {
            Visibility = false;
        }

        /// <summary>
        /// Show the UI element, it will automatically invoke the <see cref="OnVisibilityChanged"/> event,
        /// and the game object's active status would be changed to true.
        /// </summary>
        public void Show()
        {
            Visibility = true;
        }

        internal void ToggleVisibility()
        {
            Visibility = !GameObject.activeSelf;
        }

        protected virtual void OnVisibilityChanged()
        {
            GameObject.SetActive(Visibility);
            OnVisibilityChangedEvent?.Invoke(this);
        }

        private static Vector2 GetVec2FromLayout(TextAnchor anchor)
        {
            var index = (int)anchor;
            return new Vector2((index % 3) * 0.5f, 1.0f - (int)(index / 3) * 0.5f);
        }

        protected void UpdateRefreshLayout(bool force)
        {
            if (!force && !_refreshLayoutRequested) return;

            _refreshLayoutRequested = false;


            RefreshLayoutPreChildren();

            // Process Refresh of layout of all children
            if (_children != null)
            {
                // Force refresh if all children are needed for computing height or width
                var forceChildrenRefresh = _layoutStyle.adaptHeight || _layoutStyle.autoFitChildren;
                foreach (var child in _children)
                {
                    child.UpdateRefreshLayout(forceChildrenRefresh);
                }
            }

            RefreshLayoutPostChildren();
        }

        internal void RefreshLayout()
        {
            // Request refresh flag
            _refreshLayoutRequested = true;

            // Propagate up
            Owner?.RefreshLayout();
        }

        protected virtual void RefreshLayoutPreChildren()
        {
            if (!_hasRectTransform) return;

            // Calculate Anchor and Pivot position
            if (_layoutStyleHasChanged)
            {
                _layoutStyleHasChanged = false;

                RectTransform.pivot = GetVec2FromLayout(_layoutStyle.pivot);
                RectTransform.anchorMin = GetVec2FromLayout(_layoutStyle.anchor);
                RectTransform.anchorMax = GetVec2FromLayout(_layoutStyle.anchor);

                switch (_layoutStyle.layout)
                {
                    case LayoutStyle.Layout.Fixed:
                        break;

                    case LayoutStyle.Layout.Fill:
                        RectTransform.anchorMin = new Vector2(0.0f, 0.0f);
                        RectTransform.anchorMax = new Vector2(1.0f, 1.0f);
                        break;
                    case LayoutStyle.Layout.FillHorizontal:
                        RectTransform.anchorMin = new Vector2(0.0f, RectTransform.anchorMin.y);
                        RectTransform.anchorMax = new Vector2(1.0f, RectTransform.anchorMax.y);
                        break;
                    case LayoutStyle.Layout.FillVertical:
                        RectTransform.anchorMin = new Vector2(RectTransform.anchorMin.x, 0.0f);
                        RectTransform.anchorMax = new Vector2(RectTransform.anchorMax.x, 1.0f);
                        break;
                }

                // Update Mask
                if (_layoutStyle.masks)
                {
                    _mask ??= GameObject.AddComponent<RectMask2D>();
                    _mask.enabled = true;
                }
                else if (_mask != null)
                {
                    _mask.enabled = false;
                }
            }

            var offsetMin = new Vector2(_layoutStyle.LeftMargin, _layoutStyle.BottomMargin);
            var offsetMax = new Vector2(-_layoutStyle.RightMargin, -_layoutStyle.TopMargin);
            RectTransform.SetSizeOptimized(offsetMin, offsetMax, _layoutStyle.size, !_layoutStyle.isOverlayCanvas);
        }

        protected virtual void RefreshLayoutPostChildren()
        {
            if (!_hasRectTransform) return;

            if (LayoutStyle.adaptHeight)
            {
                var maxOffset = 0.0f;

                if (_children != null)
                {
                    foreach (var child in _children)
                    {
                        if (child is Flex flex)
                        {
                            maxOffset = Mathf.Max(maxOffset, flex.SizeDeltaWithMargin.y);
                        }
                    }
                }

                RectTransform.sizeDelta = new Vector2(RectTransform.sizeDelta.x, maxOffset);
            }
        }

        private void OnDestroy()
        {
            if (Owner != null)
            {
                Owner.Remove(this, false);
            }
        }

        internal void SetHeight(float height)
        {
            if (!_layoutStyle.SetHeight(height)) return;
            RefreshLayout();
        }

        internal void SetWidth(float width)
        {
            if (!_layoutStyle.SetWidth(width)) return;
            RefreshLayout();
        }
    }
}
