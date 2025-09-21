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
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Helper/diagnostic class which reads controller state and serializes it to a Unity Text asset on a
/// per-frame basis.
/// </summary>
/// <remarks>
/// Rather than being bound to a specific controller, instances of this class invoke
/// <see cref="OVRInput.GetActiveController"/> each frame to report information on whichever controller
/// is currently considered active. Consequently, there is no reason to ever have more than one
/// OVRControllerTest active at the same time.
/// </remarks>
public class OVRControllerTest : MonoBehaviour
{
    /// <summary>
    /// Simple helper class used to observe a simple closure and report its behavior over time.
    /// </summary>
    /// <remarks>
    /// BoolMonitor is used by <see cref="OVRControllerTest"/> to conveniently serialize controller behavior
    /// into a human-readable format. For example, a test of <see cref="OVRInput.Button.One"/> will observe
    /// <see cref="OVRInput.Get(OVRInput.Button, OVRInput.Controller)"/> with that input, displaying the
    /// monitor's name and current state in the format, "One: *True*," etc.
    /// </remarks>
    public class BoolMonitor
    {
        /// <summary>
        /// The closure type observed by by this BoolMonitor over time.
        /// <see cref="BoolMonitor(string, BoolGenerator, float)"/> is typically invoked with an
        /// anonymous function as its second argument, which will be interpreted as this type.
        /// </summary>
        /// <returns></returns>
        public delegate bool BoolGenerator();

        private string m_name = "";
        private BoolGenerator m_generator;
        private bool m_prevValue = false;
        private bool m_currentValue = false;
        private bool m_currentValueRecentlyChanged = false;
        private float m_displayTimeout = 0.0f;
        private float m_displayTimer = 0.0f;

        /// <summary>
        /// Constructor for BoolMonitor.
        /// </summary>
        /// <param name="name">The display name of the closure being monitored</param>
        /// <param name="generator">The closure to be monitored</param>
        /// <param name="displayTimeout">
        /// Optional parameter controlling how long after changing the display text for this monitor will
        /// remain emphasized
        /// </param>
        public BoolMonitor(string name, BoolGenerator generator, float displayTimeout = 0.5f)
        {
            m_name = name;
            m_generator = generator;
            m_displayTimeout = displayTimeout;
        }

        /// <summary>
        /// Updates the BoolMonitor's state. This invokes the <see cref="BoolGenerator"/> closure passed
        /// to this instance at construction, then updates the monitor's internal state based on whether and
        /// when the result of the closure changed.
        /// </summary>
        public void Update()
        {
            m_prevValue = m_currentValue;
            m_currentValue = m_generator();

            if (m_currentValue != m_prevValue)
            {
                m_currentValueRecentlyChanged = true;
                m_displayTimer = m_displayTimeout;
            }

            if (m_displayTimer > 0.0f)
            {
                m_displayTimer -= Time.deltaTime;

                if (m_displayTimer <= 0.0f)
                {
                    m_currentValueRecentlyChanged = false;
                    m_displayTimer = 0.0f;
                }
            }
        }

        /// <summary>
        /// Serializes the current state of the monitor into a StringBuilder to be displayed.
        /// </summary>
        /// <param name="sb">The string builder to which the serialization should be appended</param>
        /// <remarks>
        /// This is a non-modifying function and does not actually update the current state; to
        /// update state, a separate call to <see cref="Update"/> must be made.
        /// </remarks>
        public void AppendToStringBuilder(ref StringBuilder sb)
        {
            sb.Append(m_name);

            if (m_currentValue && m_currentValueRecentlyChanged)
                sb.Append(": *True*\n");
            else if (m_currentValue)
                sb.Append(":  True \n");
            else if (!m_currentValue && m_currentValueRecentlyChanged)
                sb.Append(": *False*\n");
            else if (!m_currentValue)
                sb.Append(":  False \n");
        }
    }

    /// <summary>
    /// The Unity Text object in which to display the results of the various tests this instance
    /// monitors.
    /// </summary>
    /// <remarks>
    /// <see cref="BoolMonitor"/> reflecting input state as well as specific controller information
    /// (battery percentage, position/orientation, etc.) are serialized to this text on a per-frame
    /// basis.
    /// </remarks>
    public Text uiText;
    private List<BoolMonitor> monitors;
    private StringBuilder data;

