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


using Meta.XR.ImmersiveDebugger;
using UnityEngine;

/// <summary>
/// Used for the UIEditor scene that pre-populates some debug options to quickly iterate on UI without needing headset
/// </summary>
internal class UIEditorSetup : MonoBehaviour
{
    [DebugMember]
    public float Float = 0.5f;

    [DebugMember]
    public bool Bool = true;

    [DebugMember(Tweakable = true, Min = 0.0f, Max = 1.0f)]
    public float TweakableFloat = 0.5f;

    [DebugMember(DebugColor.Red, GizmoType = DebugGizmoType.Point)]
    public Vector3 Position = Vector3.one;

    [DebugMember]
    public void Method() { }
}

