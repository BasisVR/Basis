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


using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

#if USING_META_XR_HAPTICS_SDK
using Oculus.Haptics;
#endif // USING_META_XR_HAPTICS_SDK

namespace Meta.XR.BuildingBlocks.Editor
{
    public class HapticsBlockData : Meta.XR.BuildingBlocks.Editor.BlockData
    {
        internal override bool CanBeAddedOverGameObject => true;
        private const string SAMPLE_HAPTIC = "Packages/com.meta.xr.sdk.haptics/Samples/IntegrationExample/Haptics/TestClip1.haptic";

        protected override bool UsesPrefab => false;

        protected override List<GameObject> InstallRoutine(GameObject selectedGameObject)
        {
#if USING_META_XR_HAPTICS_SDK
            if (selectedGameObject == null)
            {
                selectedGameObject = new GameObject
                {
                    name = $"{Utils.BlockPublicTag} {BlockName}"
                };
            }

            if (selectedGameObject.GetComponent<HapticSource>() == null)
            {
                var hapticSource = selectedGameObject.AddComponent<HapticSource>();
                hapticSource.clip = AssetDatabase.LoadAssetAtPath<HapticClip>(SAMPLE_HAPTIC);
            }

            return new List<GameObject> { selectedGameObject };
#else
            throw new InstallationCancelledException("It's required to install the Haptics SDK package to use this component");
#endif // USING_META_XR_HAPTICS_SDK
        }
    }
}
