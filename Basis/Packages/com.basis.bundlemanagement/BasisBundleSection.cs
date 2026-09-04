using System;

/// <summary>
/// The encrypted platform section of a .bee, as either bytes already in memory (a fresh download,
/// which has to hold them anyway to write the cache file) or a range of a file on disk (a cache
/// read, which does not). Decryption resolves whichever it was handed, so the cache path never
/// materialises a bundle-sized managed array just to feed the decryptor.
/// </summary>
public readonly struct BasisBundleSection
{
    public readonly byte[] Bytes;
    public readonly string FilePath;
    public readonly long Offset;
    public readonly long Length;

    private BasisBundleSection(byte[] bytes, string filePath, long offset, long length)
    {
        Bytes = bytes;
        FilePath = filePath;
        Offset = offset;
        Length = length;
    }

    public static BasisBundleSection FromBytes(byte[] bytes)
    {
        return new BasisBundleSection(bytes, null, 0, bytes?.LongLength ?? 0);
    }

    public static BasisBundleSection FromFile(string filePath, long offset, long length)
    {
        return new BasisBundleSection(null, filePath, offset, length);
    }

    public bool HasPayload => Length > 0 && (Bytes != null || !string.IsNullOrEmpty(FilePath));

    public override string ToString()
    {
        return Bytes != null ? $"{Length} bytes in memory" : $"{Length} bytes at {Offset} of {FilePath}";
    }
}
