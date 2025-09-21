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

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This class manages a ScrollView and its Flex by adding virtualization to its items.
    /// This discharge the Flex from having to handle a big number of children. Instead,
    /// it has a small number of children and the ProxyFlex handles the large amount of ProxyController<ControllerType>.
    /// This is mostly a work around poor performance of handling and refreshing a large amount of UI objects in the
    /// scene.
    /// </summary>
    internal class ProxyFlex<ControllerType, ProxyControllerType>
        where ControllerType : Controller, new()
        where ProxyControllerType : ProxyController<ControllerType>, new()
    {
        private readonly int _maximumNumberOfProxies;
        private readonly Dictionary<ControllerType, ProxyController<ControllerType>> _targetsDictionary = new();

        private readonly ScrollView _scrollView;
        private readonly List<ProxyControllerType> _proxyChildren = new();
        public bool Dirty { get; private set; }

        public Flex Flex => _scrollView.Flex;
        public int NumberOfProxies => _proxyChildren.Count;
        private int NumberOfControllers => Flex.Children.Count - 2;

        private readonly Controller _before;
        private readonly Controller _after;

        private readonly LayoutStyle _childrenLayoutStyle;

        private float _lastScroll;

        public ProxyFlex(int numberOfInstantiatedControllers, int maximumNumberOfProxies, LayoutStyle layoutStyle, ScrollView scrollView)
        {
            _scrollView = scrollView;

            // Instantiate controllers
            for (var i = 0; i < numberOfInstantiatedControllers; i++)
            {
                var controller = Flex.Append<ControllerType>(i.ToString());
                controller.LayoutStyle = layoutStyle;
            }

            _maximumNumberOfProxies = maximumNumberOfProxies;

            _childrenLayoutStyle = layoutStyle;

            // Instantiate spacers
            _before = Flex.Prepend<Controller>("before");
            _before.LayoutStyle = Style.Instantiate<LayoutStyle>("DynamicSpace");
            _after = Flex.Append<Controller>("after");
            _after.LayoutStyle = Style.Instantiate<LayoutStyle>("DynamicSpace");
        }

        public ProxyControllerType AppendProxy()
        {
            if (NumberOfProxies >= _maximumNumberOfProxies)
            {
                RemoveProxy(_proxyChildren[0]);
            }

            var proxy = OVRObjectPool.Get<ProxyControllerType>();
            _proxyChildren.Add(proxy);
            Dirty = true;
            return proxy;
        }

        public void RemoveProxy(ProxyControllerType proxy)
        {
            _proxyChildren.Remove(proxy);
            OVRObjectPool.Return(proxy);
            Dirty = true;
        }

        public void Clear()
        {
            foreach (var proxy in _proxyChildren)
            {
                OVRObjectPool.Return(proxy);
            }
            _proxyChildren.Clear();
            Dirty = true;
        }

        public void Update()
        {
            if (HasScrolledEnough())
            {
                Dirty = true;
            }

            if (Dirty)
            {
                Fill();
                Dirty = false;
            }
        }

        private bool HasScrolledEnough()
        {
            // Test if the scroll position has changed more than one (dynamic) pixel
            return Mathf.Abs(Flex.RectTransform.anchoredPosition.y - _lastScroll) > 1;
        }

        private void Fill()
        {
            _lastScroll = Flex.RectTransform.anchoredPosition.y;

            // First step is to get the scroll progress
            var expectedHeight = ComputeStartHeightFromProgress(_scrollView.Progress);

            // From them we can compute which is the first, which is the last
            var children = Flex.Children;
            var firstIndex = GetItemIndexAtHeight(expectedHeight);
            var lastIndex = firstIndex + NumberOfControllers - 1;

            // Offset up if needed (to avoid a gap on the top when we're not showing enough)
            var offset = Math.Max(0, Math.Min(lastIndex - NumberOfProxies, firstIndex));
            firstIndex -= offset;
            lastIndex -= offset;
            var filledIndex = 1;

            for (var proxyIndex = firstIndex; proxyIndex <= lastIndex; proxyIndex++)
            {
                if (proxyIndex < NumberOfProxies)
                {
                    var controller = children[filledIndex++] as ControllerType;
                    _proxyChildren[proxyIndex].Fill(controller, _targetsDictionary);
                }
            }

            // And we set the spacer height
            var beforeHeight = ComputeHeight(0, firstIndex - 1);
            var afterHeight = ComputeHeight(lastIndex + 1, NumberOfProxies - 1);
            _before.SetHeight(beforeHeight);
            _after.SetHeight(afterHeight);
        }

        private float ComputeTotalHeight()
        {
            // Assumption : we only care about height
            return ComputeHeight(0, Math.Max(NumberOfProxies - 1, NumberOfControllers - 1));
        }

        private float ComputeTotalUsefulHeight()
        {
            return (ComputeTotalHeight() - ComputeHeight(1, NumberOfControllers - 1) + Flex.LayoutStyle.spacing);
        }

        private float ComputeStartHeightFromProgress(float progress)
        {
            // Progress is from bottom for ScrollRects
            return (1.0f - progress) * ComputeTotalUsefulHeight();
        }

        private int GetItemIndexAtHeight(float height)
        {
            // Assumption : we only care about height
            // Assumption : all items are of the same height
            var index = (int)(height / (_childrenLayoutStyle.size.y + Flex.LayoutStyle.spacing));
            return Math.Max(0, index);
        }

        private float ComputeHeight(int startIndex, int endIndex)
        {
            // Assumption : we only care about height
            // Assumption : all items are of the same height
            var count = endIndex - startIndex + 1;
            var spacing = Flex.LayoutStyle.spacing;
            return count * (_childrenLayoutStyle.size.y + spacing) - spacing;
        }
    }
}

