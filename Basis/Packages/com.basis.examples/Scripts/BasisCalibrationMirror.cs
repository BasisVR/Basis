using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using static Basis.Scripts.UI.UI_Panels.BasisDataStoreItemKeys;

/// <summary>
/// The calibration panel's personal see-through mirror: shows ONLY the local avatar, floating on a
/// transparent background, without closing the menu when it opens.
///
/// Spawns the same embedded "Personal Mirror" addressable the Library pin uses, but skips
/// ContentLoader.LoadProp because every placement branch there calls BasisMainMenu.Close() — this
/// mirror must live beside the open calibration panel. The reflection cameras are culled to the
/// LocalPlayerAvatar layer only (a large perf win in busy instances) and cleared to (0,0,0,0); the
/// surface is swapped to the transparent BasisMirrorCutout shader, which routes the sampled
/// reflection's alpha into the material's Alpha, so only the opaque avatar reads as solid.
/// Registered into <see cref="BasisCalibrationMirrorService"/> so the calibration panel (framework
/// assembly, which cannot reference this one) can drive it.
/// </summary>
public static class BasisCalibrationMirror
{
    // Big enough to frame a full body right away.
    private const float MirrorScale = 3f;
    private const string CutoutShaderName = "BasisMirrorCutout";

    private static GameObject _instance;
    private static AsyncOperationHandle<GameObject> _handle;
    private static bool _hasHandle;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Register()
    {
        BasisCalibrationMirrorService.Provider = new ServiceBridge();
    }

    private sealed class ServiceBridge : IBasisCalibrationMirror
    {
        public bool IsUp => BasisCalibrationMirror.IsUp;
        public bool Toggle() => BasisCalibrationMirror.Toggle();
        public bool Summon() => BasisCalibrationMirror.Summon();
        public void Hide() => BasisCalibrationMirror.Hide();
    }

    /// <summary>The instance reference goes Unity-fake-null if the mirror is destroyed out from
    /// under us (world reload); the next spawn releases the lingering handle first.</summary>
    public static bool IsUp => _instance != null;

    public static bool Toggle()
    {
        if (_instance != null)
        {
            Despawn();
            return false;
        }
        Spawn();
        return _instance != null;
    }

    public static bool Summon()
    {
        if (_instance == null) Spawn();
        return _instance != null;
    }

    public static void Hide()
    {
        if (_instance != null) Despawn();
    }

    private static void Spawn()
    {
        // A prior handle can linger if the mirror was destroyed externally (world reload) —
        // release it before taking a new one so we never leak.
        if (_hasHandle && _instance == null)
        {
            Addressables.Release(_handle);
            _hasHandle = false;
        }

        string url = FindMirrorUrl();
        if (string.IsNullOrEmpty(url))
        {
            BasisDebug.LogError("BasisCalibrationMirror: no 'mirror' embedded item found in the catalog — cannot spawn.");
            return;
        }

        Vector3 head = BasisLocalCameraDriver.HeadPosition;
        Vector3 forward = BasisLocalCameraDriver.HeadForward();
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        forward.Normalize();
        Vector3 position = head + forward * 0.9f;
        Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);
        Transform parent = BasisDeviceManagement.Instance != null
            ? BasisDeviceManagement.Instance.transform : null;

        _handle = Addressables.InstantiateAsync(url, position, rotation, parent);
        _hasHandle = true;
        _instance = _handle.WaitForCompletion();

        if (_instance == null)
        {
            BasisDebug.LogError($"BasisCalibrationMirror: failed to instantiate mirror '{url}'.");
            Addressables.Release(_handle);
            _hasHandle = false;
            return;
        }

        // Distinct from the stock pinned Personal Mirror — this instance is owned by the
        // calibration panel and is deliberately not registered in BasisRuntimeSpawnRegistry.
        _instance.name = "Calibration Mirror";

        _instance.transform.localScale *= MirrorScale;

