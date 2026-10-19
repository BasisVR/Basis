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

    public BasisBundleSection Slice(long relativeOffset, long length)
    {
        if (relativeOffset < 0 || length < 0 || relativeOffset > Length - length)
            throw new ArgumentOutOfRangeException(nameof(relativeOffset), $"Section slice {relativeOffset}+{length} exceeds {Length} bytes.");

        if (Bytes == null)
            return FromFile(FilePath, Offset + relativeOffset, length);

        byte[] slice = new byte[checked((int)length)];
        Buffer.BlockCopy(Bytes, checked((int)relativeOffset), slice, 0, slice.Length);
        return FromBytes(slice);
    }

    public override string ToString()
    {
        return Bytes != null ? $"{Length} bytes in memory" : $"{Length} bytes at {Offset} of {FilePath}";
    }
}
