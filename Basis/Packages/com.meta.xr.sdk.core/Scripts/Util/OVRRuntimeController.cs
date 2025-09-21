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
/// This class connects the behavior of an <see cref="OVRInput.Controller"/> to a rendered 3D model.
/// </summary>
/// <remarks>
/// This implementation is highly specialized for and tightly coupled to the 3D model with which it is used
/// in provided prefabs; usage without that model is not supported.
/// </remarks>
[HelpURL("https://developer.oculus.com/documentation/unity/unity-runtime-controller/")]
public class OVRRuntimeController : MonoBehaviour
{
    /// <summary>
    /// The <see cref="OVRInput.Controller"/> that determines whether or not to enable rendering of the
    /// controller model. This value will be passed to <see cref="OVRInput"/> when reading controller
    /// state.
    /// </summary>
    public OVRInput.Controller m_controller;

    /// <summary>
    /// Shader that will be used for the controller model. This is needed in order to correctly initialize
    /// the material for the loaded glTF asset.
    /// </summary>
    public Shader m_controllerModelShader;

    /// <summary>
    /// Flag indicating whether the rendered model should or shouldn't be animated.
    /// </summary>
    /// <remarks>
    /// If true, values
    /// from <see cref="OVRInput.Get(OVRInput.Axis1D, OVRInput.Controller)"/>,
    /// <see cref="OVRInput.Get(OVRInput.Axis2D, OVRInput.Controller)"/>, etc. will be used to set
    /// the animation state of the rendered model, causing its various buttons to move in immitation
    /// of their real-world counterparts.
    /// </remarks>
    public bool m_supportAnimation = true;

    private GameObject m_controllerObject;

    private static string leftControllerModelPath = "/model_fb/controller/left";
    private static string rightControllerModelPath = "/model_fb/controller/right";
    private string m_controllerModelPath;

    private bool m_modelSupported = false;

    private bool m_hasInputFocus = true;
    private bool m_hasInputFocusPrev = false;
    private bool m_controllerConnectedPrev = false;
    private Dictionary<OVRGLTFInputNode, OVRGLTFAnimatinonNode> m_animationNodes;

    // Start is called before the first frame update
    void Start()
    {
        if (m_controller == OVRInput.Controller.LTouch)
            m_controllerModelPath = leftControllerModelPath;
        else if (m_controller == OVRInput.Controller.RTouch)
            m_controllerModelPath = rightControllerModelPath;

        m_modelSupported = IsModelSupported(m_controllerModelPath);

        if (m_modelSupported)
        {
            StartCoroutine(UpdateControllerModel());
        }

        OVRManager.InputFocusAcquired += InputFocusAquired;
        OVRManager.InputFocusLost += InputFocusLost;
    }

    // Update is called once per frame
    void Update()
    {
        bool controllerConnected = OVRInput.IsControllerConnected(m_controller);
        if (m_hasInputFocus != m_hasInputFocusPrev || controllerConnected != m_controllerConnectedPrev)
        {
            if (m_controllerObject != null)
            {
                m_controllerObject.SetActive(controllerConnected && m_hasInputFocus);
            }

            m_hasInputFocusPrev = m_hasInputFocus;
            m_controllerConnectedPrev = controllerConnected;
        }

        if (controllerConnected)
        {
            UpdateControllerAnimation();
        }
    }

    private bool IsModelSupported(string modelPath)
    {
        string[] modelPaths = OVRPlugin.GetRenderModelPaths();
        if (modelPaths.Length == 0)
        {
            Debug.LogError("Failed to enumerate model paths from the runtime. " +
                           "Check that the render model feature is enabled in OVRManager.");
            return false;
        }

        for (int i = 0; i < modelPaths.Length; i++)
        {
            if (modelPaths[i].Equals(modelPath))
                return true;
        }

        Debug.LogError("Render model path " + modelPath + " not supported by this device.");
        return false;
    }

