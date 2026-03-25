using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.UI.NamePlate;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Basis.Scripts.UI
{
    /// <summary>
    /// Displays join/leave notifications below the microphone icon on the player's HUD.
    /// Uses TextMeshPro + MeshFilter/MeshRenderer with a generated rounded-corner quad.
    /// Parented under BasisLocalCameraDriver.ParentOfUI for VR and desktop.
    ///
    /// Fully static – driven by BasisEventDriver.LateUpdate via Simulate().
    /// Object pool pre-allocates MaxMessages slots, reuses GameObjects.
    /// Mesh cache avoids regenerating identical rounded quads.
    /// </summary>
    public static class BasisJoinLeaveNotification
    {
        public static float MessageDuration = 5f;
        public static int MaxMessages = 5;
        public static float FadeStartTime = 3.5f;
        public static float FontSize = 28f;
        public static float FontSizeMin = 14f;
        public static float FontSizeMax = 28f;
        public static float BackgroundPadding = 4f;
        public static float MinHalfWidth = 10f;
        public static float MinHalfHeight = 1.5f;
        public static float LineSpacing = 4f;
        public static float TextRectWidth = 58f;
        public static float TextRectHeight = 10f;
        public static float RoundEdges = 0.5f;
        public static int CornerVertexCount = 8;
        public static float ZOffset = 0.06f;
        public static Vector3 LocalPosition = new Vector3(0f, -4f, 0f);
        public static Color BackgroundColor = new Color(0.1f, 0.1f, 0.1f, 1f);

        private static readonly List<NotificationSlot> activeSlots = new List<NotificationSlot>();
        private static readonly Stack<NotificationSlot> pool = new Stack<NotificationSlot>();
        private static readonly Dictionary<long, Mesh> meshCache = new Dictionary<long, Mesh>();
        private static Transform root;
        private static bool initialized;
        private static MaterialPropertyBlock mpb;
        private static Material cachedMaterial;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private class NotificationSlot
        {
            public GameObject Root;
            public GameObject BgObj;
            public GameObject TextObj;
            public TextMeshPro Text;
            public MeshRenderer BgRenderer;
            public MeshFilter BgFilter;
            public Color TextColor;
            public double SpawnTime;
        }

        public static void Create()
        {
            if (initialized)
            {
                return;
            }

            GameObject go = new GameObject("BasisJoinLeaveNotification");
            Object.DontDestroyOnLoad(go);
            root = go.transform;
            mpb = new MaterialPropertyBlock();
            initialized = true;

            PrewarmPool();

            BasisNetworkPlayer.OnRemotePlayerJoined += OnRemotePlayerJoined;
            BasisNetworkPlayer.OnRemotePlayerLeft += OnRemotePlayerLeft;

            if (BasisLocalCameraDriver.HasInstance)
            {
                AttachToCamera();
            }
            BasisLocalCameraDriver.InstanceExists += AttachToCamera;
        }

        public static void Shutdown()
        {
            if (!initialized)
            {
                return;
            }

            BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayerJoined;
            BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemotePlayerLeft;
            BasisLocalCameraDriver.InstanceExists -= AttachToCamera;

            activeSlots.Clear();
            pool.Clear();

            foreach (Mesh mesh in meshCache.Values)
            {
                Object.Destroy(mesh);
            }
            meshCache.Clear();

            if (root != null)
            {
                Object.Destroy(root.gameObject);
                root = null;
            }

            cachedMaterial = null;
            mpb = null;
            initialized = false;
        }

        /// <summary>
        /// Per-frame update driven by BasisEventDriver.LateUpdate.
        /// Handles fade-out and expiry of active notifications.
        /// </summary>
        public static void Simulate(double now)
        {
            if (!initialized || activeSlots.Count == 0)
            {
                return;
            }

            bool removed = false;
            float fadeDurationInv = 1f / (MessageDuration - FadeStartTime);

            for (int i = activeSlots.Count - 1; i >= 0; i--)
            {
                NotificationSlot slot = activeSlots[i];
                double elapsed = now - slot.SpawnTime;

                if (elapsed >= MessageDuration)
                {
                    ReleaseSlot(slot);
                    activeSlots.RemoveAt(i);
                    removed = true;
                    continue;
                }

                if (elapsed >= FadeStartTime)
                {
                    float alpha = 1f - (float)(elapsed - FadeStartTime) * fadeDurationInv;

                    Color tc = slot.TextColor;
                    tc.a = alpha;
                    slot.Text.color = tc;

                    Color bg = BackgroundColor;
                    bg.a = alpha;
                    slot.BgRenderer.GetPropertyBlock(mpb, 0);
                    mpb.SetColor(BaseColorId, bg);
                    slot.BgRenderer.SetPropertyBlock(mpb, 0);
                }
            }

            if (removed)
            {
                RepositionAll();
            }
        }

        private static void AttachToCamera()
        {
            if (root == null || BasisLocalCameraDriver.Instance == null)
            {
                return;
            }

            root.SetParent(BasisLocalCameraDriver.Instance.ParentOfUI, false);
            root.SetLocalPositionAndRotation(LocalPosition, Quaternion.identity);
            root.localScale = Vector3.one;
        }

        private static void CacheMaterial()
        {
            if (cachedMaterial != null)
            {
                return;
            }
            if (BasisRemoteNamePlateDriver.Instance != null)
            {
                cachedMaterial = BasisRemoteNamePlateDriver.Instance.SelectedNamePlateMaterial;
            }
        }

        private static void PrewarmPool()
        {
            for (int i = 0; i < MaxMessages; i++)
            {
                pool.Push(CreateSlot());
            }
        }

        private static NotificationSlot CreateSlot()
        {
            NotificationSlot slot = new NotificationSlot();

            slot.Root = new GameObject("Notification");
            slot.Root.transform.SetParent(root, false);

            slot.BgObj = new GameObject("Background");
            slot.BgObj.transform.SetParent(slot.Root.transform, false);
            slot.BgObj.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.Euler(0, 180, 0));
            slot.BgObj.transform.localScale = Vector3.one;

            slot.BgFilter = slot.BgObj.AddComponent<MeshFilter>();
            slot.BgRenderer = slot.BgObj.AddComponent<MeshRenderer>();
            slot.BgRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            slot.BgRenderer.receiveShadows = false;
            slot.BgRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

            slot.TextObj = new GameObject("Text");
            slot.TextObj.transform.SetParent(slot.Root.transform, false);
            slot.TextObj.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            slot.TextObj.transform.localScale = Vector3.one;

            slot.Text = slot.TextObj.AddComponent<TextMeshPro>();
            slot.Text.fontSize = FontSize;
            slot.Text.enableAutoSizing = true;
            slot.Text.fontSizeMin = FontSizeMin;
            slot.Text.fontSizeMax = FontSizeMax;
            slot.Text.alignment = TextAlignmentOptions.Center;
            slot.Text.textWrappingMode = TextWrappingModes.Normal;
            slot.Text.overflowMode = TextOverflowModes.Truncate;
            slot.Text.sortingOrder = 1;

            if (slot.Text.TryGetComponent<RectTransform>(out RectTransform textRect))
            {
                textRect.sizeDelta = new Vector2(TextRectWidth, TextRectHeight);
            }

            slot.Root.SetActive(false);
            return slot;
        }

        private static NotificationSlot AcquireSlot()
        {
            if (pool.Count > 0)
            {
                return pool.Pop();
            }
            return CreateSlot();
        }

        private static void ReleaseSlot(NotificationSlot slot)
        {
            slot.Root.SetActive(false);
            slot.BgRenderer.GetPropertyBlock(mpb, 0);
            mpb.SetColor(BaseColorId, BackgroundColor);
            slot.BgRenderer.SetPropertyBlock(mpb, 0);
            pool.Push(slot);
        }

        private static void OnRemotePlayerJoined(BasisNetworkPlayer networkPlayer, BasisRemotePlayer remotePlayer)
        {
            if (!BasisSettingsDefaults.JoinNotifications.RawValue)
                return;

            string name = networkPlayer.displayName;
            if (string.IsNullOrEmpty(name))
            {
                name = "Unknown";
            }

            ShowNotification(name + " joined", Color.white);
        }

        private static void OnRemotePlayerLeft(BasisNetworkPlayer networkPlayer, BasisRemotePlayer remotePlayer)
        {
            if (!BasisSettingsDefaults.LeaveNotifications.RawValue)
                return;

            string name = networkPlayer.displayName;
            if (string.IsNullOrEmpty(name))
            {
                name = "Unknown";
            }

            ShowNotification(name + " left", Color.white);
        }

        private static void ShowNotification(string message, Color color)
        {
            if (!initialized)
            {
                return;
            }

            while (activeSlots.Count >= MaxMessages)
            {
                ReleaseSlot(activeSlots[0]);
                activeSlots.RemoveAt(0);
            }

            CacheMaterial();

            NotificationSlot slot = AcquireSlot();

            if (cachedMaterial != null && slot.BgRenderer.sharedMaterial != cachedMaterial)
            {
                slot.BgRenderer.sharedMaterial = cachedMaterial;
            }

            slot.Text.text = message;
            slot.Text.color = color;
            slot.TextColor = color;
            slot.SpawnTime = Time.timeAsDouble;

            slot.Text.ForceMeshUpdate();
            Vector2 textSize = slot.Text.GetRenderedValues(true);

            float halfWidth = (textSize.x / 2f) + BackgroundPadding;
            float halfHeight = (textSize.y / 2f) + BackgroundPadding * 0.5f;
            halfWidth = Mathf.Max(halfWidth, MinHalfWidth);
            halfHeight = Mathf.Max(halfHeight, MinHalfHeight);

            slot.BgFilter.sharedMesh = GetOrCreateMesh(halfWidth, halfHeight);
            slot.BgRenderer.GetPropertyBlock(mpb, 0);
            mpb.SetColor(BaseColorId, BackgroundColor);
            slot.BgRenderer.SetPropertyBlock(mpb, 0);
            slot.Root.SetActive(true);

            activeSlots.Add(slot);
            RepositionAll();
        }

        private static Mesh GetOrCreateMesh(float halfWidth, float halfHeight)
        {
            int qw = Mathf.CeilToInt(halfWidth * 2f);
            int qh = Mathf.CeilToInt(halfHeight * 2f);
            long key = ((long)qw << 32) | (uint)qh;

            if (meshCache.TryGetValue(key, out Mesh cached))
            {
                return cached;
            }

            float actualHW = qw * 0.5f;
            float actualHH = qh * 0.5f;
            Mesh mesh = GenerateRoundedQuad(actualHW, actualHH);
            meshCache[key] = mesh;
            return mesh;
        }

        private static Mesh GenerateRoundedQuad(float halfWidth, float halfHeight)
        {
            int cornerCount = Mathf.Max(3, CornerVertexCount);
            int ringVertexCount = cornerCount * 4;
            int vertexCount = ringVertexCount + 1;

            Vector3[] v = new Vector3[vertexCount];
            Vector3[] n = new Vector3[vertexCount];
            Vector2[] uv = new Vector2[vertexCount];
            int[] t = new int[ringVertexCount * 3];

            float width = halfWidth * 2f;
            float height = halfHeight * 2f;

            float maxRadius = Mathf.Min(halfWidth, halfHeight);
            float radius = Mathf.Clamp01(RoundEdges) * maxRadius;

            float angleStep = Mathf.PI * 0.5f / (cornerCount - 1);
            Vector2 uvOff = new Vector2(0.5f, 0.5f);
            Vector2 uvScale = new Vector2(1f / width, 1f / height);

            v[0] = new Vector3(0, 0, ZOffset);
            uv[0] = uvOff;
            n[0] = Vector3.forward;

            for (int ci = 0; ci < cornerCount; ci++)
            {
                float angle = ci * angleStep;
                float sin = Mathf.Sin(angle);
                float cos = Mathf.Cos(angle);

                Vector2 tl = new Vector2(-halfWidth + (1f - cos) * radius, halfHeight - (1f - sin) * radius);
                Vector2 tr = new Vector2(halfWidth - (1f - sin) * radius, halfHeight - (1f - cos) * radius);
                Vector2 br = new Vector2(halfWidth - (1f - cos) * radius, -halfHeight + (1f - sin) * radius);
                Vector2 bl = new Vector2(-halfWidth + (1f - sin) * radius, -halfHeight + (1f - cos) * radius);

                int b = 1 + ci;
                v[b] = new Vector3(tl.x, tl.y, ZOffset);
                v[b + cornerCount] = new Vector3(tr.x, tr.y, ZOffset);
                v[b + cornerCount * 2] = new Vector3(br.x, br.y, ZOffset);
                v[b + cornerCount * 3] = new Vector3(bl.x, bl.y, ZOffset);

                uv[b] = tl * uvScale + uvOff;
                uv[b + cornerCount] = tr * uvScale + uvOff;
                uv[b + cornerCount * 2] = br * uvScale + uvOff;
                uv[b + cornerCount * 3] = bl * uvScale + uvOff;

                n[b] = Vector3.forward;
                n[b + cornerCount] = Vector3.forward;
                n[b + cornerCount * 2] = Vector3.forward;
                n[b + cornerCount * 3] = Vector3.forward;
            }

            for (int i = 0; i < ringVertexCount; i++)
            {
                int tri = i * 3;
                t[tri] = 0;
                t[tri + 1] = 1 + ((i + 1) % ringVertexCount);
                t[tri + 2] = 1 + i;
            }

            return new Mesh
            {
                name = "Notification Quad",
                vertices = v,
                normals = n,
                uv = uv,
                triangles = t
            };
        }

        private static void RepositionAll()
        {
            float y = 0f;
            for (int i = activeSlots.Count - 1; i >= 0; i--)
            {
                activeSlots[i].Root.transform.localPosition = new Vector3(0f, y, 0f);
                y -= LineSpacing;
            }
        }
    }
}
