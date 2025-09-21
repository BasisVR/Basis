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

using Meta.XR.Util;
using UnityEngine;
using UnityEngine.Assertions;

/// <summary>
/// Drives blend shapes on a <c>SkinnedMeshRenderer</c> based on the face tracking data provided by
/// <see cref="OVRFaceExpressions"/>. This is done by taking in face tracking weights (an array of weights
/// based on the Facial Action Coding System (FACS)), and applying it to a <see cref="SkinnedMeshRenderer"/> by updating
/// the blend shapes on the <see cref="SkinnedMeshRenderer"/> every frame. As the values from the face tracking data may
/// differ in range from the blend shape range on the skinned mesh renderer, a modification of values is required by
/// setting the <see cref="OVRFace.BlendShapeStrengthMultiplier"/> which will multiply the input before setting the
/// blend shape values on the <see cref="SkinnedMeshRenderer"/>.
/// For more information, please see [Face Tracking for Movement SDK for Unity](https://developer.oculus.com/documentation/unity/move-face-tracking/).
/// </summary>
/// <remarks>
/// Intended to be used as a base type that is inherited from in order to provide mapping logic from blend shape indices.
/// The mapping of <see cref="OVRFaceExpressions.FaceExpression"/> to blend shapes is accomplished by overriding <see cref="OVRFace.GetFaceExpression(int)"/>.
/// Needs to be linked to a <see cref="OVRFaceExpressions"/> component to fetch tracking data from.
/// </remarks>
[RequireComponent(typeof(SkinnedMeshRenderer))]
[HelpURL("https://developer.oculus.com/documentation/unity/move-face-tracking/")]
[Feature(Feature.FaceTracking)]
public class OVRFace : MonoBehaviour
{
    /// <summary>
    /// Interface to define a custom blendshape weights provider for
    /// a skinned mesh. Implement this interface if you wish to store
    /// the blendshape weights in a separate components for processing
    /// via <see cref="IMeshWeightsProvider.UpdateWeights(OVRFaceExpressions)"/>
    /// and later retrieval via <see cref="GetWeightValue(int, out float)"/>.
    /// </summary>
    public interface IMeshWeightsProvider
    {
        /// <summary>
        /// Updates the weights value, passed in from a
        /// <see cref="OVRFaceExpressions"/> instance. You may use to possibly
        /// store incoming weights for later modification.
        /// </summary>
        /// <param name="faceExpressions"><see cref="OVRFaceExpressions"/> instance.</param>
        void UpdateWeights(OVRFaceExpressions faceExpressions);

        /// <summary>
        /// Obtains a weight value for a given blendshape index; return a boolean
        /// based on if the call was successful or not. Use this to obtain the weight value
        /// from a provider after <see cref="IMeshWeightsProvider.UpdateWeights(OVRFaceExpressions)"/>
        /// was called.
        /// </summary>
        /// <param name="blendshapeIndex">Blendshape index on skinned mesh renderer.</param>
        /// <param name="weightValue">Output weight value.</param>
        /// <returns>True if successful, false if not.</returns>
        bool GetWeightValue(int blendshapeIndex, out float weightValue);
    }

    /// <summary>
    /// The reference to the <see cref="OVRFaceExpressions"/> component, which contains the face tracking
    /// weights to be applied to the blend shapes on the skinned mesh renderer.
    /// </summary>
    public OVRFaceExpressions FaceExpressions
    {
        get => _faceExpressions;
        set => _faceExpressions = value;
    }

    /// <summary>
    /// The multiplier applied on the <see cref="OVRFaceExpressions"/> tracking weights as they are mapped
    /// to blend shapes on the <see cref="SkinnedMeshRenderer"/>. This is needed as the blend shape range in Unity
    /// can vary based on the model (i.e. 0-100), while the weights from face tracking range from 0-1.
    /// </summary>
    public float BlendShapeStrengthMultiplier
    {
        get => _blendShapeStrengthMultiplier;
        set => _blendShapeStrengthMultiplier = value;
    }

    /// <summary>
    /// The <see cref="SkinnedMeshRenderer"/> which contains the blend shapes that will be updated with the
    /// face tracking weights from <see cref="OVRFaceExpressions"/>.
    /// </summary>
    protected SkinnedMeshRenderer SkinnedMesh => _skinnedMeshRenderer;

    internal SkinnedMeshRenderer RetrieveSkinnedMeshRenderer() => GetComponent<SkinnedMeshRenderer>();

    internal OVRFaceExpressions SearchFaceExpressions() => gameObject.GetComponentInParent<OVRFaceExpressions>();

    [SerializeField]
    [Tooltip("The OVRFaceExpressions Component to fetch the Face Tracking weights from that are to be applied")]
    protected internal OVRFaceExpressions _faceExpressions;

