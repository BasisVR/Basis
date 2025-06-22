using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using static BasisEncryptionWrapper;
public static class AssetBundleBuilder
{
    public static async Task<(BasisBundleGenerated, InformationHash)> BuildAssetBundle(AssetBundleBuild[] BundledData, string targetDirectory, BasisAssetBundleObject settings, string assetBundleName, string mode, string password, BuildTarget buildTarget, bool isEncrypted = true)
    {
        InformationHash Hash = new InformationHash();
        BasisBundleGenerated BasisBundleGenerated = new BasisBundleGenerated();
        EnsureDirectoryExists(targetDirectory);
        EditorUtility.DisplayProgressBar("Building Asset Bundles", "Initializing...", 0f);

        BuildAssetBundlesParameters buildAssetBundlesParameters = new BuildAssetBundlesParameters
        {
            outputPath = targetDirectory,
            bundleDefinitions = BundledData,
            targetPlatform = buildTarget,
            extraScriptingDefines = null,
            options = settings.BuildAssetBundleOptions,
        };
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(buildAssetBundlesParameters);

        if (manifest != null)
        {
            Hash = await ProcessAssetBundles(targetDirectory, settings, manifest, password, isEncrypted);
            BasisBundleGenerated = new BasisBundleGenerated(Hash.bundleHash.ToString(), mode, assetBundleName, Hash.CRC, true, password, buildTarget.ToString(), Hash.Length);
            DeleteManifestFiles(targetDirectory, buildTarget.ToString());
            OnPostprocessBuild(BuildReport.GetLatestReport());
        }
        else
        {
            BasisDebug.LogError("AssetBundle build failed.");
        }
        EditorUtility.ClearProgressBar();
        return new(BasisBundleGenerated, Hash);
    }
    [System.Serializable]
    public class SerializableBuildReport
    {
        public string summaryResult;
        public string outputPath;
        public double totalSizeMB;
        public string platform;
        public string TimeTaken;
        public string SummarizeErrors;

        [SerializeField]
        public BasisStoredBuildStep[] steps;
        [SerializeField]
        public BasisBuildSummary summary;
        [SerializeField]
        public BasisBuildFile[] files;
        [SerializeField]
        public BasisPackedAssets[] packedAssets;
        [System.Serializable]
        public struct BasisStoredBuildStep
        {
            public string name;
            public ulong durationTicks;
            public int depth;
            public TimeSpan duration => TimeSpan.FromTicks((long)durationTicks);
            public BasisBuildStepMessage[] messages;

        }

        [System.Serializable]
        public struct BasisBuildStepMessage
        {
            public LogType type;
            public string content;
        }

        [System.Serializable]
        public struct BasisBuildSummary
        {
            public long buildStartTimeTicks;
            public ulong totalTimeTicks;
            public string guid;
            public BuildTarget platform;
            public BuildTargetGroup platformGroup;
            public BuildOptions options;
            public string outputPath;
            public ulong totalSize;
            public int totalErrors;
            public int totalWarnings;
            public BuildResult result;
            public BuildType buildType;
            public bool multiProcessEnabled;
            public TimeSpan totalTime => new TimeSpan((long)totalTimeTicks);
        }

        [System.Serializable]
        public struct BasisBuildFile
        {
            public uint id;
            public string path;
            public string role;
            public ulong size;
        }
        [System.Serializable]
        public struct BasisPackedAssets
        {
            public string shortPath;
            public ulong overhead;
            public BasisPackedAssetInfo[] contents;
        }
        [System.Serializable]
        public struct BasisPackedAssetInfo
        {
            public long id;
            public string type;
            public ulong packedSize;
            public ulong offset;
            public string sourceAssetGUID;
            public string sourceAssetPath;
        }
    }

