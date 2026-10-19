using System.Collections.Generic;
using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Dev tool: runs the real far avatar build on a scene avatar and shows the result — an
/// interactive 3D view of the decoded far avatar, the baked atlas, payload stats and the
/// per-stage generation log. The preview is constructed from the serialized → reparsed
/// payload using the same builders the client runtime uses, so what you see is what a
/// remote player would get.
///
/// A scene copy is also spawned next to the source avatar; "Mirror source pose" drives it
/// with the same `rest * delta` composition the networked bone job applies, so posing or
/// animating the source live-validates skinning and the delta math.
///
/// Lifetime: preview assets are HideAndDontSave (the editor destroys unflagged loose
/// assets on scene/play transitions), and the payload is kept in SessionState so the
/// window rebuilds itself after every domain reload instead of going blank.
/// </summary>
public class BasisFarLodTesterWindow : EditorWindow
{
    private const string PayloadSessionKey = "BasisFarLodTester.Payload";
    private const string RawBytesSessionKey = "BasisFarLodTester.RawBytes";
    private const string Base64BytesSessionKey = "BasisFarLodTester.Base64Bytes";
    private const string ScenePreviewPrefix = "Far avatar Preview (";

    [MenuItem("Basis/Avatar/Far Avatar Tester", false, 141)]
    public static void Open()
    {
        BasisFarLodTesterWindow window = GetWindow<BasisFarLodTesterWindow>(BasisEditorLocalization.Get("sdk.farLod.tester.title"));
        window.minSize = new Vector2(390f, 580f);
    }

    [SerializeField] private BasisAvatar _avatar;
    [SerializeField] private int _tab;
    [SerializeField] private bool _mirrorPose = true;
    [SerializeField] private float _previewOffset;
    [SerializeField] private float _orbitYaw = 135f;
    [SerializeField] private float _orbitPitch = 12f;
    [SerializeField] private float _orbitZoom = 1f;
    [SerializeField] private bool _showStages = true;
    [SerializeField] private bool _showBones;

    private BasisFarLodPayload _payload;
    private BasisFarLodGenerator.GenerationReport _report;
    private int _payloadBytes;
    private int _base64Bytes;
    private string _lastError;
    private static string[] TabNames => new[]
    {
        BasisEditorLocalization.Get("sdk.farLod.tester.tab.view"),
        BasisEditorLocalization.Get("sdk.farLod.tester.tab.atlas"),
        BasisEditorLocalization.Get("editor.common.info"),
    };

    private GameObject _previewRoot;
    private Transform[] _previewBones;
    private Transform _previewHips;
    private Mesh _previewMesh;
    private Texture2D _previewTexture;
    private Material _previewMaterial;

    private Animator _sourceAnimator;
    private Transform[] _sourceBones;
    private Dictionary<HumanBodyBones, Quaternion> _sourceTposeLocals;

    private PreviewRenderUtility _previewRender;
    private Vector2 _scroll;

