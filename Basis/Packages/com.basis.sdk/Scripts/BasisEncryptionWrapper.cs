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
        if (encryptedData != null && encryptedData.Length > LargeFileThreshold)
        {
            return Task.Run(() => DecryptFromBytesInternalAsync(UniqueID, password, encryptedData, reportProgress, ct), ct);
        }

        return DecryptFromBytesInternalAsync(UniqueID, password, encryptedData, reportProgress, ct);
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

            int bufferSize = CalculateBufferSize(encryptedData.Length);

            // Defensive copy — the caller's buffer may be pooled/reused by concurrent downloads.
            // Without this, another async download completing between our awaits can overwrite the
            // ciphertext mid-decryption, causing PKCS7 padding failures under load.
            byte[] localCopy = new byte[encryptedData.Length];
            Buffer.BlockCopy(encryptedData, 0, localCopy, 0, encryptedData.Length);

            using var msInput = new MemoryStream(localCopy, writable: false);

            byte[] salt = new byte[SaltSize];
            byte[] iv = new byte[IvSize];

            // Read exactly salt+iv; if not, it's not your format or truncated.
            int readSalt = await msInput.ReadAsync(salt, 0, SaltSize, ct);
            int readIv = await msInput.ReadAsync(iv, 0, IvSize, ct);

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

            using var cryptoStream = new CryptoStream(msInput, aes.CreateDecryptor(), CryptoStreamMode.Read);

            using var msOutput = new PooledMemoryStream();

            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                long totalRead = 0;
                long estimatedSize = Math.Max(1, encryptedData.Length - SaltSize - IvSize);

                float lastReportedProgress = 0;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    int bytesRead = await cryptoStream.ReadAsync(buffer.AsMemory(0, bufferSize), ct);
                    if (bytesRead <= 0) break;

                    await msOutput.WriteAsync(buffer.AsMemory(0, bytesRead), ct);

                    totalRead += bytesRead;

                    float progress = (float)totalRead / estimatedSize * 90f + 5f;
                    if (progress - lastReportedProgress >= 1f)
                    {
                        reportProgress?.ReportProgress(UniqueID, progress, ProgressReadingData);
                        lastReportedProgress = progress;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            reportProgress?.ReportProgress(UniqueID, 100, ProgressDecryptionComplete);
            return BasisDecryptResult.Ok(msOutput.ToArray());
        }
        catch (Exception ex)
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
