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
using System;
using System.Linq;
using UnityEngine;
using OVRSimpleJSON;

public enum OVRGLTFInputNode
{
    None,
    Button_A_X,
    Button_B_Y,
    Button_Oculus_Menu,
    Trigger_Grip,
    Trigger_Front,
    ThumbStick
};

/// <summary>
/// Helper class specifically used for animating buttons, triggers, and thumbsticks on GLTF (GL Transmission Format) controller models loaded from the Meta Quest Runtime.
/// Controller input is passed in from <see cref="OVRRuntimeController"> which is then used to update the poses of each coresponding button, trigger, or thumbstick on the controller models.
/// <remarks>
/// These animation functions are used specifically for the [Controller Models](https://developer.oculus.com/documentation/unity/unity-runtime-controller/) loaded from the Meta Quest Runtime. We do not recommended using these functions any other GLTF models.
/// </remarks>
/// </summary>
public class OVRGLTFAnimatinonNode
{
    private OVRGLTFInputNode m_intputNodeType;
    private JSONNode m_jsonData;
    private GameObject m_gameObj;
    private InputNodeState m_inputNodeState = new InputNodeState();
    private OVRGLTFAnimationNodeMorphTargetHandler m_morphTargetHandler;

    private List<Vector3> m_translations = new List<Vector3>();
    private List<Quaternion> m_rotations = new List<Quaternion>();
    private List<Vector3> m_scales = new List<Vector3>();
    private List<float> m_weights = new List<float>();
    private int m_additiveWeightIndex = -1;

    private static Dictionary<OVRGLTFInputNode, int> InputNodeKeyFrames = new Dictionary<OVRGLTFInputNode, int>
    {
        { OVRGLTFInputNode.Button_A_X, 5 },
        { OVRGLTFInputNode.Button_B_Y, 8 },
        { OVRGLTFInputNode.Button_Oculus_Menu, 24 },
        { OVRGLTFInputNode.Trigger_Grip, 21 },
        { OVRGLTFInputNode.Trigger_Front, 16 },
        { OVRGLTFInputNode.ThumbStick, 0 }
    };

    private static List<int> ThumbStickKeyFrames = new List<int> { 29, 39, 34, 40, 31, 36, 32, 37 };

    private static Vector2[] CardDirections = new[]
    {
        new Vector2(0.0f, 0.0f), // none
        new Vector2(0.0f, 1.0f), // N
        new Vector2(1.0f, 1.0f), // NE
        new Vector2(1.0f, 0.0f), // E
        new Vector2(1.0f, -1.0f), // SE
        new Vector2(0.0f, -1.0f), // S
        new Vector2(-1.0f, -1.0f), // SW
        new Vector2(-1.0f, 0.0f), // W
        new Vector2(-1.0f, 1.0f) // NW
    };

    private enum ThumbstickDirection
    {
        None,
        North,
        NorthEast,
        East,
        SouthEast,
        South,
        SouthWest,
        West,
        NorthWest,
    };

    private enum OVRGLTFTransformType
    {
        None,
        Translation,
        Rotation,
        Scale,
        Weights
    };

    private enum OVRInterpolationType
    {
        None,
        LINEAR,
        STEP,
        CUBICSPLINE
    };

    private struct InputNodeState
    {
        public bool down;
        public float t;
        public Vector2 vecT;
    }

    /// <summary>
    /// Creates a new OVRGLTFAnimationNode object which is used to animate a controller button, trigger, or joystick.
    /// </summary>
    /// <param name="inputNodeType">Input node type used to specify if this node is a button, trigger, or joystick.</param>
    /// <param name="gameObj">Game object associated with the node being animated.</param>
    /// <param name="morphTargetHandler">The morpph target data that is required for animating this node.</param>
    public OVRGLTFAnimatinonNode(OVRGLTFInputNode inputNodeType,
        GameObject gameObj, OVRGLTFAnimationNodeMorphTargetHandler morphTargetHandler)
    {
        m_intputNodeType = inputNodeType;
        m_gameObj = gameObj;
        m_morphTargetHandler = morphTargetHandler;
        m_translations.Add(CloneVector3(m_gameObj.transform.localPosition));
        m_rotations.Add(CloneQuaternion(m_gameObj.transform.localRotation));
        m_scales.Add(CloneVector3(m_gameObj.transform.localScale));
    }

