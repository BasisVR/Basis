using System;
using System.Collections.Generic;
using UnityEngine;
public static class ContentPoliceControl
{
    // Reused buffers to avoid allocations (NOT thread-safe; fine for Unity main thread).
    private static readonly List<MonoBehaviour> _monoBuffer = new(256);
    private static readonly List<Collider> _colliderBuffer = new(256);
    private static Transform _stagingRoot;

    private static Transform GetStagingRoot()
    {
        if (_stagingRoot != null) return _stagingRoot;

        var go = new GameObject("__ContentPolice_Staging__");
        go.hideFlags = HideFlags.HideAndDontSave;
        go.SetActive(false);
        _stagingRoot = go.transform;
        return _stagingRoot;
    }
    /// <summary>
    /// Creates a copy of a GameObject, removes any unapproved MonoBehaviours, and returns the cleaned copy through instantiation. 
    /// </summary>
    /// <param name="original"></param>
    /// <param name="checks"></param>
    /// <param name="position"></param>
    /// <param name="rotation"></param>
    /// <param name="modifyScale"></param>
    /// <param name="scale"></param>
    /// <param name="selector"></param>
    /// <param name="parent"></param>
    /// <returns></returns>
    public static GameObject ContentControl(
        GameObject original,
        ChecksRequired checks,
        Vector3 position,
        Quaternion rotation,
        bool modifyScale,
        Vector3 scale,
        BundledContentHolder.Selector selector,
        Transform parent = null)
    {
        if (!checks.UseContentRemoval)
        {
            return parent == null
                ? UnityEngine.Object.Instantiate(original, position, rotation)
                : UnityEngine.Object.Instantiate(original, position, rotation, parent);
        }

        if (!BundledContentHolder.Instance.GetSelector(selector, out ContentPoliceSelector policeCheck))
        {
            BasisDebug.LogError("cant find Police check for " + selector, BasisDebug.LogTag.Event);
            return null;
        }

        // Instantiate under a reusable inactive staging root so it's inactive during cleanup.
        var staging = GetStagingRoot();
        var clone = UnityEngine.Object.Instantiate(original, position, rotation, staging);
        clone.SetActive(false);

        if (modifyScale)
        {
            // Logging is surprisingly expensive in bulk; consider gating this behind a debug flag.
            BasisDebug.Log($"Overriding Default scale is now {scale} for Game object {clone.name}");
            clone.transform.localScale = scale;
        }

        // Cache approved types once.
        HashSet<Type> approved = policeCheck.ApprovedTypes;

        // 1) Disable animator events (only animators)
        if (checks.DisableAnimatorEvents)
        {
            // If you want to avoid alloc here too, add an animator buffer similarly.
            var animators = clone.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                animators[i].fireEvents = false;
            }
        }

        // 2) Remove colliders (only colliders)
        if (checks.RemoveColliders)
        {
            _colliderBuffer.Clear();
            clone.GetComponentsInChildren(true, _colliderBuffer);
            for (int i = 0; i < _colliderBuffer.Count; i++)
            {
                UnityEngine.Object.Destroy(_colliderBuffer[i]);
            }
        }

        // 3) Remove unapproved scripts (only MonoBehaviours)
        _monoBuffer.Clear();
        clone.GetComponentsInChildren(true, _monoBuffer);

        bool isPlaying = Application.isPlaying;

        for (int i = 0; i < _monoBuffer.Count; i++)
        {
            var mb = _monoBuffer[i];
            if (mb == null)
            {
                continue;
            }

            var t = mb.GetType();
            if (!approved.Contains(t))
            {
                // Avoid spamming logs per component in production. Consider counting instead.
                // Debug.LogError($"MonoBehaviour {t.FullName} is not approved and will be removed.");

                if (isPlaying)
                {
                    UnityEngine.Object.Destroy(mb);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(mb);
                }
            }
        }

        // Re-parent to final parent and activate
        clone.transform.SetParent(parent, worldPositionStays: true);
        clone.SetActive(true);

        return clone;
    }
}

/// <summary>
/// Defines the checks required for content control.
/// </summary>
public struct ChecksRequired
{
    public bool UseContentRemoval;
    public bool DisableAnimatorEvents;
    public bool RemoveColliders;

    public ChecksRequired(bool useContentRemoval, bool disableAnimatorEvents, bool removeColliders)
    {
        UseContentRemoval = useContentRemoval;
        DisableAnimatorEvents = disableAnimatorEvents;
        RemoveColliders = removeColliders;
    }
}
