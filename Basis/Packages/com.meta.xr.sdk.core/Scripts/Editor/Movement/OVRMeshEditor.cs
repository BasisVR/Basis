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

[CustomEditor(typeof(OVRMesh))]
public class OVRMeshEditor : Editor
{
    private OVRMesh _mesh;

    private static OVRHandSkeletonVersion GlobalVersion =>
        OVRRuntimeSettings.GetRuntimeSettings().HandSkeletonVersion;

    public override void OnInspectorGUI()
    {
        _mesh = (OVRMesh)target;

        if ((OVRPlugin.MeshType)_mesh.GetMeshType() == OVRPlugin.MeshType.None)
        {
            EditorGUILayout.HelpBox("Please select a SkeletonType.", MessageType.Warning);
        }

        if (!IsHandVersionCorrect())
        {
            if (OVREditorUIElements.RenderWarningWithButton(
                    $"You must select an {GlobalVersion} hand mesh type.",
                    "Fix Mesh Type"))
            {
                FixHandMeshType();
            }
        }

        DrawDefaultInspector();
    }

    private bool IsHandVersionCorrect()
    {
        var meshType = _mesh.GetMeshType();
        if (meshType == OVRMesh.MeshType.None ||
            !_mesh.TryGetComponent<OVRHand>(out _))
        {
            return true;
        }

        return GlobalVersion switch
        {
            OVRHandSkeletonVersion.OVR => meshType.IsOVRHandMesh(),
            OVRHandSkeletonVersion.OpenXR => meshType.IsOpenXRHandMesh(),
            _ => true
        };
    }

    private void FixHandMeshType()
    {
        var meshType = _mesh.GetMeshType();
        var prop = serializedObject.FindProperty("_meshType");
        if (prop == null)
        {
            return;
        }
        if (GlobalVersion == OVRHandSkeletonVersion.OVR)
        {
            prop.intValue = meshType.IsLeft() ? (int)OVRMesh.MeshType.HandLeft :
                (int)OVRMesh.MeshType.HandRight;
        }
        else if (GlobalVersion == OVRHandSkeletonVersion.OpenXR)
        {
            prop.intValue = meshType.IsLeft() ? (int)OVRMesh.MeshType.XRHandLeft :
                (int)OVRMesh.MeshType.XRHandRight;
        }
        serializedObject.ApplyModifiedProperties();
    }
}
