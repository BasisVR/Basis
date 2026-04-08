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

        [SerializeField] public static float transitionDuration = 0.3f;
        [SerializeField] public static float returnDelay = 0.4f;

        public static Color StaticNormalColor;
        public static Color StaticIsTalkingColor;
        public static Color StaticOutOfRangeColor;
        public static float4 NormalColorFloat4;

        public TextMeshPro Text;

        public Material TransParentNamePlateMaterial;
        public Material OpaqueNamePlateMaterial;

        [HideInInspector] public Material SelectedNamePlateMaterial;
        [HideInInspector] public Mesh RoundedCornersMesh;

        [Range(0f, 1f)] public float RoundEdges = 0.5f;
        public int CornerVertexCount = 8;
        public float zOffset = 0.06f;

        public static bool NamePlateEnabled = true;
        public static bool NamePlateMenuOnly = false;
        public static float NamePlateHalfWidth = 30f;
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

        // Reusable combine buffer
        private readonly CombineInstance[] combineBuffer = new CombineInstance[2];

        public void Awake()
        {
            Instance = this;

            SelectedNamePlateMaterial = BasisDeviceManagement.IsMobileHardware()
                ? OpaqueNamePlateMaterial
                : TransParentNamePlateMaterial;

            NamePlateEnabled = BasisSettingsDefaults.NPEnabled.RawValue;
            NamePlateMenuOnly = BasisSettingsDefaults.NPMenuOnly.RawValue;
            NamePlateHalfWidth = BasisSettingsDefaults.NPWidth.RawValue;
            NamePlateSize = BasisSettingsDefaults.NPSize.RawValue;
            NamePlateTransparency = BasisSettingsDefaults.NPTransparency.RawValue;
            lastMenuOpenState = BasisMainMenu.Instance != null;

            UpdateCachedColors(NamePlateTransparency);
            PrecomputeCornerData();
            RoundedCornersMesh = GenerateRoundedQuad(NamePlateHalfWidth, 4.5f, "Rounded NamePlate Quad");
        }

        private void UpdateCachedColors(float transparency)
        {
            StaticNormalColor = new Color(NormalColor.r, NormalColor.g, NormalColor.b, transparency);
            StaticIsTalkingColor = new Color(IsTalkingColor.r, IsTalkingColor.g, IsTalkingColor.b, transparency);
            StaticOutOfRangeColor = new Color(OutOfRangeColor.r, OutOfRangeColor.g, OutOfRangeColor.b, transparency);
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
            if (NamePlateMenuOnly && BasisMainMenu.Instance == null) return false;
            return true;
        }

        /// <summary>
        /// Updates gameObject.SetActive on all plates based on current visibility state.
        /// Call only on state transitions, not every frame.
        /// </summary>
        private static void SetAllPlateVisibility()
        {
            for (int i = 0; i < plates.Count; i++)
            {
                var plate = plates[i];
                if (plate != null)
                    plate.gameObject.SetActive(ShouldPlateBeActive(plate));
            }
        }

        /// <summary>
        /// Called by SettingsProviderNamePlate when nameplate settings change.
        /// Re-reads settings and applies width, size, and transparency to all active plates.
        /// </summary>
        public void ApplyNamePlateSettingsFromUI()
        {
            bool enabled = BasisSettingsDefaults.NPEnabled.RawValue;
            bool menuOnly = BasisSettingsDefaults.NPMenuOnly.RawValue;
            float newWidth = BasisSettingsDefaults.NPWidth.RawValue;
            float newSize = BasisSettingsDefaults.NPSize.RawValue;
            float newTransparency = BasisSettingsDefaults.NPTransparency.RawValue;

            bool meshChanged = !Mathf.Approximately(NamePlateHalfWidth, newWidth);

            NamePlateEnabled = enabled;
            NamePlateMenuOnly = menuOnly;
            NamePlateHalfWidth = newWidth;
            NamePlateSize = newSize;
            NamePlateTransparency = newTransparency;

            UpdateCachedColors(newTransparency);

            if (meshChanged)
            {
                RoundedCornersMesh = GenerateRoundedQuad(NamePlateHalfWidth, 4.5f, "Rounded NamePlate Quad");
            }

            Vector3 scale = new Vector3(0.02f, 0.02f, 0.02f) * newSize;
            for (int i = 0; i < plates.Count; i++)
            {
                var plate = plates[i];
                if (plate == null) continue;

                if (meshChanged && plate.bakedMesh != null)
                {
                    CombinePlateMesh(plate);
                }

                if (plate.Self != null)
                {
                    plate.Self.localScale = scale;
                }

                plate.ApplyColorFromJob(StaticNormalColor);
            }

            SetAllPlateVisibility();
        }

        /// <summary>
        /// Combines RoundedCornersMesh + plate's baked text mesh.
        /// Shared by initial creation and mesh rebuilds.
        /// </summary>
        private void CombinePlateMesh(BasisRemoteNamePlate namePlate, bool setMaterials = false)
        {
            combineBuffer[0] = new CombineInstance { mesh = RoundedCornersMesh, transform = Matrix4x4.identity };
            combineBuffer[1] = new CombineInstance { mesh = namePlate.bakedMesh, transform = Matrix4x4.identity };

            Mesh combinedMesh = new Mesh { name = "CombinedNameplateMesh" };
            combinedMesh.CombineMeshes(combineBuffer, false);

            namePlate.Filter.sharedMesh = combinedMesh;

            if (setMaterials)
            {
                // NOTE: This allocates; ideally cache materials array on plate, but left as-is for compatibility.
                namePlate.Renderer.materials = new Material[]
                {
                    SelectedNamePlateMaterial,
                    namePlate.Renderer.material
                };
            }
        }

        // ===========================
        // Text bake path
        // ===========================

        public void GenerateTextFactory(BasisRemotePlayer remotePlayer, BasisRemoteNamePlate namePlate)
        {
            Text.gameObject.SetActive(true);
            Text.text = remotePlayer.DisplayName;
            Text.ForceMeshUpdate();

            Mesh textMesh = Instantiate(Text.mesh);
            FlipMesh(textMesh);

            namePlate.bakedMesh = textMesh;
            namePlate.Filter.sharedMesh = textMesh;

            CombinePlateMesh(namePlate, setMaterials: true);
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

        private static readonly List<BasisRemoteNamePlate> plates = new(256);
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

            plates.Clear();
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

            count = plates.Count;
            if (count == 0)
                return;

            EnsureCapacity(count);

            // Flip buffers (write into one, job reads that one, apply reads outputs of that one)
            writeBuffer ^= 1;

            var inBuf = (writeBuffer == 0) ? inputA : inputB;
            var outBuf = (writeBuffer == 0) ? outputA : outputB;

            // Gather inputs via unsafe pointers to bypass NativeArray safety checks
            unsafe
            {
                PlateInput* pIn = (PlateInput*)inBuf.GetUnsafePtr();

                for (int i = 0; i < count; i++)
                {
                    var p = plates[i];
                    bool pulsing = p.GetIsPulsingForJob();

                    pIn[i] = new PlateInput
                    {
                        isVisible = (ushort)p.IsVisibleRaw,
                        isPulsing = (ushort)(pulsing ? 1 : 0),
                        startTime = pulsing ? p.GetTalkStartTimeForJob() : 0,
                        talkColor = pulsing ? p.GetTalkColorFloat4ForJob() : default
                    };
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

            unsafe
            {
                PlateOutput* pOut = (PlateOutput*)outBuf.GetUnsafeReadOnlyPtr();

                for (int i = 0; i < count; i++)
                {
                    var p = plates[i];
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

                int last = plates.Count - 1;
                var lastPlate = plates[last];

                plates[idx] = lastPlate;
                plates.RemoveAt(last);

                indexOf[lastPlate] = idx;
                indexOf.Remove(p);
            }
            pendingRemove.Clear();

            // Add
            for (int a = 0; a < pendingAdd.Count; a++)
            {
                var p = pendingAdd[a];
                if (p == null) continue;
                if (indexOf.ContainsKey(p)) continue;

                int idx = plates.Count;
                plates.Add(p);
                indexOf[p] = idx;
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
