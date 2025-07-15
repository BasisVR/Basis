using System.IO;
using System.Security.Cryptography;
using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Buffers;
public static class BasisEncryptionWrapper
{
    private const int SaltSize = 16; // Size of the salt in bytes
    private const int KeySize = 32; // Size of the key in bytes (256 bits)
    private const int IvSize = 16;  // Size of the IV in bytes (128 bits)
    public const int IterationSize = 10000;

    // Constant strings
    private const string ErrMissingDataEncrypt = "Encryption Failed: Missing Data";
    private const string ErrMissingDataDecrypt = "Decryption Failed: Missing Data";
    private const string ProgressFailure = "Failure";
    private const string ProgressInitEncryption = "Initializing Encryption";
    private const string ProgressGeneratedSalt = "Generated Salt";
    private const string ProgressGeneratedIV = "Generated IV";
    private const string ProgressSaltWritten = "Salt Written";
    private const string ProgressIVWritten = "IV Written";
    private const string ProgressDataEncrypted = "Data Encrypted";
    private const string ProgressFinalizingEncryption = "Finalizing Encryption";
    private const string ProgressEncryptionComplete = "Encryption Complete";
    private const string ProgressInitDecryption = "Initializing Decryption";
    private const string ProgressSaltRead = "Salt Read";
    private const string ProgressIVRead = "IV Read";
    private const string ProgressDerivingKey = "Deriving Key";
    private const string ProgressSettingUpDecryption = "Setting Up Decryption";
    private const string ProgressDecryptingDataStream = "Decrypting Data Stream";
    private const string ProgressFinalizingDecryption = "Finalizing Decryption";
    private const string ProgressDecryptionComplete = "Decryption Complete";
    private const string ProgressReadingData = "Reading Data";
    private const string ProgressWritingData = "Writing Data";
    private const string ErrDataNullOrEmpty = "Data requested was null or empty";
    private const string InternalLogMissingDataToEncrypt = "Missing Data To Encrypt";
    private const string InternalLogMissingDataToDecrypt = "Missing Data To Decrypt";
    private const string ReadingData = "Reading Data";
    public static async Task<byte[]> EncryptDataAsync(string UniqueID, byte[] dataToEncrypt, BasisPassword RandomizedPassword, BasisProgressReport reportProgress = null)
    {
        try
        {
            var encryptedData = await Task.Run(async () => await EncryptAsync(UniqueID, RandomizedPassword, dataToEncrypt, reportProgress)); // Run encryption on a separate thread
            return encryptedData;
        }
        finally
        {
            reportProgress?.ReportProgress(UniqueID, 100, ProgressFailure);
        }
    }

    public static async Task<byte[]> DecryptDataAsync(string UniqueID, byte[] dataToDecrypt, BasisPassword Randomizedpassword, BasisProgressReport reportProgress = null)
    {
        (byte[], byte[], byte[]) decryptedData = await Task.Run(async () => await DecryptAsync(UniqueID, Randomizedpassword.VP, dataToDecrypt, reportProgress)); // Run decryption on a separate thread
        return decryptedData.Item1;
    }

    private static async Task<byte[]> EncryptAsync(string UniqueID, BasisPassword password, byte[] dataToEncrypt, BasisProgressReport reportProgress = null)
    {
        if (dataToEncrypt == null || dataToEncrypt.Length == 0)
        {
            reportProgress?.ReportProgress(UniqueID, 0f, ErrMissingDataEncrypt);
            BasisDebug.LogError(InternalLogMissingDataToEncrypt);
            return null;
        }

        reportProgress?.ReportProgress(UniqueID, 5f, ProgressInitEncryption);

        byte[] salt = new byte[SaltSize];
        using (var rng = new RNGCryptoServiceProvider())
        {
            rng.GetBytes(salt);
        }

        reportProgress?.ReportProgress(UniqueID, 15f, ProgressGeneratedSalt);

        using (var key = new Rfc2898DeriveBytes(password.VP, salt, IterationSize))
        {
            var keyBytes = key.GetBytes(KeySize);

            byte[] iv = new byte[IvSize];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(iv);
            }

            reportProgress?.ReportProgress(UniqueID, 25f, ProgressGeneratedIV);

            using (var aes = Aes.Create())
            {
                aes.Key = keyBytes;
                aes.IV = iv;

                using (var msEncrypt = new MemoryStream())
                {
                    await msEncrypt.WriteAsync(salt, 0, salt.Length);
                    reportProgress?.ReportProgress(UniqueID, 40f, ProgressSaltWritten);

                    await msEncrypt.WriteAsync(iv, 0, iv.Length);
                    reportProgress?.ReportProgress(UniqueID, 50f, ProgressIVWritten);

                    using (var cryptoStream = new CryptoStream(msEncrypt, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        await cryptoStream.WriteAsync(dataToEncrypt, 0, dataToEncrypt.Length);
                        reportProgress?.ReportProgress(UniqueID, 70f, ProgressDataEncrypted);
                    }

                    reportProgress?.ReportProgress(UniqueID, 90f, ProgressFinalizingEncryption);
                    byte[] encryptedData = msEncrypt.ToArray();
                    reportProgress?.ReportProgress(UniqueID, 100f, ProgressEncryptionComplete);
                    return encryptedData;
                }
            }
        }
    }

