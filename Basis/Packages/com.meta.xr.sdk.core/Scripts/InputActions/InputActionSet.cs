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

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Meta.XR.InputActions
{
    [CreateAssetMenu(menuName = "Meta/Action Set")]
    public class InputActionSet : ScriptableObject
    {
        /// <summary>
        /// The Interaction Profile these actions should be used with.
        /// See the OpenXR spec for info on Interaction Profiles and the Action system in general.
        /// </summary>
        [InlineLink("https://registry.khronos.org/OpenXR/specs/1.0/html/xrspec.html#semantic-path-interaction-profiles")]
        [Tooltip("The interaction profile of the device these actions should be applied to.")]
        public string InteractionProfile;

        /// <summary>
        /// A list of Input Actions.
        /// </summary>
        [Tooltip("A list of the different Input Actions that this device supports.")]
        public List<InputActionDefinition> InputActionDefinitions = new List<InputActionDefinition>();

        public override string ToString()
        {
            return JsonUtility.ToJson(this);
        }
    }
}