    /// <summary>
    /// Adds an animation channel to this animation node. Check the GLTF [animation channel](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#_animation_channels) section in the GLTF 2.0 specification page for more information.
    /// </summary>
    /// <param name="channel">The JSON node containing data for the animation channel.</param>
    /// <param name="samplers">The JSON node containing GLTF samplers.</param>
    /// <param name="dataAccessor">The OVRGLTFAccessor object for retriving animation data.</param>
    public void AddChannel(JSONNode channel, JSONNode samplers, OVRGLTFAccessor dataAccessor)
    {
        int samplerId = channel["sampler"].AsInt;
        var target = channel["target"];
        var extras = channel["extras"];
        int nodeId = target["node"].AsInt;
        OVRGLTFTransformType transformType = GetTransformType(target["path"].Value);
        ProcessAnimationSampler(samplers[samplerId], nodeId, transformType, extras, dataAccessor);
        return;
    }

    /// <summary>
    /// Updates the pose of a controller button object based on button down state.
    /// </summary>
    /// <param name="down">If the button is being pressed.</param>
    public void UpdatePose(bool down)
    {
        if (m_inputNodeState.down == down)
            return;
        m_inputNodeState.down = down;

        if (m_translations.Count > 1)
            m_gameObj.transform.localPosition = (down ? m_translations[1] : m_translations[0]);
        if (m_rotations.Count > 1)
            m_gameObj.transform.localRotation = (down ? m_rotations[1] : m_rotations[0]);
        if (m_scales.Count > 1)
            SetScale((down) ? m_scales[1] : m_scales[0]);
    }

    /// <summary>
    /// Updates the pose of a object based on a float value.
    /// </summary>
    /// <param name="t">The floating point value ranging from 0.0f to 1.0f.</param>
    /// <param name="applyDeadZone">If a dead zone should be applied to the animation. The object will not move until the dead zone threshold is exceeded.</param>
    public void UpdatePose(float t, bool applyDeadZone = true)
    {
        if (applyDeadZone)
        {
            const float deadZone = 0.05f;
            if (Math.Abs(m_inputNodeState.t - t) < deadZone)
                return;
        }

        m_inputNodeState.t = t;

        if (m_translations.Count > 1)
            m_gameObj.transform.localPosition = Vector3.Lerp(m_translations[0], m_translations[1], t);
        if (m_rotations.Count > 1)
            m_gameObj.transform.localRotation = Quaternion.Lerp(m_rotations[0], m_rotations[1], t);
        if (m_scales.Count > 1)
            SetScale(Vector3.Lerp(m_scales[0], m_scales[1], t));

        if (m_morphTargetHandler != null && m_weights.Count > 0)
        {
            // TODO: t assumes an animation channel input of [0,1].
            // Changes will be necessary if a model has animations with more keyframes for different timescales
            var stride = m_morphTargetHandler.Weights.Length;
            if (m_additiveWeightIndex == -1)
            {
                for (int i = 0; i < stride; i++)
                {
                    m_morphTargetHandler.Weights[i] = Mathf.Lerp(m_weights[i], m_weights[i + stride], t);
                }
            }
            else
            {
                m_morphTargetHandler.Weights[m_additiveWeightIndex] += Mathf.Lerp(m_weights[m_additiveWeightIndex],
                    m_weights[m_additiveWeightIndex + stride], t);
            }

            // mark the geo as dirty
            m_morphTargetHandler.MarkModified();
        }
    }

    /// <summary>
    /// Updates the pose of a controller joystick object based on the joystick's x and y position.
    /// </summary>
    /// <param name="joystick">The floating point value of the joystick's x and y position.</param>
    public void UpdatePose(Vector2 joystick)
    {
        const float deadZone = 0.05f;
        if (Math.Abs((m_inputNodeState.vecT - joystick).magnitude) < deadZone)
            return;
        m_inputNodeState.vecT.x = joystick.x;
        m_inputNodeState.vecT.y = joystick.y;

        if (m_rotations.Count != (int)ThumbstickDirection.NorthWest + 1)
        {
            Debug.LogError("Wrong joystick animation data.");
            return;
        }

        Tuple<ThumbstickDirection, ThumbstickDirection> dir = GetCardinalThumbsticks(joystick);
        Vector2 weights = GetCardinalWeights(joystick, dir);
        Quaternion a = CloneQuaternion(m_rotations[0]);
        for (int i = 0; i < 2; i++)
        {
            float t = weights[i];
            if (t != 0)
            {
                int poseIndex = (i == 0 ? (int)dir.Item1 : (int)dir.Item2) - (int)ThumbstickDirection.North;
                Quaternion b = m_rotations[poseIndex + 1];
                a = Quaternion.Slerp(a, b, t);
            }
        }

        m_gameObj.transform.localRotation = a;
        if (m_translations.Count > 1 || m_scales.Count > 1)
            Debug.LogWarning("Unsupported pose.");
    }

