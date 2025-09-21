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
#if UNITY_EDITOR
using UnityEditor;
#endif


namespace Meta.XR.BuildingBlocks
{
    /// <summary>
    /// Building Blocks are designed to help developers discover and install XR features using the Meta XR SDK.
    /// The <see cref="BuildingBlock"/> <see cref="MonoBehaviour"/> is used as a container for Metadata.
    /// It stores its <see cref="BlockId"/>, <see cref="InstanceId"/>, <see cref="Version"/>
    /// as well as <see cref="InstallationRoutineCheckpoint"/> which helps the Building Blocks system to keep track
    /// of which blocks are installed or not, specifically to properly manage dependency chains.
    /// For more information, see [Building Blocks](https://developer.oculus.com/documentation/unity/bb-overview/)
    /// </summary>
    /// <remarks>
    /// In Editor, you can delete this component by right-clicking the object
    /// and using Building Blocks > Break Block Connection, which will erase the Metadata and stop treating the
    /// <see cref="GameObject"/> as a block.
    /// Alternatively, you can also delete the component in the Inspector by clicking the Break Block Connection button.
    /// </remarks>
    /// <remarks>
    /// The <see cref="BuildingBlock"/> <see cref="MonoBehaviour"/> is not meant to be added dynamically to an existing
    /// <see cref="GameObject"/> at runtime as it requires some specific metadata to properly work register
    /// with the Building Blocks system.
    /// </remarks>
    /// <seealso cref="BlockId"/>
    /// <seealso cref="InstanceId"/>
    /// <seealso cref="Version"/>
    /// <seealso cref="InstallationRoutineCheckpoint"/>
    [HelpURL("https://developer.oculus.com/documentation/unity/bb-overview/")]
    [DisallowMultipleComponent, ExecuteInEditMode]
    public class BuildingBlock : MonoBehaviour
    {
        [SerializeField, OVRReadOnly] internal string blockId;
        /// <summary>
        /// Identifies the <see cref="Meta.XR.BuildingBlocks.Editor.BlockData"/> that this block originates from.
        /// </summary>
        public string BlockId => blockId;

        [SerializeField, HideInInspector] internal string instanceId = Guid.NewGuid().ToString();
        /// <summary>
        /// A unique identifier string representing the block instance.
        /// </summary>
        public string InstanceId => instanceId;

        [SerializeField, OVRReadOnly] internal int version = 1;
        /// <summary>
        /// The version integer of the block, set up during the installation of the block.
        /// This value is used to detect whether or not an update is available for this block.
        /// </summary>
        public int Version => version;

        [SerializeField, HideInInspector] private InstallationRoutineCheckpoint installationRoutineCheckpoint;
        /// <summary>
        /// Contains all Metadata related to the installation of the block,
        /// typically the variants and interfaces choices required by the installation process.
        /// This data is serialized into the scene (or prefab) on the related <see cref="GameObject"/>.
        /// </summary>
        /// <seealso cref="InstallationRoutineCheckpoint"/>
        public InstallationRoutineCheckpoint InstallationRoutineCheckpoint
        {
            get => installationRoutineCheckpoint;
            set => installationRoutineCheckpoint = value;
        }

        private void Awake()
        {
            if (Application.isPlaying)
            {
                return;
            }

            if (HasDuplicateInstanceId())
            {
                ResetInstanceId();
            }
        }

        private void ResetInstanceId()
        {
            instanceId = Guid.NewGuid().ToString();

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        private bool HasDuplicateInstanceId()
        {
            foreach (var block in FindObjectsByType<BuildingBlock>(FindObjectsSortMode.InstanceID))
            {
                if (block != this && block.InstanceId == InstanceId)
                {
                    return true;
                }
            }

            return false;
        }

        private void Start()
        {
            OVRTelemetry.Start(OVRTelemetryConstants.BB.MarkerId.RunBlock)
                .AddBlockInfo(this)
                .SendIf(Application.isPlaying);
        }
    }
}
