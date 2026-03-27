using Basis.BasisUI;
using Basis.Scripts.Drivers;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using static SerializableBasis;

/// <summary>
/// Interactable content share sphere that can be picked up to load content.
/// Follows the BasisAvatarPedestal pattern for interaction and dialogue.
/// </summary>
public class BasisContentSphere : BasisInteractableObject
{
    public string SphereNetID { get; private set; }
    public string ContentURL { get; private set; }
    public string UnlockPassword { get; private set; }
    public ContentShareType ContentType { get; private set; }
    public ushort CreatorPlayerID { get; private set; }

    /// <summary>
    /// Fired when any content sphere is interacted with.
    /// </summary>
    public static Action<BasisContentSphere> OnSphereInteracted;

    private float _bobPhase;
    private Vector3 _restPosition;
    private CancellationTokenSource _metaLoadCts;
    private CancellationTokenSource _previewLoadCts;
    private BasisTrackedBundleWrapper _previewBundleWrapper;
    private Transform _previewRoot;
    private GameObject _previewInstance;
    private readonly List<Material> _previewMaterials = new();
    private const float PreviewFitPadding = 0.9f;
    private const int PreviewLodIndex = 3;
    public TextMeshPro Label;
    public Renderer Renderer;
    public int MaterialIndex;
    public static float BobPhaseClock = 1.5f;
    public static float BobPhaseOffset = 0.05f;
    public static float RotationSpeed = 30f;
    public Texture2D texture;
    public void Initialize(string sphereNetID, string contentURL, string unlockPassword, ContentShareType contentType, ushort creatorPlayerID)
    {
        SphereNetID = sphereNetID;
        ContentURL = contentURL;
        UnlockPassword = unlockPassword;
        ContentType = contentType;
        CreatorPlayerID = creatorPlayerID;
        InteractRange = 2f;
        Label.text = GetContentTypeName();

        if (BasisSettingsDefaults.SharedContentPreviews.RawValue)
        {
            _previewLoadCts = new CancellationTokenSource();
            _ = LoadLivePreviewAsync(_previewLoadCts.Token);
        }
        else
        {
            _metaLoadCts = new CancellationTokenSource();
            _ = LoadMetadataImageAsync(_metaLoadCts.Token);
        }
    }

    private void Start()
    {
        _restPosition = transform.position;
        _bobPhase = UnityEngine.Random.value * Mathf.PI * 2f;
    }
    private void Update()
    {
        var DeltaTime = Time.deltaTime;
        // Gentle hover/bob animation
        _bobPhase += DeltaTime * BobPhaseClock;
        float bobOffset = Mathf.Sin(_bobPhase) * BobPhaseOffset;
        transform.position = _restPosition + Vector3.up * bobOffset;

        // Slow rotation
        transform.Rotate(Vector3.up, RotationSpeed * DeltaTime, Space.World);
    }
    private async Task LoadMetadataImageAsync(CancellationToken cancellationToken)
    {
        try
        {
            BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper { LoadableBundle = ToLoadableBundle() };
            BasisProgressReport report = new BasisProgressReport();
            await BasisBeeManagement.HandleMetaOnlyLoad(wrapper, report, cancellationToken);

            if (cancellationToken.IsCancellationRequested || this == null) return;
            ApplyMetadataFromConnector(wrapper.LoadableBundle.BasisBundleConnector);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to load metadata image for content sphere {SphereNetID}: {e.Message}");
        }
    }
    public override void OnDestroy()
    {
        GameObject.Destroy(texture);
        _metaLoadCts?.Cancel();
        _metaLoadCts?.Dispose();
        _previewLoadCts?.Cancel();
        _previewLoadCts?.Dispose();
        ReleasePreviewBundle();
        DestroyPreview();
        base.OnDestroy();

    }
    /// <summary>
    /// Constructs a BasisLoadableBundle from this sphere's metadata.
    /// </summary>
    public BasisLoadableBundle ToLoadableBundle()
    {
        return new BasisLoadableBundle
        {
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle
            {
                RemoteBeeFileLocation = ContentURL
            },
            UnlockPassword = UnlockPassword,
            BasisBundleConnector = new BasisBundleConnector(),
            BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle()
        };
    }