        OptimizeForLocalAvatar(_instance);

        // The reflection cameras are culled to LocalPlayerAvatar (above), but the calibration
        // visuals players line their trackers up with live on Default and would be invisible in
        // the mirror; the relay moves just those onto the avatar layer while the mirror is up.
        _instance.AddComponent<BasisCalibrationMirrorRelay>();
    }

    private static void Despawn()
    {
        if (_hasHandle)
        {
            Addressables.ReleaseInstance(_handle);
            _hasHandle = false;
        }
        else if (_instance != null)
        {
            Object.Destroy(_instance);
        }
        _instance = null;
    }

    /// <summary>Restrict the mirror's reflection cameras to the LOCAL avatar layer so it renders
    /// only you (the stock mask also reflects every remote avatar and the world), then swap the
    /// surface to the transparent cutout. Any failure falls back to an opaque dark backdrop —
    /// never a broken mirror.</summary>
    private static void OptimizeForLocalAvatar(GameObject go)
    {
        int localLayer = LayerMask.NameToLayer("LocalPlayerAvatar");
        if (localLayer < 0) return;

        BasisSDKMirror mirror = go.GetComponentInChildren<BasisSDKMirror>(true);
        if (mirror == null)
        {
            BasisDebug.LogWarning("BasisCalibrationMirror: spawned mirror has no BasisSDKMirror — cannot restrict it to the local avatar.");
            return;
        }

        mirror.ReflectionLayers = 1 << localLayer;

        bool transparent = false;
        try
        {
            transparent = TryMakeTransparent(mirror);
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning("BasisCalibrationMirror: transparent setup failed, using dark backdrop: " + e.Message);
        }

        if (!transparent)
        {
            ApplyClear(mirror, new Color(0.04f, 0.04f, 0.06f, 1f));
        }
    }

    /// <summary>Swap the mirror surface to the cutout material and clear its reflection to fully
    /// transparent, so only the (opaque) local avatar has alpha and everything else reads through.
    /// Returns false (dark-backdrop fallback) if the shader/renderer/textures aren't available.</summary>
    private static bool TryMakeTransparent(BasisSDKMirror mirror)
    {
        if (!BasisLocalCameraDriver.HasInstance) return false;

        Shader shader = Resources.Load<Shader>(CutoutShaderName);
        if (shader == null || !shader.isSupported) return false; // missing or failed to compile

        if (mirror.Renderer == null || mirror.PortalTextureLeft == null) return false;

        // The alpha channel of the clear is what makes the cutout work.
        ApplyClear(mirror, new Color(0f, 0f, 0f, 0f));

        // The per-frame reflection binding rides the renderer's MaterialPropertyBlock, so this
        // direct bind only seeds the material; property IDs match the stock mirror shader.
        Material material = new Material(shader) { name = "Calibration Mirror Cutout" };
        material.SetTexture("_ReflectionTexLeft", mirror.PortalTextureLeft);
        material.SetTexture("_ReflectionTexRight", mirror.PortalTextureRight != null ? mirror.PortalTextureRight : mirror.PortalTextureLeft);
        mirror.Renderer.material = material;
        return true;
    }

    private static void ApplyClear(BasisSDKMirror mirror, Color color)
    {
        if (!BasisLocalCameraDriver.HasInstance) return; // the ClearFlags setter reads the local camera
        mirror.ClearColor = color;
        mirror.ClearFlags = BasisSDKMirror.MirrorClearFlags.Color;
    }

    private static string FindMirrorUrl()
    {
        ItemKey[] keys = EmbeddedItems.HardcodedKeys;
        if (keys == null) return null;
        foreach (ItemKey key in keys)
        {
            if (key == null || string.IsNullOrEmpty(key.Url)) continue;
            if (key.Url.ToLowerInvariant().Contains("mirror")) return key.Url;
        }
        return null;
    }
}
