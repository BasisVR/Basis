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

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    /// <summary>
    /// Extension of OVRRaycaster specifically adapted to the needs of Immersive Debugger
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    internal class PanelRaycaster : OVRRaycaster
    {
        /// <summary>
        /// Automated callback from <see cref="IPointerEnterHandler"/>
        /// </summary>
        /// <remarks>We do not want to interact with the existing event system,
        /// so this overrides is ignored</remarks>
        public override void OnPointerEnter(PointerEventData e) { }

        /// <summary>
        /// Is this the currently focussed Raycaster according to the InputModule
        /// </summary>
        /// <returns>false</returns>
        /// <remarks>We do not want to interact with the existing event system,
        /// and activeGraphicRaycaster is irrelevant in our case.</remarks>
        public override bool IsFocussed() => false;

        protected override void OnEnable() => PanelInputModule.RegisterRaycaster(this);
        protected override void OnDisable() => PanelInputModule.UnregisterRaycaster(this);

        public bool IsValid => eventCamera != null;
    }


}

