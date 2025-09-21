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

using UnityEditor;
using UnityEngine;
using Meta.XR.Guides.Editor;
using Meta.XR.Editor.UserInterface;
using Color = UnityEngine.Color;

namespace Meta.XR.BuildingBlocks.Editor
{
    [CustomEditor(typeof(OcclusionBuildingBlock))]
    public class OcclusionBuildingBlockEditor : BuildingBlockEditor
    {
        protected override void ShowAdditionals()
        {
            EditorGUILayout.BeginVertical(Styles.GUIStyles.ErrorHelpBox);
#if !UNITY_2022_3_OR_NEWER
            new Icon(Styles.Contents.ErrorIcon, Color.white, "<b>Unsupported Unity Editor. Requires 2022.3.1 or 2023.2.</b>").Draw();
#endif // UNITY_2022_3_OR_NEWER

#if !XR_OCULUS_4_2_0_OR_NEWER
            new Icon(Styles.Contents.ErrorIcon, Color.white, "<b>DepthAPI package is missing. Requires com.unity.xr.oculus of version 4.2.0.</b>").Draw();
#endif // XR_OCULUS_4_2_0_OR_NEWER

#if UNITY_2022_3_OR_NEWER && XR_OCULUS_4_2_0_OR_NEWER
            new Icon(Styles.Contents.InfoIcon, Color.white, "<b>Dynamic Occlusion block made some critical changes in project.</b>").Draw();
            if (GUILayout.Button("See the changes"))
            {
                OcclusionBlockSetupInfo.Show(true);
            }
#endif // UNITY_2022_3_OR_NEWER && XR_OCULUS_4_2_0_OR_NEWER

            EditorGUILayout.EndVertical();
        }
    }
}