    [SerializeField]
    [Tooltip("A multiplier to the weights read from the OVRFaceExpressions to exaggerate facial expressions")]
    protected internal float _blendShapeStrengthMultiplier = 100.0f;

    [SerializeField]
    [Tooltip("Optional component that contains IMeshWeightsProvider.")]
    protected internal GameObject _meshWeightsProviderObject;

    private SkinnedMeshRenderer _skinnedMeshRenderer;
    private IMeshWeightsProvider _meshWeightsProvider;

    protected virtual void Awake()
    {
        if (_faceExpressions == null)
        {
            _faceExpressions = SearchFaceExpressions();
            Debug.Log($"Found OVRFaceExpression reference in {_faceExpressions.name} due to unassigned field.");
        }

        if (_meshWeightsProviderObject != null)
        {
            _meshWeightsProvider = _meshWeightsProviderObject.GetComponent<IMeshWeightsProvider>();
        }
    }

    private void OnEnable()
    {
        var manager = FindAnyObjectByType<OVRManager>();
        if (manager != null && manager.SimultaneousHandsAndControllersEnabled)
        {
            Debug.LogWarning("Please note that currently, face tracking and simultaneous hands and controllers cannot be enabled at the same time on Quest 2", this);
            return;
        }
    }

    protected virtual void Start()
    {
        Assert.IsNotNull(_faceExpressions, "OVRFace requires OVRFaceExpressions to function.");

        _skinnedMeshRenderer = GetComponent<SkinnedMeshRenderer>();
        Assert.IsNotNull(_skinnedMeshRenderer);
        Assert.IsNotNull(_skinnedMeshRenderer.sharedMesh);

        if (_meshWeightsProviderObject != null)
        {
            Assert.IsNotNull(_meshWeightsProvider, "Mesh weights provider object must have IMeshWeightsProvider component.");
        }
    }

    protected virtual void Update()
    {
        if (!_faceExpressions.FaceTrackingEnabled || !_faceExpressions.enabled)
        {
            return;
        }

        if (_meshWeightsProvider != null)
        {
            _meshWeightsProvider.UpdateWeights(_faceExpressions);
        }

        if (_faceExpressions.ValidExpressions)
        {
            int numBlendshapes = _skinnedMeshRenderer.sharedMesh.blendShapeCount;

            for (int blendShapeIndex = 0; blendShapeIndex < numBlendshapes; ++blendShapeIndex)
            {
                if (GetWeightValue(blendShapeIndex, out var currentWeight))
                {
                    _skinnedMeshRenderer.SetBlendShapeWeight(blendShapeIndex, Mathf.Clamp(currentWeight, 0f, 100f));
                }
            }
        }
    }

    /// <summary>
    /// Fetches the <see cref="OVRFaceExpressions.FaceExpression"/> for a given blend shape index on the shared mesh of the <c>SkinnedMeshRenderer</c> on the same component
    /// </summary>
    /// <remarks>
    /// Override this function to provide the mapping between blend shapes and face expressions
    /// </remarks>
    /// <param name="blendShapeIndex">The index of the blend shape, will be in-between 0 and the number of blend shapes on the shared mesh.</param>
    /// <returns>Returns the <see cref="OVRFaceExpressions.FaceExpression"/> to drive the bland shape identified by <paramref name="blendShapeIndex"/>.</returns>
    protected internal virtual OVRFaceExpressions.FaceExpression GetFaceExpression(int blendShapeIndex) =>
        OVRFaceExpressions.FaceExpression.Invalid;

    /// <summary>
    /// Calculates the value for the specific target blend shape of the shared mesh <c>SkinnedMeshRenderer</c>
    /// </summary>
    /// <param name="blendShapeIndex">Index of the blend shape of the shared mesh <c>SkinnedMeshRenderer</c></param>
    /// <param name="weightValue">Calculated value</param>
    /// <returns>true if value was calculated, false if no value available for that blend shape</returns>
    protected internal virtual bool GetWeightValue(int blendShapeIndex, out float weightValue)
    {
        if (_meshWeightsProvider != null)
        {
            bool returnValue = _meshWeightsProvider.GetWeightValue(blendShapeIndex, out weightValue);
            weightValue *= _blendShapeStrengthMultiplier;
            return returnValue;
        }

        OVRFaceExpressions.FaceExpression blendShapeToFaceExpression = GetFaceExpression(blendShapeIndex);
        if (blendShapeToFaceExpression >= OVRFaceExpressions.FaceExpression.Max || blendShapeToFaceExpression < 0)
        {
            weightValue = 0;
            return false;
        }

        weightValue = _faceExpressions[blendShapeToFaceExpression] * _blendShapeStrengthMultiplier;
        return true;
    }
}