    /// <summary>
    /// Called when the sphere is interacted with. Opens dialogue with load options.
    /// </summary>
    public void WasPressed()
    {
        OnSphereInteracted?.Invoke(this);

        string typeName = GetContentTypeName();
        string title = $"Shared {typeName}";

        string description = $"Save this shared {typeName.ToLower()} to your library?";

        BasisMainMenu.Open();
        BasisMainMenu.Instance.OpenDialogue(title, description, "Save", "Delete", value =>
        {
            if (value)
            {
                SaveToLibrary();
            }
            else
            {
                RequestRemove();
            }
        });
    }

    private async void SaveToLibrary()
    {
        BundledContentHolder.Mode mode;
        switch (ContentType)
        {
            case ContentShareType.Avatar:
                mode = BundledContentHolder.Mode.Avatar;
                break;
            case ContentShareType.Prop:
                mode = BundledContentHolder.Mode.Prop;
                break;
            case ContentShareType.World:
                mode = BundledContentHolder.Mode.World;
                break;
            default:
                return;
        }

        BasisDataStoreItemKeys.ItemKey key = new BasisDataStoreItemKeys.ItemKey
        {
            Mode = mode,
            PlacementType = BundledContentHolder.PlacementType.SpawnAtRaycast,
            Url = ContentURL,
            Pass = UnlockPassword,
        };

        await BasisDataStoreItemKeys.AddNewKey(key);
        BasisDebug.Log($"Saved content sphere to library: {ContentURL} as {mode}", BasisDebug.LogTag.Networking);
    }
    public void RequestRemove()
    {
        BasisContentShareManager.RequestRemoveSphere(SphereNetID);
    }

    public Color GetTypeColor()
    {
        switch (ContentType)
        {
            case ContentShareType.Avatar: return new Color(0.3f, 0.5f, 1.0f, 1f);
            case ContentShareType.Prop: return new Color(0.3f, 1.0f, 0.5f, 1f);
            case ContentShareType.World: return new Color(1.0f, 0.6f, 0.2f, 1f);
            default: return Color.white;
        }
    }

    public string GetContentTypeName()
    {
        switch (ContentType)
        {
            case ContentShareType.Avatar: return "Avatar";
            case ContentShareType.Prop: return "Prop";
            case ContentShareType.World: return "World";
            default: return "Unknown";
        }
    }