    private void OnEnable()
    {
        EditorApplication.update += OnEditorUpdate;

        // Restore the last result across domain reloads / play-mode transitions.
        if (_payload == null)
        {
            string stored = SessionState.GetString(PayloadSessionKey, null);
            if (!string.IsNullOrEmpty(stored))
            {
                _payload = BasisFarLodPayload.TryParseBase64(stored);
                _payloadBytes = SessionState.GetInt(RawBytesSessionKey, 0);
                _base64Bytes = SessionState.GetInt(Base64BytesSessionKey, 0);
                if (_payload != null)
                {
                    try
                    {
                        BuildWindowAssets();
                        BuildSceneCopy();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
            }
        }
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        _previewRender?.Cleanup();
        _previewRender = null;
        // Domain reloads pass through here too — assets are rebuilt from SessionState in OnEnable.
        DestroyPreview();
    }

    private void OnDestroy()
    {
        SessionState.EraseString(PayloadSessionKey);
        SessionState.EraseInt(RawBytesSessionKey);
        SessionState.EraseInt(Base64BytesSessionKey);
    }

    private void OnGUI()
    {
        BasisEditorUI.Header(BasisEditorLocalization.Get("sdk.farLod.tester.title"),
            BasisEditorLocalization.Get("sdk.farLod.tester.subtitle"));

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        DrawSourceSection();
        DrawStatusSection();
        if (_payload != null)
        {
            EditorGUILayout.Space(6);
            _tab = GUILayout.Toolbar(_tab, TabNames);
            EditorGUILayout.Space(2);
            switch (_tab)
            {
                case 0: DrawViewportTab(); break;
                case 1: DrawAtlasTab(); break;
                default: DrawInfoTab(); break;
            }
            DrawScenePreviewSection();
        }

        EditorGUILayout.EndScrollView();
    }

    // ─────────────────────────── source + generate ───────────────────────────

    private void DrawSourceSection()
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.common.source"));
        _avatar = (BasisAvatar)EditorGUILayout.ObjectField(BasisEditorLocalization.Get("sdk.farLod.tester.avatar"), _avatar, typeof(BasisAvatar), true);
        if (_avatar == null && Selection.activeGameObject != null && Selection.activeGameObject.TryGetComponent(out BasisAvatar selected))
        {
            if (GUILayout.Button(BasisEditorLocalization.Get("sdk.farLod.tester.useSelected", selected.name)))
            {
                _avatar = selected;
            }
        }

        BasisFarLodGenerator.TargetTriangleCount = EditorGUILayout.IntSlider(BasisEditorLocalization.Get("sdk.farLod.tester.targetTriangles"), BasisFarLodGenerator.TargetTriangleCount, 500, 12000);
        BasisFarLodGenerator.AtlasSize = EditorGUILayout.IntPopup(BasisEditorLocalization.Get("sdk.farLod.tester.atlasSize"), BasisFarLodGenerator.AtlasSize,
            new[] { "128", "256", "512", "1024", "2048" }, new[] { 128, 256, 512, 1024, 2048 });
        if (BasisFarLodGenerator.AtlasSize > 1024)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("sdk.farLod.tester.atlasWarning", BasisFarLodGenerator.AtlasSize), MessageType.Info);
        }

