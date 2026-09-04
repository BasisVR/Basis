using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[System.Serializable]
public class BasisBundleConnector
{
    public string UniqueVersion;
    [SerializeField]
    public BasisBundleDescription BasisBundleDescription;
    [SerializeField]
    public BasisBundleGenerated[] BasisBundleGenerated;
    public string ImageBase64;
    // Distance far LOD payload (see BasisFarLodPayload), generated at avatar build time.
    // Rides the connector so it survives the disk cache byte-identically and is available
    // from a connector-only download without touching the AssetBundle. Older bundles built
    // before this field existed deserialize with null — clients treat null/invalid as
    // "no far LOD" and fall back to existing distance behavior.
    public string FarLodBase64;
    public string DateOfCreation;
    [SerializeField]
    public BasisBounds Bounds;
    [SerializeField]
    public BasisMetaData MetaData;
    [System.Serializable]
    public struct BasisMetaData
    {
        public long TrianglesCount;
        public long MaterialCount;
        public long BonesCount;
        // Sum of GetRuntimeMemorySizeLong for unique textures referenced by avatar materials.
        // Older bundles built before this field existed deserialize with 0, which the
        // performance-limit evaluator treats as "unknown" and lets through.
        public long TextureMemoryBytes;
        // Render pipeline the bundle was built against: "URP", "HDRP", "Built-in", or
        // the SRP asset's type name for unrecognized SRPs. Older bundles built before
        // this field existed deserialize with null — display layer treats null/empty
        // as "Unknown".
        public string GraphicsPipeline;
        // What this bundle IS, stamped from the BasisContentBase the SDK was asked to build:
        // AvatarContentKind, PropContentKind or SceneContentKind. The component census below can
        // only ever say what a bundle CONTAINS, so it mis-files anything holding a component of
        // another kind — a prop that ended up with a BasisAvatar welded onto it read back as an
        // avatar. Older bundles built before this field existed deserialize with null, which sends
        // the reader back to the census.
        public string ContentKind;
        // Placement the prop's BasisProp component asks for, harvested at build time so a client
        // can honor it before the AssetBundle is downloaded. Older bundles built before this field
        // existed deserialize with Placement = Unspecified, which clients treat as "no request"
        // and place the prop exactly as they did before.
        [SerializeField]
        public BasisPropSpawnMetaData PropSpawn;
        [SerializeField]
        public BasisComponentName[] ComponentNames;
    }
    [System.Serializable]
    public struct BasisComponentName
    {
        public string Name;
        public int count;
    }
    public BasisBundleConnector(string version, BasisBundleDescription basisBundleDescription, BasisBundleGenerated[] basisBundleGenerated, string imageBytes, BasisBounds basisBounds, BasisMetaData basisMetaData, string farLodBase64 = null)
    {
        UniqueVersion = version ?? throw new ArgumentNullException(nameof(version));
        BasisBundleDescription = basisBundleDescription ?? throw new ArgumentNullException(nameof(basisBundleDescription));
        BasisBundleGenerated = basisBundleGenerated ?? throw new ArgumentNullException(nameof(basisBundleGenerated));
        ImageBase64 = imageBytes;
        FarLodBase64 = farLodBase64;
        DateOfCreation = DateTime.UtcNow.ToString("o");
        Bounds = basisBounds;
        MetaData = basisMetaData;
    }
    public BasisBundleConnector()
    {
        // if we want to initialize new bundles with a default size it can be done here
        // but this is not needed anymore I have implemented a solution in content loader to load the item beforehand and calculate it
        // if the metadata is missing for old items
        // if(Bounds.extents == Vector3.zero)
        // {
        //     Bounds.extents = new Vector3(0.1f, 0.1f, 0.1f);
        // }
    }
    public bool CheckVersion(string version)
    {
        return UniqueVersion.ToLower() == version.ToLower();
    }

    /// <summary>
    /// Platform string of the platform-agnostic fallback section (avatar-only, glTF binary
    /// payload instead of a Unity AssetBundle). Never matches a real platform, so clients
    /// built before this existed simply skip the section during the section walk.
    /// </summary>
    public const string GenericPlatform = "Generic";
    /// <summary>
    /// AssetMode of the generic section. The section bytes are an encrypted .glb (glTF 2.0
    /// binary) rather than an AssetBundle; avatar wiring rides in
    /// <see cref="BasisBundleGenerated.GenericAvatarDataJson"/>.
    /// </summary>
    public const string GltfAssetMode = "GLTF";
    /// <summary>
    /// AssetMode of a section built from a Unity scene. Only the scene branch of the SDK build
    /// pipeline writes it, and it writes it for every world ever built, so it names a world
    /// outright — unlike the component census, which also counts the props and avatars standing
    /// inside the world alongside its BasisScene.
    /// </summary>
    public const string SceneAssetMode = "Scene";
    /// <summary>
    /// AssetMode of a section built from a prefab: an avatar or a prop, told apart by
    /// <see cref="BasisMetaData.ContentKind"/> (or, on bundles built before it, the census).
    /// </summary>
    public const string GameObjectAssetMode = "GameObject";

