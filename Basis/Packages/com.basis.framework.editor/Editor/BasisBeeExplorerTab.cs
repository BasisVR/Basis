using Basis.BasisUI;
using Basis.Editor.Localization;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UObject = UnityEngine.Object;

public sealed class BasisBeeExplorerTab : BasisEditorTabPage
{
    public override string Title => BasisEditorLocalization.Get("editor.beeExplorer.title");
    public override string Subtitle =>
        BasisEditorLocalization.Get("editor.beeExplorer.subtitle");

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
    private int extractingIndex = -1;

    public override void OnDisable()
    {
        if (thumbnailTexture != null)
        {
            UObject.DestroyImmediate(thumbnailTexture);
            thumbnailTexture = null;
        }
    }

    public override void Draw()
    {
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
            BasisEditorUI.Help(statusMessage, MessageType.Info);
        }

    }

    private void DrawFileSelection()
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.beeExplorer.fileExplorer"));
        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beeWindow.beeFile"), GUILayout.Width(70));
        EditorGUILayout.SelectableLabel(beeFilePath, EditorStyles.textField, GUILayout.Height(18));
        if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.browse"), GUILayout.Width(70)))
        {
            string selected = EditorUtility.OpenFilePanel(BasisEditorLocalization.Get("editor.beeWindow.selectBeeFile"), "", "bee");
            if (!string.IsNullOrEmpty(selected))
            {
                beeFilePath = selected;
                Reset();
                TryAutoFillPassword(selected);
            }
        }
        EditorGUILayout.EndHorizontal();
    }

    private void TryAutoFillPassword(string selectedBeePath)
    {
        if (!BasisBeeFileDrop.TryFindSidecarPassword(selectedBeePath, out string sidecarPassword, out string sourceFile))
        {
            return;
        }

        password = sidecarPassword;
        statusMessage = BasisEditorLocalization.Get("editor.beeWindow.passwordAutoFilled", Path.GetFileName(sourceFile));
    }

    private void DrawPasswordField()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beeWindow.password"));
        EditorGUILayout.BeginHorizontal();
        password = showPassword ? EditorGUILayout.TextField(password) : EditorGUILayout.PasswordField(password);
        showPassword = GUILayout.Toggle(showPassword, BasisEditorLocalization.Get("editor.beeWindow.showPassword"), GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();
    }

    private async void DrawLoadButton()
    {
        EditorGUILayout.Space(4);
        EditorGUI.BeginDisabledGroup(isLoading || string.IsNullOrEmpty(beeFilePath) || string.IsNullOrEmpty(password));
        if (GUILayout.Button(isLoading ? BasisEditorLocalization.Get("editor.common.loading") : BasisEditorLocalization.Get("editor.beeExplorer.readBeeFile"), GUILayout.Height(30)))
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
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.beeExplorer.connectorMetadata"));
        string formatLabel = detectedFormat == BeeFileFormat.Disk ? BasisEditorLocalization.Get("editor.beeExplorer.formatDisk") : BasisEditorLocalization.Get("editor.beeExplorer.formatRemote");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beeExplorer.format", formatLabel), GUILayout.Width(180));
        if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.close"), GUILayout.Width(60)))
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
        DrawField(BasisEditorLocalization.Get("editor.beeWindow.version"), connector.UniqueVersion);
        DrawField(BasisEditorLocalization.Get("editor.beeWindow.created"), connector.DateOfCreation);

        if (connector.BasisBundleDescription != null)
        {
            DrawField(BasisEditorLocalization.Get("editor.common.name"), connector.BasisBundleDescription.AssetBundleName);
            DrawField(BasisEditorLocalization.Get("editor.common.description"), connector.BasisBundleDescription.AssetBundleDescription);
            var tags = connector.BasisBundleDescription.Tags;
            DrawField(BasisEditorLocalization.Get("editor.beeExplorer.tags"), tags != null && tags.Length > 0 ? string.Join(", ", tags) : BasisEditorLocalization.Get("editor.beeWindow.none"));
            BasisBundleAuthorship authorship = connector.BasisBundleDescription.Authorship;
            if (BasisBundleConnector.HasAuthorship(authorship))
            {
                DrawField(BasisEditorLocalization.Get("sdk.commonInspector.authorship.author"), authorship.Author);
                DrawField(BasisEditorLocalization.Get("sdk.commonInspector.authorship.contact"), authorship.Contact);
                DrawField(BasisEditorLocalization.Get("sdk.commonInspector.authorship.license"), authorship.License);
                DrawField(BasisEditorLocalization.Get("sdk.commonInspector.authorship.credits"), authorship.Credits);
            }
            else
            {
                DrawField(BasisEditorLocalization.Get("editor.beeExplorer.authorship"), BasisEditorLocalization.Get("editor.beeWindow.none"));
            }
        }
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        // Bounds
        EditorGUILayout.Space(6);
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.beeExplorer.bounds"));
        EditorGUI.indentLevel++;
        EditorGUILayout.Vector3Field(BasisEditorLocalization.Get("editor.beeExplorer.boundsCenter"), connector.Bounds.center);
        EditorGUILayout.Vector3Field(BasisEditorLocalization.Get("editor.common.size"), connector.Bounds.size);
        EditorGUILayout.Vector3Field(BasisEditorLocalization.Get("editor.beeExplorer.boundsExtents"), connector.Bounds.extents);
        EditorGUI.indentLevel--;

        // Metadata
        EditorGUILayout.Space(6);
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.beeExplorer.metadata"));
        EditorGUI.indentLevel++;
        DrawField(BasisEditorLocalization.Get("editor.beeExplorer.contentKind"), string.IsNullOrEmpty(connector.MetaData.ContentKind) ? BasisEditorLocalization.Get("editor.beeExplorer.contentKindUnstamped") : connector.MetaData.ContentKind);
        DrawField(BasisEditorLocalization.Get("editor.beeExplorer.triangles"), connector.MetaData.TrianglesCount.ToString("N0"));
        DrawField(BasisEditorLocalization.Get("editor.beeExplorer.materials"), connector.MetaData.MaterialCount.ToString("N0"));
        DrawField(BasisEditorLocalization.Get("editor.beeExplorer.bones"), connector.MetaData.BonesCount.ToString("N0"));
        DrawField(BasisEditorLocalization.Get("editor.beeExplorer.textureMemory"), FormatByteSize(connector.MetaData.TextureMemoryBytes));
        DrawField(BasisEditorLocalization.Get("editor.beeExplorer.graphicsPipeline"), string.IsNullOrEmpty(connector.MetaData.GraphicsPipeline) ? BasisEditorLocalization.Get("editor.beeExplorer.unknown") : connector.MetaData.GraphicsPipeline);

        if (connector.MetaData.ComponentNames != null && connector.MetaData.ComponentNames.Length > 0)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beeExplorer.components"));
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
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.beeExplorer.noPlatformBundles"), MessageType.Warning);
            return;
        }

        EditorGUILayout.Space(6);
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.beeExplorer.platformBundles", connector.BasisBundleGenerated.Length));

        // Output folder
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beeExplorer.outputFolder"), GUILayout.Width(90));
        EditorGUILayout.SelectableLabel(outputFolder, EditorStyles.textField, GUILayout.Height(18));
        if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.browse"), GUILayout.Width(70)))
        {
            string selected = EditorUtility.OpenFolderPanel(BasisEditorLocalization.Get("editor.beeExplorer.selectOutputFolder"), "", "");
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

            string platformLabel = isCurrentPlatform ? BasisEditorLocalization.Get("editor.beeExplorer.platformCurrent", bundle.Platform) : bundle.Platform;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(platformLabel, EditorStyles.boldLabel);

            bool canExtract = CanExtractSection(i) && !string.IsNullOrEmpty(outputFolder) && extractingIndex < 0;
            EditorGUI.BeginDisabledGroup(!canExtract);
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.beeExplorer.extract"), GUILayout.Width(70)))
            {
                ExtractBundle(i);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel++;
            DrawField(BasisEditorLocalization.Get("editor.beeExplorer.assetMode"), bundle.AssetMode);
            DrawField(BasisEditorLocalization.Get("editor.beeExplorer.assetName"), bundle.AssetToLoadName);
            DrawField(BasisEditorLocalization.Get("editor.beeExplorer.crc"), bundle.AssetBundleCRC.ToString());
            DrawField(BasisEditorLocalization.Get("editor.beeExplorer.hash"), bundle.AssetBundleHash);
            DrawField(BasisEditorLocalization.Get("editor.beeExplorer.encrypted"), bundle.IsEncrypted.ToString());
            DrawField(BasisEditorLocalization.Get("sdk.commonInspector.password"), string.IsNullOrEmpty(bundle.Password) ? BasisEditorLocalization.Get("editor.beeWindow.none") : bundle.Password);
            DrawField(BasisEditorLocalization.Get("editor.beeExplorer.sizeBytes"), bundle.EndByte.ToString("N0"));
            DrawField(BasisEditorLocalization.Get("editor.beeExplorer.graphicsApis"), bundle.GraphicsAPIs != null && bundle.GraphicsAPIs.Length > 0 ? string.Join(", ", bundle.GraphicsAPIs) : BasisEditorLocalization.Get("editor.beeExplorer.unknown"));
            EditorGUI.indentLevel--;

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        bool hasSectionData = detectedFormat == BeeFileFormat.Remote
            ? remoteSections != null && remoteSections.Any(s => s.Length > 0)
            : diskSectionData != null && diskSectionData.Length > 0;

        if (!hasSectionData)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.beeExplorer.connectorOnly"), MessageType.Info);
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
        EditorGUILayout.SelectableLabel(value ?? BasisEditorLocalization.Get("editor.beeExplorer.nullValue"), EditorStyles.textField, GUILayout.Height(18));
        EditorGUILayout.EndHorizontal();
    }

    // ── Loading ──────────────────────────────────────────────────────────

    private async Task LoadBeeFile()
    {
        if (string.IsNullOrEmpty(beeFilePath) || string.IsNullOrEmpty(password))
        {
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.beeExplorer.missingInfoTitle"), BasisEditorLocalization.Get("editor.beeExplorer.missingFileOrPassword"), BasisEditorLocalization.Get("editor.common.ok"));
            return;
        }

        isLoading = true;
        statusMessage = BasisEditorLocalization.Get("editor.beeExplorer.readingFile");
        Host.Repaint();

        try
        {
            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("editor.beeExplorer.progressTitle"), BasisEditorLocalization.Get("editor.beeExplorer.progressReadingFile"), 0.1f);

            BasisProgressReport progressCallback = new BasisProgressReport();
            CancellationToken ct = default;

            // Try disk format first (4-byte Int32 header)
            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("editor.beeExplorer.progressTitle"), BasisEditorLocalization.Get("editor.beeExplorer.progressTryingDisk"), 0.2f);
            BeeResult<BasisIOManagement.BeeReadResult> diskResult = await BasisIOManagement.ReadBEEFileEx(
                beeFilePath, password, progressCallback, ct);

            if (diskResult.IsSuccess)
            {
                connector = diskResult.Value.Connector;
                diskSectionData = diskResult.Value.SectionData;
                remoteSections = null;
                detectedFormat = BeeFileFormat.Disk;
                hasLoaded = true;
                statusMessage = BasisEditorLocalization.Get("editor.beeExplorer.loadedDisk");
                LoadThumbnail();
                return;
            }

            // Disk format failed, try remote format (8-byte Int64 header)
            Debug.Log("BEE Explorer: Disk format failed, trying remote format (8-byte header)...");
            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("editor.beeExplorer.progressTitle"), BasisEditorLocalization.Get("editor.beeExplorer.progressTryingRemote"), 0.4f);

            bool remoteOk = await TryReadRemoteFormat(progressCallback, ct);
            if (remoteOk)
            {
                hasLoaded = true;
                statusMessage = BasisEditorLocalization.Get("editor.beeExplorer.loadedRemote");
                LoadThumbnail();
                return;
            }

            // Both failed
            statusMessage = BasisEditorLocalization.Get("editor.beeExplorer.readFailedStatus");
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.common.error"),
                BasisEditorLocalization.Get("editor.beeExplorer.readFailedDialog", diskResult.Error),
                BasisEditorLocalization.Get("editor.common.ok"));
        }
        catch (Exception ex)
        {
            statusMessage = BasisEditorLocalization.Get("editor.beeExplorer.errorStatus", ex.Message);
            Debug.LogError("BEE Explorer error: " + ex);
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.common.error"), ex.Message, BasisEditorLocalization.Get("editor.common.ok"));
        }
        finally
        {
            isLoading = false;
            EditorUtility.ClearProgressBar();
            Host.Repaint();
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

            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("editor.beeExplorer.progressTitle"), BasisEditorLocalization.Get("editor.beeExplorer.progressDecryptingConnector"), 0.6f);

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
            UObject.DestroyImmediate(thumbnailTexture);
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
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.beeExplorer.missingInfoTitle"), BasisEditorLocalization.Get("editor.beeExplorer.missingOutputFolder"), BasisEditorLocalization.Get("editor.common.ok"));
            return;
        }

        extractingIndex = index;
        statusMessage = BasisEditorLocalization.Get("editor.beeExplorer.extracting");
        Host.Repaint();

        try
        {
            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("editor.beeExplorer.progressTitle"), BasisEditorLocalization.Get("editor.beeExplorer.progressReadingSection"), 0.2f);

            byte[] encryptedSectionData = await GetSectionBytes(index);
            if (encryptedSectionData == null || encryptedSectionData.Length == 0)
            {
                EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.common.error"), BasisEditorLocalization.Get("editor.beeExplorer.noSectionData"), BasisEditorLocalization.Get("editor.common.ok"));
                return;
            }

            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("editor.beeExplorer.progressTitle"), BasisEditorLocalization.Get("editor.beeExplorer.progressDecryptingBundle"), 0.5f);

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
                statusMessage = BasisEditorLocalization.Get("editor.beeExplorer.decryptFailedStatus", decrypted.Error);
                EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.common.error"), BasisEditorLocalization.Get("editor.beeExplorer.decryptFailedDialog", decrypted.Error, decrypted.Message), BasisEditorLocalization.Get("editor.common.ok"));
                return;
            }

            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("editor.beeExplorer.progressTitle"), BasisEditorLocalization.Get("editor.beeExplorer.progressWritingFile"), 0.9f);

            string safeFolderPath = SanitizePath(outputFolder, Path.GetInvalidPathChars());
            string fileName = SanitizePath(Path.GetFileNameWithoutExtension(beeFilePath), Path.GetInvalidFileNameChars());
            var bundle = connector.BasisBundleGenerated[index];
            string finalName = $"{fileName}_{bundle.Platform}.bundle";
            string finalPath = Path.Combine(safeFolderPath, finalName);

            await File.WriteAllBytesAsync(finalPath, decrypted.Data);

            statusMessage = BasisEditorLocalization.Get("editor.beeExplorer.extractedStatus", finalPath);
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.common.success"), BasisEditorLocalization.Get("editor.beeExplorer.extractedDialog", finalPath), BasisEditorLocalization.Get("editor.common.ok"));
        }
        catch (Exception ex)
        {
            statusMessage = BasisEditorLocalization.Get("editor.beeExplorer.errorStatus", ex.Message);
            Debug.LogError("BEE Explorer extraction error: " + ex);
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.common.error"), ex.Message, BasisEditorLocalization.Get("editor.common.ok"));
        }
        finally
        {
            extractingIndex = -1;
            EditorUtility.ClearProgressBar();
            Host.Repaint();
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
            UObject.DestroyImmediate(thumbnailTexture);
            thumbnailTexture = null;
        }

        Host.Repaint();
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

    private static string FormatByteSize(long bytes)
    {
        if (bytes <= 0) return "0 bytes";
        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;
        if (bytes >= gb) return $"{bytes:N0} bytes ({bytes / gb:F2} GB)";
        if (bytes >= mb) return $"{bytes:N0} bytes ({bytes / mb:F2} MB)";
        if (bytes >= kb) return $"{bytes:N0} bytes ({bytes / kb:F2} KB)";
        return $"{bytes:N0} bytes";
    }
}