    void Start()
    {
        if (uiText != null)
        {
            uiText.supportRichText = false;
        }

        data = new StringBuilder(2048);

        monitors = new List<BoolMonitor>()
        {
            // virtual
            new BoolMonitor("One", () => OVRInput.Get(OVRInput.Button.One)),
            new BoolMonitor("OneDown", () => OVRInput.GetDown(OVRInput.Button.One)),
            new BoolMonitor("OneUp", () => OVRInput.GetUp(OVRInput.Button.One)),
            new BoolMonitor("One (Touch)", () => OVRInput.Get(OVRInput.Touch.One)),
            new BoolMonitor("OneDown (Touch)", () => OVRInput.GetDown(OVRInput.Touch.One)),
            new BoolMonitor("OneUp (Touch)", () => OVRInput.GetUp(OVRInput.Touch.One)),
            new BoolMonitor("Two", () => OVRInput.Get(OVRInput.Button.Two)),
            new BoolMonitor("TwoDown", () => OVRInput.GetDown(OVRInput.Button.Two)),
            new BoolMonitor("TwoUp", () => OVRInput.GetUp(OVRInput.Button.Two)),
            new BoolMonitor("PrimaryIndexTrigger", () => OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger)),
            new BoolMonitor("PrimaryIndexTriggerDown", () => OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger)),
            new BoolMonitor("PrimaryIndexTriggerUp", () => OVRInput.GetUp(OVRInput.Button.PrimaryIndexTrigger)),
            new BoolMonitor("PrimaryIndexTrigger (Touch)", () => OVRInput.Get(OVRInput.Touch.PrimaryIndexTrigger)),
            new BoolMonitor("PrimaryIndexTriggerDown (Touch)",
                () => OVRInput.GetDown(OVRInput.Touch.PrimaryIndexTrigger)),
            new BoolMonitor("PrimaryIndexTriggerUp (Touch)", () => OVRInput.GetUp(OVRInput.Touch.PrimaryIndexTrigger)),
            new BoolMonitor("PrimaryHandTrigger", () => OVRInput.Get(OVRInput.Button.PrimaryHandTrigger)),
            new BoolMonitor("PrimaryHandTriggerDown", () => OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger)),
            new BoolMonitor("PrimaryHandTriggerUp", () => OVRInput.GetUp(OVRInput.Button.PrimaryHandTrigger)),
            new BoolMonitor("Up", () => OVRInput.Get(OVRInput.Button.Up)),
            new BoolMonitor("Down", () => OVRInput.Get(OVRInput.Button.Down)),
            new BoolMonitor("Left", () => OVRInput.Get(OVRInput.Button.Left)),
            new BoolMonitor("Right", () => OVRInput.Get(OVRInput.Button.Right)),

            // raw
            new BoolMonitor("Start", () => OVRInput.Get(OVRInput.RawButton.Start)),
            new BoolMonitor("StartDown", () => OVRInput.GetDown(OVRInput.RawButton.Start)),
            new BoolMonitor("StartUp", () => OVRInput.GetUp(OVRInput.RawButton.Start)),
            new BoolMonitor("Back", () => OVRInput.Get(OVRInput.RawButton.Back)),
            new BoolMonitor("BackDown", () => OVRInput.GetDown(OVRInput.RawButton.Back)),
            new BoolMonitor("BackUp", () => OVRInput.GetUp(OVRInput.RawButton.Back)),
            new BoolMonitor("A", () => OVRInput.Get(OVRInput.RawButton.A)),
            new BoolMonitor("ADown", () => OVRInput.GetDown(OVRInput.RawButton.A)),
            new BoolMonitor("AUp", () => OVRInput.GetUp(OVRInput.RawButton.A)),
        };
    }

    static string prevConnected = "";

    static BoolMonitor controllers = new BoolMonitor("Controllers Changed",
        () => { return OVRInput.GetConnectedControllers().ToString() != prevConnected; });

    void Update()
    {
        OVRInput.Controller activeController = OVRInput.GetActiveController();

        data.Length = 0;

#pragma warning disable CS0618 // Type or member is obsolete
        byte battery = OVRInput.GetControllerBatteryPercentRemaining();
        data.AppendFormat("Battery: {0}\n", battery);
#pragma warning restore CS0618 // Type or member is obsolete

        float framerate = OVRPlugin.GetAppFramerate();
        data.AppendFormat("Framerate: {0:F2}\n", framerate);

        string activeControllerName = activeController.ToString();
        data.AppendFormat("Active: {0}\n", activeControllerName);

        string connectedControllerNames = OVRInput.GetConnectedControllers().ToString();
        data.AppendFormat("Connected: {0}\n", connectedControllerNames);

        data.AppendFormat("PrevConnected: {0}\n", prevConnected);

        controllers.Update();
        controllers.AppendToStringBuilder(ref data);

        prevConnected = connectedControllerNames;

        Quaternion rot = OVRInput.GetLocalControllerRotation(activeController);
        data.AppendFormat("Orientation: ({0:F2}, {1:F2}, {2:F2}, {3:F2})\n", rot.x, rot.y, rot.z, rot.w);

        Vector3 angVel = OVRInput.GetLocalControllerAngularVelocity(activeController);
        data.AppendFormat("AngVel: ({0:F2}, {1:F2}, {2:F2})\n", angVel.x, angVel.y, angVel.z);

#pragma warning disable CS0618 // Type or member is obsolete
        Vector3 angAcc = OVRInput.GetLocalControllerAngularAcceleration(activeController);
        data.AppendFormat("AngAcc: ({0:F2}, {1:F2}, {2:F2})\n", angAcc.x, angAcc.y, angAcc.z);
#pragma warning restore CS0618 // Type or member is obsolete

        Vector3 pos = OVRInput.GetLocalControllerPosition(activeController);
        data.AppendFormat("Position: ({0:F2}, {1:F2}, {2:F2})\n", pos.x, pos.y, pos.z);

        Vector3 vel = OVRInput.GetLocalControllerVelocity(activeController);
        data.AppendFormat("Vel: ({0:F2}, {1:F2}, {2:F2})\n", vel.x, vel.y, vel.z);

#pragma warning disable CS0618 // Type or member is obsolete
        Vector3 acc = OVRInput.GetLocalControllerAcceleration(activeController);
        data.AppendFormat("Acc: ({0:F2}, {1:F2}, {2:F2})\n", acc.x, acc.y, acc.z);
#pragma warning restore CS0618 // Type or member is obsolete

        float indexTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger);
        data.AppendFormat("PrimaryIndexTriggerAxis1D: ({0:F2})\n", indexTrigger);

        float handTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger);
        data.AppendFormat("PrimaryHandTriggerAxis1D: ({0:F2})\n", handTrigger);

        for (int i = 0; i < monitors.Count; i++)
        {
            monitors[i].Update();
            monitors[i].AppendToStringBuilder(ref data);
        }

        if (uiText != null)
        {
            uiText.text = data.ToString();
        }
    }
}
