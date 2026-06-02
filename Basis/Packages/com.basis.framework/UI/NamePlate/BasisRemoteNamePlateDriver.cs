using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using System.Collections.Generic;
using TMPro;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Basis.Scripts.UI.NamePlate
{
    public static class BasisRemoteNamePlateDriver
    {
        public const string CombinedNameplateMeshName = "CombinedNameplateMesh";

        // Defaults baked from the original BasisFramework.prefab values.
        public static Color NormalColor = new Color(0.25490198f, 0.25490198f, 0.25490198f, 0.4509804f);
        public static Color IsTalkingColor = new Color(0.3529412f, 0.72156864f, 0.90588236f, 0.7490196f);
        public static Color OutOfRangeColor = new Color(0.105882354f, 0.23137255f, 0.29411766f, 0.7490196f);
        public static Color FailedLoadColor = new Color(1f, 0.2f, 0.2f, 1f);

        // Per-talk-mode nameplate colors (resting + lighter "talking" variant). Alpha is
        // replaced by NamePlateTransparency in UpdateCachedColors, so RGB is what matters.
        public static Color PrivateColor = new Color(0.6078432f, 0.1882353f, 1f, 1f);
        public static Color PrivateTalkColor = new Color(0.8156863f, 0.627451f, 1f, 1f);
        public static Color DirectColor = new Color(0.12156863f, 0.7490196f, 0.3529412f, 1f);
        public static Color DirectTalkColor = new Color(0.49803922f, 0.92156863f, 0.6509804f, 1f);
        public static Color ThisPersonColor = new Color(1f, 0.3098039f, 0.627451f, 1f);
        public static Color ThisPersonTalkColor = new Color(1f, 0.6588235f, 0.8156863f, 1f);
        public static Color ShoutColor = new Color(1f, 0.5490196f, 0f, 1f);
        public static Color ShoutTalkColor = new Color(1f, 0.7215686f, 0.3764706f, 1f);
        public static Color MutedColor = new Color(0.12f, 0.14f, 0.18f, 1f);

        public static float transitionDuration = 0.3f;
        public static float returnDelay = 0.4f;

        public static Color StaticNormalColor;
        public static Color StaticIsTalkingColor;
        public static Color StaticOutOfRangeColor;
        public static Color StaticFailedLoadColor;
        public static Color StaticPrivateColor;
        public static Color StaticPrivateTalkColor;
        public static Color StaticDirectColor;
        public static Color StaticDirectTalkColor;
        public static Color StaticThisPersonColor;
        public static Color StaticThisPersonTalkColor;
        public static Color StaticShoutColor;
        public static Color StaticShoutTalkColor;
        public static Color StaticMutedColor;
        public static float4 NormalColorFloat4;

        // Lazy-created at runtime — replaces the prefab's TMP child used for baking.
        public static TextMeshPro Text;

        // Lazy-loaded from Addressables on first Initialize.
        public static Material TransParentNamePlateMaterial;
        public static Material OpaqueNamePlateMaterial;

        public static Material SelectedNamePlateMaterial;

        // Addressables keys for the materials/font that used to be serialized on the prefab.
        private const string TransparentMaterialAddress = "Packages/com.basis.sdk/Materials/TransParentNamePlateMaterial.mat";
        private const string OpaqueMaterialAddress = "Packages/com.basis.sdk/Materials/OpaqueNamePlateMaterial.mat";
        private const string FontAddress = "Packages/com.basis.sdk/Fonts/Poppins-Regular SDF NamePlate.asset";

        public static float RoundEdges = 0.85f;
        public static int CornerVertexCount = 8;
        public static float zOffset = 0.06f;

        public static bool NamePlateEnabled = true;
        public static bool NamePlateMenuOnly = false;
        public static bool NamePlateHoverMenuOnly = false;
        public static float NamePlateSize = 1f;
        public static float NamePlateTransparency = 0.45f;
        public static float ChatSize = 1f;
        private const float ChatNameClearance = 4.5f;
        private const float ChatBubbleGap = 1.5f;
        private static bool lastMenuOpenState;
        private static bool _initialized;

        // Precomputed per CornerVertexCount
        private static int cachedCornerCount;
        private static float[] sinTable;
        private static float[] cosTable;
        private static int[] cachedTriangles;
        private static int cachedRingVertexCount;
        private static int cachedVertexCount;

        // Reusable working arrays (avoid per-call managed allocation)
        private static Vector3[] workVertices;
        private static Vector3[] workNormals;
        private static Vector2[] workUVs;

        private static bool _unicodeFallbacksEnsured;

        /// <summary>
        /// Idempotent. Triggered by <see cref="Basis.Scripts.Device_Management.BasisDeviceManagement"/>
        /// after device init completes; safe to call again after <see cref="Dispose"/>.
        /// Loads runtime-only assets (materials + TMP baking object) from Addressables on first call.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            EnsureAssetsLoaded();

            SelectedNamePlateMaterial = BasisDeviceManagement.IsMobileHardware()
                ? OpaqueNamePlateMaterial
                : TransParentNamePlateMaterial;

            NamePlateEnabled = BasisSettingsDefaults.NPEnabled.RawValue;
            NamePlateMenuOnly = BasisSettingsDefaults.NPMenuOnly.RawValue;
            NamePlateHoverMenuOnly = BasisSettingsDefaults.NPHoverMenuOnly.RawValue;
            NamePlateSize = BasisSettingsDefaults.NPSize.RawValue;
            NamePlateTransparency = BasisSettingsDefaults.NPTransparency.RawValue;
            ChatSize = BasisSettingsDefaults.ChatSize.RawValue;
            lastMenuOpenState = BasisMainMenu.Instance != null;

            UpdateCachedColors(NamePlateTransparency);
            PrecomputeCornerData();
            EnsureUnicodeFallbacksOnNameplateFont();
        }

        /// <summary>
        /// One-time load of the prefab-replacement assets (materials + TMP baking object).
        /// Baker is parented under the BasisDeviceManagement root so it inherits the
        /// framework's lifetime instead of needing DontDestroyOnLoad.
        /// </summary>
        private static void EnsureAssetsLoaded()
        {
            if (TransParentNamePlateMaterial == null)
            {
                TransParentNamePlateMaterial = Addressables.LoadAssetAsync<Material>(TransparentMaterialAddress).WaitForCompletion();
            }
            if (OpaqueNamePlateMaterial == null)
            {
                OpaqueNamePlateMaterial = Addressables.LoadAssetAsync<Material>(OpaqueMaterialAddress).WaitForCompletion();
            }
            if (Text == null)
            {
                var font = Addressables.LoadAssetAsync<TMP_FontAsset>(FontAddress).WaitForCompletion();

                var bakingGO = new GameObject("BasisNameplateBaker");
                bakingGO.transform.SetParent(BasisDeviceManagement.Instance.transform, false);
                bakingGO.SetActive(false);

                Text = bakingGO.AddComponent<TextMeshPro>();
                Text.font = font;
                Text.fontSize = BakeFontSize;
                Text.enableAutoSizing = false;
                Text.alignment = TextAlignmentOptions.Center;
                Text.color = Color.white;
                Text.enableVertexGradient = false;
                Text.textWrappingMode = TextWrappingModes.NoWrap;
                Text.overflowMode = TextOverflowModes.Overflow;
            }
        }

        /// <summary>
        /// Walks a per-script list of OS font candidates and adds the first installed
        /// match per script to the nameplate font's fallback list. This lets display
        /// names render glyphs the primary (Latin) font doesn't cover — Japanese,
        /// Korean, Chinese, Arabic, Thai, Hebrew, Devanagari — instead of silently
        /// dropping them. Scripts with no installed candidate degrade to "no glyph";
        /// no crash. Per-family dedupe avoids loading the same atlas twice when a
        /// font (e.g., Tahoma covers both Arabic and Hebrew) was added for an earlier
        /// script in the chain.
        /// </summary>
        private static void EnsureUnicodeFallbacksOnNameplateFont()
        {
            if (_unicodeFallbacksEnsured) return;
            _unicodeFallbacksEnsured = true;

            if (Text == null || Text.font == null) return;

            var primary = Text.font;
            if (primary.fallbackFontAssetTable == null)
                primary.fallbackFontAssetTable = new List<TMP_FontAsset>();

            var registered = new HashSet<string>();
            string[][] scriptCandidates =
            {
                // Japanese — kanji, hiragana, katakana
                new[] { "Yu Gothic UI", "Yu Gothic", "Meiryo UI", "Meiryo", "MS Gothic",
                        "Hiragino Sans", "Hiragino Kaku Gothic ProN",
                        "Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP", "TakaoGothic" },

                // Korean — Hangul
                new[] { "Malgun Gothic", "Gulim", "Dotum", "Batang",
                        "Apple SD Gothic Neo", "AppleGothic",
                        "Noto Sans CJK KR", "Noto Sans KR", "NanumGothic" },

                // Simplified Chinese — CN-style Han glyphs
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun",
                        "PingFang SC", "Hiragino Sans GB", "STHeiti",
                        "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC",
                        "WenQuanYi Micro Hei" },

                // Traditional Chinese — TW/HK-style Han glyphs
                new[] { "Microsoft JhengHei UI", "Microsoft JhengHei", "PMingLiU", "MingLiU",
                        "PingFang TC", "Heiti TC",
                        "Noto Sans CJK TC", "Noto Sans TC" },

                // Arabic
                new[] { "Tahoma", "Segoe UI",
                        "Geeza Pro", "Damascus",
                        "Noto Sans Arabic", "Noto Naskh Arabic", "DejaVu Sans" },

                // Thai
                new[] { "Leelawadee UI", "Leelawadee",
                        "Thonburi", "Sukhumvit Set",
                        "Noto Sans Thai", "Loma" },

                // Hebrew
                new[] { "David CLM", "Arial Hebrew",
                        "Tahoma", "Segoe UI", "Lucida Grande",
                        "Noto Sans Hebrew", "DejaVu Sans" },

                // Devanagari — Hindi, Marathi, Sanskrit
                new[] { "Nirmala UI", "Mangal",
                        "Devanagari MT", "Kohinoor Devanagari",
                        "Noto Sans Devanagari", "Lohit Devanagari" },
            };

            foreach (string[] candidates in scriptCandidates)
                AddFirstAvailableFallback(primary, candidates, registered);
        }

        private static void AddFirstAvailableFallback(TMP_FontAsset primary, string[] candidates, HashSet<string> registered)
        {
            foreach (string family in candidates)
            {
                // If a previous script already loaded this family, accept its glyph
                // coverage for the current script too — avoids double-allocating the
                // same atlas. Stop the search; we won't try worse candidates after a
                // hit on the user's preferred chain.
                if (registered.Contains(family)) return;

                // Prefilter against the installed font list. CreateFontAsset emits an
                // unconditional Debug.Log on miss, so on machines without (e.g.) the
                // Hebrew system fonts each script's chain spammed the log even though
                // the silent skip behavior was actually correct.
                if (!IsFontInstalled(family)) continue;

                TMP_FontAsset fallback = null;
                try { fallback = TMP_FontAsset.CreateFontAsset(family, "Regular"); }
                catch { continue; }
                if (fallback == null) continue;

                fallback.name = "NamePlate Fallback (" + family + ")";
                primary.fallbackFontAssetTable.Add(fallback);
                registered.Add(family);
                return;
            }
        }

        private static HashSet<string> _installedFontNamesLower;

        private static bool IsFontInstalled(string family)
        {
            if (_installedFontNamesLower == null)
            {
                HashSet<string> set = new HashSet<string>();
                try
                {
                    string[] names = Font.GetOSInstalledFontNames();
                    if (names != null)
                        foreach (string n in names)
                            if (!string.IsNullOrEmpty(n)) set.Add(n.ToLowerInvariant());
                }
                catch
                {
                    // Some platforms (mobile/console) don't enumerate system fonts —
                    // fall through to the empty set so we let TMP try anyway.
                }
                _installedFontNamesLower = set;
            }

            if (_installedFontNamesLower.Count == 0) return true;
            string f = family.ToLowerInvariant();
            if (_installedFontNamesLower.Contains(f)) return true;
            // Style suffixes ("Arial Bold", "David CLM Regular") are common — accept
            // any installed name that begins with the requested family.
            foreach (string n in _installedFontNamesLower)
                if (n.StartsWith(f)) return true;
            return false;
        }

        private static void UpdateCachedColors(float transparency)
        {
            StaticNormalColor = new Color(NormalColor.r, NormalColor.g, NormalColor.b, transparency);
            StaticIsTalkingColor = new Color(IsTalkingColor.r, IsTalkingColor.g, IsTalkingColor.b, transparency);
            StaticOutOfRangeColor = new Color(OutOfRangeColor.r, OutOfRangeColor.g, OutOfRangeColor.b, transparency);
            // Guard against prefabs saved before FailedLoadColor existed — deserialization
            // zeros the struct, which would render the failed plate invisible.
            Color failedSource = (FailedLoadColor.r == 0f && FailedLoadColor.g == 0f && FailedLoadColor.b == 0f && FailedLoadColor.a == 0f)
                ? new Color(1f, 0.2f, 0.2f, 1f)
                : FailedLoadColor;
            StaticFailedLoadColor = new Color(failedSource.r, failedSource.g, failedSource.b, transparency);
            StaticPrivateColor = new Color(PrivateColor.r, PrivateColor.g, PrivateColor.b, transparency);
            StaticPrivateTalkColor = new Color(PrivateTalkColor.r, PrivateTalkColor.g, PrivateTalkColor.b, transparency);
            StaticDirectColor = new Color(DirectColor.r, DirectColor.g, DirectColor.b, transparency);
            StaticDirectTalkColor = new Color(DirectTalkColor.r, DirectTalkColor.g, DirectTalkColor.b, transparency);
            StaticThisPersonColor = new Color(ThisPersonColor.r, ThisPersonColor.g, ThisPersonColor.b, transparency);
            StaticThisPersonTalkColor = new Color(ThisPersonTalkColor.r, ThisPersonTalkColor.g, ThisPersonTalkColor.b, transparency);
            StaticShoutColor = new Color(ShoutColor.r, ShoutColor.g, ShoutColor.b, transparency);
            StaticShoutTalkColor = new Color(ShoutTalkColor.r, ShoutTalkColor.g, ShoutTalkColor.b, transparency);
            StaticMutedColor = new Color(MutedColor.r, MutedColor.g, MutedColor.b, transparency);
            NormalColorFloat4 = new float4(StaticNormalColor.r, StaticNormalColor.g, StaticNormalColor.b, StaticNormalColor.a);
        }

        public static Color GetModeRestingColor(BasisTalkMode mode)
        {
            switch (mode)
            {
                case BasisTalkMode.Private: return StaticPrivateColor;
                case BasisTalkMode.Direct: return StaticDirectColor;
                case BasisTalkMode.ThisPerson: return StaticThisPersonColor;
                case BasisTalkMode.Shout: return StaticShoutColor;
                default: return StaticNormalColor;
            }
        }

        public static Color GetModeTalkColor(BasisTalkMode mode)
        {
            switch (mode)
            {
                case BasisTalkMode.Private: return StaticPrivateTalkColor;
                case BasisTalkMode.Direct: return StaticDirectTalkColor;
                case BasisTalkMode.ThisPerson: return StaticThisPersonTalkColor;
                case BasisTalkMode.Shout: return StaticShoutTalkColor;
                default: return StaticIsTalkingColor;
            }
        }

        /// <summary>
        /// Precomputes sin/cos lookup table, triangle indices, normals,
        /// and allocates working arrays. Only needs to run when CornerVertexCount changes.
        /// </summary>
        private static void PrecomputeCornerData()
        {
            cachedCornerCount = Mathf.Max(3, CornerVertexCount);
            cachedRingVertexCount = cachedCornerCount * 4;
            cachedVertexCount = cachedRingVertexCount + 1;

            // Trig lookup table
            float angleStep = Mathf.PI * 0.5f / (cachedCornerCount - 1);
            sinTable = new float[cachedCornerCount];
            cosTable = new float[cachedCornerCount];
            for (int ci = 0; ci < cachedCornerCount; ci++)
            {
                float angle = ci * angleStep;
                sinTable[ci] = Mathf.Sin(angle);
                cosTable[ci] = Mathf.Cos(angle);
            }

            // Triangle indices — topology is identical for all quads with same corner count
            cachedTriangles = new int[cachedRingVertexCount * 3];
            for (int i = 0; i < cachedRingVertexCount; i++)
            {
                int tri = i * 3;
                cachedTriangles[tri] = 0;
                cachedTriangles[tri + 1] = 1 + ((i + 1) % cachedRingVertexCount);
                cachedTriangles[tri + 2] = 1 + i;
            }

            // Allocate reusable working arrays
            workVertices = new Vector3[cachedVertexCount];
            workNormals = new Vector3[cachedVertexCount];
            workUVs = new Vector2[cachedVertexCount];

            // Normals are always Vector3.forward — fill once, reuse forever
            for (int i = 0; i < cachedVertexCount; i++)
                workNormals[i] = Vector3.forward;
        }

        /// <summary>
        /// Returns whether a given plate should currently be active, considering
        /// the enabled toggle, menu-only mode, and per-plate face visibility.
        /// </summary>
        public static bool ShouldPlateBeActive(BasisRemoteNamePlate plate)
        {
            if (!NamePlateEnabled) return false;
            if (!plate.IsVisible) return false;
            if (plate.BasisRemotePlayer != null && plate.BasisRemotePlayer.IsEffectivelyBlocked) return false;
            if (NamePlateMenuOnly && BasisMainMenu.Instance == null) return false;
            return true;
        }

        /// <summary>
        /// Updates gameObject.SetActive on all plates based on current visibility state.
        /// Call only on state transitions, not every frame.
        /// </summary>
        private static void SetAllPlateVisibility()
        {
            var arr = plates;
            int n = count;
            for (int i = 0; i < n; i++)
            {
                var plate = arr[i];
                if (plate != null)
                    plate.RefreshActiveState();
            }
        }

        /// <summary>
        /// Called by SettingsProviderNamePlate when nameplate settings change.
        /// Re-reads settings and applies size and transparency to all active plates.
        /// </summary>
        public static void ApplyNamePlateSettingsFromUI()
        {
            bool enabled = BasisSettingsDefaults.NPEnabled.RawValue;
            bool menuOnly = BasisSettingsDefaults.NPMenuOnly.RawValue;
            bool hoverMenuOnly = BasisSettingsDefaults.NPHoverMenuOnly.RawValue;
            float newSize = BasisSettingsDefaults.NPSize.RawValue;
            float newTransparency = BasisSettingsDefaults.NPTransparency.RawValue;

            NamePlateEnabled = enabled;
            NamePlateMenuOnly = menuOnly;
            NamePlateHoverMenuOnly = hoverMenuOnly;
            NamePlateSize = newSize;
            NamePlateTransparency = newTransparency;
            ChatSize = BasisSettingsDefaults.ChatSize.RawValue;

            UpdateCachedColors(newTransparency);

            FlushPendingStructuralChanges();

            Vector3 scale = new Vector3(0.02f, 0.02f, 0.02f) * newSize;
            var arr = plates;
            int n = count;
            for (int i = 0; i < n; i++)
            {
                var plate = arr[i];
                if (plate == null) continue;

                if (plate.Self != null)
                {
                    plate.Self.localScale = scale;
                }

                plate.ApplyTalkModeColors();
                plate.RefreshChatLayout();
            }

            SetAllPlateVisibility();
            BasisCameraNamePlateDriver.ApplyNamePlateSettingsFromUI();
        }

        // ===========================
        // Text bake path
        // ===========================

        private struct BakeRequest
        {
            public BasisRemotePlayer player;
            public BasisRemoteNamePlate plate;
        }

        private static readonly Queue<BakeRequest> bakeQueue = new(64);
        public static int MaxBakesPerFrame = 2;
        public static float MaxPlateHalfWidth = 40f;
        private const float BakeFontSize = 72f;

        private static readonly Matrix4x4 FlipX = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));

        public static void QueueTextBake(BasisRemotePlayer remotePlayer, BasisRemoteNamePlate namePlate)
        {
            if (remotePlayer == null || namePlate == null) return;
            bakeQueue.Enqueue(new BakeRequest { player = remotePlayer, plate = namePlate });
        }

        private static void ProcessBakeQueue()
        {
            if (Text == null) return;

            int budget = MaxBakesPerFrame;
            while (budget > 0 && bakeQueue.Count > 0)
            {
                BakeRequest req = bakeQueue.Dequeue();
                if (req.plate == null || req.player == null) continue;
                GenerateTextFactory(req.player, req.plate);
                budget--;
            }
        }

        public static void GenerateTextFactory(BasisRemotePlayer remotePlayer, BasisRemoteNamePlate namePlate)
        {
            GenerateTextFactory(remotePlayer.DisplayName, namePlate.Filter, namePlate.Renderer, "CombinedNameplateMesh");
        }

        public static void GenerateTextFactory(string displayName, MeshFilter filter, MeshRenderer renderer, string meshName)
        {
            if (Text == null || filter == null || renderer == null) return;

            Text.gameObject.SetActive(true);
            Text.fontSize = BakeFontSize;
            Text.text = displayName;
            Text.ForceMeshUpdate();

            const float horizontalPadding = 2f;
            Vector2 textSize = Text.GetRenderedValues(true);
            float halfWidth = (textSize.x * 0.5f) + horizontalPadding;

            float textScale = 1f;
            if (halfWidth > MaxPlateHalfWidth && textSize.x > 0.001f)
            {
                float maxTextWidth = (MaxPlateHalfWidth - horizontalPadding) * 2f;
                textScale = maxTextWidth / textSize.x;
                halfWidth = MaxPlateHalfWidth;
            }

            Matrix4x4 textTransform = textScale == 1f
                ? FlipX
                : Matrix4x4.Scale(new Vector3(-textScale, textScale, 1f));

            Mesh plateMesh = GenerateRoundedQuad(halfWidth, 4.5f, "Rounded NamePlate Quad");

            var textInfo = Text.textInfo;
            int subMeshLimit = 0;
            int textPartCount = 0;
            if (textInfo != null && textInfo.meshInfo != null)
            {
                subMeshLimit = math.min(textInfo.materialCount, textInfo.meshInfo.Length);
                for (int i = 0; i < subMeshLimit; i++)
                {
                    if (textInfo.meshInfo[i].vertexCount > 0)
                        textPartCount++;
                }
            }

            int totalParts = 1 + textPartCount;
            var combine = new CombineInstance[totalParts];
            var materials = new Material[totalParts];

            combine[0] = new CombineInstance { mesh = plateMesh, transform = Matrix4x4.identity };
            materials[0] = SelectedNamePlateMaterial;

            int writeIdx = 1;
            for (int i = 0; i < subMeshLimit; i++)
            {
                var info = textInfo.meshInfo[i];
                if (info.vertexCount == 0 || info.mesh == null) continue;

                combine[writeIdx] = new CombineInstance { mesh = info.mesh, transform = textTransform };
                materials[writeIdx] = info.material;
                writeIdx++;
            }

            Mesh combinedMesh = new Mesh { name = meshName };
            combinedMesh.CombineMeshes(combine, false);

            filter.sharedMesh = combinedMesh;
            renderer.sharedMaterials = materials;

            Object.Destroy(plateMesh);
            Text.gameObject.SetActive(false);
        }

        /// <summary>
        /// Generates a rounded quad mesh using precomputed trig tables, triangle
        /// indices, and normals. Only vertices and UVs depend on dimensions.
        /// Used for both nameplate backgrounds and chat bubbles.
        /// </summary>
        public static Mesh GenerateRoundedQuad(float halfWidth, float halfHeight, string meshName)
        {
            float width = halfWidth * 2f;
            float height = halfHeight * 2f;

            float maxRadius = Mathf.Min(halfWidth, halfHeight);
            float radius = Mathf.Clamp01(RoundEdges) * maxRadius;

            Vector2 uvOffset = new Vector2(0.5f, 0.5f);
            Vector2 uvScale = new Vector2(1f / width, 1f / height);

            workVertices[0] = new Vector3(0, 0, zOffset);
            workUVs[0] = uvOffset;

            for (int ci = 0; ci < cachedCornerCount; ci++)
            {
                float sin = sinTable[ci];
                float cos = cosTable[ci];

                float oneMinusCos = 1f - cos;
                float oneMinusSin = 1f - sin;

                Vector2 tl = new Vector2(-halfWidth + oneMinusCos * radius, halfHeight - oneMinusSin * radius);
                Vector2 tr = new Vector2(halfWidth - oneMinusSin * radius, halfHeight - oneMinusCos * radius);
                Vector2 br = new Vector2(halfWidth - oneMinusCos * radius, -halfHeight + oneMinusSin * radius);
                Vector2 bl = new Vector2(-halfWidth + oneMinusSin * radius, -halfHeight + oneMinusCos * radius);

                int idx1 = 1 + ci;
                int idx2 = idx1 + cachedCornerCount;
                int idx3 = idx2 + cachedCornerCount;
                int idx4 = idx3 + cachedCornerCount;

                workVertices[idx1] = new Vector3(tl.x, tl.y, zOffset);
                workVertices[idx2] = new Vector3(tr.x, tr.y, zOffset);
                workVertices[idx3] = new Vector3(br.x, br.y, zOffset);
                workVertices[idx4] = new Vector3(bl.x, bl.y, zOffset);

                workUVs[idx1] = tl * uvScale + uvOffset;
                workUVs[idx2] = tr * uvScale + uvOffset;
                workUVs[idx3] = br * uvScale + uvOffset;
                workUVs[idx4] = bl * uvScale + uvOffset;
            }

            return new Mesh
            {
                name = meshName,
                vertices = workVertices,
                normals = workNormals,
                uv = workUVs,
                triangles = cachedTriangles
            };
        }

        // ===========================
        // Chat bubble mesh generation
        // ===========================

        /// <summary>
        /// Generates a rounded quad background mesh for the chat bubble,
        /// sized to fit the current chat text.
        /// </summary>
        public static void GenerateChatBubble(BasisRemoteNamePlate namePlate)
        {
            if (namePlate.ChatBubbleFilter == null) return;

            TextMeshPro bubbleText = namePlate.GetBubbleSourceText();
            if (bubbleText == null) return;

            bubbleText.ForceMeshUpdate();
            Vector2 textSize = bubbleText.GetRenderedValues(true);

            float padding = 2f;
            float halfWidth = Mathf.Max((textSize.x / 2f) + padding, 6f);
            float halfHeight = Mathf.Max((textSize.y / 2f) + padding, 3f);

            if (namePlate.ChatBubbleFilter.sharedMesh != null)
            {
                Object.Destroy(namePlate.ChatBubbleFilter.sharedMesh);
            }
            namePlate.ChatBubbleFilter.sharedMesh = GenerateRoundedQuad(halfWidth, halfHeight, "Chat Bubble Quad");

            if (namePlate.ChatBubbleRenderer.sharedMaterial == null)
            {
                namePlate.ChatBubbleRenderer.material = SelectedNamePlateMaterial;
            }

            float chatScale = NamePlateSize > 0.0001f ? (ChatSize / NamePlateSize) : ChatSize;
            float localY = ChatNameClearance + ChatBubbleGap + (halfHeight * chatScale);
            ApplyChatObjectTransform(namePlate.ChatText.transform, chatScale, localY);
            ApplyChatObjectTransform(namePlate.ChatBubbleFilter.transform, chatScale, localY);
        }

        private static void ApplyChatObjectTransform(Transform t, float scale, float localY)
        {
            t.localScale = new Vector3(scale, scale, scale);
            Vector3 p = t.localPosition;
            p.y = localY;
            t.localPosition = p;
        }

        // =========================================================
        // Optimized job system (double-buffered + safe structural changes)
        // =========================================================

        // Manually-managed array (not List<T>) so the per-frame gather/apply loops
        // index a plain T[] instead of going through List<T>.this[]'s bounds-check
        // and indirection — that overhead showed up in the profiler.
        // `count` (declared below) is the live element count, maintained eagerly
        // by ApplyPendingStructuralChanges.
        private static BasisRemoteNamePlate[] plates = new BasisRemoteNamePlate[256];
        private static readonly Dictionary<BasisRemoteNamePlate, int> indexOf = new(256);

        private static readonly List<BasisRemoteNamePlate> pendingAdd = new(64);
        private static readonly List<BasisRemoteNamePlate> pendingRemove = new(64);

        // Double-buffer inputs so we don't need Complete() at the start.
        private static NativeArray<PlateInput> inputA;
        private static NativeArray<PlateInput> inputB;

        // Double-buffer outputs (optional but keeps everything symmetric)
        private static NativeArray<PlateOutput> outputA;
        private static NativeArray<PlateOutput> outputB;

        private static int writeBuffer; // 0 => A, 1 => B
        private static bool allocated;
        private static int capacity;

        public static JobHandle handle;
        public static int count;
        private static bool jobScheduled;

        public static void Register(BasisRemoteNamePlate p)
        {
            if (p == null) return;
            pendingAdd.Add(p);
        }

        public static void Unregister(BasisRemoteNamePlate p)
        {
            if (p == null) return;
            pendingRemove.Add(p);
        }

        public static void Dispose()
        {
            if (jobScheduled)
            {
                handle.Complete();
                jobScheduled = false;
            }

            DisposeArrays();

            System.Array.Clear(plates, 0, count);
            indexOf.Clear();
            pendingAdd.Clear();
            pendingRemove.Clear();
            bakeQueue.Clear();

            allocated = false;
            capacity = 0;
            count = 0;
            _initialized = false;
        }

        private static void DisposeArrays()
        {
            if (!allocated) return;

            if (inputA.IsCreated) inputA.Dispose();
            if (inputB.IsCreated) inputB.Dispose();
            if (outputA.IsCreated) outputA.Dispose();
            if (outputB.IsCreated) outputB.Dispose();
        }

        /// <summary>
        /// Call in Update. Uses cached NormalColorFloat4 to avoid per-frame conversion.
        /// </summary>
        public static void ScheduleSimulate(double now)
        {
            ProcessBakeQueue();

            if (!ShouldRunJobs())
            {
                return;
            }

            ScheduleSimulate(now, returnDelay, transitionDuration, NormalColorFloat4);
        }

        private static bool ShouldRunJobs()
        {
            if (!NamePlateEnabled) return false;
            if (NamePlateMenuOnly && BasisMainMenu.Instance == null) return false;
            return true;
        }

        public static void ScheduleSimulate(double now, float hold, float fade, float4 normalColor)
        {
            // If a job is still running, don't stomp buffers.
            if (jobScheduled && !handle.IsCompleted)
            {
                return;
            }

            // If it finished, complete it now so we can apply structural changes/resizes safely.
            if (jobScheduled)
            {
                handle.Complete();
                jobScheduled = false;
            }

            if (pendingRemove.Count > 0 || pendingAdd.Count > 0)
            {
                ApplyPendingStructuralChanges();
            }

            if (count == 0)
            {
                return;
            }

            EnsureCapacity(count);

            // Flip buffers (write into one, job reads that one, apply reads outputs of that one)
            writeBuffer ^= 1;

            var inBuf = (writeBuffer == 0) ? inputA : inputB;
            var outBuf = (writeBuffer == 0) ? outputA : outputB;

            // Gather inputs via unsafe pointers to bypass NativeArray safety checks.
            // `plates` is a plain T[] (not List<T>) so indexing skips the List indexer overhead.
            var arr = plates;
            unsafe
            {
                PlateInput* pIn = (PlateInput*)inBuf.GetUnsafePtr();

                for (int i = 0; i < count; i++)
                {
                    var p = arr[i];
                    bool pulsing = p.GetIsPulsingForJob();

                    // Mid-pulse audibility recheck: if the player became inaudible
                    // (mute, block, out-of-range, audio source unloaded, etc.) while
                    // a pulse was in flight, snap the plate back to normal now
                    // instead of letting the 0.7s hold+fade finish the transition.
                    if (pulsing && !p.CanCurrentlyBeHeard())
                    {
                        float4 rc = p.GetRestingColorFloat4ForJob();
                        p.ApplyColorFromJob(new Color(rc.x, rc.y, rc.z, rc.w));
                        p.StopPulseFromJob();
                        pulsing = false;
                    }

                    var input = new PlateInput { isVisible = (ushort)p.IsVisibleRaw };
                    input.restingColor = p.GetRestingColorFloat4ForJob();
                    if (pulsing)
                    {
                        input.isPulsing = 1;
                        input.startTime = p.GetTalkStartTimeForJob();
                        input.talkColor = p.GetTalkColorFloat4ForJob();
                    }
                    pIn[i] = input;
                }
            }

            var job = new NamePlatePulseJob
            {
                now = now,
                hold = hold,
                fade = fade,
                normalColor = normalColor,
                inputs = inBuf,
                outputs = outBuf
            };

            // For small counts, run inline — job system scheduling overhead
            // exceeds the trivial per-item computation (branch + lerp).
            if (count <= 64)
            {
                job.Run(count);
                handle = default;
            }
            else
            {
                handle = job.Schedule(count, 64);
            }
            jobScheduled = true;
        }

        /// <summary>
        /// Call in LateUpdate (or end-of-frame). This is where we sync once.
        /// </summary>
        public static void CompleteNamePlates()
        {
            // Detect menu open/close transitions for menu-only mode
            if (NamePlateMenuOnly && NamePlateEnabled)
            {
                bool menuOpen = BasisMainMenu.Instance != null;
                if (menuOpen != lastMenuOpenState)
                {
                    lastMenuOpenState = menuOpen;
                    SetAllPlateVisibility();
                }
            }

            if (!jobScheduled || count == 0)
                return;

            handle.Complete();
            jobScheduled = false;

            var outBuf = (writeBuffer == 0) ? outputA : outputB;
            var arr = plates;

            unsafe
            {
                PlateOutput* pOut = (PlateOutput*)outBuf.GetUnsafeReadOnlyPtr();

                for (int i = 0; i < count; i++)
                {
                    var p = arr[i];
                    PlateOutput o = pOut[i];

                    if (o.stopPulsing != 0)
                        p.StopPulseFromJob();

                    if (o.hasChange != 0)
                    {
                        float4 c = o.color;
                        p.ApplyColorFromJob(new Color(c.x, c.y, c.z, c.w));
                    }

                    p.UpdateChatTimeout();
                    p.RefreshTypingIndicatorAnimation();
                }
            }
        }

        /// <summary>
        /// Completes any in-flight pulse job and flushes queued plate add/removes into
        /// the live array. Safe to call off the per-frame path (e.g. on settings changes).
        /// </summary>
        private static void FlushPendingStructuralChanges()
        {
            if (jobScheduled)
            {
                handle.Complete();
                jobScheduled = false;
            }

            if (pendingAdd.Count > 0 || pendingRemove.Count > 0)
            {
                ApplyPendingStructuralChanges();
            }
        }

        private static void ApplyPendingStructuralChanges()
        {
            // Remove first (swap-back)
            for (int r = 0; r < pendingRemove.Count; r++)
            {
                var p = pendingRemove[r];
                if (p == null) continue;
                if (!indexOf.TryGetValue(p, out int idx)) continue;

                int last = count - 1;
                var lastPlate = plates[last];

                plates[idx] = lastPlate;
                plates[last] = null; // null out the now-unused tail slot so we don't pin a ref
                count = last;

                if (!ReferenceEquals(lastPlate, p))
                    indexOf[lastPlate] = idx;
                indexOf.Remove(p);
            }
            pendingRemove.Clear();

            // Add — grow backing array if needed
            int adds = pendingAdd.Count;
            if (adds > 0)
            {
                int needed = count + adds;
                if (needed > plates.Length)
                {
                    int newCap = math.max(plates.Length * 2, math.ceilpow2(needed));
                    var grown = new BasisRemoteNamePlate[newCap];
                    System.Array.Copy(plates, grown, count);
                    plates = grown;
                }

                for (int a = 0; a < adds; a++)
                {
                    var p = pendingAdd[a];
                    if (p == null) continue;
                    if (indexOf.ContainsKey(p)) continue;

                    plates[count] = p;
                    indexOf[p] = count;
                    count++;
                }
            }
            pendingAdd.Clear();
        }

        private static void EnsureCapacity(int countNeeded)
        {
            if (allocated && capacity >= countNeeded)
                return;

            int newCap = math.max(64, math.ceilpow2(countNeeded));

            var newInA = new NativeArray<PlateInput>(newCap, Allocator.Persistent);
            var newInB = new NativeArray<PlateInput>(newCap, Allocator.Persistent);
            var newOutA = new NativeArray<PlateOutput>(newCap, Allocator.Persistent);
            var newOutB = new NativeArray<PlateOutput>(newCap, Allocator.Persistent);

            if (allocated)
            {
                int copy = math.min(capacity, newCap);
                NativeArray<PlateInput>.Copy(inputA, newInA, copy);
                NativeArray<PlateInput>.Copy(inputB, newInB, copy);
                NativeArray<PlateOutput>.Copy(outputA, newOutA, copy);
                NativeArray<PlateOutput>.Copy(outputB, newOutB, copy);

                DisposeArrays();
            }

            inputA = newInA;
            inputB = newInB;
            outputA = newOutA;
            outputB = newOutB;

            capacity = newCap;
            allocated = true;
        }

        public struct PlateInput
        {
            public ushort isPulsing; // 0/1
            public ushort isVisible; // 0/1
            public double startTime;
            public float4 talkColor;
            public float4 restingColor;
        }

        public struct PlateOutput
        {
            public float4 color;
            public ushort hasChange;   // 0/1
            public ushort stopPulsing; // 0/1
        }

        [BurstCompile]
        public struct NamePlatePulseJob : IJobParallelFor
        {
            public double now;
            public float hold;
            public float fade;

            public float4 normalColor;

            [ReadOnly] public NativeArray<PlateInput> inputs;
            public NativeArray<PlateOutput> outputs;

            public void Execute(int i)
            {
                // Fast clear
                var o = new PlateOutput
                {
                    hasChange = 0,
                    stopPulsing = 0,
                    color = 0
                };

                var st = inputs[i];

                if (st.isPulsing == 0)
                {
                    outputs[i] = o;
                    return;
                }

                if (st.isVisible == 0)
                {
                    o.stopPulsing = 1;
                    outputs[i] = o;
                    return;
                }

                double elapsed = now - st.startTime;

                if (elapsed < hold)
                {
                    outputs[i] = o;
                    return;
                }

                float t = (float)((elapsed - hold) / fade);

                if (t >= 1f)
                {
                    o.color = st.restingColor;
                    o.hasChange = 1;
                    o.stopPulsing = 1;
                    outputs[i] = o;
                    return;
                }

                t = math.saturate(t);
                o.color = math.lerp(st.talkColor, st.restingColor, t);
                o.hasChange = 1;
                outputs[i] = o;
            }
        }
    }
}
