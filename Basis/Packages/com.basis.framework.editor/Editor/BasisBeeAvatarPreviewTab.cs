using Basis.Editor.Localization;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using UnityEditor;
using UnityEngine;
using UObject = UnityEngine.Object;

/// <summary>
/// Loads a final .BEE file and previews the avatar's two fallback representations: the
/// far avatar carried on the connector, and the Generic (glTF) avatar section. Both are
/// decoded exactly the way a client decodes them — the far avatar through
/// <see cref="BasisFarLodPayload"/>'s builders, the generic avatar through a real glTFast
/// import of the decrypted section — so what renders here is what remote players get on a
/// platform without a purpose-built AssetBundle.
///
/// Reads both bee layouts: the SDK export (8-byte header, all sections) and the on-disk
/// cache (4-byte header, single section). The password auto-fills from the
/// "dontuploadmepassword.txt" the SDK writes next to the exported bee.
/// </summary>
public sealed class BasisBeeAvatarPreviewTab : BasisEditorTabPage
{
    public override string Title => BasisEditorLocalization.Get("editor.beePreview.title");
    public override string Subtitle =>
        BasisEditorLocalization.Get("editor.beePreview.subtitle");

    private const string ScenePreviewPrefix = "Generic Avatar Preview (";

    [SerializeField] private string beeFilePath = "";
    [SerializeField] private string password = "";
    [SerializeField] private bool showPassword;
    [SerializeField] private int tab;
    [SerializeField] private float orbitYaw = 135f;
    [SerializeField] private float orbitPitch = 12f;
    [SerializeField] private float orbitZoom = 1f;
    [SerializeField] private bool showFarAvatarAtlas;

    private static string[] TabNames => new[] { BasisEditorLocalization.Get("editor.beePreview.farAvatar"), BasisEditorLocalization.Get("editor.beePreview.genericAvatar"), BasisEditorLocalization.Get("editor.common.info") };

    private bool isLoading;
    private bool hasLoaded;
    private string statusMessage = "";

    // Connector
    private BasisBundleConnector connector;

    // Far avatar
    private BasisFarLodPayload farLodPayload;
    private Mesh farLodMesh;
    private Texture2D farLodTexture;
    private Material farLodMaterial;
    private PreviewRenderUtility farLodPreview;

    // Generic (glTF) avatar
    private GltfImport gltfImport;
    private GameObject genericTemplate;      // hidden, inactive holder in the main scene
    private byte[] genericGlbBytes;
    private BasisGenericAvatarData genericData;
    private BasisGenericBlendshapeSidecar genericSidecar;
    private string genericSectionInfo = "";
    private string humanoidTestResult = "";
    private bool humanoidTestPassed;
    private PreviewRenderUtility genericPreview;
    private Bounds genericBounds;
    private bool genericBoundsValid;

    public override void OnDisable()
    {
        ReleaseLoaded();
    }