    private static async Task<(byte[], byte[], byte[])> DecryptAsync(string UniqueID, string RandomizedString, byte[] dataToDecrypt, BasisProgressReport reportProgress = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        if (dataToDecrypt == null || dataToDecrypt.Length == 0)
        {
            reportProgress?.ReportProgress(UniqueID, 0f, ErrMissingDataDecrypt);
            BasisDebug.LogError(InternalLogMissingDataToDecrypt);
            return (null, null, null);
        }

        reportProgress?.ReportProgress(UniqueID, 5f, ProgressInitDecryption);

        byte[] salt = new byte[SaltSize];
        Buffer.BlockCopy(dataToDecrypt, 0, salt, 0, SaltSize);
        reportProgress?.ReportProgress(UniqueID, 20f, ProgressSaltRead);

        byte[] iv = new byte[IvSize];
        Buffer.BlockCopy(dataToDecrypt, SaltSize, iv, 0, IvSize);
        reportProgress?.ReportProgress(UniqueID, 30f, ProgressIVRead);

        reportProgress?.ReportProgress(UniqueID, 40f, ProgressDerivingKey);
        Stopwatch keyDeriveWatch = Stopwatch.StartNew();

        using (var key = new Rfc2898DeriveBytes(RandomizedString, salt, IterationSize))
        {
            var keyBytes = key.GetBytes(KeySize);
            keyDeriveWatch.Stop();

            using (var aes = Aes.Create())
            {
                aes.Key = keyBytes;
                aes.IV = iv;

                reportProgress?.ReportProgress(UniqueID, 60f, ProgressSettingUpDecryption);

                using (var decryptor = aes.CreateDecryptor())
                using (var inputStream = new MemoryStream(dataToDecrypt, SaltSize + IvSize, dataToDecrypt.Length - SaltSize - IvSize, false))
                using (var cryptoStream = new CryptoStream(inputStream, decryptor, CryptoStreamMode.Read))
                {
                    int estimatedSize = dataToDecrypt.Length - SaltSize - IvSize;
                    byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(estimatedSize);
                    int totalBytesRead = 0;

                    reportProgress?.ReportProgress(UniqueID, 70f, ProgressDecryptingDataStream);
                    Stopwatch decryptWatch = Stopwatch.StartNew();

                    int bytesRead;
                    do
                    {
                        bytesRead = await cryptoStream.ReadAsync(rentedBuffer, totalBytesRead, rentedBuffer.Length - totalBytesRead);
                        totalBytesRead += bytesRead;
                    } while (bytesRead > 0);

                    decryptWatch.Stop();
                    reportProgress?.ReportProgress(UniqueID, 90f, ProgressFinalizingDecryption);

                    byte[] output = new byte[totalBytesRead];
                    Buffer.BlockCopy(rentedBuffer, 0, output, 0, totalBytesRead);
                    ArrayPool<byte>.Shared.Return(rentedBuffer);

                    reportProgress?.ReportProgress(UniqueID, 100f, ProgressDecryptionComplete);
                    BasisDebug.Log($"[{UniqueID}] Total decryption time: {stopwatch.ElapsedMilliseconds}ms");

                    return (output, salt, iv);
                }
            }
        }
    }