    /// <summary><see cref="BasisMetaData.ContentKind"/> of a bundle built from a BasisAvatar.</summary>
    public const string AvatarContentKind = "Avatar";
    /// <summary><see cref="BasisMetaData.ContentKind"/> of a bundle built from a BasisProp.</summary>
    public const string PropContentKind = "Prop";
    /// <summary><see cref="BasisMetaData.ContentKind"/> of a bundle built from a BasisScene.</summary>
    public const string SceneContentKind = "Scene";

    /// <summary>
    /// True when the bundle carries a build-time <see cref="BasisMetaData.ContentKind"/> stamp
    /// equal to <paramref name="contentKind"/>. False for every bundle built before the stamp
    /// existed, which leaves the caller on its old detection path.
    /// </summary>
    public static bool IsContentKind(BasisBundleConnector connector, string contentKind)
    {
        return connector != null && string.Equals(connector.MetaData.ContentKind, contentKind, StringComparison.OrdinalIgnoreCase);
    }

    public bool GetPlatform(out BasisBundleGenerated platformBundle)
    {
        platformBundle = BasisBundleGenerated.FirstOrDefault(bundle => PlatformMatch(bundle.Platform));
        // Exact platform sections always win; the generic (glTF) section only applies when
        // this platform has no AssetBundle in the bee, which was previously a hard failure.
        if (platformBundle == null)
        {
            platformBundle = BasisBundleGenerated.FirstOrDefault(IsGenericBundle);
        }
        return platformBundle != null;
    }
    public static bool IsPlatform(BasisBundleGenerated platformBundle)
    {
        return PlatformMatch(platformBundle.Platform);
    }
    public static bool IsGenericBundle(BasisBundleGenerated bundle)
    {
        return bundle != null && string.Equals(bundle.Platform, GenericPlatform, StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsGltfMode(BasisBundleGenerated bundle)
    {
        return bundle != null && string.Equals(bundle.AssetMode, GltfAssetMode, StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsSceneMode(BasisBundleGenerated bundle)
    {
        return bundle != null && string.Equals(bundle.AssetMode, SceneAssetMode, StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>
    /// True when any section of this bundle was built from a scene, which makes the whole bundle a
    /// world. Reads every section rather than the one matching this platform, so a world built for
    /// another platform is still recognised as a world here.
    /// </summary>
    public static bool IsSceneBundle(BasisBundleConnector connector)
    {
        BasisBundleGenerated[] sections = connector?.BasisBundleGenerated;
        if (sections == null) return false;

        for (int Index = 0; Index < sections.Length; Index++)
        {
            if (IsSceneMode(sections[Index])) return true;
        }
        return false;
    }
    private static readonly Dictionary<string, HashSet<RuntimePlatform>> platformMappings = new Dictionary<string, HashSet<RuntimePlatform>>()
    {
        { Enum.GetName(typeof(BuildTarget), BuildTarget.StandaloneWindows), new HashSet<RuntimePlatform> { RuntimePlatform.WindowsEditor, RuntimePlatform.WindowsPlayer, RuntimePlatform.WindowsServer } },
        { Enum.GetName(typeof(BuildTarget), BuildTarget.StandaloneWindows64), new HashSet<RuntimePlatform> { RuntimePlatform.WindowsEditor, RuntimePlatform.WindowsPlayer, RuntimePlatform.WindowsServer } },
        { Enum.GetName(typeof(BuildTarget), BuildTarget.StandaloneOSX), new HashSet<RuntimePlatform> { RuntimePlatform.OSXPlayer, RuntimePlatform.OSXEditor } },
        { Enum.GetName(typeof(BuildTarget), BuildTarget.Android), new HashSet<RuntimePlatform> { RuntimePlatform.Android } },
        { Enum.GetName(typeof(BuildTarget), BuildTarget.StandaloneLinux64), new HashSet<RuntimePlatform> { RuntimePlatform.LinuxEditor, RuntimePlatform.LinuxPlayer, RuntimePlatform.LinuxServer } },
        { Enum.GetName(typeof(BuildTarget), BuildTarget.iOS), new HashSet<RuntimePlatform> { RuntimePlatform.IPhonePlayer } }
    };
    public static string DebugOfPlatforms(BasisBundleConnector connector = null)
    {
        string bundlePlatforms = "  <unknown>";

        if (connector != null)
        {
            if (connector.BasisBundleGenerated == null || connector.BasisBundleGenerated.Length == 0)
            {
                bundlePlatforms = "  <none>";
            }
            else
            {
                var platforms = connector.BasisBundleGenerated
                    .Where(bundle => bundle != null)
                    .Select(bundle => string.IsNullOrWhiteSpace(bundle.Platform) ? "<empty>" : bundle.Platform)
                    .ToArray();

                bundlePlatforms = platforms.Length == 0
                    ? "  <none>"
                    : string.Join("\n", platforms.Select(platform => $"  {platform}"));
            }
        }

        string knownPlatforms = string.Join("\n", platformMappings.Select(kvp => $"  {kvp.Key} => [{string.Join(", ", kvp.Value)}]"));
        return $"Bundle Generated Platforms:\n{bundlePlatforms}\nKnown Platform Mappings:\n{knownPlatforms}";
    }
    public enum BuildTarget
    {
        StandaloneOSX = 2,
        StandaloneWindows = 5,
        iOS = 9,
        Android = 13,
        StandaloneWindows64 = 19,
        WebGL = 20,
        WSAPlayer = 21,
        StandaloneLinux64 = 24,
        PS4 = 31,
        XboxOne = 33,
        tvOS = 37,
        Switch = 38,
        LinuxHeadlessSimulation = 41,
        GameCoreXboxSeries = 42,
        GameCoreXboxOne = 43,
        PS5 = 44,
        EmbeddedLinux = 45,
        QNX = 46,
        VisionOS = 47,
        ReservedCFE = 48,
    }
    public static bool PlatformMatch(string platformRequest)
    {
        return platformMappings.TryGetValue(platformRequest, out var validPlatforms) && validPlatforms.Contains(Application.platform);
    }
}

[System.Serializable]
public class BasisBundleDescription
{
    public string AssetBundleName;//user friendly name of this asset.
    public string AssetBundleDescription;//the description of this asset
    public Texture2D AssetBundleIcon;//icon for this asset
    // Creator-authored content tags (presets like "18+", "Horror" plus custom strings).
    // Travels with the bundle metadata so clients can offer filtering. Honor system —
    // accuracy is the creator's responsibility, there is no enforcement.
    // Older bundles built before this field existed deserialize with null, which client
    // filters should treat as "unknown / no tags declared".
    public string[] Tags = new string[0];
    // Stable identity for this piece of content across rebuilds and re-uploads, generated
    // once at build time and persisted on the authored asset (unlike UniqueVersion, which
    // is new every build). Clients group library entries sharing this id into one stack.
    // Older bundles built before this field existed deserialize with null — clients treat
    // null/empty as "no group" and show the entry standalone.
    public string ContentGroupId;
    public BasisBundleDescription()
    {

    }
    public BasisBundleDescription(string assetBundleName, string assetBundleDescription, Texture2D assetBundleIcon = null)
    {
        AssetBundleName = assetBundleName ?? throw new ArgumentNullException(nameof(assetBundleName));
        AssetBundleDescription = assetBundleDescription ?? throw new ArgumentNullException(nameof(assetBundleDescription));
        AssetBundleIcon = assetBundleIcon;
    }
}
[System.Serializable]
public class BasisBundleGenerated
{
    public string AssetBundleHash;//hash stored separately
    public string AssetMode;//Scene or Gameobject
    public string AssetToLoadName;// assets name we are using out of the box.
    public uint AssetBundleCRC;//CRC of the assetbundle
    public bool IsEncrypted = true;//if the bundle is encrypted
    public string Password;//this unlocks the bundle
    public string Platform;//Deployed Platform
    public long EndByte;
    // Graphics APIs this section's shaders were compiled against, in PlayerSettings
    // priority order (e.g. ["Direct3D11", "Direct3D12", "Vulkan"] for Windows64,
    // ["Vulkan", "OpenGLES3"] for Android). Verbatim GraphicsDeviceType.ToString()
    // values so future APIs (e.g. WebGPU) appear without a mapping update. Older
    // bundles built before this field existed deserialize with null — display layer
    // treats null/empty as "Unknown".
    public string[] GraphicsAPIs;
    // Only set on the Generic (glTF) avatar section: BasisGenericAvatarData serialized as
    // JSON — humanoid rig description plus BasisAvatar wiring (visemes, eye/mouth positions)
    // that glTF itself cannot carry. Null/empty on platform AssetBundle sections and on
    // bundles built before this field existed.
    public string GenericAvatarDataJson;
    public BasisBundleGenerated()
    {
    }
    public BasisBundleGenerated(string assetBundleHash, string assetMode, string assetToLoadName, uint assetBundleCRC, bool isEncrypted, string password, string platform, long endbyte, string[] graphicsAPIs = null)
    {
        AssetBundleHash = assetBundleHash ?? throw new ArgumentNullException(nameof(assetBundleHash));
        AssetMode = assetMode ?? throw new ArgumentNullException(nameof(assetMode));
        AssetToLoadName = assetToLoadName ?? throw new ArgumentNullException(nameof(assetToLoadName));
        AssetBundleCRC = assetBundleCRC;
        IsEncrypted = isEncrypted;
        Password = password ?? throw new ArgumentNullException(nameof(password));
        Platform = platform ?? throw new ArgumentNullException(nameof(platform));
        EndByte = endbyte;
        GraphicsAPIs = graphicsAPIs;
    }
}
