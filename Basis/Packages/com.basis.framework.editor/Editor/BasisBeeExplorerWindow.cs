using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public class BasisBeeExplorerWindow : EditorWindow
{
    private string beeFilePath = "";
    private string password = "";
    private bool showPassword = false;
    private string outputFolder = "";

    private enum BeeFileFormat { Unknown, Disk, Remote }

    private struct SectionLocation
    {
        public long FileOffset;
        public long Length;
    }

    // Parsed state
    private BasisBundleConnector connector;
    private BeeFileFormat detectedFormat = BeeFileFormat.Unknown;
    private byte[] diskSectionData;              // Disk format: single section blob
    private SectionLocation[] remoteSections;     // Remote format: per-platform section offsets
    private bool hasLoaded = false;
    private bool isLoading = false;
    private string statusMessage = "";
    private Texture2D thumbnailTexture;
    private Vector2 scrollPosition;
    private int extractingIndex = -1;

    [MenuItem("Basis/Bundles/BEE Explorer")]
    public static void ShowWindow()
    {
        GetWindow<BasisBeeExplorerWindow>("BEE Explorer");
    }

    private void OnDestroy()
    {
        if (thumbnailTexture != null)
        {
            DestroyImmediate(thumbnailTexture);
            thumbnailTexture = null;
        }
    }

    public async void OnGUI()
    {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        DrawFileSelection();
        DrawPasswordField();

        EditorGUILayout.Space(6);

        if (!hasLoaded)
        {
            DrawLoadButton();
        }
        else
        {
            DrawConnectorInfo();
        }

        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawFileSelection()
    {
        EditorGUILayout.LabelField("BEE File Explorer", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("BEE File:", GUILayout.Width(70));
        EditorGUILayout.SelectableLabel(beeFilePath, EditorStyles.textField, GUILayout.Height(18));
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            string selected = EditorUtility.OpenFilePanel("Select BEE File", "", "bee");
            if (!string.IsNullOrEmpty(selected))
            {
                beeFilePath = selected;
                Reset();
            }
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawPasswordField()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Password:");
        EditorGUILayout.BeginHorizontal();
        password = showPassword ? EditorGUILayout.TextField(password) : EditorGUILayout.PasswordField(password);
        showPassword = GUILayout.Toggle(showPassword, "Show", GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();
    }

    private async void DrawLoadButton()
    {
        EditorGUILayout.Space(4);
        EditorGUI.BeginDisabledGroup(isLoading || string.IsNullOrEmpty(beeFilePath) || string.IsNullOrEmpty(password));
        if (GUILayout.Button(isLoading ? "Loading..." : "Read BEE File", GUILayout.Height(30)))
        {
            await LoadBeeFile();
        }
        EditorGUI.EndDisabledGroup();
    }

    private void DrawConnectorInfo()
    {
        if (connector == null) return;

        // Header row
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Connector Metadata", EditorStyles.boldLabel);
        string formatLabel = detectedFormat == BeeFileFormat.Disk ? "Disk (4-byte header)" : "Remote (8-byte header)";
        EditorGUILayout.LabelField($"Format: {formatLabel}", GUILayout.Width(180));
        if (GUILayout.Button("Close", GUILayout.Width(60)))
        {
            Reset();
            return;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        // Thumbnail + basic info side by side
        EditorGUILayout.BeginHorizontal();

        if (thumbnailTexture != null)
        {
            GUILayout.Label(thumbnailTexture, GUILayout.Width(128), GUILayout.Height(128));
            GUILayout.Space(8);
        }

        EditorGUILayout.BeginVertical();
        DrawField("Version", connector.UniqueVersion);
        DrawField("Created", connector.DateOfCreation);

        if (connector.BasisBundleDescription != null)
        {
            DrawField("Name", connector.BasisBundleDescription.AssetBundleName);
            DrawField("Description", connector.BasisBundleDescription.AssetBundleDescription);
        }
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        // Bounds
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Bounds", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;
        EditorGUILayout.Vector3Field("Center", connector.Bounds.center);
        EditorGUILayout.Vector3Field("Size", connector.Bounds.size);
        EditorGUI.indentLevel--;

        // Metadata
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Metadata", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;
        DrawField("Triangles", connector.MetaData.TrianglesCount.ToString("N0"));
        DrawField("Materials", connector.MetaData.MaterialCount.ToString("N0"));
        DrawField("Bones", connector.MetaData.BonesCount.ToString("N0"));

        if (connector.MetaData.ComponentNames != null && connector.MetaData.ComponentNames.Length > 0)
        {
            EditorGUILayout.LabelField("Components:");
            EditorGUI.indentLevel++;
            foreach (var comp in connector.MetaData.ComponentNames)
            {
                EditorGUILayout.LabelField($"{comp.Name} x{comp.count}");
            }
            EditorGUI.indentLevel--;
        }
        EditorGUI.indentLevel--;

        // Platform bundles
        DrawPlatformBundles();
    }

    private void DrawPlatformBundles()
    {
        if (connector.BasisBundleGenerated == null || connector.BasisBundleGenerated.Length == 0)
        {
            EditorGUILayout.HelpBox("No platform bundles found in connector.", MessageType.Warning);
            return;
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField($"Platform Bundles ({connector.BasisBundleGenerated.Length})", EditorStyles.boldLabel);

        // Output folder
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Output Folder:", GUILayout.Width(90));
        EditorGUILayout.SelectableLabel(outputFolder, EditorStyles.textField, GUILayout.Height(18));
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            string selected = EditorUtility.OpenFolderPanel("Select Output Folder", "", "");
            if (!string.IsNullOrEmpty(selected))
                outputFolder = selected;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        for (int i = 0; i < connector.BasisBundleGenerated.Length; i++)
        {
            var bundle = connector.BasisBundleGenerated[i];
            if (bundle == null) continue;

            bool isCurrentPlatform = false;
            try
            {
                isCurrentPlatform = BasisBundleConnector.IsPlatform(bundle);
            }
            catch { }

            string platformLabel = bundle.Platform + (isCurrentPlatform ? " (Current)" : "");

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(platformLabel, EditorStyles.boldLabel);

            bool canExtract = CanExtractSection(i) && !string.IsNullOrEmpty(outputFolder) && extractingIndex < 0;
            EditorGUI.BeginDisabledGroup(!canExtract);
            if (GUILayout.Button("Extract", GUILayout.Width(70)))
            {
                ExtractBundle(i);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel++;
            DrawField("Asset Mode", bundle.AssetMode);
            DrawField("Asset Name", bundle.AssetToLoadName);
            DrawField("CRC", bundle.AssetBundleCRC.ToString());
            DrawField("Hash", bundle.AssetBundleHash);
            DrawField("Encrypted", bundle.IsEncrypted.ToString());
            DrawField("Size (bytes)", bundle.EndByte.ToString("N0"));
            EditorGUI.indentLevel--;

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        bool hasSectionData = detectedFormat == BeeFileFormat.Remote
            ? remoteSections != null && remoteSections.Any(s => s.Length > 0)
            : diskSectionData != null && diskSectionData.Length > 0;

        if (!hasSectionData)
        {
            EditorGUILayout.HelpBox("This .bee file only contains the connector (no bundle section data). Extraction is not available.", MessageType.Info);
        }
    }

    private bool CanExtractSection(int index)
    {
        if (detectedFormat == BeeFileFormat.Remote)
        {
            // Remote format: each platform section is individually addressable
            return remoteSections != null && index < remoteSections.Length && remoteSections[index].Length > 0;
        }

        // Disk format: single section blob, only valid for the current platform
        if (diskSectionData == null || diskSectionData.Length == 0)
            return false;

        try
        {
            return BasisBundleConnector.IsPlatform(connector.BasisBundleGenerated[index]);
        }
        catch
        {
            return false;
        }
    }

    private void DrawField(string label, string value)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(100));
        EditorGUILayout.SelectableLabel(value ?? "(null)", EditorStyles.textField, GUILayout.Height(18));
        EditorGUILayout.EndHorizontal();
    }

    // ── Loading ──────────────────────────────────────────────────────────

    private async Task LoadBeeFile()
    {
        if (string.IsNullOrEmpty(beeFilePath) || string.IsNullOrEmpty(password))
        {
            EditorUtility.DisplayDialog("Missing Info", "Please select a BEE file and enter a password.", "OK");
            return;
        }

        isLoading = true;
        statusMessage = "Reading BEE file...";
        Repaint();

        try
        {
            EditorUtility.DisplayProgressBar("BEE Explorer", "Reading file...", 0.1f);

            BasisProgressReport progressCallback = new BasisProgressReport();
            CancellationToken ct = default;

            // Try disk format first (4-byte Int32 header)
            EditorUtility.DisplayProgressBar("BEE Explorer", "Trying disk format (4-byte header)...", 0.2f);
            BeeResult<BasisIOManagement.BeeReadResult> diskResult = await BasisIOManagement.ReadBEEFileEx(
                beeFilePath, password, progressCallback, ct);

            if (diskResult.IsSuccess)
            {
                connector = diskResult.Value.Connector;
                diskSectionData = diskResult.Value.SectionData;
                remoteSections = null;
                detectedFormat = BeeFileFormat.Disk;
                hasLoaded = true;
                statusMessage = "Loaded (disk format, 4-byte header).";
                LoadThumbnail();
                return;
            }

            // Disk format failed, try remote format (8-byte Int64 header)
            Debug.Log("BEE Explorer: Disk format failed, trying remote format (8-byte header)...");
            EditorUtility.DisplayProgressBar("BEE Explorer", "Trying remote format (8-byte header)...", 0.4f);

            bool remoteOk = await TryReadRemoteFormat(progressCallback, ct);
            if (remoteOk)
            {
                hasLoaded = true;
                statusMessage = "Loaded (remote format, 8-byte header).";
                LoadThumbnail();
                return;
            }

            // Both failed
            statusMessage = "Failed to read BEE file with both disk and remote formats. Check your password.";
            EditorUtility.DisplayDialog("Error",
                "Could not read the BEE file.\n\n" +
                $"Disk format error: {diskResult.Error}\n\n" +
                "Remote format also failed.\n\n" +
                "Please verify the password and file are correct.",
                "OK");
        }
        catch (Exception ex)
        {
            statusMessage = "Error: " + ex.Message;
            Debug.LogError("BEE Explorer error: " + ex);
            EditorUtility.DisplayDialog("Error", ex.Message, "OK");
        }
        finally
        {
            isLoading = false;
            EditorUtility.ClearProgressBar();
            Repaint();
        }
    }

    /// <summary>
    /// Reads a BEE file in the remote format: 8-byte Int64 header + connector + sequential platform sections.
    /// </summary>
    private async Task<bool> TryReadRemoteFormat(BasisProgressReport progressCallback, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(beeFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 96 * 1024, useAsync: true);

            if (fs.Length < BasisBeeConstants.RemoteHeaderSize)
            {
                Debug.LogWarning("BEE Explorer: File too small for remote header.");
                return false;
            }

            // Read 8-byte Int64 connector length
            byte[] headerBytes = await ReadExactAsync(fs, BasisBeeConstants.RemoteHeaderSize, ct);
            if (headerBytes.Length != BasisBeeConstants.RemoteHeaderSize)
                return false;

            long connectorLength = BitConverter.IsLittleEndian
                ? BitConverter.ToInt64(headerBytes, 0)
                : BitConverter.ToInt64(headerBytes.Reverse().ToArray(), 0);

            if (connectorLength <= 0 || connectorLength > BasisBeeConstants.MaxConnectorBytes)
            {
                Debug.LogWarning($"BEE Explorer: Invalid remote connector length {connectorLength}.");
                return false;
            }

            long remaining = fs.Length - fs.Position;
            if (connectorLength > remaining)
            {
                Debug.LogWarning($"BEE Explorer: Connector length {connectorLength} exceeds remaining file bytes {remaining}.");
                return false;
            }

            // Read encrypted connector bytes
            byte[] connectorBytes = await ReadExactAsync(fs, (int)connectorLength, ct);
            if (connectorBytes.Length != connectorLength)
                return false;

            EditorUtility.DisplayProgressBar("BEE Explorer", "Decrypting connector...", 0.6f);

            // Decrypt connector to get metadata
            BasisBundleConnector parsedConnector = await BasisEncryptionToData.GenerateMetaFromBytes(
                password, connectorBytes, progressCallback);

            if (parsedConnector == null)
            {
                Debug.LogWarning("BEE Explorer: Failed to decrypt connector in remote format.");
                return false;
            }

            connector = parsedConnector;
            diskSectionData = null;
            detectedFormat = BeeFileFormat.Remote;

            // Compute section offsets from connector metadata
            if (connector.BasisBundleGenerated != null && connector.BasisBundleGenerated.Length > 0)
            {
                remoteSections = new SectionLocation[connector.BasisBundleGenerated.Length];
                long sectionOffset = BasisBeeConstants.RemoteHeaderSize + connectorLength;

                for (int i = 0; i < connector.BasisBundleGenerated.Length; i++)
                {
                    var entry = connector.BasisBundleGenerated[i];
                    long sectionLength = entry != null ? entry.EndByte : 0;

                    remoteSections[i] = new SectionLocation
                    {
                        FileOffset = sectionOffset,
                        Length = sectionLength
                    };
                    sectionOffset += sectionLength;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("BEE Explorer: Remote format read failed: " + ex.Message);
            return false;
        }
    }

    // ── Thumbnail ────────────────────────────────────────────────────────

    private void LoadThumbnail()
    {
        if (thumbnailTexture != null)
        {
            DestroyImmediate(thumbnailTexture);
            thumbnailTexture = null;
        }

        if (connector == null || string.IsNullOrEmpty(connector.ImageBase64))
            return;

        try
        {
            byte[] imageBytes = Convert.FromBase64String(connector.ImageBase64);
            thumbnailTexture = new Texture2D(2, 2);
            thumbnailTexture.LoadImage(imageBytes);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Failed to decode thumbnail: " + ex.Message);
        }
    }

    // ── Extraction ───────────────────────────────────────────────────────

    private async void ExtractBundle(int index)
    {
        if (string.IsNullOrEmpty(outputFolder))
        {
            EditorUtility.DisplayDialog("Missing Info", "Please select an output folder.", "OK");
            return;
        }

        extractingIndex = index;
        statusMessage = "Extracting bundle...";
        Repaint();

        try
        {
            EditorUtility.DisplayProgressBar("BEE Explorer", "Reading section data...", 0.2f);

            byte[] encryptedSectionData = await GetSectionBytes(index);
            if (encryptedSectionData == null || encryptedSectionData.Length == 0)
            {
                EditorUtility.DisplayDialog("Error", "No section data available for this platform.", "OK");
                return;
            }

            EditorUtility.DisplayProgressBar("BEE Explorer", "Decrypting bundle...", 0.5f);

            var basisPassword = new BasisEncryptionWrapper.BasisPassword
            {
                VP = password
            };

            string uniqueID = BasisGenerateUniqueID.GenerateUniqueID();
            BasisProgressReport progressCallback = new BasisProgressReport();

            var decrypted = await BasisEncryptionWrapper.DecryptFromBytesAsync(
                uniqueID, basisPassword, encryptedSectionData, progressCallback);

            if (!decrypted.Success)
            {
                statusMessage = "Decrypt failed: " + decrypted.Error;
                EditorUtility.DisplayDialog("Error", $"Decryption failed:\n{decrypted.Error}\n{decrypted.Message}", "OK");
                return;
            }

            EditorUtility.DisplayProgressBar("BEE Explorer", "Writing file...", 0.9f);

            string safeFolderPath = SanitizePath(outputFolder, Path.GetInvalidPathChars());
            string fileName = SanitizePath(Path.GetFileNameWithoutExtension(beeFilePath), Path.GetInvalidFileNameChars());
            var bundle = connector.BasisBundleGenerated[index];
            string finalName = $"{fileName}_{bundle.Platform}.bundle";
            string finalPath = Path.Combine(safeFolderPath, finalName);

            await File.WriteAllBytesAsync(finalPath, decrypted.Data);

            statusMessage = $"Extracted to: {finalPath}";
            EditorUtility.DisplayDialog("Success", $"Bundle extracted to:\n{finalPath}", "OK");
        }
        catch (Exception ex)
        {
            statusMessage = "Error: " + ex.Message;
            Debug.LogError("BEE Explorer extraction error: " + ex);
            EditorUtility.DisplayDialog("Error", ex.Message, "OK");
        }
        finally
        {
            extractingIndex = -1;
            EditorUtility.ClearProgressBar();
            Repaint();
        }
    }

    /// <summary>
    /// Gets the encrypted section bytes for a given platform index.
    /// Handles both disk format (single blob) and remote format (seek to offset).
    /// </summary>
    private async Task<byte[]> GetSectionBytes(int index)
    {
        if (detectedFormat == BeeFileFormat.Disk)
        {
            return diskSectionData;
        }

        // Remote format: read from file at the computed offset
        if (remoteSections == null || index >= remoteSections.Length)
            return null;

        var section = remoteSections[index];
        if (section.Length <= 0)
            return null;

        using var fs = new FileStream(beeFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 96 * 1024, useAsync: true);
        fs.Seek(section.FileOffset, SeekOrigin.Begin);
        return await ReadExactAsync(fs, (int)section.Length, default);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void Reset()
    {
        connector = null;
        diskSectionData = null;
        remoteSections = null;
        detectedFormat = BeeFileFormat.Unknown;
        hasLoaded = false;
        statusMessage = "";
        extractingIndex = -1;

        if (thumbnailTexture != null)
        {
            DestroyImmediate(thumbnailTexture);
            thumbnailTexture = null;
        }

        Repaint();
    }

    private static async Task<byte[]> ReadExactAsync(FileStream fs, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int totalRead = 0;

        while (totalRead < count)
        {
            int read = await fs.ReadAsync(buffer, totalRead, count - totalRead, ct);
            if (read <= 0) break;
            totalRead += read;
        }

        if (totalRead == count)
            return buffer;

        // Return partial
        if (totalRead == 0)
            return Array.Empty<byte>();

        byte[] partial = new byte[totalRead];
        Buffer.BlockCopy(buffer, 0, partial, 0, totalRead);
        return partial;
    }

    private static string SanitizePath(string input, char[] invalidChars)
    {
        return new string(input.Where(c => !invalidChars.Contains(c)).ToArray());
    }
}