    public static SerializableBuildReport OnPostprocessBuild(BuildReport report)
    {
        var files = report.GetFiles();
        var sReport = new SerializableBuildReport
        {
            summaryResult = report.summary.result.ToString(),
            outputPath = report.summary.outputPath,
            totalSizeMB = report.summary.totalSize / (1024.0 * 1024.0),
            platform = report.summary.platform.ToString(),
            SummarizeErrors = report.SummarizeErrors(),
            TimeTaken = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            summary = new SerializableBuildReport.BasisBuildSummary
            {
                buildStartTimeTicks = report.summary.buildStartedAt.Ticks,
                totalTimeTicks = (ulong)report.summary.totalTime.Ticks,
                guid = report.summary.guid.ToString(),
                platform = report.summary.platform,
                platformGroup = report.summary.platformGroup,
                options = report.summary.options,
                outputPath = report.summary.outputPath,
                totalSize = report.summary.totalSize,
                totalErrors = report.summary.totalErrors,
                totalWarnings = report.summary.totalWarnings,
                result = report.summary.result,
                buildType = report.summary.buildType,
                multiProcessEnabled = report.summary.multiProcessEnabled,
                 
            },

            steps = report.steps.Select(step => new SerializableBuildReport.BasisStoredBuildStep
            {
                name = step.name,
                durationTicks = (ulong)step.duration.Ticks,
                depth = step.depth,
                messages = step.messages.Select(msg => new SerializableBuildReport.BasisBuildStepMessage
                {
                    type = msg.type,
                    content = msg.content
                }).ToArray()
            }).ToArray(),

            files = files.Select(file => new SerializableBuildReport.BasisBuildFile
            {
                id = file.id,
                path = file.path,
                role = file.role,
                size = file.size
            }).ToArray(),

            packedAssets = report.packedAssets.Select(packed => new SerializableBuildReport.BasisPackedAssets
            {
                overhead = packed.overhead,
                shortPath = packed.shortPath,
                contents = packed.contents.Select(content => new SerializableBuildReport.BasisPackedAssetInfo
                {
                    id = content.id,
                    offset = content.offset,
                    packedSize = content.packedSize,
                    sourceAssetGUID = content.sourceAssetGUID.ToString(),
                    sourceAssetPath = content.sourceAssetPath,
                    type = content.type.FullName
                }).ToArray()
            }).ToArray()
        };
        if (!Directory.Exists(ReportDirectoryPath))
            Directory.CreateDirectory(ReportDirectoryPath);

        string reportPath = GetBuildReportPath(report.summary.platform);
        string json = JsonUtility.ToJson(sReport, true);
         File.WriteAllText(reportPath, json);

        Debug.Log($"Build report saved to {reportPath}");
        return sReport;
    }
   public static string ReportDirectoryPath = "BuildReport";
    public static string GetBuildReportPath(BuildTarget platform)
    {
        return Path.Combine("BuildReport", $"BuildReport_{platform}.json");
    }

