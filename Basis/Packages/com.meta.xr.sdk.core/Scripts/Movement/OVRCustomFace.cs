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
using System.ComponentModel;
using Meta.XR.Util;
using UnityEngine;
using UnityEngine.Assertions;

/// <summary>
/// The component that drives blend shapes on a <see cref="SkinnedMeshRenderer"/> based on
/// the values provided by <see cref="OVRFaceExpressions"/> due to face tracking being used on the face.
/// For more information, please see [Face Tracking for Movement SDK for Unity](https://developer.oculus.com/documentation/unity/move-face-tracking/).
/// </summary>
/// <remarks>
/// See <see cref="OVRFace"/> for the base class. This specialization of <see cref="OVRFace"/> provides
/// a mapping based on an array, which is configurable from the editor. This component comes with a custom
/// editor that supports attempting to auto populate the mapping array based on string matching.
/// </remarks>
[RequireComponent(typeof(SkinnedMeshRenderer))]
[HelpURL("https://developer.oculus.com/documentation/unity/move-face-tracking/")]
[Feature(Feature.FaceTracking)]
public class OVRCustomFace : OVRFace
{
    /// <summary>
    /// The type of retargeting used to map the <see cref="OVRFaceExpressions"/> to
    /// the blend shapes that exist on a model being driven by face tracking. Use this to
    /// in case you wish to change the default retargeting behavior.
    /// </summary>
    public enum RetargetingType
    {
        OculusFace = 0,
        Custom = 1,
    }

    /// <summary>
    /// Returns the mapping from blend shape to <see cref="OVRFaceExpressions"/> value,
    /// accessed via the index of the blend shape <see cref="SkinnedMeshRenderer"/> being
    /// animated. Set this field to map a blend shape to a different <see cref="OVRFaceExpressions"/>
    /// value.
    /// </summary>
    public OVRFaceExpressions.FaceExpression[] Mappings
    {
        get => _mappings;
        set => _mappings = value;
    }

    [SerializeField]
    [Tooltip("The mapping between Face Expressions to the blend shapes available " +
             "on the shared mesh of the skinned mesh renderer")]
    internal OVRFaceExpressions.FaceExpression[] _mappings;

    [SerializeField, HideInInspector]
    internal RetargetingType retargetingType;

    protected RetargetingType RetargetingValue
    {
        get => retargetingType;
        set => retargetingType = value;
    }

    [SerializeField]
    [Tooltip("Allow duplicates when mapping blend shapes to Face Expressions")]
    internal bool _allowDuplicateMapping = true;

    protected bool AllowDuplicateMapping
    {
        get => _allowDuplicateMapping;
        set => _allowDuplicateMapping = value;
    }

    /// <inheritdoc/>
    protected override void Start()
    {
        base.Start();

        Assert.IsNotNull(_mappings);
        Assert.AreEqual(_mappings.Length, RetrieveSkinnedMeshRenderer().sharedMesh.blendShapeCount,
            "Mapping out of sync with shared mesh.");
    }

    /// <inheritdoc/>
    protected internal override OVRFaceExpressions.FaceExpression GetFaceExpression(int blendShapeIndex)
    {
        Assert.IsTrue(blendShapeIndex < _mappings.Length && blendShapeIndex >= 0);
        return _mappings[blendShapeIndex];
    }

    /// <summary>
    /// Allows the user to define their own blend shape name and face expression pair mappings.
    /// By default it will just return the Oculus version.
    /// </summary>
    /// <returns>Two arrays, each relating a blend shape name with a face expression pair.</returns>
    protected internal virtual (string[], OVRFaceExpressions.FaceExpression[])
        GetCustomBlendShapeNameAndExpressionPairs()
    {
        string[] oculusBlendShapeNames = Enum.GetNames(typeof(OVRFaceExpressions.FaceExpression));
        OVRFaceExpressions.FaceExpression[] oculusFaceExpressions =
            (OVRFaceExpressions.FaceExpression[])Enum.GetValues(typeof(OVRFaceExpressions.FaceExpression));
        return (oculusBlendShapeNames, oculusFaceExpressions);
    }
}
