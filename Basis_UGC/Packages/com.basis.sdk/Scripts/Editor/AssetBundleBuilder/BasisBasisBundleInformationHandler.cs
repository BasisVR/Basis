using BasisSerializer.OdinSerializer;
using System.IO;
using System.Threading.Tasks;
public static class BasisBasisBundleInformationHandler
{
    public static async Task<(BasisBundleConnector,string)> BasisBundleConnector(BasisAssetBundleObject BuildSettings, BasisBundleConnector BasisBundleConnector, string ConnectorPassword, bool DeleteUnEncrypted = true)
    {
        string filePath = Path.Combine(BuildSettings.AssetBundleDirectory, $"Connector{BuildSettings.BasisMetaExtension}");

        // If the file exists, delete it
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        string EcnyptedfilePath = await SaveBasisBundleConnector(BasisBundleConnector, filePath, BuildSettings, ConnectorPassword, DeleteUnEncrypted);
        return new (BasisBundleConnector, EcnyptedfilePath);
    }
    private static async Task<string> SaveBasisBundleConnector(BasisBundleConnector BasisBundleConnector, string filePath, BasisAssetBundleObject BuildSettings, string password,bool DeleteUnEncrypted = true)
    {
        byte[] Information = SerializationUtility.SerializeValue<BasisBundleConnector>(BasisBundleConnector, DataFormat.JSON);
        try
        {
            BasisDebug.Log("Saving Json " + Information.Length);
            // Write JSON data to the file
            await File.WriteAllBytesAsync(filePath, Information);
            BasisDebug.Log($"BasisBundleInformation saved to {filePath} " + password);
            string EncryptedPath = Path.Combine(filePath , BuildSettings.BasisMetaEncryptedExtension);
            var BasisPassword = new BasisEncryptionWrapper.BasisPassword
            {
                VP = password
            };
            string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
            await BasisEncryptionWrapper.EncryptFileAsync(UniqueID, BasisPassword, filePath, EncryptedPath, Report);

            // Delete the bundle file if it exists
            if (DeleteUnEncrypted && File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            return EncryptedPath;
        }
        catch (IOException ioEx)
        {
            BasisDebug.LogError($"Failed to save BasisBundleInformation to {filePath}: {ioEx.Message}");
            return string.Empty;
        }
    }
    // Function to validate BasisBundleInformation
    private static BasisProgressReport Report = new BasisProgressReport();
}