    public static async Task<SerializableBuildReport> LoadLatestBuildReport(BuildTarget platform)
    {
        string path = GetBuildReportPath(platform);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"No saved build report found for platform {platform}.");
            return null;
        }

        string json = await File.ReadAllTextAsync(path);
        var sReport = JsonUtility.FromJson<SerializableBuildReport>(json);
        Debug.Log($"Loaded build report for {platform}:\nResult: {sReport.summaryResult}\nOutput: {sReport.outputPath}\nSize: {sReport.totalSizeMB:F2} MB");
        return sReport;
    }
    private static async Task<InformationHash> ProcessAssetBundles(string targetDirectory,BasisAssetBundleObject settings,AssetBundleManifest manifest,string password,bool isEncrypted)
    {
        string[] files = manifest.GetAllAssetBundles();
        int totalFiles = files.Length;
        List<InformationHash> InformationHashes = new List<InformationHash>();
        for (int index = 0; index < totalFiles; index++)
        {
            string fileOutput = files[index];
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileOutput);
            Hash128 bundleHash = manifest.GetAssetBundleHash(fileOutput);
            BuildPipeline.GetCRCForAssetBundle(fileOutput, out uint crc);
            string actualFilePath = $"{Path.Combine(targetDirectory, fileNameWithoutExtension)}";
            InformationHash informationHash = new InformationHash
            {
                bundleHash = bundleHash,
                CRC = crc,
            };
            float progress = (float)(index + 1) / totalFiles;
            EditorUtility.DisplayProgressBar("Building Asset Bundles", $"Processing {fileOutput}...", progress);
            string encryptedFilePath = await HandleEncryption(actualFilePath, password, settings, manifest, isEncrypted);
            CleanupOriginalFile(actualFilePath);
            informationHash.EncyptedPath = encryptedFilePath;
            FileInfo FileInfo = new FileInfo(encryptedFilePath);
            informationHash.Length = FileInfo.Length;
            InformationHashes.Add(informationHash);
        }
        if (InformationHashes.Count == 1)
        {
            return InformationHashes[0];
        }
        else
        {
            if (InformationHashes.Count > 1)
            {
                BasisDebug.LogError("More then a single Bundle is being built, please check what bundles your additionally building");
                return InformationHashes[0];
            }
            else
            {
                BasisDebug.LogError("No bundles where built, this is a massive issue!");
                return new InformationHash();
            }
        }
    }
    private static async Task<string> HandleEncryption(string filePath,string password,BasisAssetBundleObject settings,AssetBundleManifest manifest, bool isEncrypted)
    {
        if (isEncrypted)
        {
            return await EncryptBundle(password, filePath, settings, manifest);
        }
        else
        {
            string decryptedFilePath = Path.ChangeExtension(filePath, settings.BasisBundleDecryptedExtension);
            File.Copy(filePath, decryptedFilePath);
            return decryptedFilePath;
        }
    }

    private static void CleanupOriginalFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static void DeleteManifestFiles(string targetDirectory, string Platform, bool DeleteManifestFiles = true)
    {
        if (DeleteManifestFiles)
        {
            string[] Files = Directory.GetFiles(targetDirectory, "*.manifest");
            foreach (string manifestFile in Files)
            {
                if (File.Exists(manifestFile))
                {
                    File.Delete(manifestFile);
                    BasisDebug.Log("Deleted manifest file: " + manifestFile);
                }
            }


            string[] BundlesFiles = Directory.GetFiles(targetDirectory);
            foreach (string assetFile in BundlesFiles)
            {
                if (Path.GetFileNameWithoutExtension(assetFile) == "AssetBundles")
                {
                    File.Delete(assetFile);
                    BasisDebug.Log("Deleted AssetBundles file: " + assetFile);
                }
                if (Path.GetFileNameWithoutExtension(assetFile) == Platform)
                {
                    File.Delete(assetFile);
                    BasisDebug.Log("Deleted Platform file: " + assetFile);
                }
            }
        }
    }
    public static async Task SaveFileAsync(string directoryPath, string fileName, string fileExtension, string fileContent,int BufferSize = 256)
    {
        // Combine directory path, file name, and extension
        string fullPath = Path.Combine(directoryPath, $"{fileName}.{fileExtension}");
        // Use asynchronous file writing
        using (FileStream fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
        {
            using (StreamWriter writer = new StreamWriter(fileStream))
            {
                await writer.WriteAsync(fileContent);
            }
        }

        BasisDebug.Log($"File saved asynchronously at: {fullPath}");
    }
    public struct InformationHash
    {
        public string EncyptedPath;
        public Hash128 bundleHash;
        public uint CRC;
        public long Length;
    }

    private static BasisProgressReport Report = new BasisProgressReport();

    // Method to encrypt a file using a password
    public static async Task<string> EncryptBundle(string password, string actualFilePath, BasisAssetBundleObject buildSettings, AssetBundleManifest assetBundleManifest)
    {
        System.Diagnostics.Stopwatch encryptionTimer = System.Diagnostics.Stopwatch.StartNew();

        // Get all asset bundles from the manifest
        string[] bundles = assetBundleManifest.GetAllAssetBundles();
        if (bundles.Length == 0)
        {
            BasisDebug.LogError("No asset bundles found in manifest.");
            return string.Empty;
        }
        string EncryptedPath = Path.ChangeExtension(actualFilePath, buildSettings.BasisBundleEncryptedExtension);

        // Delete existing encrypted file if present
        if (File.Exists(EncryptedPath))
        {
            File.Delete(EncryptedPath);
        }
        BasisDebug.Log("Encrypting " + actualFilePath);
        BasisPassword BasisPassword = new BasisPassword
        {
            VP = password
        };
        string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        await BasisEncryptionWrapper.EncryptFileAsync(UniqueID, BasisPassword, actualFilePath, EncryptedPath, Report);
        encryptionTimer.Stop();
        BasisDebug.Log("Encryption took " + encryptionTimer.ElapsedMilliseconds + " ms for " + EncryptedPath);
        return EncryptedPath;
    }
    private static void EnsureDirectoryExists(string targetDirectory)
    {
        if (!Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }
    }

}
