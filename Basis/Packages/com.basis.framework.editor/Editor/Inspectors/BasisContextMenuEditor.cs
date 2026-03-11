using Basis.Scripts.BasisSdk.Interactions;
using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Right-click context menu in the Hierarchy to add Basis component presets to GameObjects.
/// Right-click a GameObject in the Hierarchy → Basis → [Component Type]
/// Automatically attaches all required dependencies for the selected preset.
/// </summary>
public static class BasisContextMenuEditor
{
    #region Type Resolution

    /// <summary>
    /// Finds a Component-derived type by name across all loaded assemblies.
    /// Used for types in packages that may not be directly referenced by the editor assembly.
    /// </summary>
    static Type FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == name && typeof(Component).IsAssignableFrom(t))
                        return t;
                }
            }
            catch
            {
                // ReflectionTypeLoadException on some assemblies, safe to skip
            }
        }
        return null;
    }

    /// <summary>
    /// Ensures a component of type T exists on the GameObject. Adds it with Undo support if missing.
    /// </summary>
    static T EnsureComponent<T>(GameObject go) where T : Component
    {
        if (!go.TryGetComponent<T>(out var c))
            c = Undo.AddComponent<T>(go);
        return c;
    }

    /// <summary>
    /// Ensures a component exists by type name (resolved via reflection).
    /// Returns the existing or newly added component, or null if the type was not found.
    /// </summary>
    static Component EnsureComponentByName(GameObject go, string typeName)
    {
        Type type = FindType(typeName);
        if (type == null)
        {
            Debug.LogWarning($"[Basis] Component type not found: {typeName}. Is the package installed?");
            return null;
        }
        if (go.TryGetComponent(type, out var existing))
            return existing;
        return Undo.AddComponent(go, type);
    }

    /// <summary>
    /// Ensures at least one Collider exists on the GameObject. Adds a BoxCollider if none found.
    /// </summary>
    static void EnsureCollider(GameObject go)
    {
        if (!go.TryGetComponent<Collider>(out _))
            Undo.AddComponent<BoxCollider>(go);
    }

    #endregion

    #region Validation

    // All context menu items require at least one selected GameObject.
    [MenuItem("GameObject/Basis/Pickup", true)]
    [MenuItem("GameObject/Basis/Pickup (Joint)", true)]
    [MenuItem("GameObject/Basis/Seat", true)]
    [MenuItem("GameObject/Basis/Avatar Pedestal", true)]
    [MenuItem("GameObject/Basis/Interactable Button", true)]
    [MenuItem("GameObject/Basis/Object Sync", true)]
    [MenuItem("GameObject/Basis/Seat Sync", true)]
    [MenuItem("GameObject/Basis/Scene", true)]
    [MenuItem("GameObject/Basis/Avatar", true)]
    [MenuItem("GameObject/Basis/Prop", true)]
    [MenuItem("GameObject/Basis/Mirror", true)]
    static bool Validate() => Selection.activeGameObject != null;

    #endregion

    #region Interaction Presets

    /// <summary>
    /// Adds a standard physics-based pickup with network sync.
    /// Components: BoxCollider (if needed) + Rigidbody + BasisPickupInteractable + BasisObjectSyncNetworking
    /// </summary>
    [MenuItem("GameObject/Basis/Pickup", false, 0)]
    static void AddPickup()
    {
        foreach (var go in Selection.gameObjects)
        {
            EnsureCollider(go);
            var rb = EnsureComponent<Rigidbody>(go);
            var pickup = EnsureComponent<BasisPickupInteractable>(go);
            pickup.RigidRef = rb;
            EnsureComponent<BasisObjectSyncNetworking>(go);
            EditorUtility.SetDirty(go);
        }
    }

    /// <summary>
    /// Adds a joint-based pickup for physics-constrained manipulation.
    /// Components: BoxCollider (if needed) + Rigidbody + ConfigurableJoint + BasisPickupJointInteractable
    /// </summary>
    [MenuItem("GameObject/Basis/Pickup (Joint)", false, 1)]
    static void AddPickupJoint()
    {
        foreach (var go in Selection.gameObjects)
        {
            EnsureCollider(go);
            var rb = EnsureComponent<Rigidbody>(go);
            var joint = EnsureComponent<ConfigurableJoint>(go);
            var pickup = EnsureComponent<BasisPickupJointInteractable>(go);
            pickup.RigidRef = rb;
            pickup.ColliderRef = go.GetComponent<Collider>();
            joint.autoConfigureConnectedAnchor = false;
            joint.configuredInWorldSpace = true;
            EditorUtility.SetDirty(go);
        }
    }

    /// <summary>
    /// Adds a seat with network sync for multiplayer seating.
    /// Components: BoxCollider (if needed) + BasisSeat + BasisSeatSync
    /// </summary>
    [MenuItem("GameObject/Basis/Seat", false, 2)]
    static void AddSeat()
    {
        foreach (var go in Selection.gameObjects)
        {
            EnsureCollider(go);
            var seat = EnsureComponent<BasisSeat>(go);
            var sync = EnsureComponent<BasisSeatSync>(go);
            sync.Seat = seat;
            EditorUtility.SetDirty(go);
        }
    }

    /// <summary>
    /// Adds an avatar pedestal for displaying and loading avatars.
    /// Components: BasisAvatarPedestal (auto-creates its own CapsuleCollider at runtime)
    /// </summary>
    [MenuItem("GameObject/Basis/Avatar Pedestal", false, 3)]
    static void AddAvatarPedestal()
    {
        foreach (var go in Selection.gameObjects)
        {
            EnsureComponent<BasisAvatarPedestal>(go);
            EditorUtility.SetDirty(go);
        }
    }

    /// <summary>
    /// Adds an interactable button with visual feedback.
    /// Components: BoxCollider (if needed) + MeshRenderer (if needed) + BasisInteractableButton
    /// </summary>
    [MenuItem("GameObject/Basis/Interactable Button", false, 4)]
    static void AddButton()
    {
        foreach (var go in Selection.gameObjects)
        {
            EnsureCollider(go);
            EnsureComponent<MeshRenderer>(go);
            EnsureComponentByName(go, "BasisInteractableButton");
            EditorUtility.SetDirty(go);
        }
    }

    #endregion

    #region Networking

    /// <summary>
    /// Adds object sync networking for synchronizing transform state across clients.
    /// Requires a BasisPickupInteractable on the same GameObject or children.
    /// Components: BasisObjectSyncNetworking
    /// </summary>
    [MenuItem("GameObject/Basis/Object Sync", false, 20)]
    static void AddObjectSync()
    {
        foreach (var go in Selection.gameObjects)
        {
            EnsureComponent<BasisObjectSyncNetworking>(go);
            EditorUtility.SetDirty(go);
        }
    }

    /// <summary>
    /// Adds seat sync networking for synchronizing seat occupancy across clients.
    /// Requires a BasisSeat on the same GameObject.
    /// Components: BasisSeatSync
    /// </summary>
    [MenuItem("GameObject/Basis/Seat Sync", false, 21)]
    static void AddSeatSync()
    {
        foreach (var go in Selection.gameObjects)
        {
            var sync = EnsureComponent<BasisSeatSync>(go);
            if (go.TryGetComponent<BasisSeat>(out var seat))
            {
                sync.Seat = seat;
            }
            EditorUtility.SetDirty(go);
        }
    }

    #endregion

    #region SDK Types

    /// <summary>
    /// Adds the BasisScene component for scene configuration (spawn points, respawn, audio).
    /// Components: BasisScene
    /// </summary>
    [MenuItem("GameObject/Basis/Scene", false, 40)]
    static void AddScene()
    {
        foreach (var go in Selection.gameObjects)
        {
            EnsureComponentByName(go, "BasisScene");
            EditorUtility.SetDirty(go);
        }
    }

    /// <summary>
    /// Adds the BasisAvatar component for avatar setup.
    /// Components: Animator (if needed) + BasisAvatar
    /// </summary>
    [MenuItem("GameObject/Basis/Avatar", false, 41)]
    static void AddAvatar()
    {
        foreach (var go in Selection.gameObjects)
        {
            EnsureComponent<Animator>(go);
            EnsureComponentByName(go, "BasisAvatar");
            EditorUtility.SetDirty(go);
        }
    }

    /// <summary>
    /// Adds the BasisProp component for networked prop objects.
    /// Components: BasisProp
    /// </summary>
    [MenuItem("GameObject/Basis/Prop", false, 42)]
    static void AddProp()
    {
        foreach (var go in Selection.gameObjects)
        {
            EnsureComponentByName(go, "BasisProp");
            EditorUtility.SetDirty(go);
        }
    }

    #endregion

    #region World Components

    /// <summary>
    /// Adds a mirror/reflection component for real-time reflections.
    /// Components: MeshFilter (if needed) + MeshRenderer (if needed) + BasisSDKMirror
    /// </summary>
    [MenuItem("GameObject/Basis/Mirror", false, 60)]
    static void AddMirror()
    {
        foreach (var go in Selection.gameObjects)
        {
            EnsureComponent<MeshFilter>(go);
            EnsureComponent<MeshRenderer>(go);
            EnsureComponentByName(go, "BasisSDKMirror");
            EditorUtility.SetDirty(go);
        }
    }

    #endregion
}
