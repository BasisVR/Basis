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
        [SerializeField]
        public BasisComponentName[] ComponentNames;
    }
    [System.Serializable]
    public struct BasisComponentName
    {
        public string Name;
        public int count;
    }
    public BasisBundleConnector(string version, BasisBundleDescription basisBundleDescription, BasisBundleGenerated[] basisBundleGenerated, string imageBytes, BasisBounds basisBounds, BasisMetaData basisMetaData)
    {
        UniqueVersion = version ?? throw new ArgumentNullException(nameof(version));
        BasisBundleDescription = basisBundleDescription ?? throw new ArgumentNullException(nameof(basisBundleDescription));
        BasisBundleGenerated = basisBundleGenerated ?? throw new ArgumentNullException(nameof(basisBundleGenerated));
        ImageBase64 = imageBytes;
        DateOfCreation = DateTime.UtcNow.ToString("o");
        Bounds = basisBounds;
        MetaData = basisMetaData;
    }
    public BasisBundleConnector()
    {
        if(Bounds.extents == Vector3.zero)
        {
            Bounds.extents = new Vector3(0.1f, 0.1f, 0.1f);
        }
    }
    public bool CheckVersion(string version)
    {
        return UniqueVersion.ToLower() == version.ToLower();
    }

    public bool GetPlatform(out BasisBundleGenerated platformBundle)
    {
        platformBundle = BasisBundleGenerated.FirstOrDefault(bundle => PlatformMatch(bundle.Platform));
        return platformBundle != null;
    }
    public static bool IsPlatform(BasisBundleGenerated platformBundle)
    {
        return PlatformMatch(platformBundle.Platform);
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
    public BasisBundleDescription()
    {

    }
    public BasisBundleDescription(string assetBundleName, string assetBundleDescription)
    {
        AssetBundleName = assetBundleName ?? throw new ArgumentNullException(nameof(assetBundleName));
        AssetBundleDescription = assetBundleDescription ?? throw new ArgumentNullException(nameof(assetBundleDescription));
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
    public BasisBundleGenerated()
    {
    }
    public BasisBundleGenerated(string assetBundleHash, string assetMode, string assetToLoadName, uint assetBundleCRC, bool isEncrypted, string password, string platform, long endbyte)
    {
        AssetBundleHash = assetBundleHash ?? throw new ArgumentNullException(nameof(assetBundleHash));
        AssetMode = assetMode ?? throw new ArgumentNullException(nameof(assetMode));
        AssetToLoadName = assetToLoadName ?? throw new ArgumentNullException(nameof(assetToLoadName));
        AssetBundleCRC = assetBundleCRC;
        IsEncrypted = isEncrypted;
        Password = password ?? throw new ArgumentNullException(nameof(password));
        Platform = platform ?? throw new ArgumentNullException(nameof(platform));
        EndByte = endbyte;
    }
}
