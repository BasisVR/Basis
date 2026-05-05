using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
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
    public string CreatorUUID { get; private set; }
    public string CreatorDisplayName { get; private set; }

    /// <summary>
    /// Fired when any content sphere is interacted with.
    /// </summary>
    public static Action<BasisContentSphere> OnSphereInteracted;

    private float _bobPhase;
    private Vector3 _restPosition;
    private CancellationTokenSource _metaLoadCts;
    public TextMeshPro Label;
    public Renderer Renderer;
    public int MaterialIndex;
    public static float BobPhaseClock = 1.5f;
    public static float BobPhaseOffset = 0.05f;
    public static float RotationSpeed = 30f;
    public Texture2D texture;
    public void Initialize(string sphereNetID, string contentURL, string unlockPassword, ContentShareType contentType, ushort creatorPlayerID, string creatorUUID, string creatorDisplayName)
    {
        SphereNetID = sphereNetID;
        ContentURL = contentURL;
        UnlockPassword = unlockPassword;
        ContentType = contentType;
        CreatorPlayerID = creatorPlayerID;
        CreatorUUID = creatorUUID;
        CreatorDisplayName = creatorDisplayName;
        InteractRange = 2f;

        // Server shares carry a connection string in ContentURL — there's no
        // bundle to introspect, so skip the metadata fetch and just show the
        // address as the label.
        if (contentType == ContentShareType.Server)
        {
            ApplyServerLabel();
        }
        else
        {
            _metaLoadCts = new CancellationTokenSource();
            _ = LoadMetadataImageAsync(_metaLoadCts.Token);
            Label.text = GetContentTypeName();
        }
    }

    private void ApplyServerLabel()
    {
        string display = ContentURL ?? string.Empty;
        if (SavedServerStore.TryParseConnectionString(display, out string addr, out ushort port, out bool _, out string _))
        {
            display = $"{addr}:{port}";
        }
        if (Label != null) Label.text = $"{GetContentTypeName()}\n{display}";

        if (Renderer != null)
        {
            // Solid type color, no atlas texture lookup needed.
            texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, GetTypeColor());
            texture.Apply();
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            Renderer.GetPropertyBlock(block, MaterialIndex);
            block.SetTexture("_MainTex", texture);
            block.SetTexture("_EmissionMap", texture);
            Renderer.SetPropertyBlock(block, MaterialIndex);
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
            BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper
            {
                LoadableBundle = ToLoadableBundle()
            };

            BasisProgressReport report = new BasisProgressReport();
            await BasisBeeManagement.HandleMetaOnlyLoad(wrapper, report, cancellationToken);

            if (cancellationToken.IsCancellationRequested || this == null) return;

            Color typeColor = GetTypeColor();
            if (wrapper.LoadableBundle.BasisBundleConnector.ImageBase64 != null)
            {
                texture = BasisTextureCompression.FromPngBytes(wrapper.LoadableBundle.BasisBundleConnector.ImageBase64);

                // Blend 50% type color with 50% texture
                Color[] pixels = texture.GetPixels();
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = Color.Lerp(typeColor, pixels[i], 0.5f);
                }
                texture.SetPixels(pixels);
                texture.Apply();

                // Update label with bundle name if available
                string bundleName = wrapper.LoadableBundle.BasisBundleConnector.BasisBundleDescription?.AssetBundleName;
                if (!string.IsNullOrEmpty(bundleName) && Label != null)
                {
                    Label.text = $"{GetContentTypeName()}\n{bundleName}";
                }
            }
            else
            {
                texture = new Texture2D(1, 1);
                texture.SetPixel(0, 0, typeColor);
                texture.Apply();
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

        string sharerLine;
        bool hasName = !string.IsNullOrEmpty(CreatorDisplayName);
        bool hasUUID = !string.IsNullOrEmpty(CreatorUUID);
        // Render the UUID/DID at half size via TMP rich text — the full DID is
        // long enough to wrap and dominate the dialog when shown at body size.
        if (hasName && hasUUID)
        {
            sharerLine = $"Shared by {CreatorDisplayName} <size=50%>({CreatorUUID})</size>";
        }
        else if (hasName)
        {
            sharerLine = $"Shared by {CreatorDisplayName}";
        }
        else if (hasUUID)
        {
            sharerLine = $"Shared by <size=50%>{CreatorUUID}</size>";
        }
        else
        {
            sharerLine = "Shared by an unknown player";
        }

        string description;
        if (ContentType == ContentShareType.Server)
        {
            string addrLine = ContentURL ?? string.Empty;
            if (SavedServerStore.TryParseConnectionString(addrLine, out string a, out ushort p, out bool _, out string _))
                addrLine = $"{a}:{p}";
            description = $"{sharerLine}\n\nAdd {addrLine} to your saved server list?";
        }
        else
        {
            description = $"{sharerLine}\n\nSave this shared {typeName.ToLower()} to your library?";
        }

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
        if (ContentType == ContentShareType.Server)
        {
            SaveServerToSavedList();
            return;
        }

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

    private void SaveServerToSavedList()
    {
        if (!SavedServerStore.TryParseConnectionString(ContentURL, out string address, out ushort port, out bool _, out string password))
        {
            BasisDebug.LogWarning($"Server share had unparseable connection string: {ContentURL}");
            return;
        }

        List<SavedServerEntry> entries = SavedServerStore.Load();
        // Skip duplicates by (address, port) so re-tapping the same orb (or
        // accepting two overlapping shares) doesn't bloat the saved list.
        foreach (SavedServerEntry existing in entries)
        {
            if (string.Equals(existing.Address, address, StringComparison.OrdinalIgnoreCase)
                && existing.Port == port)
            {
                BasisDebug.Log($"Server share '{address}:{port}' already in saved list — skipping.");
                return;
            }
        }

        entries.Add(new SavedServerEntry
        {
            DisplayName = string.IsNullOrEmpty(CreatorDisplayName) ? string.Empty : $"Shared by {CreatorDisplayName}",
            Address = address,
            Port = port,
            HasPassword = !string.IsNullOrEmpty(password),
            Password = password ?? string.Empty,
        });
        SavedServerStore.Save(entries);
        BasisDebug.Log($"Saved server share '{address}:{port}' from {CreatorDisplayName}.", BasisDebug.LogTag.Networking);
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
            case ContentShareType.Server: return new Color(0.8f, 0.4f, 1.0f, 1f);
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
            case ContentShareType.Server: return "Server";
            default: return "Unknown";
        }
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