        bool persistent = _avatar != null && EditorUtility.IsPersistent(_avatar);
        if (persistent)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("sdk.farLod.tester.persistentAvatar"), MessageType.Info);
        }

        using (new EditorGUI.DisabledScope(_avatar == null || persistent))
        {
            if (BasisEditorUI.PrimaryButton(BasisEditorLocalization.Get("editor.common.generate"), 30f))
            {
                Generate();
                GUIUtility.ExitGUI();
            }
        }
    }

    private void DrawStatusSection()
    {
        if (!string.IsNullOrEmpty(_lastError))
        {
            BasisEditorUI.Help(_lastError, MessageType.Error);
        }

        if (_report == null || _report.Entries.Count == 0)
        {
            return;
        }
        _showStages = EditorGUILayout.Foldout(_showStages, BasisEditorLocalization.Get("sdk.farLod.tester.logHeader", _report.TotalSeconds), true);
        if (!_showStages)
        {
            return;
        }
        using (new EditorGUI.IndentLevelScope())
        {
            for (int i = 0; i < _report.Entries.Count; i++)
            {
                var entry = _report.Entries[i];
                EditorGUILayout.LabelField($"{entry.Label} — {entry.Seconds:0.00}s", EditorStyles.miniBoldLabel);
                if (!string.IsNullOrEmpty(entry.Detail))
                {
                    EditorGUILayout.LabelField(entry.Detail, EditorStyles.miniLabel);
                }
            }
        }
    }

    private void Generate()
    {
        _lastError = null;
        _payload = null;
        _report = new BasisFarLodGenerator.GenerationReport();
        BasisFarLodPayload generated = null;
        BasisFarLodGenerator.VerboseLogging = true;
        BasisFarLodGenerator.ActiveReport = _report;
        try
        {
            generated = BasisFarLodGenerator.Generate(_avatar);
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            _lastError = BasisEditorLocalization.Get("sdk.farLod.tester.error.generationThrew", e.GetType().Name, e.Message);
            return;
        }
        finally
        {
            BasisFarLodGenerator.ActiveReport = null;
            BasisFarLodGenerator.VerboseLogging = false;
            EditorUtility.ClearProgressBar();
        }
        if (generated == null)
        {
            _lastError = BasisEditorLocalization.Get("sdk.farLod.tester.error.noResult");
            return;
        }

        try
        {
            // Round-trip through the wire format so the preview is what a client would decode.
            byte[] bytes = generated.Serialize();
            string base64 = System.Convert.ToBase64String(bytes);
            _payloadBytes = bytes.Length;
            _base64Bytes = base64.Length;
            _payload = BasisFarLodPayload.TryParse(bytes);
            if (_payload == null)
            {
                _lastError = BasisEditorLocalization.Get("sdk.farLod.tester.error.roundTrip");
                return;
            }

            SessionState.SetString(PayloadSessionKey, base64);
            SessionState.SetInt(RawBytesSessionKey, _payloadBytes);
            SessionState.SetInt(Base64BytesSessionKey, _base64Bytes);

            DestroyPreview();
            BuildWindowAssets();
            BuildSceneCopy();
            _tab = 0;
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            _lastError = BasisEditorLocalization.Get("sdk.farLod.tester.error.previewThrew", e.GetType().Name, e.Message);
            DestroyPreview();
        }
    }

    // ─────────────────────────── result tabs ───────────────────────────

    private void DrawViewportTab()
    {
        if (_previewMesh == null || _previewMaterial == null)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("sdk.farLod.tester.noMesh"), MessageType.Warning);
            return;
        }

        float side = Mathf.Clamp(position.width - 24f, 200f, 460f);
        Rect rect = GUILayoutUtility.GetRect(side, side, GUILayout.ExpandWidth(false));
        HandleOrbitInput(rect);

        if (Event.current.type == EventType.Repaint)
        {
            _previewRender ??= CreatePreviewRender();

            Bounds bounds = new Bounds(
                (_payload.PositionBoundsMin + _payload.PositionBoundsMax) * 0.5f,
                _payload.PositionBoundsMax - _payload.PositionBoundsMin);
            float distance = bounds.extents.magnitude * 2.1f / Mathf.Max(_orbitZoom, 0.05f) + 0.05f;
            Quaternion orbit = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);

            _previewRender.BeginPreview(rect, GUIStyle.none);
            Camera camera = _previewRender.camera;
            camera.transform.SetPositionAndRotation(bounds.center + orbit * (Vector3.back * distance), orbit);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = distance * 6f + 10f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.16f, 0.17f, 0.19f, 1f);

            _previewRender.lights[0].intensity = 1.2f;
            _previewRender.lights[0].transform.rotation = Quaternion.Euler(40f, _orbitYaw - 30f, 0f);
            _previewRender.lights[1].intensity = 0.4f;
            _previewRender.ambientColor = new Color(0.32f, 0.32f, 0.34f, 1f);

            _previewRender.DrawMesh(_previewMesh, Matrix4x4.identity, _previewMaterial, 0);
            camera.Render();
            Texture result = _previewRender.EndPreview();
            GUI.DrawTexture(rect, result, ScaleMode.StretchToFill, false);
        }

        BasisEditorUI.Note(BasisEditorLocalization.Get("sdk.farLod.tester.orbitHelp"));
    }

    private PreviewRenderUtility CreatePreviewRender()
    {
        PreviewRenderUtility preview = new PreviewRenderUtility();
        preview.camera.fieldOfView = 30f;
        return preview;
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
            _orbitYaw += current.delta.x * 0.6f;
            _orbitPitch = Mathf.Clamp(_orbitPitch + current.delta.y * 0.6f, -85f, 85f);
            current.Use();
            Repaint();
        }
        else if (current.type == EventType.ScrollWheel)
        {
            _orbitZoom = Mathf.Clamp(_orbitZoom * (1f - current.delta.y * 0.04f), 0.2f, 6f);
            current.Use();
            Repaint();
        }
    }

    private void DrawAtlasTab()
    {
        if (_previewTexture == null)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("sdk.farLod.tester.noAtlas"), MessageType.Warning);
            return;
        }
        float side = Mathf.Clamp(position.width - 24f, 200f, 460f);
        Rect rect = GUILayoutUtility.GetRect(side, side, GUILayout.ExpandWidth(false));
        EditorGUI.DrawPreviewTexture(rect, _previewTexture);
        BasisEditorUI.Note(BasisEditorLocalization.Get("sdk.farLod.tester.atlasDecoded", _previewTexture.format, _previewTexture.width, _previewTexture.height, _previewTexture.mipmapCount));
        for (int i = 0; i < _payload.Textures.Length; i++)
        {
            var texture = _payload.Textures[i];
            BasisEditorUI.Note(BasisEditorLocalization.Get("sdk.farLod.tester.atlasPayload", texture.Format, texture.Width, texture.Height, texture.MipCount, texture.Data.Length / 1024f));
        }
    }

    private void DrawInfoTab()
    {
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("sdk.farLod.tester.payload"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.farLod.tester.triangles"), _payload.TriangleCount.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.farLod.tester.vertices"), _payload.VertexCount.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.farLod.tester.bones"), _payload.BoneCount.ToString());
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.farLod.tester.rawSize"), $"{_payloadBytes / 1024f:0.0} KB");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.farLod.tester.inConnector"), $"{_base64Bytes / 1024f:0.0} KB");
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.farLod.tester.authoredScale"), _payload.AuthoredRootScale.ToString("0.###"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.farLod.tester.lightingResponse"), BasisEditorLocalization.Get("sdk.farLod.tester.lightingValue", _payload.MinBrightness.ToString("0.###"), _payload.MaxBrightness >= 4f ? BasisEditorLocalization.Get("sdk.farLod.tester.uncapped") : _payload.MaxBrightness.ToString("0.##")));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.farLod.tester.eyeHeight"), _payload.AvatarEyePosition.ToString("0.###"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.farLod.tester.mouthHeight"), _payload.AvatarMouthPosition.ToString("0.###"));
        Vector3 size = _payload.PositionBoundsMax - _payload.PositionBoundsMin;
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.farLod.tester.bounds"), size.ToString("0.###"));

        _showBones = EditorGUILayout.Foldout(_showBones, BasisEditorLocalization.Get("sdk.farLod.tester.skeleton"), true);
        if (_showBones)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                for (int i = 0; i < _payload.BoneCount; i++)
                {
                    HumanBodyBones bone = (HumanBodyBones)_payload.BoneHumanBodyBone[i];
                    byte parent = _payload.BoneParentIndex[i];
                    string parentName = parent == 0xFF ? BasisEditorLocalization.Get("sdk.farLod.tester.rootBone") : ((HumanBodyBones)_payload.BoneHumanBodyBone[parent]).ToString();
                    BasisEditorUI.Note($"{bone} ← {parentName}");
                }
            }
        }
    }

    // ─────────────────────────── preview building ───────────────────────────

    /// <summary>Mesh, texture and material for the in-window viewport — no scene objects.</summary>
    private void BuildWindowAssets()
    {
        _previewMesh = _payload.CreateMesh();
        _previewTexture = _payload.CreateTexture();
        if (_previewMesh == null || _previewTexture == null)
        {
            _lastError = BasisEditorLocalization.Get("sdk.farLod.tester.error.decode");
            DestroyPreview();
            return;
        }

        Shader shader = Shader.Find("Basis/AvatarFarLod");
        if (shader == null)
        {
            shader = Shader.Find("Universal Render Pipeline/Unlit");
            Debug.LogWarning("Basis/AvatarFarLod shader not found — previewing with URP Unlit.");
        }
        _previewMaterial = new Material(shader) { enableInstancing = true };
        _previewMaterial.SetTexture("_BaseMap", _previewTexture);
        _previewMaterial.SetFloat("_MinBrightness", _payload.MinBrightness);
        _previewMaterial.SetFloat("_MaxBrightness", _payload.MaxBrightness);

        // Without HideAndDontSave the editor destroys loose created assets on scene/play
        // transitions — this is the "texture disappears after a while" failure mode.
        _previewMesh.hideFlags = HideFlags.HideAndDontSave;
        _previewTexture.hideFlags = HideFlags.HideAndDontSave;
        _previewMaterial.hideFlags = HideFlags.HideAndDontSave;
    }

    /// <summary>Spawns the posable copy next to the source avatar.</summary>
    private void BuildSceneCopy()
    {
        DestroyStaleSceneCopies();
        if (_previewMesh == null || _previewMaterial == null || _avatar == null || _avatar.Animator == null)
        {
            return;
        }

        _sourceAnimator = _avatar.Animator;
        _sourceTposeLocals = BasisFarLodGenerator.CaptureActualTposeLocals(_sourceAnimator);

        _previewRoot = new GameObject($"{ScenePreviewPrefix}{_avatar.name})")
        {
            hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild,
        };

        int boneCount = _payload.BoneCount;
        _previewBones = new Transform[boneCount];
        _sourceBones = new Transform[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            HumanBodyBones bone = (HumanBodyBones)_payload.BoneHumanBodyBone[i];
            GameObject boneObject = new GameObject(bone.ToString());
            Transform boneTransform = boneObject.transform;
            byte parent = _payload.BoneParentIndex[i];
            boneTransform.SetParent(parent == 0xFF ? _previewRoot.transform : _previewBones[parent], false);
            boneTransform.SetLocalPositionAndRotation(_payload.BoneRestLocalPosition[i], _payload.BoneRestLocalRotation[i]);
            _previewBones[i] = boneTransform;
            _sourceBones[i] = _sourceAnimator.GetBoneTransform(bone);
        }
        int hipsIndex = _payload.FindBone(HumanBodyBones.Hips);
        _previewHips = hipsIndex >= 0 ? _previewBones[hipsIndex] : _previewBones[0];

        GameObject meshObject = new GameObject("Mesh");
        meshObject.transform.SetParent(_previewRoot.transform, false);
        SkinnedMeshRenderer renderer = meshObject.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = _previewMesh;
        renderer.sharedMaterial = _previewMaterial;
        renderer.bones = _previewBones;
        renderer.rootBone = _previewHips;
        renderer.localBounds = new Bounds(_payload.LocalBoundsCenter, _payload.LocalBoundsExtents * 2f);
        renderer.quality = SkinQuality.Bone2;
        renderer.updateWhenOffscreen = false;

        PositionPreviewRoot();
        if (_mirrorPose)
        {
            ApplyMirrorPose();
        }
        SceneView.RepaintAll();
    }

    /// <summary>
    /// Scene copies survive domain reloads while our references don't — sweep leftovers by
    /// name so a rebuild never stacks duplicates next to the avatar.
    /// </summary>
    private void DestroyStaleSceneCopies()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!scene.IsValid())
        {
            return;
        }
        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            if (roots[i] != null && roots[i] != _previewRoot && roots[i].name.StartsWith(ScenePreviewPrefix))
            {
                DestroyImmediate(roots[i]);
            }
        }
    }

    // ─────────────────────────── scene copy section ───────────────────────────

    private void DrawScenePreviewSection()
    {
        EditorGUILayout.Space(6);
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("sdk.farLod.tester.sceneCopy"));
        if (_previewRoot == null)
        {
            if (_avatar != null && _previewMesh != null && GUILayout.Button(BasisEditorLocalization.Get("sdk.farLod.tester.spawnCopy")))
            {
                BuildSceneCopy();
            }
            else
            {
                BasisEditorUI.Note(BasisEditorLocalization.Get("sdk.farLod.tester.notSpawned"));
            }
            return;
        }
        _mirrorPose = EditorGUILayout.Toggle(BasisEditorLocalization.Get("sdk.farLod.tester.mirrorPose"), _mirrorPose);
        _previewOffset = EditorGUILayout.FloatField(BasisEditorLocalization.Get("sdk.farLod.tester.offset"), _previewOffset);
        BasisEditorUI.Note(BasisEditorLocalization.Get("sdk.farLod.tester.mirrorPaused"));
        DrawPoseAudit();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(BasisEditorLocalization.Get("sdk.farLod.tester.selectInScene")))
        {
            EditorGUIUtility.PingObject(_previewRoot);
            Selection.activeGameObject = _previewRoot;
        }
        if (GUILayout.Button(BasisEditorLocalization.Get("sdk.farLod.tester.destroyCopy")))
        {
            DestroyScenePreviewOnly();
        }
        EditorGUILayout.EndHorizontal();
    }

    /// <summary>
    /// Live world-rotation error per bone between the source avatar and the mirrored copy —
    /// this is the exact composition the networked bone job applies, so any error here will
    /// reproduce at runtime, and a clean audit here with a wrong runtime pose means the
    /// difference lives in the wire/runtime T-pose provenance instead.
    /// </summary>
    private void DrawPoseAudit()
    {
        if (!_mirrorPose || _previewBones == null || _sourceBones == null || _payload == null)
        {
            return;
        }
        float worst = 0f;
        string worstBone = null;
        int shown = 0;
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("sdk.farLod.tester.auditHeader"));
        for (int i = 0; i < _previewBones.Length; i++)
        {
            Transform source = _sourceBones[i];
            Transform copy = _previewBones[i];
            if (source == null || copy == null)
            {
                continue;
            }
            float angle = Quaternion.Angle(source.rotation, copy.rotation);
            if (angle > worst)
            {
                worst = angle;
                worstBone = ((HumanBodyBones)_payload.BoneHumanBodyBone[i]).ToString();
            }
            if (angle > 1f && shown < 10)
            {
                BasisEditorUI.Note($"{(HumanBodyBones)_payload.BoneHumanBodyBone[i]}: {angle:0.0}°");
                shown++;
            }
        }
        EditorGUILayout.LabelField(worst <= 1f
            ? BasisEditorLocalization.Get("sdk.farLod.tester.auditClean", worst, worstBone ?? BasisEditorLocalization.Get("sdk.farLod.tester.notAvailable"))
            : BasisEditorLocalization.Get("sdk.farLod.tester.auditWorst", worst, worstBone),
            worst <= 1f ? EditorStyles.miniLabel : EditorStyles.boldLabel);
        Repaint();
    }

    private float ResolveOffset()
    {
        if (_previewOffset > 0.0001f)
        {
            return _previewOffset;
        }
        float width = _payload.PositionBoundsMax.x - _payload.PositionBoundsMin.x;
        float scale = Mathf.Max(_sourceAnimator.transform.localScale.x, 0.01f);
        return width * scale * 1.25f + 0.25f;
    }

    private void PositionPreviewRoot()
    {
        Transform source = _sourceAnimator.transform;
        Vector3 offset = source.rotation * Vector3.right * ResolveOffset();
        _previewRoot.transform.SetPositionAndRotation(source.position + offset, source.rotation);
        _previewRoot.transform.localScale = source.localScale;
    }

    private void ApplyMirrorPose()
    {
        Vector3 offset = _previewRoot.transform.position - _sourceAnimator.transform.position;
        for (int i = 0; i < _previewBones.Length; i++)
        {
            HumanBodyBones bone = (HumanBodyBones)_payload.BoneHumanBodyBone[i];
            if (bone == HumanBodyBones.Hips)
            {
                continue;
            }
            Transform source = _sourceBones[i];
            if (source == null || !_sourceTposeLocals.TryGetValue(bone, out Quaternion tposeLocal))
            {
                continue;
            }
            // Exactly the wire composition: delta is the source bone's rotation off its own
            // T-pose local frame, applied on top of the far avatar's collapsed rest local.
            Quaternion delta = Quaternion.Inverse(tposeLocal) * source.localRotation;
            _previewBones[i].localRotation = _payload.BoneRestLocalRotation[i] * delta;
        }

        Transform sourceHips = _sourceAnimator.GetBoneTransform(HumanBodyBones.Hips);
        if (sourceHips != null)
        {
            // Hips is world-applied at runtime (ApplyHipsWorldJob) — copy world plus the view offset.
            _previewHips.SetPositionAndRotation(sourceHips.position + offset, sourceHips.rotation);
        }
    }

    private void OnEditorUpdate()
    {
        if (_previewRoot == null || _avatar == null || _sourceAnimator == null)
        {
            return;
        }
        // Hands-off while the user is posing/inspecting the copy itself — otherwise the
        // mirror rewrites every bone rotation each tick and manual rotations appear dead.
        Transform selected = Selection.activeTransform;
        if (selected != null && selected.IsChildOf(_previewRoot.transform))
        {
            return;
        }
        PositionPreviewRoot();
        if (_mirrorPose)
        {
            ApplyMirrorPose();
        }
    }

    private void DestroyScenePreviewOnly()
    {
        if (_previewRoot != null)
        {
            DestroyImmediate(_previewRoot);
        }
        _previewRoot = null;
        _previewBones = null;
        _previewHips = null;
        _sourceBones = null;
    }

    private void DestroyPreview()
    {
        DestroyScenePreviewOnly();
        if (_previewMesh != null)
        {
            DestroyImmediate(_previewMesh);
        }
        if (_previewTexture != null)
        {
            DestroyImmediate(_previewTexture);
        }
        if (_previewMaterial != null)
        {
            DestroyImmediate(_previewMaterial);
        }
        _previewMesh = null;
        _previewTexture = null;
        _previewMaterial = null;
    }
}
