using Unity.Scripting.LifecycleManagement;
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
[AutoStaticsCleanup]
public static partial class BasisActionDriver
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
    public static string SavePath => SavePathFor(BasisDeviceManagement.StaticCurrentMode);

    /// <summary>
    /// Full path to the bindings file of <paramref name="mode"/>.
    /// </summary>
    public static string SavePathFor(string mode) => Path.Combine(Application.persistentDataPath, FolderPath, mode, FileName);

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

    public static string BindingsMode { get; private set; }

    public static event Action<string> OnBindingsLoaded;

    private static int s_LoadCount;

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
        /// Descends while the grip button is held (fly/noclip modes).
        /// </summary>
        DescendOnGripButton = 7,

        /// <summary>
        /// Keep this as the last entry for sizing arrays.
        /// </summary>
        Count = 8
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
        string mode = BasisDeviceManagement.StaticCurrentMode;
        int load = ++s_LoadCount;
        BindingsMode = null;
        ApplyToDriver(mode, default);

        string path = SavePathFor(mode);
        BindingWrapper? saved = null;
        if (File.Exists(path))
        {
            saved = await ReadWrapperAsync(path);
            if (load != s_LoadCount)
            {
                return;
            }
        }

        if (saved.HasValue)
        {
            ApplyToDriver(mode, saved.Value);
            BindingsMode = mode;
        }
        else
        {
            BindingsMode = mode;
            await SaveFromDriver();
        }

        if (load == s_LoadCount)
        {
            OnBindingsLoaded?.Invoke(mode);
        }
    }

    private static void ApplyToDriver(string mode, BindingWrapper saved)
    {
        s_ActionToRoles.Clear();
        s_RoleToActions.Clear();
        s_RoleToCompiled.Clear();

        s_SuppressRebuild = true;
        ComposeBindings(mode, saved, Bind);
        s_SuppressRebuild = false;
        RebuildAllCompiled();
    }

    internal static void ComposeBindings(string mode, BindingWrapper saved, Action<ActionId, BasisBoneTrackedRole> bind)
    {
        HashSet<(ActionId, BasisBoneTrackedRole)> unbound = new HashSet<(ActionId, BasisBoneTrackedRole)>();
        if (saved.unbound != null)
        {
            for (int Index = 0; Index < saved.unbound.Length; Index++)
            {
                if (TryReadRecord(saved.unbound[Index], out ActionId action, out BasisBoneTrackedRole role))
                {
                    unbound.Add((action, role));
                }
            }
        }

        BindDefaults(mode, (action, role) =>
        {
            if (!unbound.Contains((action, role)))
            {
                bind(action, role);
            }
        });

        if (saved.records == null)
        {
            return;
        }

        for (int Index = 0; Index < saved.records.Length; Index++)
        {
            if (TryReadRecord(saved.records[Index], out ActionId action, out BasisBoneTrackedRole role))
            {
                bind(action, role);
            }
        }
    }

    internal static BindingWrapper BuildWrapper(string mode, Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> bindings)
    {
        List<BasisBindingRecord> records = new List<BasisBindingRecord>(32);
        foreach (var pair in bindings)
        {
            if (pair.Key < 0 || pair.Key >= ActionId.Count)
            {
                continue;
            }
            foreach (var role in pair.Value)
            {
                records.Add(new BasisBindingRecord { action = pair.Key.ToString(), role = role.ToString() });
            }
        }

        List<BasisBindingRecord> unbound = new List<BasisBindingRecord>(4);
        BindDefaults(mode, (action, role) =>
        {
            if (!bindings.TryGetValue(action, out var roles) || !roles.Contains(role))
            {
                unbound.Add(new BasisBindingRecord { action = action.ToString(), role = role.ToString() });
            }
        });

        return new BindingWrapper { records = records.ToArray(), unbound = unbound.ToArray() };
    }

    private static bool TryReadRecord(BasisBindingRecord record, out ActionId action, out BasisBoneTrackedRole role)
    {
        role = default;
        return EnumTryParse(record.action, out action)
            && action >= 0 && action < ActionId.Count
            && EnumTryParse(record.role, out role)
            && Enum.IsDefined(typeof(BasisBoneTrackedRole), role);
    }

    private static async Task<BindingWrapper?> ReadWrapperAsync(string path)
    {
        try
        {
            string json = await BasisFiles.ReadAllTextAsync(path);
            if (string.IsNullOrEmpty(json))
            {
                return default(BindingWrapper);
            }
            return JsonUtility.FromJson<BindingWrapper>(json);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Bindings Failed to read/parse bindings file {path}: {ex.Message}", BasisDebug.LogTag.Input);
            return null;
        }
    }

    /// <summary>
    /// The default bindings of <paramref name="mode"/>, handed to <paramref name="bind"/> one pair at a time.
    /// </summary>
    private static void BindDefaults(string mode, Action<ActionId, BasisBoneTrackedRole> bind)
    {
        bind(ActionId.SetMovementSpeedMultiplierFromPrimary2DAxis, BasisBoneTrackedRole.LeftHand);
        bind(ActionId.SetMovementVectorFromPrimary2DAxis, BasisBoneTrackedRole.LeftHand);
        bind(ActionId.TickMovementSpeed, BasisBoneTrackedRole.LeftHand);
        bind(ActionId.ToggleHamburgerOnSecondaryRelease, BasisBoneTrackedRole.LeftHand);

        bind(ActionId.RotateFromPrimary2DAxis, BasisBoneTrackedRole.RightHand);
        bind(ActionId.JumpOnPrimaryButton, BasisBoneTrackedRole.RightHand);
        bind(ActionId.DescendOnGripButton, BasisBoneTrackedRole.RightHand);

        bind(ActionId.ToggleMicOnPrimaryReleaseIfNoHover,
            BasisDeviceManagement.IsVRMode(mode) ? BasisBoneTrackedRole.LeftHand : BasisBoneTrackedRole.CenterEye);
    }

    /// <summary>
    /// Reads the bindings of <paramref name="mode"/> without touching the live driver, built the
    /// same way <see cref="LoadBindings"/> builds them (defaults, then the saved file on top). For
    /// the current mode this is a copy of the live bindings. Used by the settings page to edit a
    /// mode the player is not in.
    /// </summary>
    public static async Task<Dictionary<ActionId, HashSet<BasisBoneTrackedRole>>> ReadBindingsForMode(string mode)
    {
        var map = new Dictionary<ActionId, HashSet<BasisBoneTrackedRole>>();
        void Add(ActionId action, BasisBoneTrackedRole role)
        {
            if (!map.TryGetValue(action, out var roles))
            {
                roles = new HashSet<BasisBoneTrackedRole>();
                map[action] = roles;
            }
            roles.Add(role);
        }

        if (IsLoadedMode(mode))
        {
            foreach (var pair in s_ActionToRoles)
            {
                foreach (var role in pair.Value)
                {
                    Add(pair.Key, role);
                }
            }
            return map;
        }

        string path = SavePathFor(mode);
        BindingWrapper? saved = File.Exists(path) ? await ReadWrapperAsync(path) : null;
        ComposeBindings(mode, saved ?? default, Add);
        return map;
    }

    private static bool IsLoadedMode(string mode) => mode != null && string.Equals(mode, BindingsMode, StringComparison.Ordinal);

    /// <summary>
    /// Saves <paramref name="bindings"/> as the bindings file of <paramref name="mode"/>.
    /// </summary>
    public static async Task WriteBindingsForMode(string mode, Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> bindings)
    {
        if (!IsLoadedMode(mode))
        {
            await WriteWrapperToDisk(BuildWrapper(mode, bindings), SavePathFor(mode));
            return;
        }

        List<(ActionId action, BasisBoneTrackedRole role)> removed = new List<(ActionId action, BasisBoneTrackedRole role)>();
        foreach (var pair in s_ActionToRoles)
        {
            foreach (var role in pair.Value)
            {
                if (!bindings.TryGetValue(pair.Key, out var roles) || !roles.Contains(role))
                {
                    removed.Add((pair.Key, role));
                }
            }
        }

        s_SuppressRebuild = true;
        for (int Index = 0; Index < removed.Count; Index++)
        {
            Unbind(removed[Index].action, removed[Index].role);
        }
        foreach (var pair in bindings)
        {
            if (pair.Key < 0 || pair.Key >= ActionId.Count)
            {
                continue;
            }
            foreach (var role in pair.Value)
            {
                Bind(pair.Key, role);
            }
        }
        s_SuppressRebuild = false;
        RebuildAllCompiled();

        await SaveFromDriver();
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

        s_DispatchRole = trackedRole;
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
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
    /// Dispatches a role's actions once per frame using the combined input state of every device
    /// currently holding that role. This makes edge-triggered actions (menu toggle, mic, jump) fire
    /// exactly once per role even when two devices share a hand role — e.g. a duplicate controller
    /// left over from a SteamVR reconnect, or hand-tracking coexisting with a controller. Booleans
    /// are OR'd across holders so either one can trigger an edge (see <see cref="BasisInputState.MergeFrom"/>).
    /// With a single holder (the common case) the aggregate equals that device, so behaviour is
    /// unchanged. Used for roles that may legitimately have multiple holders; other roles dispatch
    /// directly through <see cref="UpdatePlayerControl"/>.
    /// </summary>
    /// <param name="role">The role whose actions will be executed.</param>
    public static void UpdatePlayerControlForRole(BasisBoneTrackedRole role)
    {
        if (!s_RoleToCompiled.TryGetValue(role, out var compiled) || compiled.Length == 0)
        {
            return;
        }

        // Coalesce: only the first device to reach dispatch for this role this frame runs it.
        // The aggregate below already folds in every other holder, so later devices no-op.
        int frame = Time.frameCount;
        if (s_RoleDispatchFrame.TryGetValue(role, out int lastFrame) && lastFrame == frame)
        {
            return;
        }
        s_RoleDispatchFrame[role] = frame;

        if (!TryGetRoleState(role, s_AggCurrent, s_AggLast))
        {
            return;
        }

        UpdatePlayerControl(role, ref s_AggCurrent, ref s_AggLast);
    }

    /// <summary>
    /// Fills <paramref name="current"/> and <paramref name="last"/> with the combined state of every
    /// device holding <paramref name="role"/>, the same merge the role dispatch acts on, so a reader
    /// sees exactly the input that drives the player. False when no device holds the role.
    /// </summary>
    public static bool TryGetRoleState(BasisBoneTrackedRole role, BasisInputState current, BasisInputState last)
    {
        BasisDeviceManagement management = BasisDeviceManagement.Instance;
        if (management == null || current == null || last == null)
        {
            return false;
        }

        var devices = management.AllInputDevices;
        int count = devices.Count;
        bool seeded = false;
        for (int Index = 0; Index < count; Index++)
        {
            BasisInput device = devices[Index];
            if (!device.TryGetRole(out BasisBoneTrackedRole deviceRole) || deviceRole != role)
            {
                continue;
            }
            if (!seeded)
            {
                device.CurrentInputState.CopyTo(current);
                device.LastInputState.CopyTo(last);
                seeded = true;
            }
            else
            {
                current.MergeFrom(device.CurrentInputState);
                last.MergeFrom(device.LastInputState);
            }
        }
        return seeded;
    }

    public static async void ResetBindingsToDefaultsAsyncIgnored()
    {
      await  ResetBindingsToDefaultsAsync();
    }
    // <summary>
    /// Resets bindings for the current device mode back to defaults:
    /// deletes the saved bindings file and reloads defaults (which will be saved).
    /// </summary>
    public static async Task ResetBindingsToDefaultsAsync()
    {
        // Remove any persisted overrides for this mode
        DeleteSaveFile();

        // Rebuild driver state from defaults (and SaveFromDriver() will run because file is gone)
        await LoadBindings();

        BasisDebug.Log($"Bindings reset to defaults for mode {BasisDeviceManagement.StaticCurrentMode}.",
            BasisDebug.LogTag.Input);
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
        Vector2 axis = LocomotionAxis(current.Primary2DAxisDeadZoned);
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
        controller.SetMovementVector(LocomotionAxis(current.Primary2DAxisDeadZoned));
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
#if !BASIS_DISABLE_MICROPHONE
        // Desktop mute is handled by the rebindable ToggleMicMute action in BasisLocalInputActions.
        // This path stays for VR so the primary controller release still toggles mute there.
        if (BasisDeviceManagement.IsCurrentModeVR() == false)
            return;
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
#endif
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
        driver.Rotation = LocomotionAxis(current.Primary2DAxisButterfly);
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

    /// <summary>
    /// Sets the descend flag while the grip button is held, allowing descent in fly/noclip modes.
    /// </summary>
    /// <param name="current">Current input snapshot.</param>
    /// <param name="last">Previous input snapshot.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DescendOnGripButton(ref BasisInputState current, ref BasisInputState last)
    {
        BasisLocalPlayer.Instance.LocalCharacterDriver.IsDescendHeld = current.GripButton;
    }

    [NoAutoStaticsCleanup] private static readonly InputAction[] s_ActionImplArray = new InputAction[(int)ActionId.Count]
    {
        SetMovementSpeedMultiplierFromPrimary2DAxis,   // 0
        SetMovementVectorFromPrimary2DAxis,            // 1
        TickMovementSpeed,                             // 2
        ToggleHamburgerOnSecondaryRelease,             // 3
        ToggleMicOnPrimaryReleaseIfNoHover,            // 4
        RotateFromPrimary2DAxis,                       // 5
        JumpOnPrimaryButton,                           // 6
        DescendOnGripButton                            // 7
    };

    private static readonly Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> s_ActionToRoles = new Dictionary<ActionId, HashSet<BasisBoneTrackedRole>>(capacity: 16);
    private static readonly Dictionary<BasisBoneTrackedRole, List<ActionId>> s_RoleToActions = new Dictionary<BasisBoneTrackedRole, List<ActionId>>(capacity: 8);
    private static readonly Dictionary<BasisBoneTrackedRole, InputAction[]> s_RoleToCompiled = new Dictionary<BasisBoneTrackedRole, InputAction[]>(capacity: 8);

    private static readonly List<ActionId> s_EmptyActions = new List<ActionId>(0);
    private static readonly List<BasisBoneTrackedRole> s_EmptyRoles = new List<BasisBoneTrackedRole>(0);
    [NoAutoStaticsCleanup] private static readonly InputAction[] s_EmptyImpls = Array.Empty<InputAction>();
    private static bool s_SuppressRebuild;

    // Per-role frame guard + reusable aggregate buffers for UpdatePlayerControlForRole: a role's
    // actions run once per frame against the combined state of all devices that hold the role.
    private static readonly Dictionary<BasisBoneTrackedRole, int> s_RoleDispatchFrame = new Dictionary<BasisBoneTrackedRole, int>(capacity: 4);
    private static  BasisInputState s_AggCurrent = new BasisInputState();
    private static  BasisInputState s_AggLast = new BasisInputState();

    /// <summary>
    /// The role whose actions are running right now. Action delegates are handed input state and
    /// nothing else, so this is how they know whose stick they are reading — see
    /// <see cref="LocomotionAxis"/>.
    /// </summary>
    private static BasisBoneTrackedRole s_DispatchRole;

    /// <summary>
    /// Stick input for the role being dispatched, dropped entirely while that hand's stick is
    /// driving a settings slider (<see cref="Basis.BasisUI.BasisPanelJoystickBind"/>). Tuning a
    /// value must not also move the player.
    ///
    /// <para>Forward/back alone was not enough. A thumbstick pushed forward is never pushed
    /// exactly forward, and the sideways part that survived still reached
    /// <see cref="BasisLocalCharacterDriver.MovementVector"/>.x to strafe and
    /// <see cref="BasisLocalCharacterDriver.Rotation"/>.x to snap-turn — turning was never
    /// suppressed at all, since it reads the axis this kept. It also left
    /// <see cref="BasisLocalCharacterDriver.IsLocomoting"/> true for the whole sweep, which is
    /// what unplants the feet.</para>
    ///
    /// <para>Only the bound hand goes quiet. The other stick keeps whatever it was doing, so a
    /// bind costs locomotion on the hand that was given away and nothing else.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 LocomotionAxis(Vector2 axis)
    {
        return Basis.BasisUI.BasisPanelJoystickBind.CapturesStick(s_DispatchRole) ? Vector2.zero : axis;
    }

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
        string mode = BindingsMode ?? BasisDeviceManagement.StaticCurrentMode;
        await WriteWrapperToDisk(BuildWrapper(mode, s_ActionToRoles), SavePathFor(mode));
    }

    /// <summary>
    /// Loads bindings from disk and applies them to the driver.
    /// </summary>
    public static async Task LoadApplyToDriverAsync()
    {
        string mode = BindingsMode ?? BasisDeviceManagement.StaticCurrentMode;
        string path = SavePathFor(mode);
        if (!File.Exists(path))
        {
            return;
        }

        int load = s_LoadCount;
        BindingWrapper? saved = await ReadWrapperAsync(path);
        if (load != s_LoadCount)
        {
            return;
        }

        if (!saved.HasValue)
        {
            await SaveFromDriver();
            return;
        }

        ApplyToDriver(mode, saved.Value);
        OnBindingsLoaded?.Invoke(mode);
    }

    /// <summary>
    /// Writes the wrapper to disk as JSON.
    /// </summary>
    /// <param name="wrapper">The container of binding records.</param>
    /// <param name="path">The bindings file to write.</param>
    private static async Task WriteWrapperToDisk(BindingWrapper wrapper, string path)
    {
        string json = JsonUtility.ToJson(wrapper, prettyPrint: true);

        try
        {
            string dir = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await BasisFiles.WriteAllTextAsync(path, json);

#if UNITY_EDITOR
            BasisDebug.Log($"Bindings Saved {wrapper.records?.Length ?? 0} bindings to {path}", BasisDebug.LogTag.Input);
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

        public BasisBindingRecord[] unbound;
    }
}
