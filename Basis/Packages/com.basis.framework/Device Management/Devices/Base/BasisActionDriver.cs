using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Routes input actions to one or more tracked roles and runs the bound action delegates efficiently.
/// </summary>
public static class BasisActionDriver
{
    /// <summary>
    /// File name used to persist bindings to disk.
    /// </summary>
    public const string FileName = "BasisActionBindingsV1.json";

    /// <summary>
    /// Folder within <see cref="Application.persistentDataPath"/> where bindings are saved.
    /// </summary>
    public const string FolderPath = "BasisActions";

    /// <summary>
    /// Full path to the current-mode bindings file.
    /// </summary>
    public static string SavePath => Path.Combine(Application.persistentDataPath, FolderPath, BasisDeviceManagement.StaticCurrentMode, FileName);

    /// <summary>
    /// True if a bindings file exists on disk.
    /// </summary>
    public static bool HasSavedBindings
    {
        get
        {
            return File.Exists(SavePath);
        }
    }

    /// <summary>
    /// Identifiers for executable input actions.
    /// </summary>
    public enum ActionId
    {
        // Movement

        /// <summary>
        /// Sets the movement speed multiplier from the dominant axis of the primary 2D input.
        /// </summary>
        SetMovementSpeedMultiplierFromPrimary2DAxis = 0,

        /// <summary>
        /// Sets the movement vector from the primary 2D input.
        /// </summary>
        SetMovementVectorFromPrimary2DAxis = 1,

        /// <summary>
        /// Updates the character movement speed each frame.
        /// </summary>
        TickMovementSpeed = 2,

        // UI / System

        /// <summary>
        /// Toggles the hamburger menu when the secondary button is released.
        /// </summary>
        ToggleHamburgerOnSecondaryRelease = 3,

        /// <summary>
        /// Toggles microphone pause when the primary button is released and no UI hover is present.
        /// </summary>
        ToggleMicOnPrimaryReleaseIfNoHover = 4,

        // Camera/Character orientation & locomotion

        /// <summary>
        /// Rotates the character from the primary 2D input.
        /// </summary>
        RotateFromPrimary2DAxis = 5,

        /// <summary>
        /// Triggers jump while the primary button is held.
        /// </summary>
        JumpOnPrimaryButton = 6,

        /// <summary>
        /// Keep this as the last entry for sizing arrays.
        /// </summary>
        Count = 7
    }

    /// <summary>
    /// Binds an action to a role. Duplicate binds are ignored.
    /// </summary>
    /// <param name="action">The action to bind.</param>
    /// <param name="role">The tracked role that will execute the action.</param>
    public static void Bind(ActionId action, BasisBoneTrackedRole role)
    {
        if (!s_RoleToActions.TryGetValue(role, out var actionsForRole))
        {
            actionsForRole = new List<ActionId>(8);
            s_RoleToActions[role] = actionsForRole;
        }

        if (!actionsForRole.Contains(action))
        {
            actionsForRole.Add(action);
            if (!s_SuppressRebuild)
            {
                RebuildCompiledActionsForRole(role);
            }
        }

        if (!s_ActionToRoles.TryGetValue(action, out var rolesForAction))
        {
            rolesForAction = new HashSet<BasisBoneTrackedRole>();
            s_ActionToRoles[action] = rolesForAction;
        }

        if (!rolesForAction.Contains(role))
        {
            rolesForAction.Add(role);
        }
    }

    /// <summary>
    /// Unbinds an action from a specific role.
    /// </summary>
    /// <param name="action">The action to unbind.</param>
    /// <param name="role">The role to remove the binding from.</param>
    public static void Unbind(ActionId action, BasisBoneTrackedRole role)
    {
        if (s_RoleToActions.TryGetValue(role, out var list))
        {
            if (list.Remove(action))
            {
                if (!s_SuppressRebuild)
                {
                    RebuildCompiledActionsForRole(role);
                }
            }
        }

        if (s_ActionToRoles.TryGetValue(action, out var set))
        {
            set.Remove(role);
            if (set.Count == 0)
            {
                s_ActionToRoles.Remove(action);
            }
        }
    }