    public static async Task ReadFileAsync(string UniqueID, string filePath, Func<byte[], Task> processChunk, BasisProgressReport reportProgress = null, int bufferSize = 4194304)
    {
        reportProgress.ReportProgress(UniqueID, 0f, ProgressReadingData);
        var fileSize = new FileInfo(filePath).Length;
        var buffer = new byte[bufferSize];
        long totalRead = 0;

        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        {
            int bytesRead;
            while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalRead += bytesRead;
                await processChunk(buffer[..bytesRead]);
                reportProgress.ReportProgress(UniqueID, (float)totalRead / fileSize * 100f, ProgressReadingData);
            }
        }
        reportProgress.ReportProgress(UniqueID, 100f, ProgressReadingData);
    }

    public static async Task WriteFileAsync(string UniqueID, string filePath, byte[] data, FileMode fileMode, BasisProgressReport reportProgress = null, int bufferSize = 4194304)
    {
        reportProgress.ReportProgress(UniqueID, 0f, ProgressWritingData);
        long totalWritten = 0;

        using (var fs = new FileStream(filePath, fileMode, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int bytesToWrite = Math.Min(bufferSize, data.Length - offset);
                await fs.WriteAsync(data, offset, bytesToWrite);
                totalWritten += bytesToWrite;
                offset += bytesToWrite;

                reportProgress.ReportProgress(UniqueID, (float)totalWritten / data.Length * 100f, ProgressWritingData);
            }
        }

        reportProgress.ReportProgress(UniqueID, 100f, ProgressWritingData);
    }

    public struct BasisPassword
    {
        public string VP;
    }

    public static async Task EncryptFileAsync(string UniqueID, BasisPassword password, string inputFilePath, string outputFilePath, BasisProgressReport reportProgress, int bufferSize = 4194304)
    {
        byte[] dataToEncrypt = await ReadAllBytesAsync(UniqueID, inputFilePath, reportProgress);
        var encryptedData = await EncryptDataAsync(UniqueID, dataToEncrypt, password, reportProgress);
        await WriteFileAsync(UniqueID, outputFilePath, encryptedData, FileMode.Create, reportProgress, bufferSize);
    }

    public static async Task DecryptFileAsync(string UniqueID, BasisPassword password, string inputFilePath, string outputFilePath, BasisProgressReport reportProgress, int bufferSize = 4194304)
    {
        byte[] dataToDecrypt = await ReadAllBytesAsync(UniqueID, inputFilePath, reportProgress);
        if (dataToDecrypt == null || dataToDecrypt.Length == 0)
        {
            throw new Exception(ErrDataNullOrEmpty);
        }
        var decryptedData = await DecryptDataAsync(UniqueID, dataToDecrypt, password, reportProgress);
        await WriteFileAsync(UniqueID, outputFilePath, decryptedData, FileMode.Create, reportProgress, bufferSize);
    }

    public static async Task<byte[]> DecryptFileAsync(string UniqueID, BasisPassword password, string inputFilePath, BasisProgressReport reportProgress, int bufferSize = 4194304)
    {
        byte[] dataToDecrypt = await ReadAllBytesAsync(UniqueID, inputFilePath, reportProgress, bufferSize);
        if (dataToDecrypt == null || dataToDecrypt.Length == 0)
        {
            BasisDebug.LogError(ErrDataNullOrEmpty);
            return null;
        }
        var decryptedData = await DecryptDataAsync(UniqueID, dataToDecrypt, password, reportProgress);
        return decryptedData;
    }

    private static async Task<byte[]> ReadAllBytesAsync(string UniqueID, string filePath, BasisProgressReport reportProgress, int bufferSize = 4194304)
    {
        reportProgress.ReportProgress(UniqueID, 0f, ProgressReadingData);

        var fileInfo = new FileInfo(filePath);
        byte[] data = new byte[fileInfo.Length];

        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
        {
            int totalRead = 0;
            int bytesRead;
            byte[] buffer = new byte[bufferSize];

            while ((bytesRead = await fs.ReadAsync(buffer, 0, Math.Min(bufferSize, data.Length - totalRead))) > 0)
            {
                Buffer.BlockCopy(buffer, 0, data, totalRead, bytesRead);
                totalRead += bytesRead;
                reportProgress.ReportProgress(UniqueID, (float)totalRead / fileInfo.Length * 100f, ReadingData);
            }
        }

        reportProgress.ReportProgress(UniqueID, 100f, ProgressReadingData);
        return data;
    }
}