    private async Task LoadLivePreviewAsync(CancellationToken cancellationToken)
    {
        GameObject stagedInstance = null;
        List<Material> stagedMaterials = new();
        BasisTrackedBundleWrapper wrapper = null;
        try
        {
            wrapper = await RetainPreviewBundleAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyMetadataFromConnector(wrapper.LoadableBundle.BasisBundleConnector);

            GameObject previewPrefab = await LoadPreviewPrefabAsync(wrapper, cancellationToken);
            if (previewPrefab == null)
            {
                BasisDebug.LogWarning($"Shared content preview missing loadable GameObject for sphere {SphereNetID} ({ContentURL}).");
                return;
            }

            if (this == null || gameObject == null)
            {
                return;
            }

            EnsurePreviewRoot();
            stagedInstance = GameObject.Instantiate(previewPrefab, _previewRoot, false);
            stagedInstance.name = $"{previewPrefab.name}_SharePreview";
            stagedInstance.transform.localPosition = Vector3.zero;
            stagedInstance.transform.localRotation = Quaternion.identity;
            stagedInstance.transform.localScale = Vector3.one;

            SanitizePreviewInstance(stagedInstance);
            ApplyPreviewOrientation(stagedInstance);
            ForcePreviewLods(stagedInstance);
            ApplyPreviewFallbackMaterials(stagedInstance, stagedMaterials);

            if (!TryFitPreviewToSphere(stagedInstance, wrapper.LoadableBundle.BasisBundleConnector.Bounds))
            {
                CleanupStagedPreview(stagedInstance, stagedMaterials);
                BasisDebug.LogWarning($"Shared content preview had no render bounds for sphere {SphereNetID} ({ContentURL}).");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            DestroyPreview();
            ReleasePreviewBundle();
            _previewBundleWrapper = wrapper;
            _previewMaterials.AddRange(stagedMaterials);
            _previewInstance = stagedInstance;
            wrapper = null;
            stagedInstance = null;
            HideMetadataPreviewLayer();
        }
        catch (OperationCanceledException)
        {
            CleanupStagedPreview(stagedInstance, stagedMaterials);
            ReleasePreviewBundle(wrapper);
        }
        catch (Exception ex)
        {
            CleanupStagedPreview(stagedInstance, stagedMaterials);
            ReleasePreviewBundle(wrapper);
            BasisDebug.LogWarning($"Shared content preview failed for sphere {SphereNetID} ({ContentURL}): {ex.Message}");
        }
    }

    private async Task<BasisTrackedBundleWrapper> RetainPreviewBundleAsync(CancellationToken cancellationToken)
    {
        string remoteUrl = ContentURL;
        BasisProgressReport report = new BasisProgressReport();

        while (true)
        {
            if (BasisLoadHandler.LoadedBundles.TryGetValue(remoteUrl, out BasisTrackedBundleWrapper existingWrapper))
            {
                existingWrapper.Increment();
                try
                {
                    await existingWrapper.WaitForBundleLoadAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (existingWrapper.AssetBundle == null)
                    {
                        throw new Exception("Bundle load did not produce an AssetBundle.");
                    }

                    return existingWrapper;
                }
                catch
                {
                    existingWrapper.DeIncrement();
                    throw;
                }
            }

            BasisTrackedBundleWrapper newWrapper = new BasisTrackedBundleWrapper
            {
                LoadableBundle = ToLoadableBundle()
            };

            if (!BasisLoadHandler.LoadedBundles.TryAdd(remoteUrl, newWrapper))
            {
                continue;
            }

            newWrapper.Increment();
            try
            {
                await BasisBeeManagement.HandleBundleAndMetaLoading(newWrapper, report, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (newWrapper.AssetBundle == null)
                {
                    throw new Exception("Bundle load did not produce an AssetBundle.");
                }

                return newWrapper;
            }
            catch
            {
                newWrapper.DeIncrement();
                BasisLoadHandler.LoadedBundles.TryRemove(remoteUrl, out _);
                if (newWrapper.AssetBundle != null)
                {
                    newWrapper.AssetBundle.Unload(true);
                }
                throw;
            }
        }
    }

    private async Task<GameObject> LoadPreviewPrefabAsync(BasisTrackedBundleWrapper wrapper, CancellationToken cancellationToken)
    {
        if (wrapper?.AssetBundle == null)
        {
            return null;
        }

        BasisBundleGenerated generated = null;
        BasisBundleConnector connector = wrapper.LoadableBundle?.BasisBundleConnector;
        if (connector != null)
        {
            connector.GetPlatform(out generated);
        }

        string preferredAssetName = generated?.AssetToLoadName;
        if (!string.IsNullOrEmpty(preferredAssetName))
        {
            GameObject preferred = await LoadAssetAsGameObjectAsync(wrapper.AssetBundle, preferredAssetName, cancellationToken);
            if (preferred != null)
            {
                return preferred;
            }
        }

        string[] assetNames = wrapper.AssetBundle.GetAllAssetNames();
        for (int index = 0; index < assetNames.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GameObject fallback = await LoadAssetAsGameObjectAsync(wrapper.AssetBundle, assetNames[index], cancellationToken);
            if (fallback != null)
            {
                return fallback;
            }
        }

        return null;
    }

    private static async Task<GameObject> LoadAssetAsGameObjectAsync(AssetBundle assetBundle, string assetName, CancellationToken cancellationToken)
    {
        if (assetBundle == null || string.IsNullOrEmpty(assetName))
        {
            return null;
        }

        string[] candidateNames =
        {
            assetName,
            assetName.Replace(".bundle", ".prefab")
        };

        for (int index = 0; index < candidateNames.Length; index++)
        {
            string candidate = candidateNames[index];
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            AssetBundleRequest request = assetBundle.LoadAssetAsync<GameObject>(candidate);
            while (!request.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (request.asset is GameObject gameObject)
            {
                return gameObject;
            }
        }

        return null;
    }

    private void ApplyMetadataFromConnector(BasisBundleConnector connector)
    {
        if (this == null || connector == null)
        {
            return;
        }

        Texture2D nextTexture = BuildMetadataTexture(connector);
        if (texture != null)
        {
            GameObject.Destroy(texture);
        }
        texture = nextTexture;

        string bundleName = connector.BasisBundleDescription?.AssetBundleName;
        if (!string.IsNullOrEmpty(bundleName) && Label != null)
        {
            Label.text = $"{GetContentTypeName()}\n{bundleName}";
        }

        if (Renderer != null)
        {
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            Renderer.GetPropertyBlock(block, MaterialIndex);
            block.SetTexture("_MainTex", texture);
            block.SetTexture("_EmissionMap", texture);
            Renderer.SetPropertyBlock(block, MaterialIndex);
        }
    }

    private Texture2D BuildMetadataTexture(BasisBundleConnector connector)
    {
        Color typeColor = GetTypeColor();
        if (connector.ImageBase64 != null)
        {
            Texture2D previewTexture = BasisTextureCompression.FromPngBytes(connector.ImageBase64);
            Color[] pixels = previewTexture.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Color.Lerp(typeColor, pixels[i], 0.5f);
            }
            previewTexture.SetPixels(pixels);
            previewTexture.Apply();
            return previewTexture;
        }

        Texture2D fallbackTexture = new Texture2D(1, 1);
        fallbackTexture.SetPixel(0, 0, typeColor);
        fallbackTexture.Apply();
        return fallbackTexture;
    }

    private void EnsurePreviewRoot()
    {
        if (_previewRoot != null)
        {
            return;
        }

        GameObject previewRoot = new GameObject("Shared Content Preview");
        previewRoot.transform.SetParent(transform, false);
        previewRoot.transform.localPosition = Vector3.zero;
        previewRoot.transform.localRotation = Quaternion.identity;
        previewRoot.transform.localScale = Vector3.one;
        _previewRoot = previewRoot.transform;
    }

    private void SanitizePreviewInstance(GameObject instance)
    {
        if (instance == null)
        {
            return;
        }

        foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
        {
            collider.enabled = false;
        }

        foreach (Rigidbody rigidbody in instance.GetComponentsInChildren<Rigidbody>(true))
        {
            rigidbody.isKinematic = true;
            rigidbody.detectCollisions = false;
        }

        foreach (AudioSource audioSource in instance.GetComponentsInChildren<AudioSource>(true))
        {
            audioSource.enabled = false;
        }

        foreach (Animator animator in instance.GetComponentsInChildren<Animator>(true))
        {
            animator.enabled = false;
        }

        foreach (ParticleSystem particleSystem in instance.GetComponentsInChildren<ParticleSystem>(true))
        {
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.gameObject.SetActive(false);
        }

        foreach (BasisInteractableObject interactable in instance.GetComponentsInChildren<BasisInteractableObject>(true))
        {
            interactable.enabled = false;
        }

        foreach (MonoBehaviour monoBehaviour in instance.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (monoBehaviour == null)
            {
                continue;
            }

            if (monoBehaviour is BasisInteractableObject)
            {
                continue;
            }

            monoBehaviour.enabled = false;
        }
    }

    private void ApplyPreviewFallbackMaterials(GameObject instance, List<Material> createdMaterials)
    {
        if (instance == null || BundledContentHolder.Instance == null)
        {
            return;
        }

        Shader fallbackShader = BundledContentHolder.Instance.UrpShader;
        if (fallbackShader == null)
        {
            return;
        }

        foreach (Renderer childRenderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            if (childRenderer == null)
            {
                continue;
            }

            BasisAvatarDriver.MaterialCorrection(childRenderer, fallbackShader, createdMaterials);
        }
    }

    private void ForcePreviewLods(GameObject instance)
    {
        if (instance == null)
        {
            return;
        }

        foreach (Renderer childRenderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            if (childRenderer == null)
            {
                continue;
            }

            childRenderer.forceMeshLod = PreviewLodIndex;
        }

        foreach (LODGroup lodGroup in instance.GetComponentsInChildren<LODGroup>(true))
        {
            if (lodGroup == null)
            {
                continue;
            }

            int lodCount = lodGroup.GetLODs().Length;
            if (lodCount <= 0)
            {
                continue;
            }

            lodGroup.RecalculateBounds();
            lodGroup.ForceLOD(Mathf.Min(PreviewLodIndex, lodCount - 1));
        }
    }

    private void ApplyPreviewOrientation(GameObject instance)
    {
        if (instance == null)
        {
            return;
        }

        if (ContentType == ContentShareType.Avatar)
        {
            instance.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        }
    }

    private void HideMetadataPreviewLayer()
    {
        if (Renderer == null)
        {
            return;
        }
        Renderer.enabled = false;
    }

    private bool TryFitPreviewToSphere(GameObject instance, BasisBounds fallbackBounds)
    {
        if (instance == null || _previewRoot == null)
        {
            return false;
        }

        if (!TryGetCombinedRendererBounds(instance, out Bounds previewBounds))
        {
            if (fallbackBounds.size == Vector3.zero)
            {
                return false;
            }

            previewBounds = new Bounds(instance.transform.position + fallbackBounds.center, fallbackBounds.size);
        }

        float targetDiameter = 1f;
        Vector3 targetCenter = transform.position;
        if (TryGetShellBounds(out Bounds shellBounds))
        {
            targetDiameter = Mathf.Min(shellBounds.size.x, shellBounds.size.y, shellBounds.size.z) * PreviewFitPadding;
            targetCenter = shellBounds.center;
        }

        float previewMaxSize = Mathf.Max(previewBounds.size.x, previewBounds.size.y, previewBounds.size.z);
        if (previewMaxSize <= Mathf.Epsilon)
        {
            return false;
        }

        float scaleFactor = targetDiameter / previewMaxSize;
        instance.transform.localScale = instance.transform.localScale * scaleFactor;

        if (!TryGetCombinedRendererBounds(instance, out previewBounds))
        {
            previewBounds = new Bounds(instance.transform.position + fallbackBounds.center, fallbackBounds.size * scaleFactor);
        }

        Vector3 centerOffset = targetCenter - previewBounds.center;
        instance.transform.position += centerOffset;
        return true;
    }

    private bool TryGetShellBounds(out Bounds bounds)
    {
        Renderer[] shellRenderers = GetComponentsInChildren<Renderer>(true);
        bounds = default;
        bool hasBounds = false;

        for (int index = 0; index < shellRenderers.Length; index++)
        {
            Renderer shellRenderer = shellRenderers[index];
            if (shellRenderer == null || !shellRenderer.enabled)
            {
                continue;
            }

            if (_previewRoot != null && shellRenderer.transform.IsChildOf(_previewRoot))
            {
                continue;
            }

            if (Label != null && shellRenderer.transform.IsChildOf(Label.transform))
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = shellRenderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(shellRenderer.bounds);
            }
        }

        return hasBounds;
    }

    private static bool TryGetCombinedRendererBounds(GameObject instance, out Bounds bounds)
    {
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        bounds = default;
        bool hasBounds = false;
        for (int index = 0; index < renderers.Length; index++)
        {
            Renderer childRenderer = renderers[index];
            if (childRenderer == null || !childRenderer.enabled)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = childRenderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(childRenderer.bounds);
            }
        }

        return hasBounds;
    }

