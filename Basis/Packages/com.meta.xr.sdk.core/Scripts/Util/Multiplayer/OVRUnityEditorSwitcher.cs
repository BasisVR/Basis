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
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
#if UNITY_NEW_INPUT_SYSTEM_INSTALLED
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Simple helper script for switching between multiple vr players when testing multiplayer using parallel sync.
/// </summary>
/// <description>
/// How it works:
/// 1) Attach this script to an exiting game object, or create a new one
/// 2) After opening multiple unity windows and run the game with parallel sync, use custom keybindings to switch between players
///     - can modify custom keybindings in inspector after attaching script to object
///     - more options are available when using unity's new input system vs old input system
/// 3) By changing which editor is in focus, you can control the currently active vr player in that editor
///
/// Feel free to use this script as a reference for further customization
/// </description>
#if UNITY_EDITOR_WIN
public class OVRUnityEditorSwitcher : MonoBehaviour
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

#if !ENABLE_INPUT_SYSTEM
    public KeyCode editorOneKey = KeyCode.Alpha1;
    public KeyCode editorTwoKey = KeyCode.Alpha2;
    public KeyCode editorThreeKey = KeyCode.Alpha3;
#endif

#if ENABLE_INPUT_SYSTEM && UNITY_NEW_INPUT_SYSTEM_INSTALLED
    public InputAction editorOneAction = new InputAction(binding: "<Keyboard>/1");
    public InputAction editorTwoAction = new InputAction(binding: "<Keyboard>/2");
    public InputAction editorThreeAction = new InputAction(binding: "<Keyboard>/3");
#endif

    private List<IntPtr> unityWindowHandles = new List<IntPtr>();
    private bool initialized = false;

    void Start() {
#if ENABLE_INPUT_SYSTEM && UNITY_NEW_INPUT_SYSTEM_INSTALLED
        editorOneAction.performed += _ => SwitchToUnityWindow(0);
        editorTwoAction.performed += _ => SwitchToUnityWindow(1);
        editorThreeAction.performed += _ => SwitchToUnityWindow(2);
        editorOneAction.Enable();
        editorTwoAction.Enable();
        editorThreeAction.Enable();
#endif
#if ENABLE_INPUT_SYSTEM && !UNITY_NEW_INPUT_SYSTEM_INSTALLED
        UnityEngine.Debug.Log("Please install unity new input system in package manager to use OVRUnityEditorSwitcher");
#endif
    }

    /// <summary>
    /// Initiate to get all running unity editor window process
    /// </summary>
    void Init()
    {
        Process[] processes = Process.GetProcesses();
        foreach (Process process in processes)
        {
            if (process.HasExited) continue;
            if (process.ProcessName != "Unity") continue;
            string processTitle = GetWindowTitle(process.MainWindowHandle);
            if (string.IsNullOrEmpty(processTitle)) continue;
            UnityEngine.Debug.Log($"Found Unity process with ID: {process.Id} - {process.ProcessName} - {processTitle} -");
            unityWindowHandles.Add(process.MainWindowHandle);
        }
    }

    /// <summary>
    /// Detect key press in legacy input system
    /// </summary>
    void Update() {
#if !ENABLE_INPUT_SYSTEM
        if (Input.GetKeyDown(editorOneKey)) {
            SwitchToUnityWindow(0);
        }
        if (Input.GetKeyDown(editorTwoKey)) {
            SwitchToUnityWindow(1);
        }
        if (Input.GetKeyDown(editorThreeKey)) {
            SwitchToUnityWindow(2);
        }
#endif
    }

    /// <summary>
    /// Focus a particular editor window using index
    /// </summary>
    public void SwitchToUnityWindow(int index) {
        if (!initialized) {
            Init();
            initialized = true;
        }
        if (index < unityWindowHandles.Count) {
            SwitchToThisWindow(unityWindowHandles[index], true);
        }
    }

    /// <summary>
    /// Get window title of a process. To verify if it is a editor window or background process
    /// </summary>
    string GetWindowTitle(IntPtr hWnd)
    {
        const int nChars = 256;
        StringBuilder buffer = new StringBuilder(nChars);
        if (GetWindowText(hWnd, buffer, nChars) > 0)
        {
            return buffer.ToString();
        }
        return null;
    }
}

#endif //UNITY_EDITOR_WIN
