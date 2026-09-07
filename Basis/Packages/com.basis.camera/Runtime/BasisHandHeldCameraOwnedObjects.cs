using System.Collections.Generic;
using UnityEngine;

public partial class BasisHandHeldCamera
{
    private readonly List<Transform> spawnedRoots = new List<Transform>(4);

    /// <summary>
    /// Registers something the camera has put into the world in its own right — the follow puck,
    /// or the wireframe marker's grab handle — so everything the camera aims at can tell the
    /// camera's own furniture apart from the scene.
    /// <para>
    /// These are scene roots, not children, because they have to stay put while the camera moves.
    /// That makes them ordinary world geometry to a raycast, and each carries a collider so it can
    /// be grabbed: both park a hand's width off the lens, so click-to-focus was hitting them before
    /// anything else and racking the focus plane to its minimum on every click.
    /// </para>
    /// </summary>
    public void RegisterSpawnedObject(GameObject spawned)
    {
        if (spawned == null) return;

        Transform root = spawned.transform;
        if (!spawnedRoots.Contains(root)) spawnedRoots.Add(root);
    }

    /// <summary>Drops a registration on the way out. Safe to call for something never registered.</summary>
    public void ForgetSpawnedObject(GameObject spawned)
    {
        if (spawned == null) return;

        spawnedRoots.Remove(spawned.transform);
    }

    /// <summary>
    /// True when the transform is part of this camera, or of anything the camera spawned. Destroyed
    /// registrations are dropped as they are met, so nothing has to unregister on teardown for this
    /// to stay correct.
    /// </summary>
    public bool OwnsTransform(Transform candidate)
    {
        if (candidate == null) return false;
        if (candidate.IsChildOf(transform)) return true;

        for (int index = spawnedRoots.Count - 1; index >= 0; index--)
        {
            Transform root = spawnedRoots[index];
            if (root == null)
            {
                spawnedRoots.RemoveAt(index);
                continue;
            }
            if (candidate.IsChildOf(root)) return true;
        }
        return false;
    }
}