    private void DestroyPreview()
    {
        if (_previewInstance != null)
        {
            GameObject.Destroy(_previewInstance);
            _previewInstance = null;
        }

        for (int index = 0; index < _previewMaterials.Count; index++)
        {
            if (_previewMaterials[index] != null)
            {
                GameObject.Destroy(_previewMaterials[index]);
            }
        }
        _previewMaterials.Clear();
    }

    private void ReleasePreviewBundle()
    {
        if (_previewBundleWrapper == null)
        {
            return;
        }

        ReleasePreviewBundle(_previewBundleWrapper);
        _previewBundleWrapper = null;
    }

    private static void ReleasePreviewBundle(BasisTrackedBundleWrapper wrapper)
    {
        if (wrapper?.LoadableBundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation == null)
        {
            return;
        }

        _ = BasisLoadHandler.RequestDeIncrementOfBundle(wrapper.LoadableBundle);
    }

    private static void CleanupStagedPreview(GameObject stagedInstance, List<Material> stagedMaterials)
    {
        if (stagedInstance != null)
        {
            GameObject.Destroy(stagedInstance);
        }

        for (int index = 0; index < stagedMaterials.Count; index++)
        {
            if (stagedMaterials[index] != null)
            {
                GameObject.Destroy(stagedMaterials[index]);
            }
        }
        stagedMaterials.Clear();
    }

