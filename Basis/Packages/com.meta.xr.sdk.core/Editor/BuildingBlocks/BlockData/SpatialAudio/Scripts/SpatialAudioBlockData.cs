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
using UnityEditor;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Editor
{
    public class SpatialAudioBlockData : BlockData
    {
        protected override bool UsesPrefab => false;
        internal override bool CanBeAddedOverGameObject => true;

        internal override IReadOnlyCollection<InstallationStepInfo> InstallationSteps =>
            new List<InstallationStepInfo>
            {
                new(null, "Spawn a new GameObject unless installed upon an existing one."),
                new(null, "Add an <b>AudioSource</b> component if not present."),
                new(null, "Add a <b>MetaXRAudioSource</b> component if not present."),
                new(null, "Add a <b>MetaXRAudioSourceExperimentalFeatures</b> component if not present.")
            };

        protected override List<GameObject> InstallRoutine(GameObject selectedGameObject)
        {
#if USING_META_XR_AUDIO_SDK
            if (selectedGameObject == null)
            {
                selectedGameObject = new GameObject
                {
                    name = $"{Utils.BlockPublicTag} {BlockName}"
                };
            }

            AddComponentIfMissing<AudioSource>(selectedGameObject);
            AddComponentIfMissing<MetaXRAudioSource>(selectedGameObject);
            AddComponentIfMissing<MetaXRAudioSourceExperimentalFeatures>(selectedGameObject);

            Undo.RegisterCreatedObjectUndo(selectedGameObject, "Create " + selectedGameObject.name);

            return new List<GameObject> { selectedGameObject };
#else
            throw new InstallationCancelledException("It's required to install the Audio SDK package to use this component");
#endif // USING_META_XR_AUDIO_SDK
        }

        private static void AddComponentIfMissing<T>(GameObject go) where T : Component
        {
            if (go.GetComponent<T>() == null)
            {
                go.AddComponent<T>();
            }
        }
    }
}