    // We will blend the 2 closest animations, this picks which 2.
    private Tuple<ThumbstickDirection, ThumbstickDirection> GetCardinalThumbsticks(Vector2 joystick)
    {
        const float deadZone = 0.005f;
        if (joystick.magnitude < deadZone)
        {
            return new Tuple<ThumbstickDirection, ThumbstickDirection>(ThumbstickDirection.None,
                ThumbstickDirection.None);
        }

        // East half
        if (joystick.x >= 0.0f)
        {
            // Northeast quadrant
            if (joystick.y >= 0.0f)
            {
                // North-Northeast
                if (joystick.y > joystick.x)
                {
                    return new Tuple<ThumbstickDirection, ThumbstickDirection>(ThumbstickDirection.North,
                        ThumbstickDirection.NorthEast);
                }
                // East-Northeast
                else
                {
                    return new Tuple<ThumbstickDirection, ThumbstickDirection>(ThumbstickDirection.NorthEast,
                        ThumbstickDirection.East);
                }
            }
            // Southeast quadrant
            else
            {
                // East-Southeast
                if (joystick.x > -joystick.y)
                {
                    return new Tuple<ThumbstickDirection, ThumbstickDirection>(ThumbstickDirection.East,
                        ThumbstickDirection.SouthEast);
                }
                // South-southeast
                else
                {
                    return new Tuple<ThumbstickDirection, ThumbstickDirection>(ThumbstickDirection.SouthEast,
                        ThumbstickDirection.South);
                }
            }
        }
        // West half
        else
        {
            // Southwest quadrant
            if (joystick.y < 0.0f)
            {
                // South-Southwest
                if (joystick.x > joystick.y)
                {
                    return new Tuple<ThumbstickDirection, ThumbstickDirection>(ThumbstickDirection.South,
                        ThumbstickDirection.SouthWest);
                }
                // West-Southwest
                else
                {
                    return new Tuple<ThumbstickDirection, ThumbstickDirection>(ThumbstickDirection.SouthWest,
                        ThumbstickDirection.West);
                }
            }
            // Northwest quadrant
            else
            {
                // West-Northwest
                if (-joystick.x > joystick.y)
                {
                    return new Tuple<ThumbstickDirection, ThumbstickDirection>(ThumbstickDirection.West,
                        ThumbstickDirection.NorthWest);
                }
                // North-Northwest
                else
                {
                    return new Tuple<ThumbstickDirection, ThumbstickDirection>(ThumbstickDirection.NorthWest,
                        ThumbstickDirection.North);
                }
            }
        }
    }

    // This figures out how much of each of the 2 animations to blend, based on where in between the 2
    // cardinal directions the user is actually pushing the thumbstick, and how far they are pushing
    // the thumbstick("animations" themselves are a fixed pose for a "maximum" push.
    private Vector2 GetCardinalWeights(Vector2 joystick, Tuple<ThumbstickDirection, ThumbstickDirection> cardinals)
    {
        // follows ThumbstickDirection, can use ThumbstickDirection to directly index into this

        if (cardinals.Item1 == ThumbstickDirection.None || cardinals.Item2 == ThumbstickDirection.None)
        {
            return new Vector2(0.0f, 0.0f);
        }

        // Compute the barycentric coordinates of the joystick position in the triangle formed by the 2
        // cardinal directions
        Vector2 triangleEdge1 = CardDirections[(int)(cardinals.Item1)];
        Vector2 triangleEdge2 = CardDirections[(int)(cardinals.Item2)];
        float dot11 = Vector2.Dot(triangleEdge1, triangleEdge1);
        float dot12 = Vector2.Dot(triangleEdge1, triangleEdge2);
        float dot1j = Vector2.Dot(triangleEdge1, joystick);
        float dot22 = Vector2.Dot(triangleEdge2, triangleEdge2);
        float dot2j = Vector2.Dot(triangleEdge2, joystick);

        float invDenom = 1.0f / (dot11 * dot22 - dot12 * dot12);
        float weight1 = (dot22 * dot1j - dot12 * dot2j) * invDenom;
        float weight2 = (dot11 * dot2j - dot12 * dot1j) * invDenom;

        return new Vector2(weight1, weight2);
    }

