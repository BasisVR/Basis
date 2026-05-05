using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public static class BasisGizmoManager
{
    public static Dictionary<int, BasisGizmos> Gizmos = new Dictionary<int, BasisGizmos>();
    public static Dictionary<int, BasisLineGizmos> GizmosLine = new Dictionary<int, BasisLineGizmos>();
    private static int _nextID = 0; // Counter for unique IDs.

    public static string MaterialGizmo = "GizmoMaterial";
    public static string GameobjectGizmo = "Packages/com.basis.gizmos/Gizmo.prefab";
    public static string GameobjectGizmoLine = "Packages/com.basis.gizmos/GizmoLine.prefab";
    public static Action<bool> OnUseGizmosChanged; // Callback delegate.
    public static GameObject LoadedLineGizmo;
    public static GameObject LoadedGizmo;
    public static GameObject Parent;
    public static void TryCreateParent()
    {
        if (Parent == null)
        {
            Parent = new GameObject("Parent Of Debug Data");
        }
    }
    public static void DestroyParent()
    {
        if(Parent != null)
        {
            GameObject.Destroy(Parent);
        }
    }
    /// <summary>
    /// Creates a new sphere gizmo.
    /// </summary>
    public static bool CreateSphereGizmo(string GizmoName,out int linkedID, Vector3 position, float size, Color color)
    {
        TryCreateParent();
        linkedID = CreateNewID();

        if (Gizmos.ContainsKey(linkedID))
        {
            BasisDebug.LogError($"Gizmo with ID {linkedID} already exists. Use UpdateSphereGizmo to modify it.", BasisDebug.LogTag.Gizmo);
            return false;
        }

        if (LoadedGizmo == null)
        {
            AsyncOperationHandle<GameObject> Loadable = Addressables.LoadAssetAsync<GameObject>(GameobjectGizmo);
            LoadedGizmo = Loadable.WaitForCompletion();
        }
        if (LoadedGizmo == null)
        {
            BasisDebug.LogError($"Failed to load Gizmo prefab from {GameobjectGizmo}", BasisDebug.LogTag.Gizmo);
            return false;
        }

        GameObject tempSphere = UnityEngine.Object.Instantiate(LoadedGizmo, Parent.transform);
        tempSphere.name = $"SphereGizmo_{linkedID}";

        AsyncOperationHandle<Material> materialLoad = Addressables.LoadAssetAsync<Material>(MaterialGizmo);
        Material material = materialLoad.WaitForCompletion();
        if (material == null)
        {
            BasisDebug.LogError($"Failed to load Gizmo material from {MaterialGizmo}", BasisDebug.LogTag.Gizmo);
            UnityEngine.Object.Destroy(tempSphere);
            return false;
        }

        if (tempSphere.TryGetComponent(out BasisGizmos basisGizmos))
        {
            basisGizmos.ConfigureMeshGizmo(material, Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx"), color);
            Gizmos[linkedID] = basisGizmos;
            tempSphere.name = GizmoName;
            basisGizmos.UpdatePosition(position);
            basisGizmos.UpdateSize(Vector3.one * size);

            BasisDebug.Log($"Created SphereGizmo with ID {linkedID}", BasisDebug.LogTag.Gizmo);
            return true;
        }
        else
        {
            BasisDebug.LogError($"Prefab missing BasisGizmos component.", BasisDebug.LogTag.Gizmo);
            UnityEngine.Object.Destroy(tempSphere);
            return false;
        }
    }

    /// <summary>
    /// Updates an existing sphere gizmo.
    /// </summary>
    public static bool UpdateSphereGizmo(int linkedID, Vector3 position,Vector3 Scale)
    {
        if (!Gizmos.TryGetValue(linkedID, out BasisGizmos gizmo))
        {
            BasisDebug.LogError($"No SphereGizmo found with ID {linkedID}. Use CreateSphereGizmo first.", BasisDebug.LogTag.Gizmo);
            return false;
        }

        gizmo.UpdatePosition(position);
        gizmo.UpdateSize(Scale);
        return true;
    }

    /// <summary>
    /// Updates an existing sphere gizmo with rotation included. Use this when
    /// the gizmo is anchored to a bone whose orientation matters
    /// (e.g. calibration spheres so they roll with the avatar's bone instead
    /// of staying axis-aligned in world space).
    /// </summary>
    public static bool UpdateSphereGizmo(int linkedID, Vector3 position, Quaternion rotation, Vector3 Scale)
    {
        if (!Gizmos.TryGetValue(linkedID, out BasisGizmos gizmo))
        {
            BasisDebug.LogError($"No SphereGizmo found with ID {linkedID}. Use CreateSphereGizmo first.", BasisDebug.LogTag.Gizmo);
            return false;
        }

        gizmo.UpdatePositionAndRotation(position, rotation);
        gizmo.UpdateSize(Scale);
        return true;
    }
    public static bool CreateLineGizmo(string GizmoName,int linkedID, Vector3 start, Vector3 end, float width, Color color,GameObject Reference)
    {
        TryCreateParent();
        GameObject gizmoObject = UnityEngine.Object.Instantiate(Reference,Parent.transform);
        if (gizmoObject.TryGetComponent(out BasisLineGizmos basisGizmos))
        {
            LineRenderer lineRenderer = basisGizmos.LineRenderer;
            lineRenderer.positionCount = 2;
            lineRenderer.SetPositions(new[] { start, end });
            lineRenderer.startWidth = width;
            lineRenderer.endWidth = width;
            lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            lineRenderer.startColor = color;
            lineRenderer.endColor = color;
            lineRenderer.name = GizmoName;
            GizmosLine[linkedID] = basisGizmos;

            BasisDebug.Log($"Created LineGizmo with ID {linkedID}", BasisDebug.LogTag.Gizmo);
            return true;
        }
        else
        {
            BasisDebug.LogError($"Prefab missing BasisLineGizmos component.", BasisDebug.LogTag.Gizmo);
            UnityEngine.Object.Destroy(gizmoObject);
            return false;
        }
    }
    public static bool CreateLineGizmo(string GizmoName,out int linkedID, Vector3 start, Vector3 end, float width, Color color)
    {
        linkedID = CreateNewID();
        if (GizmosLine.ContainsKey(linkedID))
        {
            BasisDebug.LogError($"LineGizmo with ID {linkedID} already exists. Use UpdateLineGizmo to modify it.", BasisDebug.LogTag.Gizmo);
            return false;
        }
        if (LoadedLineGizmo == null)
        {
            AsyncOperationHandle<GameObject> LoadAble = Addressables.LoadAssetAsync<GameObject>(GameobjectGizmoLine);
            LoadedLineGizmo = LoadAble.WaitForCompletion();
        }
        if (LoadedLineGizmo == null)
        {
            BasisDebug.LogError($"Failed to load LineGizmo prefab from {GameobjectGizmoLine}", BasisDebug.LogTag.Gizmo);
            return false;
        }
        return CreateLineGizmo(GizmoName,linkedID, start, end, width, color, LoadedLineGizmo);
    }

    /// <summary>
    /// Updates an existing line gizmo.
    /// </summary>
    public static bool UpdateLineGizmo(int linkedID, Vector3 start, Vector3 end)
    {
        if (!GizmosLine.TryGetValue(linkedID, out BasisLineGizmos gizmo))
        {
            BasisDebug.LogError($"No LineGizmo found with ID {linkedID}. Use CreateLineGizmo first.", BasisDebug.LogTag.Gizmo);
            return false;
        }
        if (gizmo == null || gizmo.LineRenderer == null)
        {
            return false;
        }
        gizmo.LineRenderer.SetPosition(0, start);
        gizmo.LineRenderer.SetPosition(1, end);
        return true;
    }

    /// <summary>
    /// Toggles a gizmo's GameObject visibility without destroying it. Used by
    /// sub-toggles that hide/show subsets of gizmos under the master ShowGizmos.
    /// </summary>
    public static void SetGizmoActive(int linkedID, bool active)
    {
        if (Gizmos.TryGetValue(linkedID, out BasisGizmos gizmo) && gizmo != null)
        {
            gizmo.gameObject.SetActive(active);
            return;
        }
        if (GizmosLine.TryGetValue(linkedID, out BasisLineGizmos lineGizmo) && lineGizmo != null)
        {
            lineGizmo.gameObject.SetActive(active);
        }
    }

    /// <summary>
    /// Destroys a gizmo with the specified ID.
    /// </summary>
    public static void DestroyGizmo(int linkedID)
    {
        if (Gizmos.Remove(linkedID, out BasisGizmos gizmo))
        {
            UnityEngine.Object.Destroy(gizmo.gameObject);
            BasisDebug.Log($"Destroyed SphereGizmo with ID {linkedID}", BasisDebug.LogTag.Gizmo);
        }
        else if (GizmosLine.Remove(linkedID, out BasisLineGizmos lineGizmo))
        {
            UnityEngine.Object.Destroy(lineGizmo.gameObject);
            BasisDebug.Log($"Destroyed LineGizmo with ID {linkedID}", BasisDebug.LogTag.Gizmo);
        }
        else
        {
            BasisDebug.LogWarning($"No Gizmo found with ID {linkedID} to destroy.", BasisDebug.LogTag.Gizmo);
        }
    }

    /// <summary>
    /// Creates a new unique ID for a gizmo.
    /// </summary>
    private static int CreateNewID()
    {
        return ++_nextID;
    }
}
