using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
public static class BasisEncryptionToData
{
    public static BasisBundleSection AssetBundlePart(BasisBundleGenerated generated, BasisBundleSection section)
    {
        long length = generated != null && generated.AssetBundleEndByte > 0
            ? generated.AssetBundleEndByte
            : section.Length;
        return section.Slice(0, length);
    }

    public static bool TryGetEmbeddedGraphicsStatePart(
        BasisBundleGenerated generated, BasisBundleSection section, out BasisBundleSection payloadSection)
    {
        payloadSection = default;
        if (!BasisGraphicsStatePrewarm.Enabled || !BasisGraphicsStatePrewarm.BackendBenefits() ||
            generated?.GraphicsStatePayloads == null)
            return false;

        string api = SystemInfo.graphicsDeviceType.ToString();
        BasisGraphicsStatePayload payload = null;
        for (int i = 0; i < generated.GraphicsStatePayloads.Length; i++)
        {
            BasisGraphicsStatePayload candidate = generated.GraphicsStatePayloads[i];
            if (candidate != null &&
                string.Equals(candidate.GraphicsAPI, api, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(candidate.RuntimePlatform) || string.Equals(candidate.RuntimePlatform, Application.platform.ToString(), StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(candidate.QualityLevelName) || string.Equals(candidate.QualityLevelName, QualitySettings.names[QualitySettings.GetQualityLevel()], StringComparison.Ordinal)))
            {
                payload = candidate;
                break;
            }
        }
        if (payload == null || payload.Length <= 0) return false;
        payloadSection = section.Slice(payload.Offset, payload.Length);
        return true;
    }

    public static async Task<GraphicsStateCollection> LoadEmbeddedGraphicsStates(
        string password, BasisBundleSection encryptedPayload, BasisProgressReport progressCallback)
    {
        if (!encryptedPayload.HasPayload) return null;
        string api = SystemInfo.graphicsDeviceType.ToString();

        string temporaryPath = null;
        try
        {
            var basisPassword = new BasisEncryptionWrapper.BasisPassword { VP = password };
            var decrypted = await DecryptSection(
                BasisGenerateUniqueID.GenerateUniqueID(), basisPassword, encryptedPayload,
                progressCallback ?? new BasisProgressReport());
            if (!decrypted.Success || decrypted.Data == null || decrypted.Data.Length == 0)
            {
                BasisDebug.LogWarning($"BEE PSO: failed to decrypt embedded {api} states ({decrypted.Message}).");
                return null;
            }

            temporaryPath = Path.Combine(Application.temporaryCachePath, $"basis_embedded_{Guid.NewGuid():N}.{api}.graphicsstate");
            File.WriteAllBytes(temporaryPath, decrypted.Data);
            GraphicsStateCollection collection = new GraphicsStateCollection();
            if (!collection.LoadFromFile(temporaryPath))
            {
                UnityEngine.Object.Destroy(collection);
                BasisDebug.LogWarning($"BEE PSO: Unity rejected the embedded {api} collection.");
                return null;
            }
            string quality = QualitySettings.names[QualitySettings.GetQualityLevel()];
            if (collection.graphicsDeviceType != SystemInfo.graphicsDeviceType ||
                collection.runtimePlatform != Application.platform ||
                !string.Equals(collection.qualityLevelName, quality, StringComparison.Ordinal))
            {
                BasisDebug.LogWarning(
                    $"BEE PSO: embedded collection targets {collection.runtimePlatform}/{collection.graphicsDeviceType}/{collection.qualityLevelName}, " +
                    $"current is {Application.platform}/{SystemInfo.graphicsDeviceType}/{quality}; ignored.");
                UnityEngine.Object.Destroy(collection);
                return null;
            }
            BasisDebug.Log($"BEE PSO: loaded {collection.variantCount} embedded {api} variant(s).", BasisDebug.LogTag.Event);
            return collection;
        }
        catch (Exception ex)
        {
            BasisDebug.LogWarning($"BEE PSO: embedded collection load failed ({ex.Message}).");
            return null;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static Task<BasisEncryptionWrapper.BasisDecryptResult> DecryptSection(string uniqueID, BasisEncryptionWrapper.BasisPassword password, BasisBundleSection section, BasisProgressReport progressCallback, System.Threading.CancellationToken ct = default)
    {
        if (section.Bytes != null)
        {
            return BasisEncryptionWrapper.DecryptFromBytesAsync(uniqueID, password, section.Bytes, progressCallback, ct);
        }

        return BasisEncryptionWrapper.DecryptFromFileAsync(uniqueID, password, section.FilePath, section.Offset, section.Length, progressCallback, ct);
    }

    public static async Task<AssetBundleCreateRequest> GenerateBundleFromFile(string Password, BasisBundleSection Section, uint CRC, BasisProgressReport progressCallback, Action<string> unityVersionResolved = null)
    {
        // Define the password object for decryption
        var BasisPassword = new BasisEncryptionWrapper.BasisPassword
        {
            VP = Password
        };
        if (progressCallback == null)
        {
            progressCallback = new BasisProgressReport();
        }
        string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        // Decrypt the file asynchronously
        var decrypted = await DecryptSection(UniqueID, BasisPassword, Section, progressCallback.Stage(UniqueID, 0, 20));

        if (!decrypted.Success || decrypted.Data == null || decrypted.Data.Length == 0)
        {
            BasisDebug.LogError($"Decrypt failed: {decrypted.Error} | {decrypted.Message}");
            return null; // <-- critical
        }

        unityVersionResolved?.Invoke(TryReadUnityVersion(decrypted.Data, out string builtWithUnityVersion)
            ? builtWithUnityVersion
            : null);

        BasisDebug.Log("Attempting Asset Bundle Load...", BasisDebug.LogTag.Event);

        AssetBundleCreateRequest assetBundleCreateRequest;
        try
        {
            assetBundleCreateRequest = AssetBundle.LoadFromMemoryAsync(decrypted.Data, CRC);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"LoadFromMemoryAsync threw: {ex}");
            return null;
        }
        while (!assetBundleCreateRequest.isDone)
        {
            progressCallback.ReportProgress(UniqueID, 20 + Mathf.Min(assetBundleCreateRequest.progress, 0.99f) * 80, "Loading bundle");
            await BasisTasks.Delay(50);
        }

        progressCallback.ReportProgress(UniqueID, 100, "Loading bundle");
        await assetBundleCreateRequest;

        // req.assetBundle can still be null if CRC fails or bytes aren’t a bundle.
        if (assetBundleCreateRequest.assetBundle == null)
        {
            BasisDebug.LogError("AssetBundle load finished but assetBundle is null (CRC mismatch or invalid bundle bytes).");
            return null;
        }

        return assetBundleCreateRequest;
    }

    /// <summary>
    /// Reads the Unity editor version stored in a native AssetBundle header. Modern bundles use
    /// UnityFS: a null-terminated signature, a big-endian format revision, then the editor version
    /// and editor revision as null-terminated ASCII strings.
    /// </summary>
    public static bool TryReadUnityVersion(byte[] bundleBytes, out string unityVersion)
    {
        unityVersion = null;
        if (bundleBytes == null || bundleBytes.Length < 16)
            return false;

        int offset = 0;
        if (!TryReadNullTerminatedAscii(bundleBytes, ref offset, 16, out string signature) ||
            !string.Equals(signature, "UnityFS", StringComparison.Ordinal))
            return false;

        // SerializedFile format version. Its value is not needed here, but it occupies four bytes.
        if (offset > bundleBytes.Length - 4)
            return false;
        offset += 4;

        if (!TryReadNullTerminatedAscii(bundleBytes, ref offset, 64, out string version) ||
            !TryReadNullTerminatedAscii(bundleBytes, ref offset, 64, out string revision))
            return false;

        // Unity commonly writes the generic bundle-format marker "5.x.x" first and the
        // concrete editor version (for example 6000.5.10f1) in the revision field.
        if (IsConcreteUnityVersion(revision))
            unityVersion = revision;
        else if (IsConcreteUnityVersion(version))
            unityVersion = version;
        else
            unityVersion = string.IsNullOrWhiteSpace(revision) ? version : revision;

        return !string.IsNullOrWhiteSpace(unityVersion);
    }

    private static bool IsConcreteUnityVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        int dot = value.IndexOf('.');
        return dot > 0 && int.TryParse(value.Substring(0, dot), out _);
    }

    private static bool TryReadNullTerminatedAscii(byte[] bytes, ref int offset, int maxLength, out string value)
    {
        value = null;
        if (offset < 0 || offset >= bytes.Length)
            return false;

        int start = offset;
        int limit = Math.Min(bytes.Length, start + maxLength + 1);
        while (offset < limit && bytes[offset] != 0)
        {
            byte c = bytes[offset];
            if (c < 0x20 || c > 0x7E)
                return false;
            offset++;
        }
        if (offset >= limit || bytes[offset] != 0)
            return false;

        value = Encoding.ASCII.GetString(bytes, start, offset - start);
        offset++;
        return true;
    }
    public static async Task<BasisBundleConnector> GenerateMetaFromBytes(string password, byte[] encryptedBytes, BasisProgressReport progressCallback)
    {
        var basisPassword = new BasisEncryptionWrapper.BasisPassword { VP = password };
        string uniqueID = BasisGenerateUniqueID.GenerateUniqueID();

        var decryptedMeta = await BasisEncryptionWrapper.DecryptFromBytesAsync(uniqueID, basisPassword, encryptedBytes, progressCallback).ConfigureAwait(false);


        if (decryptedMeta.Success)
        {
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

        try
        {
            connector = BasisSerialization.DeserializeValue<BasisBundleConnector>(data);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"DeserializeValue<BasisBundleConnector> threw {ex.GetType().Name}: {ex.Message}. PlaintextLength={data.Length}. Head={JsonHeadPreview(data)}");
            return false;
        }

        if (connector == null)
        {
            BasisDebug.LogError($"DeserializeValue<BasisBundleConnector> returned null (decrypt OK but wrapper/Value missing). PlaintextLength={data.Length}. Head={JsonHeadPreview(data)}");
            return false;
        }

        return true;
    }

    private static string JsonHeadPreview(byte[] data)
    {
        if (data == null || data.Length == 0) return "<empty>";
        int count = Math.Min(data.Length, 256);
        string preview = Encoding.UTF8.GetString(data, 0, count);
        StringBuilder sb = new StringBuilder(preview.Length);
        for (int i = 0; i < preview.Length; i++)
        {
            char c = preview[i];
            sb.Append(c < 0x20 || c == 0x7F ? '.' : c);
        }
        if (data.Length > count) sb.Append("...");
        return sb.ToString();
    }
}
