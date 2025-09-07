public static class BasisBeeConstants
{
    public static readonly string BasisMetaExtension = ".BME";
    public static readonly string BasisEncryptedExtension = ".BEE";
    public static readonly string AssetBundlesFolder = "BEEData";
    public static int TimeUntilMemoryRemoval = 30;
    public const string DefaultAvatar = "LoadingAvatar";
    /// <summary>Number of bytes in the REMOTE header (Int64 connector length).</summary>
    public const int RemoteHeaderSize = 8;

    /// <summary>Number of bytes in the ON-DISK header (Int32 connector length).</summary>
    public const int DiskHeaderSize = 4;

    public const long MaxConnectorBytes = 64L * 1024 * 1024; // 64 MB safeguard for connector
    public const long MaxSectionBytes = 4L * 1024 * 1024 * 1024; // 4 GB safeguard for section
}
