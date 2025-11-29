using UnityEngine;
using System.Collections.Generic;
using System;
using Basis.Scripts.Device_Management;

public static class BasisCursorManagement
{
    // A list to keep track of cursor lock requests
    private static List<string> cursorLockRequests = new List<string>();
    // Event that gets triggered whenever the cursor state changes
    public static event Action<CursorLockMode, bool> OnCursorStateChange;
    public static CursorLockMode ActiveLockState()
    {
        return Cursor.lockState;
    }
    public static bool IsVisible()
    {
        return Cursor.visible;
    }

    /// <summary>
    /// Locks the cursor to the center of the screen and hides it.
    /// Adds a request to lock the cursor.
    /// </summary>
    public static void LockCursor(string requestName)
    {
        if (ShouldIgnoreCursorRequests()) return;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        // BasisDebug.Log("Cursor Locked");
        cursorLockRequests.Add(requestName);
        OnCursorStateChange?.Invoke(CursorLockMode.Locked, false);
    }

    /// <summary>
    /// Unlocks the cursor and makes it visible.
    /// Removes a request to lock the cursor.
    /// </summary>
    public static void UnlockCursor(string requestName, bool FireCursorStateChange = true)
    {
        if (ShouldIgnoreCursorRequests()) return;

        InternalUnlockCursor(requestName, FireCursorStateChange);
    }

    /// <summary>
    /// Unlocks the cursor and makes it visible. Bypasses checks that would have prevented it from being unlocked.
    /// </summary>
    public static void UnlockCursorBypassChecks(string requestName, bool FireCursorStateChange = true)
    {
        InternalUnlockCursor(requestName, FireCursorStateChange);
    }

    private static void InternalUnlockCursor(string requestName, bool FireCursorStateChange)
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        //  BasisDebug.Log("Cursor Unlocked");
        cursorLockRequests.Remove(requestName);
        if (FireCursorStateChange)
        {
            OnCursorStateChange?.Invoke(CursorLockMode.None, true);
        }
    }

    /// <summary>
    /// Locks the cursor to the screen boundaries but allows movement.
    /// Adds a request to confine the cursor.
    /// </summary>
    public static void ConfineCursor(string requestName)
    {
        if (ShouldIgnoreCursorRequests()) return;

        Cursor.lockState = CursorLockMode.Confined;
        Cursor.visible = true;
        //  BasisDebug.Log("Cursor Confined");
        OnCursorStateChange?.Invoke(CursorLockMode.Confined, true);
    }
    private static bool ShouldIgnoreCursorRequests()
    {
        var isUserInVR = !BasisDeviceManagement.IsUserInDesktop();
        // When in VR mode, all cursor lock requests are must be ignored,
        // so that cursor control is not taken away from other external desktop overlay applications.
        return isUserInVR;
    }
    public static void OnReset()
    {
        cursorLockRequests.Clear();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        OnCursorStateChange?.Invoke(CursorLockMode.None, true);
    }
}
