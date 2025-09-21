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

using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    internal class PanelScrollRect : ScrollRect
    {
        public override void OnScroll(PointerEventData eventData)
        {
            // Filtering out any event that is not triggered during our bespoke processing of the PanelInputModule.
            if (!PanelInputModule.Processing)
            {
                return;
            }

            base.OnScroll(eventData);
        }

        public override void OnInitializePotentialDrag(PointerEventData eventData)
        {
            // Filtering out any event that is not triggered during our bespoke processing of the PanelInputModule.
            if (!PanelInputModule.Processing)
            {
                return;
            }

            base.OnInitializePotentialDrag(eventData);
        }

        public override void OnBeginDrag(PointerEventData eventData)
        {
            // Filtering out any event that is not triggered during our bespoke processing of the PanelInputModule.
            if (!PanelInputModule.Processing)
            {
                return;
            }

            base.OnBeginDrag(eventData);
        }

        public override void OnEndDrag(PointerEventData eventData)
        {
            // Filtering out any event that is not triggered during our bespoke processing of the PanelInputModule.
            if (!PanelInputModule.Processing)
            {
                return;
            }

            base.OnEndDrag(eventData);
        }

        public override void OnDrag(PointerEventData eventData)
        {
            // Filtering out any event that is not triggered during our bespoke processing of the PanelInputModule.
            if (!PanelInputModule.Processing)
            {
                return;
            }

            base.OnDrag(eventData);
        }
    }
}