    #region BasisInteractableObject Implementation

    public override bool CanHover(BasisInput input)
    {
        return InteractableEnabled &&
            Inputs.IsInputAdded(input) &&
            input.TryGetRole(out BasisBoneTrackedRole role) &&
            Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
            found.GetState() == BasisInteractInputState.Ignored &&
            IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
    }

    public override bool CanInteract(BasisInput input)
    {
        return InteractableEnabled &&
            Inputs.IsInputAdded(input) &&
            input.TryGetRole(out BasisBoneTrackedRole role) &&
            Inputs.TryGetByRole(role, out BasisInputWrapper found) &&
            found.GetState() == BasisInteractInputState.Hovering &&
            IsWithinRange(found.BoneControl.OutgoingWorldData.position, InteractRange);
    }

    public override void OnHoverStart(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        if (found != null && found.Value.GetState() != BasisInteractInputState.Ignored)
            BasisDebug.LogWarning("BasisContentSphere input state is not ignored OnHoverStart");
        Inputs.ChangeStateByRole(found.Value.Role, BasisInteractInputState.Hovering);
        OnHoverStartEvent?.Invoke(input);
    }

    public override void OnHoverEnd(BasisInput input, bool willInteract)
    {
        if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out _))
        {
            if (!willInteract)
            {
                Inputs.ChangeStateByRole(role, BasisInteractInputState.Ignored);
            }
            OnHoverEndEvent?.Invoke(input, willInteract);
        }
    }

    public override void OnInteractStart(BasisInput input)
    {
        if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
        {
            if (wrapper.GetState() == BasisInteractInputState.Hovering)
            {
                WasPressed();
                OnInteractStartEvent?.Invoke(input);
            }
        }
    }

    public override void OnInteractEnd(BasisInput input)
    {
        if (input.TryGetRole(out BasisBoneTrackedRole role) && Inputs.TryGetByRole(role, out BasisInputWrapper wrapper))
        {
            if (wrapper.GetState() == BasisInteractInputState.Interacting)
            {
                Inputs.ChangeStateByRole(wrapper.Role, BasisInteractInputState.Ignored);
                OnInteractEndEvent?.Invoke(input);
            }
        }
    }

    public override bool IsInteractingWith(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        return found.HasValue && found.Value.GetState() == BasisInteractInputState.Interacting;
    }

    public override bool IsHoveredBy(BasisInput input)
    {
        var found = Inputs.FindExcludeExtras(input);
        return found.HasValue && found.Value.GetState() == BasisInteractInputState.Hovering;
    }

    public override void InputUpdate() { }

    public override bool IsInteractTriggered(BasisInput input)
    {
        return HasState(input.CurrentInputState, InputKey);
    }

    #endregion
}
