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

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>
/// Helper script for managing the rendering of a controller. This script takes into account the
/// <see cref="ControllerType"/> of the associated physical controller, initializes the correct assets,
/// manages animation, and hides the visuals when the controller is disconnected.
///
/// You can assign new prefabs to the model fields to control which prefab represents the given controller
/// type, though replacement prefabs must be very carefully constructed to fulfill all requirements.
/// </summary>
[HelpURL("https://developer.oculus.com/documentation/unity/controller-animations/")]
public class OVRControllerHelper : MonoBehaviour,
    OVRInputModule.InputSource
{
    /// <summary>
    /// The root GameObject that represents the prefab that controls the Oculus Touch for Quest And RiftS Controller model (Left).
    /// </summary>
    /// <remarks>
    /// You can assign this field from the Unity Editor to control which prefab is used to represent this
    /// controller type.
    /// </remarks>
    public GameObject m_modelOculusTouchQuestAndRiftSLeftController;

    /// <summary>
    /// The root GameObject that represents the prefab for the Oculus Touch for Quest And RiftS Controller model (Right).
    /// </summary>
    public GameObject m_modelOculusTouchQuestAndRiftSRightController;

    /// <summary>
    /// The root GameObject that represents the prefab that controls the Oculus Touch for Rift Controller model (Left).
    /// </summary>
    /// <remarks>
    /// This field can be assigned from the Unity Editor to control which prefab is used to represent this
    /// controller type.
    /// </remarks>
    public GameObject m_modelOculusTouchRiftLeftController;

    /// <summary>
    /// The root GameObject that represents the prefab that controls the Oculus Touch for Rift Controller model (Right).
    /// </summary>
    /// <remarks>
    /// This field can be assigned from the Unity Editor to control which prefab is used to represent this
    /// controller type.
    /// </remarks>
    public GameObject m_modelOculusTouchRiftRightController;

    /// <summary>
    /// The root GameObject that represents the prefab for the Oculus Touch for Quest 2 Controller model (Left).
    /// </summary>
    public GameObject m_modelOculusTouchQuest2LeftController;

    /// <summary>
    /// The root GameObject that represents the Oculus Touch for Quest 2 Controller model (Right).
    /// </summary>
    public GameObject m_modelOculusTouchQuest2RightController;

    /// <summary>
    /// The root GameObject that represents the prefab that controls the Meta Touch Pro Controller model (Left).
    /// </summary>

    public GameObject m_modelMetaTouchProLeftController;

    /// <summary>
    /// The root GameObject that represents the prefab for the Meta Touch Pro Controller model (Right).
    /// </summary>
    public GameObject m_modelMetaTouchProRightController;

    /// <summary>
    /// The root GameObject that represents the prefab for Meta Quest Plus Controller model (Left).
    /// </summary>

    public GameObject m_modelMetaTouchPlusLeftController;

    /// <summary>
    /// The root GameObject that represents the Meta Quest Plus Controller model (Right).
    /// </summary>
    public GameObject m_modelMetaTouchPlusRightController;

    /// <summary>
    /// The <see cref="OVRInput.Controller"/> that should be reflected by the rendered assets.
    /// OVRControllerHelper queries <see cref="OVRInput"/> and the underlying system for the appropriate
    /// controller data depending on the controller specified in this setting.
    /// </summary>
    public OVRInput.Controller m_controller;

    /// <summary>
    /// Determines if the controller should be hidden based on held state. But default, this value is set
    /// to <see cref="OVRInput.InputDeviceShowState.ControllerInHandOrNoHand"/>.
    /// </summary>
    public OVRInput.InputDeviceShowState m_showState = OVRInput.InputDeviceShowState.ControllerInHandOrNoHand;

    /// <summary>
    /// If controller-driven hand poses is on, and the mode is Natural, controllers will be hidden unless
    /// this is true; in other words, enabling this setting will cause the controller visuals to be rendered
    /// while controller-driven hands are enabled in Natural mode.
    /// </summary>
    public bool showWhenHandsArePoweredByNaturalControllerPoses = false;

    /// <summary>
    /// The animator component that contains the controller animation controller for animating buttons and
    /// triggers.
    /// </summary>
    private Animator m_animator;

    /// <summary>
    /// An optional component for providing basic shell-like ray interaction functionality, highlighting
    /// where you're selecting in the UI and responding to pinches / button presses.
    /// </summary>
    public OVRRayHelper RayHelper;

    private GameObject m_activeController;

    private bool m_controllerModelsInitialized = false;

    private bool m_hasInputFocus = true;
    private bool m_hasInputFocusPrev = false;
    private bool m_isActive = false;

    private enum ControllerType
    {
        QuestAndRiftS = 1,
        Rift = 2,
        Quest2 = 3,
        TouchPro = 4,
        TouchPlus = 5,
    }

    private ControllerType activeControllerType = ControllerType.Rift;

    private bool m_prevControllerConnected = false;
    private bool m_prevControllerConnectedCached = false;

    private OVRInput.ControllerInHandState m_prevControllerInHandState = OVRInput.ControllerInHandState.NoHand;

    void Start()
    {
        if (OVRManager.OVRManagerinitialized)
        {
            InitializeControllerModels();
        }
    }

    void OnEnable()
    {
        OVRInputModule.TrackInputSource(this);
        SceneManager.activeSceneChanged += OnSceneChanged;
    }

    void OnDisable()
    {
        OVRInputModule.UntrackInputSource(this);
        SceneManager.activeSceneChanged -= OnSceneChanged;
    }

    void OnSceneChanged(Scene unloading, Scene loading)
    {
        OVRInputModule.TrackInputSource(this);
    }

    void InitializeControllerModels()
    {
        if (m_controllerModelsInitialized)
            return;

        OVRPlugin.SystemHeadset headset = OVRPlugin.GetSystemHeadsetType();
        OVRPlugin.Hand controllerHand = m_controller == OVRInput.Controller.LTouch
            ? OVRPlugin.Hand.HandLeft
            : OVRPlugin.Hand.HandRight;
        OVRPlugin.InteractionProfile profile = OVRPlugin.GetCurrentInteractionProfile(controllerHand);
        // If multimodality is enabled, then overwrite the value if we find the controllers to be unheld
        if (OVRPlugin.IsMultimodalHandsControllersSupported())
        {
            OVRPlugin.InteractionProfile detachedProfile =
                OVRPlugin.GetCurrentDetachedInteractionProfile(controllerHand);
            if (detachedProfile != OVRPlugin.InteractionProfile.None)
            {
                profile = detachedProfile;
            }
        }

        switch (headset)
        {
            case OVRPlugin.SystemHeadset.Rift_CV1:
                activeControllerType = ControllerType.Rift;
                break;
            case OVRPlugin.SystemHeadset.Oculus_Quest_2:
                if (profile == OVRPlugin.InteractionProfile.TouchPro)
                {
                    activeControllerType = ControllerType.TouchPro;
                }
                else
                {
                    activeControllerType = ControllerType.Quest2;
                }

                break;
            case OVRPlugin.SystemHeadset.Oculus_Link_Quest_2:
                if (profile == OVRPlugin.InteractionProfile.TouchPro)
                {
                    activeControllerType = ControllerType.TouchPro;
                }
                else
                {
                    activeControllerType = ControllerType.Quest2;
                }

                break;
            case OVRPlugin.SystemHeadset.Meta_Quest_Pro:
                activeControllerType = ControllerType.TouchPro;
                break;
            case OVRPlugin.SystemHeadset.Meta_Link_Quest_Pro:
                activeControllerType = ControllerType.TouchPro;
                break;
            case OVRPlugin.SystemHeadset.Meta_Quest_3:
            case OVRPlugin.SystemHeadset.Meta_Quest_3S:
            case OVRPlugin.SystemHeadset.Meta_Link_Quest_3:
            case OVRPlugin.SystemHeadset.Meta_Link_Quest_3S:
                if (profile == OVRPlugin.InteractionProfile.TouchPro)
                {
                    activeControllerType = ControllerType.TouchPro;
                }
                else
                {
                    activeControllerType = ControllerType.TouchPlus;
                }
                break;
            default:
                activeControllerType = ControllerType.QuestAndRiftS;
                break;
        }

        Debug.LogFormat("OVRControllerHelp: Active controller type: {0} for product {1} (headset {2}, hand {3})",
            activeControllerType, OVRPlugin.productName, headset, controllerHand);

        // Hide all controller models until controller get connected
        m_modelOculusTouchQuestAndRiftSLeftController.SetActive(false);
        m_modelOculusTouchQuestAndRiftSRightController.SetActive(false);
        m_modelOculusTouchRiftLeftController.SetActive(false);
        m_modelOculusTouchRiftRightController.SetActive(false);
        m_modelOculusTouchQuest2LeftController.SetActive(false);
        m_modelOculusTouchQuest2RightController.SetActive(false);
        m_modelMetaTouchProLeftController.SetActive(false);
        m_modelMetaTouchProRightController.SetActive(false);
        m_modelMetaTouchPlusLeftController.SetActive(false);
        m_modelMetaTouchPlusRightController.SetActive(false);

        OVRManager.InputFocusAcquired += InputFocusAquired;
        OVRManager.InputFocusLost += InputFocusLost;

        m_controllerModelsInitialized = true;
    }

    void Update()
    {
        m_isActive = false;
        if (!m_controllerModelsInitialized)
        {
            if (OVRManager.OVRManagerinitialized)
            {
                InitializeControllerModels();
            }
            else
            {
                return;
            }
        }

        OVRInput.Hand handOfController = (m_controller == OVRInput.Controller.LTouch)
            ? OVRInput.Hand.HandLeft
            : OVRInput.Hand.HandRight;
        OVRInput.ControllerInHandState controllerInHandState = OVRInput.GetControllerIsInHandState(handOfController);

        bool controllerConnected = OVRInput.IsControllerConnected(m_controller);

        if ((controllerConnected != m_prevControllerConnected) || !m_prevControllerConnectedCached ||
            (controllerInHandState != m_prevControllerInHandState) ||
            (m_hasInputFocus != m_hasInputFocusPrev))
        {
            if (activeControllerType == ControllerType.Rift)
            {
                m_modelOculusTouchQuestAndRiftSLeftController.SetActive(false);
                m_modelOculusTouchQuestAndRiftSRightController.SetActive(false);
                m_modelOculusTouchRiftLeftController.SetActive(controllerConnected &&
                                                               (m_controller == OVRInput.Controller.LTouch));
                m_modelOculusTouchRiftRightController.SetActive(controllerConnected &&
                                                                (m_controller == OVRInput.Controller.RTouch));
                m_modelOculusTouchQuest2LeftController.SetActive(false);
                m_modelOculusTouchQuest2RightController.SetActive(false);
                m_modelMetaTouchProLeftController.SetActive(false);
                m_modelMetaTouchProRightController.SetActive(false);
                m_modelMetaTouchPlusLeftController.SetActive(false);
                m_modelMetaTouchPlusRightController.SetActive(false);

                m_animator = m_controller == OVRInput.Controller.LTouch
                    ? m_modelOculusTouchRiftLeftController.GetComponent<Animator>()
                    : m_modelOculusTouchRiftRightController.GetComponent<Animator>();
                m_activeController = m_controller == OVRInput.Controller.LTouch
                    ? m_modelOculusTouchRiftLeftController
                    : m_modelOculusTouchRiftRightController;
            }
            else if (activeControllerType == ControllerType.Quest2)
            {
                m_modelOculusTouchQuestAndRiftSLeftController.SetActive(false);
                m_modelOculusTouchQuestAndRiftSRightController.SetActive(false);
                m_modelOculusTouchRiftLeftController.SetActive(false);
                m_modelOculusTouchRiftRightController.SetActive(false);
                m_modelOculusTouchQuest2LeftController.SetActive(controllerConnected &&
                                                                 (m_controller == OVRInput.Controller.LTouch));
                m_modelOculusTouchQuest2RightController.SetActive(controllerConnected &&
                                                                  (m_controller == OVRInput.Controller.RTouch));
                m_modelMetaTouchProLeftController.SetActive(false);
                m_modelMetaTouchProRightController.SetActive(false);
                m_modelMetaTouchPlusLeftController.SetActive(false);
                m_modelMetaTouchPlusRightController.SetActive(false);

                m_animator = m_controller == OVRInput.Controller.LTouch
                    ? m_modelOculusTouchQuest2LeftController.GetComponent<Animator>()
                    : m_modelOculusTouchQuest2RightController.GetComponent<Animator>();
                m_activeController = m_controller == OVRInput.Controller.LTouch
                    ? m_modelOculusTouchQuest2LeftController
                    : m_modelOculusTouchQuest2RightController;
            }
            else if (activeControllerType == ControllerType.QuestAndRiftS)
            {
                m_modelOculusTouchQuestAndRiftSLeftController.SetActive(controllerConnected &&
                                                                        (m_controller == OVRInput.Controller.LTouch));
                m_modelOculusTouchQuestAndRiftSRightController.SetActive(controllerConnected &&
                                                                         (m_controller == OVRInput.Controller.RTouch));
                m_modelOculusTouchRiftLeftController.SetActive(false);
                m_modelOculusTouchRiftRightController.SetActive(false);
                m_modelOculusTouchQuest2LeftController.SetActive(false);
                m_modelOculusTouchQuest2RightController.SetActive(false);
                m_modelMetaTouchProLeftController.SetActive(false);
                m_modelMetaTouchProRightController.SetActive(false);
                m_modelMetaTouchPlusLeftController.SetActive(false);
                m_modelMetaTouchPlusRightController.SetActive(false);

                m_animator = m_controller == OVRInput.Controller.LTouch
                    ? m_modelOculusTouchQuestAndRiftSLeftController.GetComponent<Animator>()
                    : m_modelOculusTouchQuestAndRiftSRightController.GetComponent<Animator>();
                m_activeController = m_controller == OVRInput.Controller.LTouch
                    ? m_modelOculusTouchQuestAndRiftSLeftController
                    : m_modelOculusTouchQuestAndRiftSRightController;
            }
            else if (activeControllerType == ControllerType.TouchPro)
            {
                m_modelOculusTouchQuestAndRiftSLeftController.SetActive(false);
                m_modelOculusTouchQuestAndRiftSRightController.SetActive(false);
                m_modelOculusTouchRiftLeftController.SetActive(false);
                m_modelOculusTouchRiftRightController.SetActive(false);
                m_modelOculusTouchQuest2LeftController.SetActive(false);
                m_modelOculusTouchQuest2RightController.SetActive(false);
                m_modelMetaTouchProLeftController.SetActive(controllerConnected &&
                                                            (m_controller == OVRInput.Controller.LTouch));
                m_modelMetaTouchProRightController.SetActive(controllerConnected &&
                                                             (m_controller == OVRInput.Controller.RTouch));
                m_modelMetaTouchPlusLeftController.SetActive(false);
                m_modelMetaTouchPlusRightController.SetActive(false);

                m_animator = m_controller == OVRInput.Controller.LTouch
                    ? m_modelMetaTouchProLeftController.GetComponent<Animator>()
                    : m_modelMetaTouchProRightController.GetComponent<Animator>();
                m_activeController = m_controller == OVRInput.Controller.LTouch
                    ? m_modelMetaTouchProLeftController
                    : m_modelMetaTouchProRightController;
            }
            else /*if (activeControllerType == ControllerType.TouchPlus)*/
            {
                m_modelOculusTouchQuestAndRiftSLeftController.SetActive(false);
                m_modelOculusTouchQuestAndRiftSRightController.SetActive(false);
                m_modelOculusTouchRiftLeftController.SetActive(false);
                m_modelOculusTouchRiftRightController.SetActive(false);
                m_modelOculusTouchQuest2LeftController.SetActive(false);
                m_modelOculusTouchQuest2RightController.SetActive(false);
                m_modelMetaTouchProLeftController.SetActive(false);
                m_modelMetaTouchProRightController.SetActive(false);
                m_modelMetaTouchPlusLeftController.SetActive(controllerConnected &&
                                                            (m_controller == OVRInput.Controller.LTouch));
                m_modelMetaTouchPlusRightController.SetActive(controllerConnected &&
                                                             (m_controller == OVRInput.Controller.RTouch));

                m_animator = m_controller == OVRInput.Controller.LTouch
                    ? m_modelMetaTouchPlusLeftController.GetComponent<Animator>()
                    : m_modelMetaTouchPlusRightController.GetComponent<Animator>();
                m_activeController = m_controller == OVRInput.Controller.LTouch
                    ? m_modelMetaTouchPlusLeftController
                    : m_modelMetaTouchPlusRightController;
            }

            m_prevControllerConnected = controllerConnected;
            m_prevControllerConnectedCached = true;
            m_prevControllerInHandState = controllerInHandState;
            m_hasInputFocusPrev = m_hasInputFocus;
        }

        bool shouldSetControllerActive = m_hasInputFocus && controllerConnected;
        switch (m_showState)
        {
            case OVRInput.InputDeviceShowState.Always:
                // intentionally blank
                break;
            case OVRInput.InputDeviceShowState.ControllerInHandOrNoHand:
                if (controllerInHandState == OVRInput.ControllerInHandState.ControllerNotInHand)
                {
                    shouldSetControllerActive = false;
                }

                break;
            case OVRInput.InputDeviceShowState.ControllerInHand:
                if (controllerInHandState != OVRInput.ControllerInHandState.ControllerInHand)
                {
                    shouldSetControllerActive = false;
                }

                break;
            case OVRInput.InputDeviceShowState.ControllerNotInHand:
                if (controllerInHandState != OVRInput.ControllerInHandState.ControllerNotInHand)
                {
                    shouldSetControllerActive = false;
                }

                break;
            case OVRInput.InputDeviceShowState.NoHand:
                if (controllerInHandState != OVRInput.ControllerInHandState.NoHand)
                {
                    shouldSetControllerActive = false;
                }

                break;
        }

        if (!showWhenHandsArePoweredByNaturalControllerPoses && OVRPlugin.IsControllerDrivenHandPosesEnabled() && OVRPlugin.AreControllerDrivenHandPosesNatural())
        {
            shouldSetControllerActive = false;
        }

        m_isActive = shouldSetControllerActive;

        if (m_activeController != null)
        {
            m_activeController.SetActive(shouldSetControllerActive);
        }

        if (RayHelper != null)
        {
            RayHelper.gameObject.SetActive(shouldSetControllerActive);
        }


        if (m_animator != null && m_animator.gameObject.activeSelf)
        {
            m_animator.SetFloat("Button 1", OVRInput.Get(OVRInput.Button.One, m_controller) ? 1.0f : 0.0f);
            m_animator.SetFloat("Button 2", OVRInput.Get(OVRInput.Button.Two, m_controller) ? 1.0f : 0.0f);
            m_animator.SetFloat("Button 3", OVRInput.Get(OVRInput.Button.Start, m_controller) ? 1.0f : 0.0f);

            m_animator.SetFloat("Joy X", OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, m_controller).x);
            m_animator.SetFloat("Joy Y", OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, m_controller).y);

            m_animator.SetFloat("Trigger", OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, m_controller));
            m_animator.SetFloat("Grip", OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, m_controller));
        }
    }

    /// <summary>
    /// Sets the associated controller to have input focus.
    /// </summary>
    /// <remarks>
    /// This is typically invoked in response to a button press or other clearly-intentional input and
    /// is used for purposes such as showing pointer rays, etc.
    /// </remarks>
    public void InputFocusAquired()
    {
        m_hasInputFocus = true;
    }

    /// <summary>
    /// Sets the associated controller to not have input focus.
    /// </summary>
    /// <remarks>
    /// This is typically invoked in response to a button press or other clearly-intentional input on
    /// a different controller and is used for purposes such as hiding pointer rays, etc.
    /// </remarks>
    public void InputFocusLost()
    {
        m_hasInputFocus = false;
    }

    /// <summary>
    /// Checks whether the state of the associated controller is "pressed," a convenience wrapper around
    /// checking <see cref="OVRInput.GetDown(OVRInput.Button, OVRInput.Controller)"/> on the
    /// <see cref="OVRInput.Button.PrimaryIndexTrigger"/>.
    /// </summary>
    /// <returns>True if the primary index trigger is down, false otherwise</returns>
    public bool IsPressed()
    {
        return OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, m_controller);
    }

    /// <summary>
    /// Checks whether the state of the associated controller is "released," a convenience wrapper around
    /// checking <see cref="OVRInput.GetUp(OVRInput.Button, OVRInput.Controller)"/> on the
    /// <see cref="OVRInput.Button.PrimaryIndexTrigger"/>.
    /// </summary>
    /// <returns>True if the primary index trigger is up, false otherwise</returns>
    public bool IsReleased()
    {
        return OVRInput.GetUp(OVRInput.Button.PrimaryIndexTrigger, m_controller);
    }

    /// <summary>
    /// Retrieves the Unity Transform which should be used as the origin of raycasts from the associated
    /// controller. Specifically, raycasts should begin at the position and proceed in the forward direction
    /// of the returned transform.
    /// </summary>
    /// <returns>The source transform for raycasts from the associated controller</returns>
    public Transform GetPointerRayTransform()
    {
        return transform;
    }

    /// <summary>
    /// Checks whether or not this OVRControllerHelper instance has been Destroyed; a convenience wrapper
    /// around the Unity idiomatic `this != null` check.
    /// </summary>
    /// <returns>True if this instance has been Destroyed, false otherwise</returns>
    public bool IsValid()
    {
        return this != null;
    }

    /// <summary>
    /// Checks whether the associated controller is considered to be "active," i.e. in use. Unrelated to
    /// Unity's built-in Behaviour.isActiveAndEnabled property or any other Unity-related concept of
    /// activity.
    /// </summary>
    /// <returns>True if the associated controller is active, false otherwise</returns>
    public bool IsActive()
    {
        return m_isActive;
    }

    /// <summary>
    /// Queries the handedness of an associated Oculus Touch controller.
    /// </summary>
    /// <returns>
    /// <see cref="OVRPlugin.Hand.HandLeft"/> if the associated controller is
    /// <see cref="OVRInput.Controller.LTouch"/>, otherwise returns <see cref="OVRPlugin.Hand.HandRight"/>.
    /// </returns>
    /// <remarks>
    /// Because this method only checks for equivalency to <see cref="OVRInput.Controller.LTouch"/>, it cannot
    /// be used to accurately query the handedness of non-Oculus Touch controllers as, even for
    /// <see cref="OVRInput.Controller.LHand"/>, it will return <see cref="OVRPlugin.Hand.HandRight"/>.
    /// </remarks>
    public OVRPlugin.Hand GetHand()
    {
        return m_controller == OVRInput.Controller.LTouch ? OVRPlugin.Hand.HandLeft : OVRPlugin.Hand.HandRight;
    }

    /// <summary>
    /// If <see cref="RayHelper"/> is set, applies the provided <see cref="OVRInputRayData"/> to that helper and
    /// sets its activation and strength based on the <see cref="OVRInput.Button.PrimaryIndexTrigger"/> of the
    /// associated controller. Does nothing if <see cref="RayHelper"/> is null.
    /// </summary>
    /// <param name="rayData">The <see cref="OVRInputRayData"/> to be provided to the ray helper</param>
    public void UpdatePointerRay(OVRInputRayData rayData)
    {
        if (RayHelper)
        {
            rayData.IsActive = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, m_controller);
            rayData.ActivationStrength = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, m_controller);
            RayHelper.UpdatePointerRay(rayData);
        }
    }
}