    private bool LoadControllerModel(string modelPath)
    {
        var modelProperties = new OVRPlugin.RenderModelProperties();
        if (OVRPlugin.GetRenderModelProperties(modelPath, ref modelProperties))
        {
            if (modelProperties.ModelKey != OVRPlugin.RENDER_MODEL_NULL_KEY)
            {
                byte[] modelData = OVRPlugin.LoadRenderModel(modelProperties.ModelKey);

                if (modelData != null)
                {
                    OVRGLTFLoader loader = new OVRGLTFLoader(modelData);
                    loader.SetModelShader(m_controllerModelShader);
                    OVRGLTFScene scene = loader.LoadGLB(m_supportAnimation);
                    m_controllerObject = scene.root;
                    m_animationNodes = scene.animationNodes;

                    if (m_controllerObject != null)
                    {
                        m_controllerObject.transform.SetParent(transform, false);

                        // Apply the OpenXR grip pose offset so runtime controller models are in the right position
                        m_controllerObject.transform.parent.localPosition = new Vector3(0.0f, -0.03f, -0.04f);
                        m_controllerObject.transform.parent.localRotation =
                            Quaternion.AngleAxis(-60.0f, new Vector3(1.0f, 0.0f, 0.0f));
                        return true;
                    }
                }
            }

            Debug.LogError("Retrived a null model key of " + modelPath);
        }

        Debug.LogError("Failed to load controller model of " + modelPath);
        return false;
    }

    private IEnumerator UpdateControllerModel()
    {
        while (true)
        {
            bool controllerConnected = OVRInput.IsControllerConnected(m_controller);
            if (m_controllerObject == null && controllerConnected)
            {
                LoadControllerModel(m_controllerModelPath);
            }

            yield return new WaitForSeconds(.5f);
        }
    }

    private void UpdateControllerAnimation()
    {
        if (m_animationNodes == null)
        {
            return;
        }

        if (m_animationNodes.ContainsKey(OVRGLTFInputNode.Button_A_X))
            m_animationNodes[OVRGLTFInputNode.Button_A_X].UpdatePose(
                OVRInput.Get(m_controller == OVRInput.Controller.LTouch ? OVRInput.RawButton.X : OVRInput.RawButton.A));

        if (m_animationNodes.ContainsKey(OVRGLTFInputNode.Button_B_Y))
            m_animationNodes[OVRGLTFInputNode.Button_B_Y].UpdatePose(
                OVRInput.Get(m_controller == OVRInput.Controller.LTouch ? OVRInput.RawButton.Y : OVRInput.RawButton.B));

        if (m_animationNodes.ContainsKey(OVRGLTFInputNode.Button_Oculus_Menu))
            m_animationNodes[OVRGLTFInputNode.Button_Oculus_Menu].UpdatePose(
                OVRInput.Get(OVRInput.RawButton.Start));

        if (m_animationNodes.ContainsKey(OVRGLTFInputNode.Trigger_Grip))
            m_animationNodes[OVRGLTFInputNode.Trigger_Grip].UpdatePose(
                OVRInput.Get(m_controller == OVRInput.Controller.LTouch
                    ? OVRInput.RawAxis1D.LHandTrigger
                    : OVRInput.RawAxis1D.RHandTrigger));

        if (m_animationNodes.ContainsKey(OVRGLTFInputNode.Trigger_Front))
            m_animationNodes[OVRGLTFInputNode.Trigger_Front].UpdatePose(
                OVRInput.Get(m_controller == OVRInput.Controller.LTouch
                    ? OVRInput.RawAxis1D.LIndexTrigger
                    : OVRInput.RawAxis1D.RIndexTrigger));

        if (m_animationNodes.ContainsKey(OVRGLTFInputNode.ThumbStick))
            m_animationNodes[OVRGLTFInputNode.ThumbStick].UpdatePose(
                OVRInput.Get(m_controller == OVRInput.Controller.LTouch
                    ? OVRInput.RawAxis2D.LThumbstick
                    : OVRInput.RawAxis2D.RThumbstick));
    }

    /// <summary>
    /// Informs this instance that the associated controller (designated by <see cref="m_controller"/>) is
    /// considered by the app to have input focus, which affects how the controller is displayed.
    /// </summary>
    public void InputFocusAquired()
    {
        m_hasInputFocus = true;
    }

    /// <summary>
    /// Informs this instance that the associated controller (designated by <see cref="m_controller"/>) is
    /// considered by the app to not have input focus, which affects how the controller is displayed.
    /// </summary>
    public void InputFocusLost()
    {
        m_hasInputFocus = false;
    }
}
