using System.Threading.Tasks;
using UnityEngine;
public static class BasisEncryptionToData
{
    public static async Task<AssetBundleCreateRequest> GenerateBundleFromFile(string Password, byte[] Bytes, uint CRC, BasisProgressReport progressCallback)
    {
        // Define the password object for decryption
        var BasisPassword = new BasisEncryptionWrapper.BasisPassword
        {
            VP = Password
        };
        string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        // Decrypt the file asynchronously
        var LoadedBundleData = await BasisEncryptionWrapper.DecryptFromBytesAsync(UniqueID, BasisPassword, Bytes, progressCallback);

        if (LoadedBundleData.Success)
        {
            BasisDebug.Log("Attempting Asset Bundle Load...", BasisDebug.LogTag.Event);
        }
        else
        {
            BasisDebug.LogError($"Failed to Decrypt, {LoadedBundleData.Error} | {LoadedBundleData.Message} | {LoadedBundleData.Exception}");
        }

        // Start the AssetBundle loading process from memory with CRC check
        AssetBundleCreateRequest assetBundleCreateRequest = AssetBundle.LoadFromMemoryAsync(LoadedBundleData.Data, CRC);
        // Track the last reported progress
        int lastReportedProgress = -1;

        // Periodically check the progress of AssetBundleCreateRequest and report progress
        while (!assetBundleCreateRequest.isDone)
        {
            // Convert the progress to a percentage (0-100)
            int progress = Mathf.RoundToInt(assetBundleCreateRequest.progress * 100);

            // Report progress only if it has changed
            if (progress > lastReportedProgress)
            {
                lastReportedProgress = progress;

                // Call the progress callback with the current progress
                progressCallback.ReportProgress(UniqueID.ToString(),progress, "loading bundle");
            }

            // Wait a short period before checking again to avoid busy waiting
            await Task.Delay(50); // Adjust delay as needed (e.g., 50ms)
        }

        // Ensure progress reaches 100% after completion
        progressCallback.ReportProgress(UniqueID.ToString(), 100, "loading bundle");

        // Await the request completion
        await assetBundleCreateRequest;

        return assetBundleCreateRequest;
    }
    public static async Task<BasisBundleConnector> GenerateMetaFromBytes(string password, byte[] encryptedBytes, BasisProgressReport progressCallback)
    {
        var basisPassword = new BasisEncryptionWrapper.BasisPassword { VP = password };
        string uniqueID = BasisGenerateUniqueID.GenerateUniqueID();

        var decryptedMeta = await BasisEncryptionWrapper.DecryptFromBytesAsync(uniqueID, basisPassword, encryptedBytes, progressCallback);


        if (decryptedMeta.Success)
        {
            BasisDebug.Log("Converting decrypted meta file to BasisBundleInformation...", BasisDebug.LogTag.Event);
            return ConvertBytesToJson(decryptedMeta.Data, out var connector) ? connector : null;
        }
        else
        {
            BasisDebug.LogError($"Failed to Decrypt, {decryptedMeta.Error} | {decryptedMeta.Message} | {decryptedMeta.Exception}");
            return null;
        }
    }

    public static bool ConvertBytesToJson(byte[] data, out BasisBundleConnector connector)
    {
        connector = null;

        if (data == null || data.Length == 0)
        {
            BasisDebug.LogError($"Data for {nameof(BasisBundleConnector)} is empty or null.", BasisDebug.LogTag.Event);
            return false;
        }

        BasisDebug.Log("Converting byte array to JSON string...", BasisDebug.LogTag.Event);
        connector = BasisSerialization.DeserializeValue<BasisBundleConnector>(data);
        BasisDebug.Log("Converted byte array to JSON string...", BasisDebug.LogTag.Event);
        return true;
    }
}