    private void ProcessAnimationSampler(JSONNode samplerNode, int nodeId, OVRGLTFTransformType transformType,
        JSONNode extras, OVRGLTFAccessor _dataAccessor)
    {
        int outputId = samplerNode["output"].AsInt;
        OVRInterpolationType interpolationId = ToOVRInterpolationType(samplerNode["interpolation"].Value);
        if (interpolationId == OVRInterpolationType.None)
        {
            Debug.LogError("Unsupported interpolation type: " + samplerNode["interpolation"].Value);
            return;
        }

        int inputId = samplerNode["input"].AsInt;
        _dataAccessor.Seek(inputId);
        float[] inputFloats = _dataAccessor.ReadFloat();
        // implementation assumes inputFloats = [0, 1]
        if (inputFloats.Length > 2 && m_intputNodeType == OVRGLTFInputNode.None)
        {
            Debug.LogWarning("Unsupported keyframe count");
        }
        // Changes will be necessary if a model has animations with more keyframes for different timescales

        _dataAccessor.Seek(outputId);
        switch (transformType)
        {
            case OVRGLTFTransformType.Translation:
                CopyData(ref m_translations, _dataAccessor.ReadVector3(OVRGLTFLoader.GLTFToUnitySpace));
                break;
            case OVRGLTFTransformType.Rotation:
                CopyData(ref m_rotations, _dataAccessor.ReadQuaterion(OVRGLTFLoader.GLTFToUnitySpace_Rotation));
                break;
            case OVRGLTFTransformType.Scale:
                CopyData(ref m_scales, _dataAccessor.ReadVector3(Vector3.one));
                break;
            case OVRGLTFTransformType.Weights:
                CopyData(ref m_weights, _dataAccessor.ReadFloat());
                if (extras != null && extras["additiveWeightIndex"] != null)
                {
                    m_additiveWeightIndex = extras["additiveWeightIndex"].AsInt;
                }

                if (m_morphTargetHandler != null)
                {
                    m_morphTargetHandler.Weights = new float[m_weights.Count / inputFloats.Length];
                }

                break;
            default:
                Debug.LogError("Unsupported transform type: " + transformType.ToString());
                break;
        }
    }

    private OVRGLTFTransformType GetTransformType(string transform)
    {
        switch (transform)
        {
            case "translation":
                return OVRGLTFTransformType.Translation;
            case "rotation":
                return OVRGLTFTransformType.Rotation;
            case "scale":
                return OVRGLTFTransformType.Scale;
            case "weights":
                return OVRGLTFTransformType.Weights;
            case "none":
                return OVRGLTFTransformType.None;
            default:
                Debug.LogError("Unsupported transform type: " + transform);
                return OVRGLTFTransformType.None;
        }
    }

    private OVRInterpolationType ToOVRInterpolationType(string interpolationType)
    {
        switch (interpolationType)
        {
            case "LINEAR":
                return OVRInterpolationType.LINEAR;
            case "STEP":
                Debug.LogError("Unsupported interpolationType type." + interpolationType);
                return OVRInterpolationType.STEP;
            case "CUBICSPLINE":
                Debug.LogError("Unsupported interpolationType type." + interpolationType);
                return OVRInterpolationType.CUBICSPLINE;
            default:
                Debug.LogError("Unsupported interpolationType type." + interpolationType);
                return OVRInterpolationType.None;
        }
    }

    private void CopyData<T>(ref List<T> dest, T[] src)
    {
        if (m_intputNodeType == OVRGLTFInputNode.None)
        {
            dest = src.ToList();
        }
        else if (m_intputNodeType == OVRGLTFInputNode.ThumbStick)
        {
            foreach (int idx in ThumbStickKeyFrames)
            {
                if (idx < src.Length)
                    dest.Add(src[idx]);
            }
        }
        else
        {
            int idx = InputNodeKeyFrames[m_intputNodeType];
            if (idx < src.Length)
                dest.Add(src[idx]);
        }
    }

    private Vector3 CloneVector3(Vector3 v)
    {
        return new Vector3(v.x, v.y, v.z);
    }

    private Quaternion CloneQuaternion(Quaternion q)
    {
        return new Quaternion(q.x, q.y, q.z, q.w);
    }

    private void SetScale(Vector3 scale)
    {
        m_gameObj.transform.localScale = scale;
        // disable any zero-scale gameobjects to reduce drawcalls
        m_gameObj.SetActive(m_gameObj.transform.localScale != Vector3.zero);
    }
}
