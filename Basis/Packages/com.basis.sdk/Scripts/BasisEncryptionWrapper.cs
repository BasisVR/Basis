using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

public static partial class BasisEncryptionWrapper
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int IvSize = 16;
    public const int IterationSize = 10000;
    /// <summary>
    /// Largest single-dimension byte[] the runtime will allocate (0x7FFFFFC7), and therefore the
    /// hard ceiling on one decrypted section. AssetBundle.LoadFromMemoryAsync takes a byte[] too,
    /// so this is the real limit on one platform section regardless of how it is read.
    /// A .bee file as a whole is not bound by it — only one section at a time is decrypted.
    /// </summary>
    public const long MaxPlaintextBytes = 2147483591L;

    // Format-compatible drop-in for `new Rfc2898DeriveBytes(password, salt, iters).GetBytes(outputBytes)`.
    // Same algorithm (PBKDF2-HMAC-SHA1, UTF-8 password encoding), so output is byte-identical and existing
    // bundles still decrypt. BouncyCastle's managed PBKDF2 is significantly faster on Mono/IL2CPP than
    // .NET's Rfc2898DeriveBytes and avoids the multi-MB garbage the managed iteration loop produces.
    private static byte[] DeriveKeyPbkdf2Sha1(string password, byte[] salt, int iterations, int outputBytes)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var gen = new Pkcs5S2ParametersGenerator(new Sha1Digest());
            gen.Init(passwordBytes, salt, iterations);
            var keyParam = (KeyParameter)gen.GenerateDerivedMacParameters(outputBytes * 8);
            return keyParam.GetKey();
        }
        finally
        {
            Array.Clear(passwordBytes, 0, passwordBytes.Length);
        }
    }

    // Progress/Status Messages
    private const string ProgressInitEncryption = "Initializing Encryption";
    private const string ProgressEncryptionComplete = "Encryption Complete";
    private const string ProgressInitDecryption = "Initializing Decryption";
    private const string ProgressDecryptionComplete = "Decryption Complete";
    private const string ProgressReadingData = "Reading Data";
    private const string ProgressWritingData = "Writing Data";

    public struct BasisPassword
    {
        public string VP;
    }

    private static int CalculateBufferSize(long dataLength)
    {
        if (dataLength > 1024L * 1024L * 1024L) // > 1 GB
            return 32 * 1024 * 1024; // 32 MB buffer
        if (dataLength > 100L * 1024L * 1024L) // > 100 MB
            return 16 * 1024 * 1024; // 16 MB buffer
        if (dataLength > 1L * 1024L * 1024L) // > 1 MB
            return 4 * 1024 * 1024; // 4 MB buffer
        if (dataLength > 8192)
            return 8192; // 8 KB buffer
        return (int)dataLength;
    }

    // Threshold to decide when to offload encryption to a separate thread
    private const long LargeFileThreshold = 10L * 1024L * 1024L; // 25 MB

    public static Task EncryptFileAsync(string UniqueID, BasisPassword password, string inputPath, string outputPath, BasisProgressReport reportProgress)
    {
        var inputFileInfo = new FileInfo(inputPath);

        if (inputFileInfo.Length > LargeFileThreshold)
        {
            // Offload to background thread for large files
            return Task.Run(() => EncryptFileInternalAsync(UniqueID, password, inputPath, outputPath, reportProgress));
        }
        else
        {
            // Run directly (async IO) for small files
            return EncryptFileInternalAsync(UniqueID, password, inputPath, outputPath, reportProgress);
        }
    }

    private static async Task EncryptFileInternalAsync(string UniqueID, BasisPassword password, string inputPath, string outputPath, BasisProgressReport reportProgress)
    {
        reportProgress?.ReportProgress(UniqueID, 0, ProgressInitEncryption);

        FileInfo inputFileInfo = new FileInfo(inputPath);
        int bufferSize = CalculateBufferSize(inputFileInfo.Length);

        byte[] salt = new byte[SaltSize];
        byte[] iv = new byte[IvSize];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
            rng.GetBytes(iv);
        }

        byte[] keyBytes = DeriveKeyPbkdf2Sha1(password.VP, salt, IterationSize, KeySize);

        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.IV = iv;

        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        reportProgress?.ReportProgress(UniqueID, 5, "Writing Salt & IV");
        await output.WriteAsync(salt, 0, salt.Length);
        await output.WriteAsync(iv, 0, iv.Length);

        using var cryptoStream = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write);

        // Rent buffer from pool to reduce allocations
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            long totalRead = 0;
            long totalLength = input.Length;

            float lastReportedProgress = 0;

            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer.AsMemory(0, bufferSize))) > 0)
            {
                await cryptoStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                float progress = (float)totalRead / totalLength * 90f + 5f;
                if (progress - lastReportedProgress >= 1)
                {
                    reportProgress?.ReportProgress(UniqueID, progress, ProgressWritingData);
                    lastReportedProgress = progress;
                }            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        cryptoStream.FlushFinalBlock();

        reportProgress?.ReportProgress(UniqueID, 100, ProgressEncryptionComplete);
    }

    public static Task<BasisDecryptResult> DecryptFromBytesAsync(
        string UniqueID,
        BasisPassword password,
        byte[] encryptedData,
        BasisProgressReport reportProgress,
        CancellationToken ct = default)
    {
        return Task.Run(() => DecryptFromBytesInternalAsync(UniqueID, password, encryptedData, reportProgress, ct), ct);
    }

    /// <summary>
    /// Decrypts a section straight off disk. The bytes entry point has to be handed the whole
    /// encrypted payload and then takes a defensive copy of it, because a caller's buffer can be
    /// rewritten by a concurrent download mid-decrypt; a file on disk cannot, so both of those
    /// bundle-sized arrays go away and only the plaintext is allocated.
    /// </summary>
    public static Task<BasisDecryptResult> DecryptFromFileAsync(
        string UniqueID,
        BasisPassword password,
        string filePath,
        long offset,
        long length,
        BasisProgressReport reportProgress,
        CancellationToken ct = default)
    {
        return Task.Run(() => DecryptFromFileInternalAsync(UniqueID, password, filePath, offset, length, reportProgress, ct), ct);
    }

    private static async Task<BasisDecryptResult> DecryptFromFileInternalAsync(
        string UniqueID,
        BasisPassword password,
        string filePath,
        long offset,
        long length,
        BasisProgressReport reportProgress,
        CancellationToken ct)
    {
        try
        {
            reportProgress?.ReportProgress(UniqueID, 0, ProgressInitDecryption);

            if (ct.IsCancellationRequested)
            {
                return BasisDecryptResult.Fail(BasisDecryptError.Cancelled, "Decryption cancelled.");
            }

            if (string.IsNullOrWhiteSpace(password.VP))
            {
                return BasisDecryptResult.Fail(BasisDecryptError.InvalidPassword, "Password was null/empty.");
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return BasisDecryptResult.Fail(BasisDecryptError.DataNullOrEmpty, $"Encrypted file missing: {filePath}");
            }

            int minLen = SaltSize + IvSize + 1;
            if (length < minLen)
            {
                return BasisDecryptResult.Fail(
                    BasisDecryptError.HeaderTooShort,
                    $"Encrypted section too short. Length={length}, minimum={minLen}.");
            }

            // Answered from the requested length alone, ahead of anything that touches the file:
            // a section this large cannot be decrypted whatever the file turns out to contain, and
            // "too large to load" is the actionable answer rather than a range complaint.
            if (length - SaltSize - IvSize > MaxPlaintextBytes)
            {
                return BasisDecryptResult.Fail(BasisDecryptError.Unknown, OversizedMessage(length));
            }

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 96 * 1024, useAsync: true);
            if (offset < 0 || offset + length > fileStream.Length)
            {
                return BasisDecryptResult.Fail(
                    BasisDecryptError.WrongFormatOrCorruptHeader,
                    $"Section range {offset}..{offset + length} lies outside {filePath} ({fileStream.Length} bytes).");
            }

            fileStream.Seek(offset, SeekOrigin.Begin);
            using var bounded = new BoundedReadStream(fileStream, length);
            return await DecryptStreamInternalAsync(UniqueID, password, bounded, length, reportProgress, ct);
        }
        catch (Exception ex)
        {
            return FailFromException(ex);
        }
    }

    private static async Task<BasisDecryptResult> DecryptFromBytesInternalAsync(
        string UniqueID,
        BasisPassword password,
        byte[] encryptedData,
        BasisProgressReport reportProgress,
        CancellationToken ct)
    {
        try
        {
            reportProgress?.ReportProgress(UniqueID, 0, ProgressInitDecryption);

            if (ct.IsCancellationRequested)
            {
                return BasisDecryptResult.Fail(BasisDecryptError.Cancelled, "Decryption cancelled.");
            }

            if (string.IsNullOrWhiteSpace(password.VP))
            {
                return BasisDecryptResult.Fail(BasisDecryptError.InvalidPassword, "Password was null/empty.");
            }

            if (encryptedData == null || encryptedData.Length == 0)
            {
                return BasisDecryptResult.Fail(BasisDecryptError.DataNullOrEmpty, "Encrypted data was null/empty.");
            }

            int minLen = SaltSize + IvSize + 1; // need at least 1 byte of ciphertext
            if (encryptedData.Length < minLen)
            {
                return BasisDecryptResult.Fail(
                    BasisDecryptError.HeaderTooShort,
                    $"Encrypted data too short. Length={encryptedData.Length}, minimum={minLen}.");
            }

            // Defensive copy — the caller's buffer may be pooled/reused by concurrent downloads.
            // Without this, another async download completing between our awaits can overwrite the
            // ciphertext mid-decryption, causing PKCS7 padding failures under load.
            byte[] localCopy = new byte[encryptedData.Length];
            Buffer.BlockCopy(encryptedData, 0, localCopy, 0, encryptedData.Length);

            using var msInput = new MemoryStream(localCopy, writable: false);
            return await DecryptStreamInternalAsync(UniqueID, password, msInput, encryptedData.Length, reportProgress, ct);
        }
        catch (Exception ex)
        {
            return FailFromException(ex);
        }
    }

    private static string OversizedMessage(long totalLength)
    {
        return $"Section is {totalLength} bytes; the decrypted payload would exceed the {MaxPlaintextBytes} byte array limit.";
    }

    private static BasisDecryptResult FailFromException(Exception ex)
    {
        if (ex is OperationCanceledException oce)
        {
            return BasisDecryptResult.Fail(BasisDecryptError.Cancelled, "Decryption cancelled.", oce);
        }

        // Treat all crypto failures the same (wrong password OR corrupt data)
        // NOTE: avoid referencing CryptographicException in a catch clause.
        if (ex.GetType().FullName == "System.Security.Cryptography.CryptographicException")
        {
            return BasisDecryptResult.Fail( BasisDecryptError.WrongPasswordOrCorruptedData, "Decryption failed: wrong password or data corrupted (unauthenticated ciphertext).", ex);
        }

        return BasisDecryptResult.Fail(BasisDecryptError.Unknown, "Decryption failed with an unexpected error.", ex);
    }

    /// <summary>
    /// Shared decrypt body. <paramref name="source"/> must be positioned at the salt and must end
    /// exactly at the end of the ciphertext: PKCS7 trims the plaintext, so the read loop relies on
    /// the stream reporting EOF to finish the final block.
    /// </summary>
    private static async Task<BasisDecryptResult> DecryptStreamInternalAsync(
        string UniqueID,
        BasisPassword password,
        Stream source,
        long totalLength,
        BasisProgressReport reportProgress,
        CancellationToken ct)
    {
        // The plaintext is handed back as a byte[], so a section past the runtime's array ceiling
        // cannot be decrypted here however it was read. Say so, rather than throwing an
        // OverflowException out of the int cast further down.
        if (totalLength - SaltSize - IvSize > MaxPlaintextBytes)
        {
            return BasisDecryptResult.Fail(BasisDecryptError.Unknown, OversizedMessage(totalLength));
        }

        byte[] salt = new byte[SaltSize];
        byte[] iv = new byte[IvSize];

        // Read exactly salt+iv; if not, it's not your format or truncated.
        int readSalt = await ReadExactlyAsync(source, salt, SaltSize, ct);
        int readIv = await ReadExactlyAsync(source, iv, IvSize, ct);

        if (readSalt != SaltSize || readIv != IvSize)
        {
            return BasisDecryptResult.Fail(
                BasisDecryptError.WrongFormatOrCorruptHeader,
                $"Failed to read header (salt/iv). ReadSalt={readSalt}/{SaltSize}, ReadIv={readIv}/{IvSize}.");
        }

        byte[] keyBytes = DeriveKeyPbkdf2Sha1(password.VP, salt, IterationSize, KeySize);

        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var cryptoStream = new CryptoStream(source, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);

        // CBC + PKCS7 plaintext is always shorter than the ciphertext, so decrypt
        // straight into one upper-bound buffer and trim once at the end. The old
        // rented-chunk -> pooled-stream -> ToArray path copied the payload an
        // extra time and churned the LOH growing the stream under large sections.
        int cipherLength = checked((int)(totalLength - SaltSize - IvSize));
        byte[] plain = new byte[cipherLength];
        int totalRead = 0;
        float lastReportedProgress = 0;

        while (totalRead < cipherLength)
        {
            ct.ThrowIfCancellationRequested();

            int bytesRead = await cryptoStream.ReadAsync(plain.AsMemory(totalRead, cipherLength - totalRead), ct);
            if (bytesRead <= 0) break;

            totalRead += bytesRead;

            float progress = (float)totalRead / cipherLength * 90f + 5f;
            if (progress - lastReportedProgress >= 1f)
            {
                reportProgress?.ReportProgress(UniqueID, progress, ProgressReadingData);
                lastReportedProgress = progress;
            }
        }

        byte[] result;
        if (totalRead == plain.Length)
        {
            result = plain;
        }
        else
        {
            result = new byte[totalRead];
            Buffer.BlockCopy(plain, 0, result, 0, totalRead);
        }

        reportProgress?.ReportProgress(UniqueID, 100, ProgressDecryptionComplete);
        return BasisDecryptResult.Ok(result);
    }

    private static async Task<int> ReadExactlyAsync(Stream source, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await source.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n <= 0) break;
            read += n;
        }
        return read;
    }

    /// <summary>
    /// Read-only window over a stream. CryptoStream drains its source to EOF, so a section that is
    /// not the last thing in the file has to report its own end or the decryptor keeps chewing.
    /// </summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream inner;
        private long remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            this.inner = inner;
            remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining <= 0) return 0;
            int toRead = (int)Math.Min(count, remaining);
            int read = inner.Read(buffer, offset, toRead);
            remaining -= read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (remaining <= 0) return 0;
            int toRead = (int)Math.Min(count, remaining);
            int read = await inner.ReadAsync(buffer.AsMemory(offset, toRead), cancellationToken);
            remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (remaining <= 0) return 0;
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await inner.ReadAsync(buffer.Slice(0, toRead), cancellationToken);
            remaining -= read;
            return read;
        }
    }
    public static async Task<byte[]> EncryptToBytesAsync(string UniqueID, BasisPassword password, byte[] data, BasisProgressReport reportProgress)
    {
        reportProgress?.ReportProgress(UniqueID, 0, ProgressInitEncryption);

        int bufferSize = CalculateBufferSize(data.Length);

        byte[] salt = new byte[SaltSize];
        byte[] iv = new byte[IvSize];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
            rng.GetBytes(iv);
        }

        byte[] keyBytes = DeriveKeyPbkdf2Sha1(password.VP, salt, IterationSize, KeySize);

        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.IV = iv;

        using var msOut = new MemoryStream();
        reportProgress?.ReportProgress(UniqueID, 5, "Writing Salt & IV");
        await msOut.WriteAsync(salt, 0, salt.Length);
        await msOut.WriteAsync(iv, 0, iv.Length);

        using var cryptoStream = new CryptoStream(msOut, aes.CreateEncryptor(), CryptoStreamMode.Write);

        // Rent buffer
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            long totalRead = 0;
            long totalLength = data.Length;

            int bytesRead;
            float lastReportedProgress = 0;

            using var msIn = new MemoryStream(data, writable: false);
            while ((bytesRead = await msIn.ReadAsync(buffer.AsMemory(0, bufferSize))) > 0)
            {
                await cryptoStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                float progress = (float)totalRead / totalLength * 90f + 5f;
                if (progress - lastReportedProgress >= 1)
                {
                    reportProgress?.ReportProgress(UniqueID, progress, ProgressWritingData);
                    lastReportedProgress = progress;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        cryptoStream.FlushFinalBlock();

        reportProgress?.ReportProgress(UniqueID, 100, ProgressEncryptionComplete);

        return msOut.ToArray();
    }

    // Custom MemoryStream that minimizes allocations by exposing the internal buffer directly.
    // Only use when safe, here for efficiency in DecryptFromBytesInternalAsync.
    private sealed class PooledMemoryStream : MemoryStream
    {
        public PooledMemoryStream() : base() { }

        public override byte[] ToArray()
        {
            // Avoids copying if possible (internal buffer might be larger than Length)
            return base.GetBuffer().AsSpan(0, (int)Length).ToArray();
        }
    }
}
