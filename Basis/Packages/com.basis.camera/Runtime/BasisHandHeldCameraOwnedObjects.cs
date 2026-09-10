using System.Collections.Generic;
using UnityEngine;
public partial class BasisHandHeldCamera
{
    private readonly List<Transform> spawnedRoots = new List<Transform>(4);
    public void RegisterSpawnedObject(GameObject spawned)
    {
        if (spawned == null) return;

        Transform root = spawned.transform;
        if (!spawnedRoots.Contains(root)) spawnedRoots.Add(root);
    }
    public void ForgetSpawnedObject(GameObject spawned)
    {
        if (spawned == null) return;

        spawnedRoots.Remove(spawned.transform);
    }
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
