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

namespace Meta.XR.BuildingBlocks
{
    /// <summary>
    /// Contains all Metadata related to the installation of a block,
    /// typically the variants and interfaces choices required by the installation process.
    /// The data is serialized in the owner <see cref="BuildingBlock"/> <see cref="MonoBehaviour"/>.
    /// Installation Routines and Variants are a part of Meta XR Building Blocks. For more information,
    /// see [Building Blocks](https://developer.oculus.com/documentation/unity/bb-overview/)
    /// </summary>
    /// <remarks>
    /// The data is set automatically into these serializable classes on installation of a <see cref="BuildingBlock"/>.
    /// </remarks>
    [Serializable]
    public class InstallationRoutineCheckpoint
    {
        [SerializeField, HideInInspector] private string _installationRoutineId;
        /// <summary>
        /// Identifies the <see cref="Meta.XR.BuildingBlocks.Editor.InstallationRoutine"/> used to install the owner
        /// <see cref="BuildingBlock"/>. During the installation, this is used to know which routine should be applied.
        /// After installation, this is stored to keep an history on how the block was installed.
        /// </summary>
        public string InstallationRoutineId => _installationRoutineId;

        [SerializeField, HideInInspector] private List<VariantCheckpoint> _installationVariants;
        /// <summary>
        /// List of all variant options used for the installation of the owning <see cref="BuildingBlock"/>.
        /// During the installation, these are used to know which variants to apply during the installation routine.
        /// After installation, these are used to keep an history on the variants that were used for this block.
        /// </summary>
        public List<VariantCheckpoint> InstallationVariants => _installationVariants;

        /// <summary>
        /// Constructor for a <see cref="InstallationRoutineCheckpoint"/>, which helps serializing data used during
        /// the installation of a <see cref="BuildingBlock"/>.
        /// </summary>
        /// <param name="installationRoutineId"></param>
        /// <param name="installationVariants"></param>
        public InstallationRoutineCheckpoint(string installationRoutineId, List<VariantCheckpoint> installationVariants)
        {
            _installationRoutineId = installationRoutineId;
            _installationVariants = installationVariants;
        }
    }

    /// <summary>
    /// Describes a variant used for the installation of a block.
    /// The data is serialized within the <see cref="InstallationRoutineCheckpoint"/> which is stored within the owner
    /// <see cref="BuildingBlock"/> <see cref="MonoBehaviour"/>.
    /// Installation Routines and Variants are a part of Meta XR Building Blocks. For more information,
    /// see [Building Blocks](https://developer.oculus.com/documentation/unity/bb-overview/)
    /// </summary>
    /// <remarks>
    /// The data is set automatically into these serializable classes on installation of a <see cref="BuildingBlock"/>.
    /// </remarks>
    [Serializable]
    public class VariantCheckpoint
    {
        [SerializeField] protected string _memberName;
        /// <summary>
        /// Name of the member set by this variant. During installation, it is used by reflection to figure out
        /// which member needs to be set by this variant. After installation, it is stored to keep an history of
        /// the selected variants.
        /// </summary>
        public string MemberName => _memberName;

        [SerializeField] protected string _value;
        /// <summary>
        /// Serialized value, as a string, set by this variant. During installation, it is used to set the value
        /// of the variant (to the member represented by <see cref="MemberName"/>). After installation, it is stored
        /// to keep an history of the selected variants.
        /// </summary>
        public string Value => _value;

        /// <summary>
        /// Constructor for a <see cref="VariantCheckpoint"/>, which helps serializing data defined by a variant
        /// such as the member name and the value set by the variant.
        /// </summary>
        /// <param name="memberName">Name of the member set by the variant</param>
        /// <param name="value">Value, serialized as a string, set by the variant</param>
        public VariantCheckpoint(string memberName, string value)
        {
            _memberName = memberName;
            _value = value;
        }
    }
}
