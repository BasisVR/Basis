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

namespace Meta.XR.BuildingBlocks.Editor
{
    /// <summary>
    /// Annotate field or property with this will be used as a variant for an InstallationRoutine.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    internal class VariantAttribute : PropertyAttribute
    {
        public enum VariantBehavior
        {
            /// <summary>
            /// Definitions are used to identify a broad type of implementations, ideally cross blocks
            /// For instance relying on a specific back-end implementation
            /// </summary>
            Definition,

            /// <summary>
            /// Parameters are variables that will be ask to the user and passed to the installation routine
            /// </summary>
            Parameter,

            /// <summary>
            /// Constants are not meant to be changed by the user, used by the installation routine.
            /// They're basically just normal fields, but bringing them as variants help better identifying the routine.
            /// </summary>
            Constant,
        }

        /// <summary>
        /// Whether the variant is used to define an InstallationRoutine, or passed as a parameter to it.
        /// </summary>
        public VariantBehavior Behavior { get; set; } = VariantBehavior.Parameter;

        /// <summary>
        /// Optional grouping for a variant, to ensure cross-block variants.
        /// </summary>
        public string Group { get; set; } = null;

        /// <summary>
        /// Optional MethodName that is used as a delegate to check whether or not this variant is required
        /// </summary>
        public string Condition { get; set; } = null;

        /// <summary>
        /// Optional Default value for this Variant Attribute, this will reset to this value independently of the
        /// routines values each time the popup is shown.
        /// </summary>
        public object Default { get; set; } = null;

        /// <summary>
        /// Optional Description for this Variant Attribute
        /// </summary>
        public string Description { get; set; } = null;
    }
}
