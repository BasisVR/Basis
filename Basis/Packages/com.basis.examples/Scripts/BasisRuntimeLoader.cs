using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
using UnityEngine.SceneManagement;
using static SerializableBasis;

/// <summary>
/// Loads a Basis scene or GameObject at runtime and unloads it when disabled.
/// </summary>
[AddComponentMenu("Basis/Runtime Loader")]
public class BasisRuntimeLoader : MonoBehaviour
{
    [Header("Content Source")]
    [Tooltip("The password/secret used by the Basis backend for this asset.")]
    [SerializeField] private string password = string.Empty;

    [Tooltip("Basis BEE URL (BEEUrl) for the scene or object to load.")]
    [SerializeField] private string BEEURL = string.Empty;

    [Header("Load Mode")]
    [Tooltip("If enabled, loads a Scene. If disabled, loads a GameObject/Prefab.")]
    [SerializeField] private bool loadAsScene = false;

    [Tooltip("If enabled, the loaded content persists across scene changes on the client.")]
    [SerializeField] private bool persistent = false;

    [Header("Spawn Settings (GameObject mode)")]
    [Tooltip("If enabled, spawns at this component's Transform position. If disabled, spawns at the local player's position (if available).")]
    [SerializeField] private bool useCustomSpawnPosition = false;

    [Tooltip("Optional manual position override (will be set automatically at load time).")]
    [SerializeField] private Vector3 spawnPosition;

    [Tooltip("Rotation used when spawning a GameObject.")]
    [SerializeField] private Quaternion spawnRotation = Quaternion.identity;

    [Tooltip("If enabled, applies the custom scale below on spawn.")]
    [SerializeField] private bool applyCustomScale = false;

    [Tooltip("Scale used when spawning a GameObject (only applied if 'Apply Custom Scale' is enabled).")]
    [SerializeField] private Vector3 spawnScale = Vector3.one;

    // Runtime handle to whatever was loaded.
    public LocalLoadResource LoadedResource;

    private void OnEnable()
    {
        UnloadAllItemsCheck();
        TryLoad();
    }

    private void OnDisable()
    {
        TryUnload();
    }

    /// <summary>
    /// Attempts to load the configured Basis asset.
    /// </summary>
    private void TryLoad()
    {
        if (string.IsNullOrWhiteSpace(BEEURL))
        {
            Debug.LogError("[BasisRuntimeLoader] MetaUrl is empty. Cannot load.");
            return;
        }

        if (loadAsScene)
        {
            BasisNetworkSpawnItem.RequestSceneLoad(password, BEEURL, persistent, out LoadedResource);
            return;
        }

        if (!useCustomSpawnPosition)
        {
            if (BasisLocalPlayer.Instance != null)
            {
                spawnPosition = BasisLocalPlayer.Instance.transform.position;
            }
            else
            {
                // Fall back gracefully and warn.
                Debug.LogWarning("[BasisRuntimeLoader] BasisLocalPlayer.Instance is null; using this component's Transform position instead.");
            }
        }

        // Spawn request
        BasisNetworkSpawnItem.RequestGameObjectLoad(password, BEEURL, spawnPosition, spawnRotation, applyCustomScale ? spawnScale : Vector3.one, persistent,  applyCustomScale, out LoadedResource);
    }

    /// <summary>
    /// Attempts to unload the previously loaded Basis asset.
    /// </summary>
    private void TryUnload()
    {
        if (loadAsScene)
        {
            BasisNetworkSpawnItem.RequestSceneUnLoad(LoadedResource.LoadedNetID);
        }
        else
        {
            BasisNetworkSpawnItem.RequestGameObjectUnLoad(LoadedResource.LoadedNetID);
        }
    }
    public bool UnloadAllLoadedItems = false;
    public void UnloadAllItemsCheck()
    {
        if (UnloadAllLoadedItems)
        {
            int count = SceneManager.sceneCount;
            for (int SceneIndex = 0; SceneIndex < count; SceneIndex++)
            {
                Scene Scene = SceneManager.GetSceneAt(SceneIndex);
                GameObject[] Roots = Scene.GetRootGameObjects();
                foreach (GameObject Root in Roots)
                {
                    BasisContentBase[] bases = Root.GetComponentsInChildren<BasisContentBase>(true);
                    for (int Index = 0; Index < bases.Length; Index++)
                    {
                        BasisContentBase Base = bases[Index];
                        if (Base.TryGetNetworkGUIDIdentifier(out string id))
                        {
                            if (Base is BasisScene)
                            {
                                BasisNetworkSpawnItem.RequestSceneUnLoad(id);
                            }
                            else
                            {
                                BasisNetworkSpawnItem.RequestGameObjectUnLoad(id);
                            }
                        }
                    }
                }
            }
        }
    }
#if UNITY_EDITOR
    // Keep fields tidy in the editor.
    private void OnValidate()
    {
        if (!applyCustomScale)
        {
            // Ensure scale is sane even if not applied yet.
            if (spawnScale == Vector3.zero)
                spawnScale = Vector3.one;
        }

        // Quaternions can be odd if edited directly; ensure it's normalized.
        if (spawnRotation == new Quaternion(0, 0, 0, 0))
            spawnRotation = Quaternion.identity;
    }
#endif
    /// <summary>Loads immediately if not already loaded.</summary>
    public void LoadNow()
    {
        TryLoad();
    }

    /// <summary>Unloads immediately if loaded.</summary>
    public void UnloadNow()
    {
        TryUnload();
    }

    // Expose some setters with validation if desired.

    public void SetMetadataUrl(string url) => BEEURL = url;
    public void SetPassword(string pwd) => password = pwd;
    public void SetPersistent(bool isPersistent) => persistent = isPersistent;
    public void SetLoadAsScene(bool asScene) => loadAsScene = asScene;
    public void UseCustomSpawn(bool useCustom) => useCustomSpawnPosition = useCustom;
    public void SetSpawnTransform(Vector3 position, Quaternion rotation)
    {
        useCustomSpawnPosition = true;
        spawnPosition = position;
        spawnRotation = rotation;
    }
    public void SetSpawnScale(Vector3 scale, bool apply = true)
    {
        spawnScale = scale == Vector3.zero ? Vector3.one : scale;
        applyCustomScale = apply;
    }
}
