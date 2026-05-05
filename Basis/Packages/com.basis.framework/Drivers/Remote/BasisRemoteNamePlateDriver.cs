using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using System.Collections.Generic;
using TMPro;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.UI.NamePlate
{
    public class BasisRemoteNamePlateDriver : MonoBehaviour
    {
        public static BasisRemoteNamePlateDriver Instance;

        public Color NormalColor;
        public Color IsTalkingColor;
        public Color OutOfRangeColor;
        public Color FailedLoadColor = new Color(1f, 0.2f, 0.2f, 1f);

        [SerializeField] public static float transitionDuration = 0.3f;
        [SerializeField] public static float returnDelay = 0.4f;

        public static Color StaticNormalColor;
        public static Color StaticIsTalkingColor;
        public static Color StaticOutOfRangeColor;
        public static Color StaticFailedLoadColor;
        public static float4 NormalColorFloat4;

        public TextMeshPro Text;

        public Material TransParentNamePlateMaterial;
        public Material OpaqueNamePlateMaterial;

        [HideInInspector] public Material SelectedNamePlateMaterial;

        [Range(0f, 1f)] public float RoundEdges = 0.5f;
        public int CornerVertexCount = 8;
        public float zOffset = 0.06f;

        public static bool NamePlateEnabled = true;
        public static bool NamePlateMenuOnly = false;
        public static bool NamePlateHoverMenuOnly = false;
        public static float NamePlateSize = 1f;
        public static float NamePlateTransparency = 0.45f;
        private static bool lastMenuOpenState;

        // Precomputed per CornerVertexCount
        private int cachedCornerCount;
        private float[] sinTable;
        private float[] cosTable;
        private int[] cachedTriangles;
        private int cachedRingVertexCount;
        private int cachedVertexCount;

        // Reusable working arrays (avoid per-call managed allocation)
        private Vector3[] workVertices;
        private Vector3[] workNormals;
        private Vector2[] workUVs;

        private static bool _unicodeFallbacksEnsured;

        public void Awake()
        {
            Instance = this;

            SelectedNamePlateMaterial = BasisDeviceManagement.IsMobileHardware()
                ? OpaqueNamePlateMaterial
                : TransParentNamePlateMaterial;

            NamePlateEnabled = BasisSettingsDefaults.NPEnabled.RawValue;
            NamePlateMenuOnly = BasisSettingsDefaults.NPMenuOnly.RawValue;
            NamePlateHoverMenuOnly = BasisSettingsDefaults.NPHoverMenuOnly.RawValue;
            NamePlateSize = BasisSettingsDefaults.NPSize.RawValue;
            NamePlateTransparency = BasisSettingsDefaults.NPTransparency.RawValue;
            lastMenuOpenState = BasisMainMenu.Instance != null;

            UpdateCachedColors(NamePlateTransparency);
            PrecomputeCornerData();
            EnsureUnicodeFallbacksOnNameplateFont();
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
        private void EnsureUnicodeFallbacksOnNameplateFont()
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

        private void UpdateCachedColors(float transparency)
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
            NormalColorFloat4 = new float4(StaticNormalColor.r, StaticNormalColor.g, StaticNormalColor.b, StaticNormalColor.a);
        }

        /// <summary>
        /// Precomputes sin/cos lookup table, triangle indices, normals,
        /// and allocates working arrays. Only needs to run when CornerVertexCount changes.
        /// </summary>
        private void PrecomputeCornerData()
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
        public void ApplyNamePlateSettingsFromUI()
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

            UpdateCachedColors(newTransparency);

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

                plate.ApplyColorFromJob(StaticNormalColor);
            }

            SetAllPlateVisibility();
        }

        // ===========================
        // Text bake path
        // ===========================

        public void GenerateTextFactory(BasisRemotePlayer remotePlayer, BasisRemoteNamePlate namePlate)
        {
            Text.gameObject.SetActive(true);
            Text.text = remotePlayer.DisplayName;
            Text.ForceMeshUpdate();

            // Measure the baked text so the background fits its actual content.
            Vector2 textSize = Text.GetRenderedValues(true);
            const float horizontalPadding = 2f;
            float halfWidth = (textSize.x * 0.5f) + horizontalPadding;

            Mesh plateMesh = GenerateRoundedQuad(halfWidth, 4.5f, "Rounded NamePlate Quad");

            // Walk textInfo.meshInfo for every populated sub-mesh — index 0 is the
            // primary font, indices 1+ are fallback font atlases used when characters
            // (e.g., kanji) aren't present in the primary font. Cloning only Text.mesh
            // would silently drop those fallback glyphs because TMP places them on
            // auto-generated TMP_SubMesh children, not on the main Text mesh.
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

            Mesh primaryFlipped = null;
            int writeIdx = 1;
            for (int i = 0; i < subMeshLimit; i++)
            {
                var info = textInfo.meshInfo[i];
                if (info.vertexCount == 0 || info.mesh == null) continue;

                Mesh subClone = Instantiate(info.mesh);
                FlipMesh(subClone);

                combine[writeIdx] = new CombineInstance { mesh = subClone, transform = Matrix4x4.identity };
                materials[writeIdx] = info.material;
                if (primaryFlipped == null) primaryFlipped = subClone;
                writeIdx++;
            }

            Mesh combinedMesh = new Mesh { name = "CombinedNameplateMesh" };
            combinedMesh.CombineMeshes(combine, false);

            namePlate.bakedMesh = primaryFlipped;
            namePlate.Filter.sharedMesh = combinedMesh;
            namePlate.Renderer.materials = materials;

            Text.gameObject.SetActive(false);
        }

        private static void FlipMesh(Mesh mesh)
        {
            // Mirror vertices along X axis so text reads correctly from the opposite side.
            // Negating X implicitly reverses triangle winding, which makes the mesh
            // face the opposite direction - no explicit winding reversal needed.
            Vector3[] vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i].x = -vertices[i].x;
            }
            mesh.vertices = vertices;

            // Flip normals to match the new facing direction
            Vector3[] normals = mesh.normals;
            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = -normals[i];
            }
            mesh.normals = normals;
        }

        /// <summary>
        /// Generates a rounded quad mesh using precomputed trig tables, triangle
        /// indices, and normals. Only vertices and UVs depend on dimensions.
        /// Used for both nameplate backgrounds and chat bubbles.
        /// </summary>
        public Mesh GenerateRoundedQuad(float halfWidth, float halfHeight, string meshName)
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
        public void GenerateChatBubble(BasisRemoteNamePlate namePlate)
        {
            if (namePlate.ChatText == null || namePlate.ChatBubbleFilter == null) return;

            namePlate.ChatText.ForceMeshUpdate();
            Vector2 textSize = namePlate.ChatText.GetRenderedValues(true);

            float padding = 2f;
            float halfWidth = Mathf.Max((textSize.x / 2f) + padding, 6f);
            float halfHeight = Mathf.Max((textSize.y / 2f) + padding, 3f);

            namePlate.ChatBubbleFilter.sharedMesh = GenerateRoundedQuad(halfWidth, halfHeight, "Chat Bubble Quad");

            if (namePlate.ChatBubbleRenderer.sharedMaterial == null)
            {
                namePlate.ChatBubbleRenderer.material = SelectedNamePlateMaterial;
            }
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

            allocated = false;
            capacity = 0;
            count = 0;
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
            if (!ShouldRunJobs())
                return;

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
                return;

            // If it finished, complete it now so we can apply structural changes/resizes safely.
            if (jobScheduled)
            {
                handle.Complete();
                jobScheduled = false;
            }

            if (pendingRemove.Count > 0 || pendingAdd.Count > 0)
                ApplyPendingStructuralChanges();

            if (count == 0)
                return;

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
                        p.ApplyColorFromJob(StaticNormalColor);
                        p.StopPulseFromJob();
                        pulsing = false;
                    }

                    var input = new PlateInput { isVisible = (ushort)p.IsVisibleRaw };
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
                }
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
                    o.color = normalColor;
                    o.hasChange = 1;
                    o.stopPulsing = 1;
                    outputs[i] = o;
                    return;
                }

                t = math.saturate(t);
                o.color = math.lerp(st.talkColor, normalColor, t);
                o.hasChange = 1;
                outputs[i] = o;
            }
        }
    }
}
