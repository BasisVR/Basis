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
using UnityEngine;

/// <summary>
/// Helper class that updates mesh verticies and UVs based on provided weights using the mesh's morph targets. This is required when animating the GLTF model so that all
/// mesh attributes are updated correctly when the mesh moves.
/// </summary>
public class OVRGLTFAnimationNodeMorphTargetHandler
{
    private OVRMeshData _meshData;
    public float[] Weights;

    private bool _modified = false;

    private OVRMeshAttributes _meshModifiableData;

    public OVRGLTFAnimationNodeMorphTargetHandler(OVRMeshData meshData)
    {
        _meshData = meshData;

        _meshModifiableData.vertices = new Vector3[_meshData.baseAttributes.vertices.Length];
        _meshModifiableData.texcoords = new Vector2[_meshData.baseAttributes.texcoords.Length];
    }

    /// <summary>
    /// Updates the mesh vertices and UVs when the weights are modified. This should be called during an animation update to ensure the mesh is updated correctly.
    /// </summary>
    public void Update()
    {
        if (!_modified)
        {
            return;
        }

        // reset _meshModifiableData to the base;
        Array.Copy(_meshData.baseAttributes.vertices, _meshModifiableData.vertices,
            _meshData.baseAttributes.vertices.Length);
        Array.Copy(_meshData.baseAttributes.texcoords, _meshModifiableData.texcoords,
            _meshData.baseAttributes.texcoords.Length);

        var updatedVertices = false;
        var updatedTexcoords = false;

        for (var i = 0; i < _meshData.morphTargets.Length; i++)
        {
            if (_meshData.morphTargets[i].vertices != null)
            {
                updatedVertices = true;
                var vi = i / 2;
                if (i % 2 == 0)
                {
                    var morphedData = _meshData.morphTargets[i].vertices[vi].x *
                                      Weights[i];
                    _meshModifiableData.vertices[vi].x += morphedData;
                }
                else
                {
                    var morphedData = _meshData.morphTargets[i].vertices[vi].y *
                                      Weights[i];
                    _meshModifiableData.vertices[vi].y += morphedData;
                }
            }

            if (_meshData.morphTargets[i].texcoords != null)
            {
                updatedTexcoords = true;
                var ti = i - 8;
                var tii = ti / 2;
                if (i % 2 == 0)
                {
                    _meshModifiableData.texcoords[tii].x += _meshData.morphTargets[i].texcoords[tii].x *
                                                      Weights[i];
                }
                else
                {
                    _meshModifiableData.texcoords[tii].y += _meshData.morphTargets[i].texcoords[tii].y *
                                                      Weights[i];
                }
            }
        }

        if (updatedVertices)
        {
            _meshData.mesh.vertices = _meshModifiableData.vertices;
            _meshData.mesh.RecalculateBounds();
        }
        if (updatedTexcoords)
        {
            _meshData.mesh.uv = _meshModifiableData.texcoords;
        }
        if (updatedVertices || updatedTexcoords)
        {
            _meshData.mesh.MarkModified();
        }
        _modified = false;
    }

    /// <summary>
    /// Marks the mesh data as modified so that <see cref="Update"/> will process the new morph target weights.
    /// </summary>
    public void MarkModified()
    {
        _modified = true;
    }
}
