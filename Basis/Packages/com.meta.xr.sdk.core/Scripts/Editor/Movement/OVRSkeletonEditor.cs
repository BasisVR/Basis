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
using UnityEditor.SceneManagement;

[CustomEditor(typeof(OVRSkeleton))]
public class OVRSkeletonEditor : Editor
{
    private OVRSkeleton _skeleton;

    private static OVRHandSkeletonVersion GlobalVersion =>
        OVRRuntimeSettings.GetRuntimeSettings().HandSkeletonVersion;

    public override void OnInspectorGUI()
    {
        _skeleton = (OVRSkeleton)target;

        if ((OVRPlugin.SkeletonType)_skeleton.GetSkeletonType() == OVRPlugin.SkeletonType.None)
        {
            EditorGUILayout.HelpBox("Please select a SkeletonType.", MessageType.Warning);
        }

        if (!IsSkeletonProperlyConfigured(_skeleton))
        {
            if (OVREditorUIElements.RenderWarningWithButton(
                    $"An OVRBody component with the `{_skeleton.GetRequiredBodyJointSet()}` joint set is required.",
                    "Add OVRBody component"))
            {
                FixOVRBodyConfiguration(_skeleton, _skeleton.GetRequiredBodyJointSet());
            }
        }

        if (!IsHandVersionCorrect())
        {
            if (OVREditorUIElements.RenderWarningWithButton(
                    $"You must select an {GlobalVersion} hand skeleton type.",
                    "Fix Skeleton Type"))
            {
                FixHandSkeletonType();
            }
        }

        DrawDefaultInspector();
    }

    internal static bool IsSkeletonProperlyConfigured(OVRSkeleton skeleton)
    {
        return !OVRSkeleton.IsBodySkeleton(skeleton.GetSkeletonType()) ||
            skeleton.SearchSkeletonDataProvider() != null;
    }

    internal static void FixOVRBodyConfiguration(OVRSkeleton skeleton, OVRPlugin.BodyJointSet jointSet)
    {
        var gameObject = skeleton.gameObject;
        Undo.IncrementCurrentGroup();
        var body = gameObject.AddComponent<OVRBody>();
        body.ProvidedSkeletonType = jointSet;
        Undo.RegisterCreatedObjectUndo(body, "Add OVRBody component");
        EditorUtility.SetDirty(body);
        EditorSceneManager.MarkSceneDirty(gameObject.scene);
        Undo.SetCurrentGroupName("Add OVRBody component");
    }

    private bool IsHandVersionCorrect()
    {
        var skeletonType = _skeleton.GetSkeletonType();
        if (!skeletonType.IsHand() ||
            !_skeleton.TryGetComponent<OVRHand>(out _))
        {
            return true;
        }

        return GlobalVersion switch
        {
            OVRHandSkeletonVersion.OVR => skeletonType.IsOVRHandSkeleton(),
            OVRHandSkeletonVersion.OpenXR => skeletonType.IsOpenXRHandSkeleton(),
            _ => true
        };
    }

    private void FixHandSkeletonType()
    {
        var skeletonType = _skeleton.GetSkeletonType();
        var prop = serializedObject.FindProperty("_skeletonType");
        if (!skeletonType.IsHand() || prop == null)
        {
            return;
        }
        if (GlobalVersion == OVRHandSkeletonVersion.OVR)
        {
            prop.intValue = skeletonType.IsLeft() ? (int)OVRSkeleton.SkeletonType.HandLeft :
                (int)OVRSkeleton.SkeletonType.HandRight;
        }
        else if (GlobalVersion == OVRHandSkeletonVersion.OpenXR)
        {
            prop.intValue = skeletonType.IsLeft() ? (int)OVRSkeleton.SkeletonType.XRHandLeft :
                (int)OVRSkeleton.SkeletonType.XRHandRight;
        }
        serializedObject.ApplyModifiedProperties();
    }
}
