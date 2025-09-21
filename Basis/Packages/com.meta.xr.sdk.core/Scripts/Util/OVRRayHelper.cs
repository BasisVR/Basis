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

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Helper script that can be placed on any game object that will cast a visible ray cast out from the object and places a cursor on a canvas if it intersects one.
/// </summary>
public class OVRRayHelper : MonoBehaviour
{
    /// <summary>
    /// Reference to the mesh renderer that is used to render the visible ray cast.
    /// </summary>
    public MeshRenderer Renderer;

    /// <summary>
    /// The default material used for the ray cast object.
    /// </summary>
    public Material NormalMaterial;

    /// <summary>
    /// The material used for the ray cast object when it's active. Being active can come from button or trigger input from controllers or pinch input from hand tracking.
    /// </summary>
    public Material PinchMaterial;

    /// <summary>
    /// The game object used to represent the cursor which is placed on the canvas where the ray intersects it.
    /// </summary>
    public GameObject Cursor;

    /// <summary>
    /// The sprite object that will be displayed on the cursor when the ray cast is active. Being active can come from button or trigger input from controllers or pinch input from hand tracking.
    /// </summary>
    public SpriteRenderer CursorFill;

    void Start()
    {
        if (Renderer != null)
        {
            _initialScale = Renderer.transform.localScale;
        }
        if (Cursor != null)
        {
            _cursorIntitialSize = Cursor.transform.localScale;
        }
    }

    /// <summary>
    /// Updates the ray cast pointer with the provided ray data. This function will update properties on the ray and cursor based
    /// on <see cref="OVRInputRayData.IsActive"/> and <see cref="OVRInputRayData.IsOverCanvas"/>.
    /// </summary>
    /// <param name="rayData"></param>
    public void UpdatePointerRay(OVRInputRayData rayData)
    {
        if (Renderer != null)
        {
            float targetLength = rayData.IsOverCanvas ? rayData.DistanceToCanvas : DefaultLength;
            Renderer.transform.localPosition = Vector3.forward * (targetLength * 0.5f + 0.05f);
            Renderer.transform.localScale = new Vector3(_initialScale.x, targetLength * 0.5f - 0.025f, _initialScale.z);
            Renderer.sharedMaterial = rayData.IsActive ? PinchMaterial : NormalMaterial;
        }

        if (Cursor != null)
        {
            Cursor.SetActive(rayData.IsOverCanvas);
            Cursor.transform.localScale = Mathf.Lerp(1f, _cursorSelectedScaleFactor, rayData.ActivationStrength) * _cursorIntitialSize;
            if (CursorFill != null)
            {
                float alpha = Mathf.Lerp(0f, 1f, rayData.ActivationStrength);
                CursorFill.color = new Color(1, 1, 1, alpha);
            }
            if (rayData.IsOverCanvas)
            {
                // Apply a slight offset here to correctly render on top of canvas elements.
                Cursor.transform.position = rayData.WorldPosition + rayData.WorldNormal * 0.05f;
                Cursor.transform.forward = rayData.WorldNormal;
            }
        }
    }

    private Vector3 _initialScale;

    /// <summary>
    /// The default length of the ray cast object.
    /// </summary>
    public float DefaultLength;

    private Vector3 _cursorIntitialSize;
    private const float _cursorSelectedScaleFactor = 0.5f;
}

public struct OVRInputRayData
{
    /// <summary>
    /// Indicates whether the ray is active/selecting something. Being active can come from button or trigger input from controllers or pinch input from hand tracking.
    /// </summary>
    public bool IsActive;

    /// <summary>
    /// A float from 0 to 1 that controls the cursor activation state. As the value moves closer to one, the cursor will shrink in size and the cursor fill will become more visible.
    /// </summary>
    public float ActivationStrength;

    /// <summary>
    /// Indicates whether the ray cast is intersecting a canvas.
    /// </summary>
    public bool IsOverCanvas;

    /// <summary>
    /// The distance from the base of the ray cast game object to the canvas it intersected with.
    /// </summary>
    public float DistanceToCanvas;

    /// <summary>
    /// The world position of the ray cast game object.
    /// </summary>
    public Vector3 WorldPosition;

    /// <summary>
    /// The normal of the ray cast game object.
    /// </summary>
    public Vector3 WorldNormal;
}
