using System;
using System.Collections.Generic;
using Basis.BasisUI;
using UnityEngine;

public static class BasisHandHeldCameraRegistry
{
    /// <summary>
    /// Catalog Url of the camera prop, as it appears in EmbeddedItemsCatalog. Spawning goes
    /// through the generic prop path, so this is what identifies the request as ours.
    /// </summary>
    public const string SpawnUrl = "Photo Camera";

    private static readonly List<BasisHandHeldCamera> cameras = new List<BasisHandHeldCamera>();

    public static IReadOnlyList<BasisHandHeldCamera> Cameras => cameras;
    public static int Count => cameras.Count;

    public static event Action OnChanged;

    /// <summary>
    /// Claims the camera's own spawn request while a hidden camera exists, so pressing the
    /// button brings that one back instead of leaving an invisible camera running and putting
    /// a second one in your hand.
    /// </summary>
    [RuntimeInitializeOnLoadMethod]
    public static void RegisterSpawnClaim()
    {
        ContentLoader.OnBeforePropSpawn -= ClaimSpawnForHiddenCamera;
        ContentLoader.OnBeforePropSpawn += ClaimSpawnForHiddenCamera;
    }

    private static bool ClaimSpawnForHiddenCamera(string url)
    {
        if (!string.Equals(url, SpawnUrl, StringComparison.Ordinal)) return false;

        for (int Index = 0; Index < cameras.Count; Index++)
        {
            BasisHandHeldCamera camera = cameras[Index];
            if (camera == null || !camera.IsCameraHidden) continue;

            camera.RevealAsFreshSpawn();
            return true;
        }
        return false;
    }

    public static void Add(BasisHandHeldCamera camera)
    {
        if (camera == null) return;
        if (cameras.Contains(camera)) return;
        cameras.Add(camera);
        OnChanged?.Invoke();
    }

    public static void Remove(BasisHandHeldCamera camera)
    {
        if (camera == null) return;
        if (cameras.Remove(camera)) OnChanged?.Invoke();
    }
}