    public override void Draw()
    {
        DrawFileSection();

        if (hasLoaded && connector != null)
        {
            EditorGUILayout.Space(6);
            tab = GUILayout.Toolbar(tab, TabNames);
            EditorGUILayout.Space(2);
            switch (tab)
            {
                case 0: DrawFarAvatarTab(); break;
                case 1: DrawGenericTab(); break;
                default: DrawInfoTab(); break;
            }
        }

        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.Space(4);
            BasisEditorUI.Help(statusMessage, MessageType.Info);
        }

    }

    // ─────────────────────────── file + load ───────────────────────────

    private void DrawFileSection()
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.beePreview.sectionTitle"));
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
                TryAutoFillPassword(selected);
                ReleaseLoaded();
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beeWindow.password"), GUILayout.Width(70));
        password = showPassword ? EditorGUILayout.TextField(password) : EditorGUILayout.PasswordField(password);
        showPassword = GUILayout.Toggle(showPassword, BasisEditorLocalization.Get("editor.beeWindow.showPassword"), GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);
        using (new EditorGUI.DisabledScope(isLoading || string.IsNullOrEmpty(beeFilePath) || string.IsNullOrEmpty(password)))
        {
            if (GUILayout.Button(isLoading ? BasisEditorLocalization.Get("editor.common.loading") : hasLoaded ? BasisEditorLocalization.Get("editor.beePreview.reload") : BasisEditorLocalization.Get("editor.beePreview.loadBee"), GUILayout.Height(28)))
            {
                _ = LoadBeeAsync();
            }
        }
    }

    /// <summary>
    /// The SDK writes the bundle password to "dontuploadmepassword.txt" in the export folder;
    /// picking a bee from that folder should not require pasting it by hand.
    /// </summary>
    private void TryAutoFillPassword(string selectedBeePath)
    {
        try
        {
            string directory = Path.GetDirectoryName(selectedBeePath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }
            string passwordFile = Path.Combine(directory, "dontuploadmepassword.txt");
            if (File.Exists(passwordFile))
            {
                string content = File.ReadAllText(passwordFile).Trim();
                if (!string.IsNullOrEmpty(content))
                {
                    password = content;
                    statusMessage = BasisEditorLocalization.Get("editor.beeWindow.passwordAutoFilled", Path.GetFileName(passwordFile));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Password auto-fill failed: " + ex.Message);
        }
    }

    private async Task LoadBeeAsync()
    {
        ReleaseLoaded();
        isLoading = true;
        statusMessage = BasisEditorLocalization.Get("editor.beePreview.readingBee");
        Host.Repaint();

        try
        {
            BasisProgressReport report = new BasisProgressReport();
            byte[] encryptedGenericSection = null;

            // SDK export layout first (8-byte header, all sections addressable)...
            var (remoteConnector, remoteGeneric) = await TryReadRemoteLayout(report);
            if (remoteConnector != null)
            {
                connector = remoteConnector;
                encryptedGenericSection = remoteGeneric;
            }
            else
            {
                // ...then the on-disk cache layout (4-byte header, single section blob).
                BeeResult<BasisIOManagement.BeeReadResult> diskResult = await BasisIOManagement.ReadBEEFileEx(beeFilePath, password, report, CancellationToken.None);
                if (!diskResult.IsSuccess || diskResult.Value?.Connector == null)
                {
                    statusMessage = BasisEditorLocalization.Get("editor.beePreview.readFailed", diskResult.Error ?? "");
                    return;
                }
                connector = diskResult.Value.Connector;
                // The cache holds exactly one section; whether it is the generic one is
                // settled after decryption by the GLB magic check.
                encryptedGenericSection = diskResult.Value.SectionData;
            }

            hasLoaded = true;
            statusMessage = BasisEditorLocalization.Get("editor.beePreview.connectorLoaded", connector.BasisBundleDescription?.AssetBundleName ?? connector.UniqueVersion);

            BuildFarAvatarAssets();
            await BuildGenericAvatarAsync(encryptedGenericSection, report);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            statusMessage = BasisEditorLocalization.Get("editor.beePreview.loadFailed", ex.Message);
        }
        finally
        {
            isLoading = false;
            EditorUtility.ClearProgressBar();
            Host.Repaint();
        }
    }

    /// <summary>
    /// Reads the remote/SDK-export layout and returns the connector plus the encrypted bytes
    /// of the Generic section (null when the bee carries none). Returns (null, null) when the
    /// file is not in this layout.
    /// </summary>
    private async Task<(BasisBundleConnector, byte[])> TryReadRemoteLayout(BasisProgressReport report)
    {
        try
        {
            using var fs = new FileStream(beeFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 96 * 1024, useAsync: true);
            if (fs.Length < BasisBeeConstants.RemoteHeaderSize)
            {
                return (null, null);
            }

            byte[] header = new byte[BasisBeeConstants.RemoteHeaderSize];
            if (await fs.ReadAsync(header, 0, header.Length) != header.Length)
            {
                return (null, null);
            }
            long connectorLength = BitConverter.ToInt64(header, 0);
            if (connectorLength <= 0 || connectorLength > BasisBeeConstants.MaxConnectorBytes || connectorLength > fs.Length - fs.Position)
            {
                return (null, null);
            }

            byte[] connectorBytes = new byte[connectorLength];
            int read = 0;
            while (read < connectorLength)
            {
                int n = await fs.ReadAsync(connectorBytes, read, (int)connectorLength - read);
                if (n <= 0)
                {
                    return (null, null);
                }
                read += n;
            }

            BasisBundleConnector parsed = await BasisEncryptionToData.GenerateMetaFromBytes(password, connectorBytes, report);
            if (parsed?.BasisBundleGenerated == null)
            {
                return (null, null);
            }

            // Walk section lengths to the generic entry's offset.
            long offset = BasisBeeConstants.RemoteHeaderSize + connectorLength;
            for (int Index = 0; Index < parsed.BasisBundleGenerated.Length; Index++)
            {
                BasisBundleGenerated entry = parsed.BasisBundleGenerated[Index];
                long length = entry?.EndByte ?? 0;
                if (length <= 0)
                {
                    return (parsed, null);
                }
                if (BasisBundleConnector.IsGenericBundle(entry))
                {
                    if (offset + length > fs.Length)
                    {
                        return (parsed, null);
                    }
                    byte[] section = new byte[length];
                    fs.Seek(offset, SeekOrigin.Begin);
                    int got = 0;
                    while (got < length)
                    {
                        int n = await fs.ReadAsync(section, got, (int)length - got);
                        if (n <= 0)
                        {
                            return (parsed, null);
                        }
                        got += n;
                    }
                    return (parsed, section);
                }
                offset += length;
            }
            return (parsed, null);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Remote-layout read failed: " + ex.Message);
            return (null, null);
        }
    }

    // ─────────────────────────── far avatar ───────────────────────────

    private void BuildFarAvatarAssets()
    {
        farLodPayload = BasisFarLodPayload.TryParseBase64(connector.FarLodBase64);
        if (farLodPayload == null)
        {
            return;
        }
        farLodMesh = farLodPayload.CreateMesh();
        farLodTexture = farLodPayload.CreateTexture();
        if (farLodMesh == null || farLodTexture == null)
        {
            farLodPayload = null;
            return;
        }
        Shader shader = Shader.Find("Basis/AvatarFarLod");
        if (shader == null)
        {
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        }
        farLodMaterial = new Material(shader);
        farLodMaterial.SetTexture("_BaseMap", farLodTexture);
        farLodMesh.hideFlags = HideFlags.HideAndDontSave;
        farLodTexture.hideFlags = HideFlags.HideAndDontSave;
        farLodMaterial.hideFlags = HideFlags.HideAndDontSave;
    }

    private void DrawFarAvatarTab()
    {
        if (farLodPayload == null)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.beePreview.noFarAvatar"), MessageType.Info);
            return;
        }

        Rect rect = ReserveViewportRect();
        HandleOrbitInput(rect);
        if (Event.current.type == EventType.Repaint)
        {
            farLodPreview ??= CreatePreview();
            Bounds bounds = new Bounds(
                (farLodPayload.PositionBoundsMin + farLodPayload.PositionBoundsMax) * 0.5f,
                farLodPayload.PositionBoundsMax - farLodPayload.PositionBoundsMin);

            farLodPreview.BeginPreview(rect, GUIStyle.none);
            FrameCamera(farLodPreview, bounds);
            farLodPreview.DrawMesh(farLodMesh, Matrix4x4.identity, farLodMaterial, 0);
            farLodPreview.Render(true);
            Texture result = farLodPreview.EndPreview();
            GUI.DrawTexture(rect, result, ScaleMode.StretchToFill, false);
        }

        EditorGUILayout.LabelField(
            BasisEditorLocalization.Get("editor.beePreview.farAvatarStats", farLodPayload.TriangleCount, farLodPayload.VertexCount, farLodPayload.BoneCount, farLodTexture.width, farLodTexture.height, farLodTexture.format),
            EditorStyles.miniLabel);
        BasisEditorUI.Note(BasisEditorLocalization.Get("editor.beePreview.farAvatarHint"));

        // A flat single-color atlas means the bake captured nothing — showing it makes that
        // failure obvious at a glance instead of reading as a lighting issue in the viewport.
        showFarAvatarAtlas = EditorGUILayout.Foldout(showFarAvatarAtlas, BasisEditorLocalization.Get("editor.beePreview.atlas"), true);
        if (showFarAvatarAtlas)
        {
            float atlasSide = Mathf.Clamp(Host.position.width - 48f, 128f, 320f);
            Rect atlasRect = GUILayoutUtility.GetRect(atlasSide, atlasSide, GUILayout.ExpandWidth(false));
            EditorGUI.DrawPreviewTexture(atlasRect, farLodTexture);
        }
    }

    // ─────────────────────────── generic (glTF) avatar ───────────────────────────

    private async Task BuildGenericAvatarAsync(byte[] encryptedSection, BasisProgressReport report)
    {
        genericSectionInfo = "";
        BasisBundleGenerated genericEntry = connector.BasisBundleGenerated != null
            ? Array.Find(connector.BasisBundleGenerated, BasisBundleConnector.IsGenericBundle)
            : null;

        if (genericEntry == null)
        {
            genericSectionInfo = BasisEditorLocalization.Get("editor.beePreview.noGenericSection");
            return;
        }
        if (encryptedSection == null || encryptedSection.Length == 0)
        {
            genericSectionInfo = BasisEditorLocalization.Get("editor.beePreview.genericBytesMissing");
            return;
        }

        var basisPassword = new BasisEncryptionWrapper.BasisPassword { VP = password };
        string uniqueId = BasisGenerateUniqueID.GenerateUniqueID();
        var decrypted = await BasisEncryptionWrapper.DecryptFromBytesAsync(uniqueId, basisPassword, encryptedSection, report);
        if (!decrypted.Success || decrypted.Data == null || decrypted.Data.Length < 4)
        {
            genericSectionInfo = BasisEditorLocalization.Get("editor.beePreview.genericDecryptFailed", decrypted.Error, decrypted.Message);
            return;
        }

        // GLB magic check settles whether a disk-cache bee's single section is really the
        // generic one (it can be a platform AssetBundle instead). The section is the GLB plus
        // an optional Basis blendshape sidecar appended after it.
        byte[] sectionBytes = decrypted.Data;
        int glbLength = BasisGenericBlendshapeSidecar.GetGlbLength(sectionBytes);
        if (glbLength <= 0)
        {
            genericSectionInfo = BasisEditorLocalization.Get("editor.beePreview.notGlb");
            return;
        }
        genericSidecar = null;
        if (glbLength < sectionBytes.Length)
        {
            genericSidecar = BasisGenericBlendshapeSidecar.TryParse(sectionBytes, glbLength);
            genericGlbBytes = new byte[glbLength];
            Buffer.BlockCopy(sectionBytes, 0, genericGlbBytes, 0, glbLength);
        }
        else
        {
            genericGlbBytes = sectionBytes;
        }
        genericData = BasisGenericAvatarData.FromJson(genericEntry.GenericAvatarDataJson);

        gltfImport = new GltfImport(deferAgent: new UninterruptedDeferAgent());
        ImportSettings importSettings = new ImportSettings
        {
            NodeNameMethod = NameImportMethod.Original,
            AnimationMethod = AnimationMethod.None,
            GenerateMipMaps = false,
        };
        bool loaded = await gltfImport.Load(genericGlbBytes, null, importSettings);
        if (!loaded)
        {
            genericSectionInfo = BasisEditorLocalization.Get("editor.beePreview.gltfParseFailed");
            gltfImport.Dispose();
            gltfImport = null;
            return;
        }

        genericTemplate = new GameObject("BeePreviewGenericTemplate")
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
        genericTemplate.SetActive(false);
        bool instantiated = await gltfImport.InstantiateMainSceneAsync(genericTemplate.transform);
        if (!instantiated)
        {
            genericSectionInfo = BasisEditorLocalization.Get("editor.beePreview.gltfInstantiateFailed");
            UObject.DestroyImmediate(genericTemplate);
            genericTemplate = null;
            gltfImport.Dispose();
            gltfImport = null;
            return;
        }

        // Node-state and sidecar paths are relative to the avatar ROOT NODE, which sits below
        // the holder and glTFast's scene wrapper — resolving them against the holder finds
        // nothing.
        Transform genericAvatarRoot = LocateGenericAvatarRoot();
        genericData?.ApplyNodeState(genericAvatarRoot != null ? genericAvatarRoot : genericTemplate.transform);
        genericSidecar?.ApplyTo(genericAvatarRoot != null ? genericAvatarRoot : genericTemplate.transform);
        if (genericData != null && genericAvatarRoot != null)
        {
            // Same gate the client runs: the shipped avatar selector asset. Fail closed if
            // it cannot be loaded.
            ContentPoliceSelector avatarSelector = AssetDatabase.LoadAssetAtPath<ContentPoliceSelector>("Packages/com.basis.sdk/Settings/AvatarContentPoliceSelector.asset");
            BasisGenericComponentReplicator.Apply(genericData.ComponentsJson, genericAvatarRoot, avatarSelector);
        }

        int sidecarShapes = 0;
        if (genericSidecar != null)
        {
            foreach (var sidecarMesh in genericSidecar.Meshes)
            {
                sidecarShapes += sidecarMesh.Shapes.Count;
            }
        }
        genericSectionInfo = BasisEditorLocalization.Get("editor.beePreview.genericSectionInfo", genericGlbBytes.LongLength / 1024f, genericEntry.EndByte / 1024f, sidecarShapes);
        genericBoundsValid = false;
        Host.Repaint();
    }

    private void DrawGenericTab()
    {
        if (genericTemplate == null)
        {
            BasisEditorUI.Help(string.IsNullOrEmpty(genericSectionInfo) ? BasisEditorLocalization.Get("editor.beePreview.noGenericLoaded") : genericSectionInfo, MessageType.Info);
            return;
        }

        Rect rect = ReserveViewportRect();
        HandleOrbitInput(rect);
        if (Event.current.type == EventType.Repaint)
        {
            if (genericPreview == null)
            {
                genericPreview = CreatePreview();
                GameObject previewClone = UObject.Instantiate(genericTemplate);
                genericPreview.AddSingleGO(previewClone);
                previewClone.SetActive(true);
                genericBounds = ComputeRendererBounds(previewClone);
                genericBoundsValid = true;
            }

            genericPreview.BeginPreview(rect, GUIStyle.none);
            FrameCamera(genericPreview, genericBoundsValid ? genericBounds : new Bounds(Vector3.up, Vector3.one * 2f));
            genericPreview.Render(true);
            Texture result = genericPreview.EndPreview();
            GUI.DrawTexture(rect, result, ScaleMode.StretchToFill, false);
        }

        Renderer[] renderers = genericTemplate.GetComponentsInChildren<Renderer>(true);
        BasisEditorUI.Note(BasisEditorLocalization.Get("editor.beePreview.rendererCount", renderers.Length, genericSectionInfo));
        BasisEditorUI.Note(BasisEditorLocalization.Get("editor.beePreview.genericHint"));

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(BasisEditorLocalization.Get("editor.beePreview.spawnInScene")))
        {
            SpawnGenericInScene();
        }
        if (GUILayout.Button(BasisEditorLocalization.Get("editor.beePreview.saveGlb")))
        {
            SaveGlb();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void SpawnGenericInScene()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.IsValid())
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root != null && root.name.StartsWith(ScenePreviewPrefix))
                {
                    UObject.DestroyImmediate(root);
                }
            }
        }

        GameObject spawned = UObject.Instantiate(genericTemplate);
        spawned.name = $"{ScenePreviewPrefix}{connector.BasisBundleDescription?.AssetBundleName ?? connector.UniqueVersion})";
        spawned.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        spawned.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        spawned.SetActive(true);
        Selection.activeGameObject = spawned;
        EditorGUIUtility.PingObject(spawned);
        SceneView.RepaintAll();
    }

    private void SaveGlb()
    {
        string suggested = (connector.BasisBundleDescription?.AssetBundleName ?? "avatar") + ".glb";
        string target = EditorUtility.SaveFilePanel(BasisEditorLocalization.Get("editor.beePreview.saveGlbTitle"), "", suggested, "glb");
        if (!string.IsNullOrEmpty(target))
        {
            File.WriteAllBytes(target, genericGlbBytes);
            statusMessage = BasisEditorLocalization.Get("editor.beePreview.saved", target);
        }
    }

    // ─────────────────────────── info tab ───────────────────────────

    private void DrawInfoTab()
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.beePreview.connector"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.common.name"), connector.BasisBundleDescription?.AssetBundleName ?? BasisEditorLocalization.Get("editor.beeWindow.none"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beeWindow.version"), connector.UniqueVersion ?? BasisEditorLocalization.Get("editor.beeWindow.none"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beeWindow.created"), connector.DateOfCreation ?? BasisEditorLocalization.Get("editor.beeWindow.none"));
        if (connector.BasisBundleGenerated != null)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beePreview.sections"), string.Join(", ", connector.BasisBundleGenerated.Where(b => b != null).Select(b => b.Platform)));
        }

        EditorGUILayout.Space(4);
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.beePreview.farAvatar"));
        if (farLodPayload != null)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beePreview.payload"), BasisEditorLocalization.Get("editor.beePreview.payloadSummary", connector.FarLodBase64.Length / 1024f, farLodPayload.TriangleCount, farLodPayload.BoneCount));
        }
        else
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beeWindow.none"));
        }

        EditorGUILayout.Space(4);
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.beePreview.genericGltfAvatar"));
        if (genericTemplate == null)
        {
            EditorGUILayout.LabelField(string.IsNullOrEmpty(genericSectionInfo) ? BasisEditorLocalization.Get("editor.beeWindow.none") : genericSectionInfo, EditorStyles.wordWrappedMiniLabel);
            return;
        }
        EditorGUILayout.LabelField(genericSectionInfo, EditorStyles.wordWrappedMiniLabel);

        if (genericData == null)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.beePreview.noGenericAvatarData"), MessageType.Warning);
            return;
        }

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beePreview.humanBones"), (genericData.HumanBones?.Length ?? 0).ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beePreview.skeletonBones"), (genericData.SkeletonBones?.Length ?? 0).ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beePreview.eyeHeightFwd"), genericData.AvatarEyePosition.ToString("0.###"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beePreview.mouthHeightFwd"), genericData.AvatarMouthPosition.ToString("0.###"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beePreview.visemeMesh"), DescribeMeshPath(genericData.FaceVisemeMeshPath));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beePreview.blinkMesh"), DescribeMeshPath(genericData.FaceBlinkMeshPath));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beePreview.hiddenNodes"), (genericData.InactiveNodePaths?.Length ?? 0).ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.beePreview.replicatedComponents"), string.IsNullOrEmpty(genericData.ComponentsJson) ? BasisEditorLocalization.Get("editor.beeWindow.none") : $"{genericData.ComponentsJson.Length / 1024f:0.0} KB json");

        EditorGUILayout.Space(4);
        if (GUILayout.Button(BasisEditorLocalization.Get("editor.beePreview.testHumanoidRebuild")))
        {
            humanoidTestResult = RunHumanoidRebuildTest();
        }
        if (!string.IsNullOrEmpty(humanoidTestResult))
        {
            BasisEditorUI.Help(humanoidTestResult, humanoidTestPassed ? MessageType.Info : MessageType.Error);
        }
    }

    /// <summary>
    /// Runs the same AvatarBuilder path the client runtime uses on a throwaway clone, so a
    /// broken humanoid rebuild is caught at the desk instead of on a headset.
    /// </summary>
    private string RunHumanoidRebuildTest()
    {
        humanoidTestPassed = false;
        GameObject testClone = null;
        Avatar built = null;
        try
        {
            testClone = UObject.Instantiate(genericTemplate);
            testClone.hideFlags = HideFlags.HideAndDontSave;
            testClone.transform.position = new Vector3(0f, -4096f, 0f);
            testClone.SetActive(true);

            Transform rigRoot = FindByNameRecursive(testClone.transform, genericData.RootNodeName);
            if (rigRoot == null)
            {
                return BasisEditorLocalization.Get("editor.beePreview.humanoidRootMissing", genericData.RootNodeName);
            }
            rigRoot.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

            built = AvatarBuilder.BuildHumanAvatar(rigRoot.gameObject, genericData.ToHumanDescription());
            if (built == null || !built.isValid || !built.isHuman)
            {
                return BasisEditorLocalization.Get("editor.beePreview.humanoidInvalid");
            }
            humanoidTestPassed = true;
            return BasisEditorLocalization.Get("editor.beePreview.humanoidPass", genericData.HumanBones?.Length ?? 0);
        }
        catch (Exception ex)
        {
            return BasisEditorLocalization.Get("editor.beePreview.humanoidFail", ex.Message);
        }
        finally
        {
            if (built != null)
            {
                UObject.DestroyImmediate(built);
            }
            if (testClone != null)
            {
                UObject.DestroyImmediate(testClone);
            }
        }
    }

    private static string DescribeMeshPath(string path)
    {
        // null = the source avatar had no such mesh; empty = the mesh sits on the root node.
        return path == null ? BasisEditorLocalization.Get("editor.beeWindow.none") : path.Length == 0 ? BasisEditorLocalization.Get("editor.beePreview.rootMesh") : path;
    }

    /// <summary>
    /// The avatar rig root inside the imported template — glTFast wraps exported roots in a
    /// scene GameObject under the holder. Mirrors the runtime loader's lookup.
    /// </summary>
    private Transform LocateGenericAvatarRoot()
    {
        if (genericTemplate == null)
        {
            return null;
        }
        Transform holder = genericTemplate.transform;
        if (genericData != null && !string.IsNullOrEmpty(genericData.RootNodeName))
        {
            Transform named = FindByNameRecursive(holder, genericData.RootNodeName);
            if (named != null)
            {
                return named;
            }
        }
        Transform cursor = holder;
        while (cursor.childCount == 1)
        {
            cursor = cursor.GetChild(0);
            if (cursor.childCount != 1 || cursor.GetComponent<Renderer>() != null)
            {
                break;
            }
        }
        return cursor != holder ? cursor : (holder.childCount > 0 ? holder.GetChild(0) : null);
    }

    private static Transform FindByNameRecursive(Transform parent, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        for (int Index = 0; Index < parent.childCount; Index++)
        {
            Transform child = parent.GetChild(Index);
            if (child.name == name)
            {
                return child;
            }
            Transform nested = FindByNameRecursive(child, name);
            if (nested != null)
            {
                return nested;
            }
        }
        return null;
    }

    // ─────────────────────────── viewport shared ───────────────────────────

    private Rect ReserveViewportRect()
    {
        float side = Mathf.Clamp(Host.position.width - 24f, 200f, 480f);
        return GUILayoutUtility.GetRect(side, side, GUILayout.ExpandWidth(false));
    }

    private PreviewRenderUtility CreatePreview()
    {
        PreviewRenderUtility preview = new PreviewRenderUtility();
        preview.camera.fieldOfView = 30f;
        preview.camera.nearClipPlane = 0.01f;
        preview.camera.clearFlags = CameraClearFlags.SolidColor;
        preview.camera.backgroundColor = new Color(0.16f, 0.17f, 0.19f, 1f);
        return preview;
    }

    private void FrameCamera(PreviewRenderUtility preview, Bounds bounds)
    {
        float distance = bounds.extents.magnitude * 2.1f / Mathf.Max(orbitZoom, 0.05f) + 0.05f;
        Quaternion orbit = Quaternion.Euler(orbitPitch, orbitYaw, 0f);
        Camera camera = preview.camera;
        camera.transform.SetPositionAndRotation(bounds.center + orbit * (Vector3.back * distance), orbit);
        camera.farClipPlane = distance * 6f + 10f;

        preview.lights[0].intensity = 1.2f;
        preview.lights[0].transform.rotation = Quaternion.Euler(40f, orbitYaw - 30f, 0f);
        preview.lights[1].intensity = 0.4f;
        preview.ambientColor = new Color(0.32f, 0.32f, 0.34f, 1f);
    }

    private void HandleOrbitInput(Rect rect)
    {
        Event current = Event.current;
        if (!rect.Contains(current.mousePosition))
        {
            return;
        }
        if (current.type == EventType.MouseDrag && (current.button == 0 || current.button == 1))
        {
            orbitYaw += current.delta.x * 0.6f;
            orbitPitch = Mathf.Clamp(orbitPitch + current.delta.y * 0.6f, -85f, 85f);
            current.Use();
            Host.Repaint();
        }
        else if (current.type == EventType.ScrollWheel)
        {
            orbitZoom = Mathf.Clamp(orbitZoom * (1f - current.delta.y * 0.04f), 0.2f, 6f);
            current.Use();
            Host.Repaint();
        }
    }

    private static Bounds ComputeRendererBounds(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        bool hasAny = false;
        Bounds bounds = new Bounds(Vector3.up, Vector3.one);
        for (int Index = 0; Index < renderers.Length; Index++)
        {
            Renderer renderer = renderers[Index];
            if (renderer == null)
            {
                continue;
            }
            if (!hasAny)
            {
                bounds = renderer.bounds;
                hasAny = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }
        return bounds;
    }

    // ─────────────────────────── teardown ───────────────────────────

    private void ReleaseLoaded()
    {
        hasLoaded = false;
        connector = null;
        humanoidTestResult = "";
        genericSectionInfo = "";
        genericGlbBytes = null;
        genericData = null;
        genericSidecar = null;
        genericBoundsValid = false;

        farLodPreview?.Cleanup();
        farLodPreview = null;
        genericPreview?.Cleanup();
        genericPreview = null;

        farLodPayload = null;
        if (farLodMesh != null)
        {
            UObject.DestroyImmediate(farLodMesh);
            farLodMesh = null;
        }
        if (farLodTexture != null)
        {
            UObject.DestroyImmediate(farLodTexture);
            farLodTexture = null;
        }
        if (farLodMaterial != null)
        {
            UObject.DestroyImmediate(farLodMaterial);
            farLodMaterial = null;
        }

        if (genericTemplate != null)
        {
            UObject.DestroyImmediate(genericTemplate);
            genericTemplate = null;
        }
        if (gltfImport != null)
        {
            gltfImport.Dispose();
            gltfImport = null;
        }

        Host.Repaint();
    }
}
