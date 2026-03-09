using UnityEngine;
using System.Collections.Generic;
using System;
using Basis.Scripts.Device_Management;

public static class BasisCursorManagement
{
    // A list of unique requests to unlock the cursor.
    private static List<string> cursorUnlockRequests = new List<string>();
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

#if UNITY_EDITOR
    /// <summary>
    /// Editor-only debug view of active unlock requests.
    /// </summary>
    public static IReadOnlyList<string> CursorUnlockRequestsDebug => cursorUnlockRequests;
#endif
    /// <summary>
    /// Removes this unlock request.
    /// If there are no remaining unlock requests, locks the cursor and makes it invisible.
    /// </summary>
    public static void LockCursor(string requestName)
    {
        if (ShouldIgnoreCursorRequests())
        {
            BasisDebug.Log("Skipping Lock Request", BasisDebug.LogTag.Local);
            return;
        }

        cursorUnlockRequests.Remove(requestName);
        if (cursorUnlockRequests.Count == 0)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            OnCursorStateChange?.Invoke(CursorLockMode.Locked, false);
        }
    }

    /// <summary>
    /// Unlocks the cursor and makes it visible.
    /// Adds an unlock request, which prevents the cursor from being locked until this request is removed with LockCursor.
    /// </summary>
    public static void UnlockCursor(string requestName, bool FireCursorStateChange = true)
    {
        if (ShouldIgnoreCursorRequests())
        {
            BasisDebug.Log("Skipping Unlock Request", BasisDebug.LogTag.Local);
            return;
        }

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
        if (!cursorUnlockRequests.Contains(requestName))
        {
            cursorUnlockRequests.Add(requestName);
        }
        if (Cursor.lockState == CursorLockMode.None)
        {
            return;
        }
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
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
        if (ShouldIgnoreCursorRequests())
        {
            return;
        }

        Cursor.lockState = CursorLockMode.Confined;
        Cursor.visible = true;
        //  BasisDebug.Log("Cursor Confined");
        OnCursorStateChange?.Invoke(CursorLockMode.Confined, true);
    }
    private static bool ShouldIgnoreCursorRequests()
    {
        // When in VR mode, all cursor lock requests are must be ignored,
        // so that cursor control is not taken away from other external desktop overlay applications.
        return BasisDeviceManagement.IsCurrentModeVR();
    }
    public static void OnReset()
    {
        cursorUnlockRequests.Clear();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        OnCursorStateChange?.Invoke(CursorLockMode.None, true);
    }
}