    /// <summary>
    /// Unbinds an action from all roles.
    /// </summary>
    /// <param name="action">The action to remove from all roles.</param>
    public static void UnbindAll(ActionId action)
    {
        if (!s_ActionToRoles.TryGetValue(action, out var set) || set.Count == 0)
        {
            return;
        }

        s_SuppressRebuild = true;

        foreach (var role in set)
        {
            if (s_RoleToActions.TryGetValue(role, out var list))
            {
                list.Remove(action);
            }
        }

        s_ActionToRoles.Remove(action);
        s_SuppressRebuild = false;

        foreach (var role in s_RoleToActions.Keys)
        {
            RebuildCompiledActionsForRole(role);
        }
    }

    /// <summary>
    /// Gets the first role bound to an action or <c>null</c> if none.
    /// </summary>
    /// <param name="action">The action to query.</param>
    /// <returns>The first role if any; otherwise <c>null</c>.</returns>
    public static BasisBoneTrackedRole? GetBinding(ActionId action)
    {
        if (s_ActionToRoles.TryGetValue(action, out var set))
        {
            foreach (var r in set)
            {
                return r;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets all roles bound to an action.
    /// </summary>
    /// <param name="action">The action to query.</param>
    /// <returns>A read-only list of roles (may be empty).</returns>
    public static IReadOnlyList<BasisBoneTrackedRole> GetBindings(ActionId action)
    {
        if (s_ActionToRoles.TryGetValue(action, out var set))
        {
            if (set.Count == 0)
            {
                return s_EmptyRoles;
            }

            var arr = new BasisBoneTrackedRole[set.Count];
            var i = 0;

            foreach (var r in set)
            {
                arr[i++] = r;
            }

            return arr;
        }

        return s_EmptyRoles;
    }

    /// <summary>
    /// Gets all actions currently bound to a role.
    /// </summary>
    /// <param name="role">The role to query.</param>
    /// <returns>A read-only list of actions (may be empty).</returns>
    public static IReadOnlyList<ActionId> GetActionsForRole(BasisBoneTrackedRole role)
    {
        if (s_RoleToActions.TryGetValue(role, out var list))
        {
            return list;
        }

        return s_EmptyActions;
    }

    /// <summary>
    /// Loads default bindings, then loads saved bindings if present; otherwise saves defaults.
    /// </summary>
    public static async Task LoadBindings()
    {
        s_ActionToRoles.Clear();
        s_RoleToActions.Clear();
        s_RoleToCompiled.Clear();

        s_SuppressRebuild = true;

        Bind(ActionId.SetMovementSpeedMultiplierFromPrimary2DAxis, BasisBoneTrackedRole.LeftHand);
        Bind(ActionId.SetMovementVectorFromPrimary2DAxis, BasisBoneTrackedRole.LeftHand);
        Bind(ActionId.TickMovementSpeed, BasisBoneTrackedRole.LeftHand);
        Bind(ActionId.ToggleHamburgerOnSecondaryRelease, BasisBoneTrackedRole.LeftHand);

        Bind(ActionId.RotateFromPrimary2DAxis, BasisBoneTrackedRole.RightHand);
        Bind(ActionId.JumpOnPrimaryButton, BasisBoneTrackedRole.RightHand);

        if (BasisDeviceManagement.IsCurrentModeVR() == false)
        {
            Bind(ActionId.ToggleMicOnPrimaryReleaseIfNoHover, BasisBoneTrackedRole.CenterEye);
        }
        else
        {
            Bind(ActionId.ToggleMicOnPrimaryReleaseIfNoHover, BasisBoneTrackedRole.LeftHand);
        }

        s_SuppressRebuild = false;
        RebuildAllCompiled();

        if (File.Exists(SavePath))
        {
            await LoadApplyToDriverAsync();
        }
        else
        {
            await SaveFromDriver();
        }
    }

    /// <summary>
    /// Executes all compiled actions for a role given current and last input states.
    /// </summary>
    /// <param name="trackedRole">The role whose actions will be executed.</param>
    /// <param name="CurrentInputState">Current input snapshot.</param>
    /// <param name="LastInputState">Previous input snapshot.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdatePlayerControl(BasisBoneTrackedRole trackedRole, ref BasisInputState CurrentInputState, ref BasisInputState LastInputState)
    {
        if (!s_RoleToCompiled.TryGetValue(trackedRole, out var compiled) || compiled.Length == 0)
        {
            return;
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        for (int Index = 0; Index < compiled.Length; Index++)
        {
            var actionImpl = compiled[Index];

            try
            {
                actionImpl(ref CurrentInputState, ref LastInputState);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex, BasisDebug.LogTag.Input);
            }
        }
#else
        for (int Index = 0; Index < compiled.Length; Index++)
        {
            compiled[Index](ref CurrentInputState, ref LastInputState);
        }
#endif
    }

    /// <summary>
    /// Delegate signature for compiled input actions.
    /// </summary>
    /// <param name="current">Current input snapshot.</param>
    /// <param name="last">Previous input snapshot.</param>
    public delegate void InputAction(ref BasisInputState current, ref BasisInputState last);

    /// <summary>
    /// Sets movement speed multiplier from the dominant axis of the primary 2D input.
    /// </summary>
    /// <param name="current">Current input snapshot.</param>
    /// <param name="last">Previous input snapshot.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetMovementSpeedMultiplierFromPrimary2DAxis(ref BasisInputState current, ref BasisInputState last)
    {
        Vector2 axis = current.Primary2DAxisDeadZoned;
        float largestValue = Mathf.Abs(axis.x) > Mathf.Abs(axis.y) ? axis.x : axis.y;
        var controller = BasisLocalPlayer.Instance.LocalCharacterDriver;
        controller.SetMovementSpeedMultiplier(largestValue);
    }

    /// <summary>
    /// Sets the character movement vector from the primary 2D input.
    /// </summary>
    /// <param name="current">Current input snapshot.</param>
    /// <param name="last">Previous input snapshot.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetMovementVectorFromPrimary2DAxis(ref BasisInputState current, ref BasisInputState last)
    {
        BasisLocalCharacterDriver controller = BasisLocalPlayer.Instance.LocalCharacterDriver;
        controller.SetMovementVector(current.Primary2DAxisDeadZoned);
    }

    /// <summary>
    /// Updates the character movement speed.
    /// </summary>
    /// <param name="current">Current input snapshot.</param>
    /// <param name="last">Previous input snapshot.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TickMovementSpeed(ref BasisInputState current, ref BasisInputState last)
    {
        var controller = BasisLocalPlayer.Instance.LocalCharacterDriver;
        controller.UpdateMovementSpeed(true);
    }

    /// <summary>
    /// Toggles the hamburger menu on secondary button release.
    /// </summary>
    /// <param name="current">Current input snapshot.</param>
    /// <param name="last">Previous input snapshot.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ToggleHamburgerOnSecondaryRelease(ref BasisInputState current, ref BasisInputState last)
    {
        if (current.SecondaryButtonGetState == false && last.SecondaryButtonGetState)
        {

            Basis.BasisUI.BasisMainMenu.Toggle();
        }
    }

    /// <summary>
    /// Toggles the microphone pause state on primary button release when not hovering UI.
    /// </summary>
    /// <param name="current">Current input snapshot.</param>
    /// <param name="last">Previous input snapshot.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ToggleMicOnPrimaryReleaseIfNoHover(ref BasisInputState current, ref BasisInputState last)
    {
        if (BasisInputModuleHandler.Instance.HasHoverONInput == false)
        {
            switch (SMDMicrophone.Current.TalkMode)
            {
                case SMDMicrophone.BasisMicrophoneMode.OnActivation:
                    if (current.PrimaryButtonGetState == false && last.PrimaryButtonGetState)
                    {
                        // Simple toggle on release
                        BasisLocalMicrophoneDriver.ToggleIsPaused();
                    }
                    break;

                case SMDMicrophone.BasisMicrophoneMode.PushToTalk:
                    // Button up edge
                    if (current.PrimaryButtonGetState)
                    {
                        if (BasisLocalMicrophoneDriver.isPaused)
                        {
                            BasisLocalMicrophoneDriver.ToggleIsPaused();
                        }
                    }
                    else
                    {
                        if (BasisLocalMicrophoneDriver.isPaused == false && current.PrimaryButtonGetState == false)
                        {
                            BasisLocalMicrophoneDriver.ToggleIsPaused();
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Sets the character rotation from the primary 2D input.
    /// </summary>
    /// <param name="current">Current input snapshot.</param>
    /// <param name="last">Previous input snapshot.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RotateFromPrimary2DAxis(ref BasisInputState current, ref BasisInputState last)
    {
        var driver = BasisLocalPlayer.Instance.LocalCharacterDriver;
        driver.Rotation = current.Primary2DAxisButterfly;
    }

    /// <summary>
    /// Triggers the jump handler while the primary button is held.
    /// </summary>
    /// <param name="current">Current input snapshot.</param>
    /// <param name="last">Previous input snapshot.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void JumpOnPrimaryButton(ref BasisInputState current, ref BasisInputState last)
    {
        BasisLocalPlayer.Instance.LocalCharacterDriver.IsJumpHeld = current.PrimaryButtonGetState;
        if (current.PrimaryButtonGetState)
        {
            BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJumpRequest();
        }
    }

    private static readonly InputAction[] s_ActionImplArray = new InputAction[(int)ActionId.Count]
    {
        SetMovementSpeedMultiplierFromPrimary2DAxis,   // 0
        SetMovementVectorFromPrimary2DAxis,            // 1
        TickMovementSpeed,                             // 2
        ToggleHamburgerOnSecondaryRelease,             // 3
        ToggleMicOnPrimaryReleaseIfNoHover,            // 4
        RotateFromPrimary2DAxis,                       // 5
        JumpOnPrimaryButton                            // 6
    };

    private static readonly Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> s_ActionToRoles = new Dictionary<ActionId, HashSet<BasisBoneTrackedRole>>(capacity: 16);
    private static readonly Dictionary<BasisBoneTrackedRole, List<ActionId>> s_RoleToActions = new Dictionary<BasisBoneTrackedRole, List<ActionId>>(capacity: 8);
    private static readonly Dictionary<BasisBoneTrackedRole, InputAction[]> s_RoleToCompiled = new Dictionary<BasisBoneTrackedRole, InputAction[]>(capacity: 8);

    private static readonly List<ActionId> s_EmptyActions = new List<ActionId>(0);
    private static readonly List<BasisBoneTrackedRole> s_EmptyRoles = new List<BasisBoneTrackedRole>(0);
    private static readonly InputAction[] s_EmptyImpls = Array.Empty<InputAction>();
    private static bool s_SuppressRebuild;

    /// <summary>
    /// Rebuilds and caches the compiled action delegate array for a single role.
    /// </summary>
    /// <param name="role">The role to rebuild for.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RebuildCompiledActionsForRole(BasisBoneTrackedRole role)
    {
        if (!s_RoleToActions.TryGetValue(role, out var list) || list == null || list.Count == 0)
        {
            s_RoleToCompiled[role] = s_EmptyImpls;
            return;
        }

        int count = list.Count;
        var compiled = new InputAction[count];

        for (int Index = 0; Index < count; Index++)
        {
            var action = list[Index];
            compiled[Index] = s_ActionImplArray[(int)action];
        }

        s_RoleToCompiled[role] = compiled;
    }

    /// <summary>
    /// Rebuilds and caches compiled action delegates for all roles.
    /// </summary>
    private static void RebuildAllCompiled()
    {
        foreach (var kvp in s_RoleToActions)
        {
            RebuildCompiledActionsForRole(kvp.Key);
        }
    }

    /// <summary>
    /// Deletes the saved bindings file if it exists.
    /// </summary>
    public static void DeleteSaveFile()
    {
        if (!File.Exists(SavePath))
        {
            return;
        }

        try
        {
            File.Delete(SavePath);
            BasisDebug.Log($"Bindings Deleted {SavePath}", BasisDebug.LogTag.Input);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Bindings Failed to delete save file: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves all current (action, role) bindings to disk.
    /// </summary>
    public static async Task SaveFromDriver()
    {
        List<BasisBindingRecord> list = new List<BasisBindingRecord>(32);

        foreach (var pair in s_ActionToRoles)
        {
            ActionId action = pair.Key;

            if (action != ActionId.Count)
            {
                foreach (var role in pair.Value)
                {
                    list.Add(new BasisBindingRecord
                    {
                        action = action.ToString(),
                        role = role.ToString()
                    });
                }
            }
        }

        BindingWrapper wrapper = new BindingWrapper { records = list.ToArray() };
        await WriteWrapperToDisk(wrapper);
    }

    /// <summary>
    /// Loads bindings from disk and applies them to the driver.
    /// </summary>
    public static async Task LoadApplyToDriverAsync()
    {
        if (!File.Exists(SavePath))
        {
            return;
        }

        BindingWrapper wrapper;

        try
        {
            string json = await File.ReadAllTextAsync(SavePath);

            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            wrapper = JsonUtility.FromJson<BindingWrapper>(json);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Bindings Failed to read/parse bindings file: {ex.Message}", BasisDebug.LogTag.Input);
            await SaveFromDriver();
            return;
        }

        if (wrapper.records == null || wrapper.records.Length == 0)
        {
            return;
        }

        s_SuppressRebuild = true;

        for (int Index = 0; Index < wrapper.records.Length; Index++)
        {
            var rec = wrapper.records[Index];

            if (EnumTryParse(rec.action, out ActionId action) && EnumTryParse(rec.role, out BasisBoneTrackedRole role))
            {
                Bind(action, role);
            }
        }

        s_SuppressRebuild = false;
        RebuildAllCompiled();
    }

    /// <summary>
    /// Writes the wrapper to disk as JSON.
    /// </summary>
    /// <param name="wrapper">The container of binding records.</param>
    private static async Task WriteWrapperToDisk(BindingWrapper wrapper)
    {
        string json = JsonUtility.ToJson(wrapper, prettyPrint: true);

        try
        {
            string dir = Path.GetDirectoryName(SavePath);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(SavePath, json);

#if UNITY_EDITOR
            BasisDebug.Log($"Bindings Saved {wrapper.records?.Length ?? 0} bindings to {SavePath}", BasisDebug.LogTag.Input);
#endif
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Bindings Failed to save bindings to disk: {ex.Message}", BasisDebug.LogTag.Input);
        }
    }

    /// <summary>
    /// Tries to parse an enum value from a string in a Unity and version-friendly way.
    /// </summary>
    /// <typeparam name="TEnum">Enum type to parse.</typeparam>
    /// <param name="s">Input string.</param>
    /// <param name="value">Parsed enum value on success; default on failure.</param>
    /// <returns><c>true</c> if parsed; otherwise <c>false</c>.</returns>
    private static bool EnumTryParse<TEnum>(string s, out TEnum value) where TEnum : struct
    {
#if UNITY_2021_2_OR_NEWER
        return Enum.TryParse(s, true, out value);
#else
        try
        {
            value = (TEnum)Enum.Parse(typeof(TEnum), s, true);
            return true;
        }
        catch
        {
            value = default;
            return false;
        }
#endif
    }

    /// <summary>
    /// Serializable record describing a single (action, role) binding.
    /// </summary>
    [Serializable]
    public struct BasisBindingRecord
    {
        /// <summary>
        /// Action identifier (string form of <see cref="ActionId"/>).
        /// </summary>
        public string action;

        /// <summary>
        /// Role identifier (string form of <see cref="BasisBoneTrackedRole"/>).
        /// </summary>
        public string role;
    }

    /// <summary>
    /// Serializable wrapper used for JSON persistence of bindings.
    /// </summary>
    [Serializable]
    public struct BindingWrapper
    {
        /// <summary>
        /// Array of (action, role) binding records.
        /// </summary>
        public BasisBindingRecord[] records;
    }
}
