using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Runtime debug-gizmo drawing. The Create/Update/Destroy id API is retained so callers
/// can hold onto gizmos across frames, but the backend is batched: spheres and lines are
/// plain data slots submitted once per frame as a handful of draws — instanced meshes for
/// spheres, a single dynamic ribbon mesh for every line — so cost scales with visible
/// geometry instead of GameObject count. Text labels stay TextMeshPro objects (pooled),
/// and only the <see cref="MaxVisibleLabels"/> nearest the viewer render at once, which
/// bounds the TMP re-tessellation cost regardless of how many labels exist.
/// <see cref="Render"/> must run once per frame; SMModuleDebugOptions.Simulate drives it.
/// </summary>
public static class BasisGizmoManager
{
    public static Action<bool> OnUseGizmosChanged; // Callback delegate.
    public static GameObject Parent;

    /// <summary>
    /// Layer the batched sphere/line draws submit on. The calibration mirror relay points
    /// this at LocalPlayerAvatar while its cutout mirror is alive so gizmos show up in the
    /// reflection (whose camera culls to that layer), and restores it on teardown.
    /// </summary>
    public static int RenderLayer = 0;

    /// <summary>
    /// Optional viewer-distance cull for sphere/line gizmos, in meters. Defaults to
    /// unlimited so nothing silently disappears; load-test sessions with hundreds of
    /// players can set a radius to only pay for nearby gizmos.
    /// </summary>
    public static float MaxDrawDistance = float.PositiveInfinity;

    /// <summary>
    /// How many text labels may render at once — the nearest ones to the viewer win.
    /// Labels beyond the cap keep their slot and data but their renderer is disabled,
    /// so far-away (unreadable anyway) labels cost nothing.
    /// </summary>
    public static int MaxVisibleLabels = 32;

    /// <summary>
    /// Material for solid-style sphere gizmos (see <see cref="CreateSolidSphereGizmo"/>) —
    /// a shared asset assigned by the feature that owns the look (the tracker marker balls
    /// point it at the FallbackSphere material). Drawn lit/depth-tested with shadows, unlike
    /// the additive overlay spheres; per-slot color is ignored. Solid slots are skipped while
    /// this is null. Never destroyed by <see cref="DestroyAll"/>.
    /// </summary>
    public static Material SolidSphereMaterial;

    private static int _nextID = 0; // Counter for unique IDs.

    private static int CreateNewID()
    {
        return ++_nextID;
    }

    public static void TryCreateParent()
    {
        if (Parent == null)
        {
            Parent = new GameObject("Parent Of Debug Data");
        }
    }

    public static void DestroyParent()
    {
        if (Parent != null)
        {
            GameObject.Destroy(Parent);
            Parent = null;
        }
    }

    // ── Sphere slots ────────────────────────────────────────────────────────

    private struct SphereSlot
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public Vector4 Color;
        public bool HasRotation;
        public bool Solid;
        public bool Active;
        public bool Used;
    }

    private static SphereSlot[] _spheres = new SphereSlot[64];
    private static int _sphereHighWater;
    private static readonly Stack<int> _sphereFree = new Stack<int>();
    private static readonly Dictionary<int, int> _sphereByID = new Dictionary<int, int>();

    // ── Line slots ──────────────────────────────────────────────────────────

    private class LineSlot
    {
        public Vector3[] Points;
        public int Count;
        public float HalfWidth;
        public Color32 UniformColor;
        public Gradient Gradient;      // null unless SetLineGizmoGradient was used
        public Color32[] PointColors;  // evaluated gradient cache, length == Count
        public bool Loop;
        public bool Active;
    }

    private static readonly Dictionary<int, LineSlot> _linesByID = new Dictionary<int, LineSlot>();

    // ── Text slots ──────────────────────────────────────────────────────────

    private class TextSlot
    {
        public BasisTextGizmos Component;
        public Vector3 Position;   // last requested — ranks nearest-K even while hidden
        public bool Active = true;
        public bool Visible = true;
    }

    private static readonly Dictionary<int, TextSlot> _textByID = new Dictionary<int, TextSlot>();
    private static readonly Stack<BasisTextGizmos> _labelPool = new Stack<BasisTextGizmos>();
    private const int MaxPooledLabels = 64;

    /// <summary>
    /// Creates a new sphere gizmo.
    /// </summary>
    public static bool CreateSphereGizmo(string GizmoName, out int linkedID, Vector3 position, float size, Color color)
    {
        linkedID = CreateNewID();
        int slot = AllocSphereSlot();
        ref SphereSlot s = ref _spheres[slot];
        s.Position = position;
        s.Rotation = Quaternion.identity;
        s.Scale = Vector3.one * size;
        s.Color = color;
        s.HasRotation = false;
        s.Active = true;
        s.Used = true;
        _sphereByID[linkedID] = slot;
        return true;
    }

    /// <summary>
    /// Creates a solid-style sphere gizmo: lit, depth-tested, shadowed, drawn with the
    /// shared <see cref="SolidSphereMaterial"/> (which also supplies the color — there is
    /// no per-slot tint). Used for player-facing visuals like the tracker marker balls
    /// that must sit in the world rather than glow through it. Update/destroy through the
    /// same sphere APIs as overlay spheres.
    /// </summary>
    public static bool CreateSolidSphereGizmo(string GizmoName, out int linkedID, Vector3 position, float size)
    {
        linkedID = CreateNewID();
        int slot = AllocSphereSlot();
        ref SphereSlot s = ref _spheres[slot];
        s.Position = position;
        s.Rotation = Quaternion.identity;
        s.Scale = Vector3.one * size;
        s.Color = UnityEngine.Color.white;
        s.HasRotation = false;
        s.Solid = true;
        s.Active = true;
        s.Used = true;
        _sphereByID[linkedID] = slot;
        return true;
    }

    /// <summary>
    /// Updates an existing sphere gizmo.
    /// </summary>
    public static bool UpdateSphereGizmo(int linkedID, Vector3 position, Vector3 Scale)
    {
        if (!_sphereByID.TryGetValue(linkedID, out int slot))
        {
            BasisDebug.LogError($"No SphereGizmo found with ID {linkedID}. Use CreateSphereGizmo first.", BasisDebug.LogTag.Gizmo);
            return false;
        }
        ref SphereSlot s = ref _spheres[slot];
        s.Position = position;
        s.Scale = Scale;
        return true;
    }

    /// <summary>
    /// Updates an existing sphere gizmo with rotation included. Use this when
    /// the gizmo is anchored to a bone whose orientation matters
    /// (e.g. calibration spheres so they roll with the avatar's bone instead
    /// of staying axis-aligned in world space).
    /// </summary>
    public static bool UpdateSphereGizmo(int linkedID, Vector3 position, Quaternion rotation, Vector3 Scale)
    {
        if (!_sphereByID.TryGetValue(linkedID, out int slot))
        {
            BasisDebug.LogError($"No SphereGizmo found with ID {linkedID}. Use CreateSphereGizmo first.", BasisDebug.LogTag.Gizmo);
            return false;
        }
        ref SphereSlot s = ref _spheres[slot];
        s.Position = position;
        s.Rotation = rotation;
        s.Scale = Scale;
        s.HasRotation = true;
        return true;
    }

    public static bool CreateLineGizmo(string GizmoName, int linkedID, Vector3 start, Vector3 end, float width, Color color, GameObject Reference)
    {
        if (linkedID >= _nextID)
        {
            _nextID = linkedID;
        }
        _linesByID[linkedID] = new LineSlot
        {
            Points = new[] { start, end },
            Count = 2,
            HalfWidth = width * 0.5f,
            UniformColor = color,
            Active = true,
        };
        return true;
    }

    public static bool CreateLineGizmo(string GizmoName, out int linkedID, Vector3 start, Vector3 end, float width, Color color)
    {
        linkedID = CreateNewID();
        _linesByID[linkedID] = new LineSlot
        {
            Points = new[] { start, end },
            Count = 2,
            HalfWidth = width * 0.5f,
            UniformColor = color,
            Active = true,
        };
        return true;
    }

    /// <summary>
    /// Updates an existing line gizmo.
    /// </summary>
    public static bool UpdateLineGizmo(int linkedID, Vector3 start, Vector3 end)
    {
        if (!_linesByID.TryGetValue(linkedID, out LineSlot slot))
        {
            BasisDebug.LogError($"No LineGizmo found with ID {linkedID}. Use CreateLineGizmo first.", BasisDebug.LogTag.Gizmo);
            return false;
        }
        if (slot.Count != 2)
        {
            slot.Points = new Vector3[2];
            slot.Count = 2;
            RefreshGradientColors(slot);
        }
        slot.Points[0] = start;
        slot.Points[1] = end;
        return true;
    }

    /// <summary>
    /// Creates a multi-point line gizmo. Set <paramref name="loop"/> = true to close the
    /// polyline back to its first point — useful for drawing circles or wireframe caps
    /// with a single line gizmo rather than N edge segments.
    /// </summary>
    public static bool CreateLineGizmo(string GizmoName, out int linkedID, Vector3[] positions, float width, Color color, bool loop = false)
    {
        linkedID = CreateNewID();
        _linesByID[linkedID] = new LineSlot
        {
            Points = (Vector3[])positions.Clone(),
            Count = positions.Length,
            HalfWidth = width * 0.5f,
            UniformColor = color,
            Loop = loop,
            Active = true,
        };
        return true;
    }

    /// <summary>
    /// Updates an existing multi-point line gizmo. Reuses the slot's point buffer,
    /// only resizing if the point count actually changed.
    /// </summary>
    public static bool UpdateLineGizmo(int linkedID, Vector3[] positions)
    {
        if (!_linesByID.TryGetValue(linkedID, out LineSlot slot))
        {
            BasisDebug.LogError($"No LineGizmo found with ID {linkedID}. Use CreateLineGizmo first.", BasisDebug.LogTag.Gizmo);
            return false;
        }
        if (slot.Count != positions.Length)
        {
            slot.Points = new Vector3[positions.Length];
            slot.Count = positions.Length;
            RefreshGradientColors(slot);
        }
        Array.Copy(positions, slot.Points, positions.Length);
        return true;
    }

    /// <summary>
    /// Replaces the line gizmo's color sampling with a Gradient — points interpolate
    /// across it by their normalized position. Use for multi-point lines that should
    /// fade through per-segment colors (skeleton chains with one stop per bone).
    /// </summary>
    public static bool SetLineGizmoGradient(int linkedID, Gradient gradient)
    {
        if (!_linesByID.TryGetValue(linkedID, out LineSlot slot))
        {
            BasisDebug.LogError($"No LineGizmo found with ID {linkedID}. Use CreateLineGizmo first.", BasisDebug.LogTag.Gizmo);
            return false;
        }
        slot.Gradient = gradient;
        RefreshGradientColors(slot);
        return true;
    }

    private static void RefreshGradientColors(LineSlot slot)
    {
        if (slot.Gradient == null)
        {
            slot.PointColors = null;
            return;
        }
        if (slot.PointColors == null || slot.PointColors.Length != slot.Count)
        {
            slot.PointColors = new Color32[slot.Count];
        }
        float denominator = slot.Count > 1 ? slot.Count - 1 : 1;
        for (int i = 0; i < slot.Count; i++)
        {
            slot.PointColors[i] = slot.Gradient.Evaluate(i / denominator);
        }
    }

    /// <summary>
    /// Creates a world-space text label. Labels come from a pool; the TMP component
    /// picks up the project's default font. Drive it each frame with
    /// <see cref="UpdateTextGizmo"/> (which billboards + recolors only on change).
    /// </summary>
    public static bool CreateTextGizmo(string GizmoName, out int linkedID, Vector3 position, string text, Color color)
    {
        TryCreateParent();
        linkedID = CreateNewID();
        BasisTextGizmos component = RentLabel(GizmoName, position, text, color);
        _textByID[linkedID] = new TextSlot
        {
            Component = component,
            Position = position,
        };
        return true;
    }

    /// <summary>
    /// Updates a text label's pose, scale, text and color in one call. Text and color
    /// are only re-applied when they actually changed, so a static label costs just a
    /// transform write (cheap billboard) per frame. Labels hidden by the nearest-
    /// <see cref="MaxVisibleLabels"/> cap skip everything but the position bookkeeping.
    /// <paramref name="rotation"/> is typically a billboard facing the listener camera.
    /// </summary>
    public static bool UpdateTextGizmo(int linkedID, Vector3 position, Quaternion rotation, float scale, string text, Color color)
    {
        if (!_textByID.TryGetValue(linkedID, out TextSlot slot) || slot.Component == null)
        {
            return false;
        }
        slot.Position = position;
        if (!slot.Visible || !slot.Active)
        {
            return true;
        }
        slot.Component.Apply(position, rotation, scale);
        slot.Component.SetText(text);
        slot.Component.SetColor(color);
        return true;
    }

    private static TMPro.TMP_FontAsset _gizmoFont;
    private static bool _gizmoFontResolved;
    private static Shader _textOverlayShader;
    private static bool _textOverlayResolved;

    private static TMPro.TMP_FontAsset GetGizmoFont()
    {
        if (_gizmoFontResolved)
        {
            return _gizmoFont;
        }
        _gizmoFont = TMPro.TMP_Settings.defaultFontAsset;
        if (_gizmoFont == null)
        {
            _gizmoFont = Resources.Load<TMPro.TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        }
        if (_gizmoFont == null)
        {
            _gizmoFont = Resources.Load<TMPro.TMP_FontAsset>("LiberationSans SDF");
        }
        if (_gizmoFont == null)
        {
            BasisDebug.LogError("TextGizmo: no TMP font asset found — labels will be invisible. Import TMP Essentials / set a default font asset.", BasisDebug.LogTag.Gizmo);
        }
        _gizmoFontResolved = true;
        return _gizmoFont;
    }

    private static Shader GetTextOverlayShader()
    {
        if (_textOverlayResolved)
        {
            return _textOverlayShader;
        }
        _textOverlayShader = Shader.Find("TextMeshPro/Distance Field Overlay");
        _textOverlayResolved = true;
        return _textOverlayShader;
    }

    private static BasisTextGizmos RentLabel(string gizmoName, Vector3 position, string text, Color color)
    {
        BasisTextGizmos component = null;
        while (_labelPool.Count > 0 && component == null)
        {
            component = _labelPool.Pop();
        }
        if (component == null)
        {
            component = BuildLabel();
        }
        if (component == null)
        {
            return null;
        }
        Transform t = component.transform;
        if (t.parent != Parent.transform)
        {
            t.SetParent(Parent.transform, false);
        }
        t.position = position;
        component.gameObject.name = gizmoName;
        component.ResetContent(text, color);
        component.gameObject.SetActive(true);
        return component;
    }

    private static BasisTextGizmos BuildLabel()
    {
        GameObject go = new GameObject("GizmoLabel");
        go.transform.SetParent(Parent.transform, false);

        TMPro.TextMeshPro tmp = go.AddComponent<TMPro.TextMeshPro>();

        // AddComponent does NOT reliably assign a font at runtime — without one the
        // mesh is empty and the label is invisible. Assign the default explicitly.
        TMPro.TMP_FontAsset font = GetGizmoFont();
        if (font != null)
        {
            tmp.font = font;
        }

        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.enableAutoSizing = false;
        tmp.fontSize = 36;
        tmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        tmp.overflowMode = TMPro.TextOverflowModes.Overflow;
        tmp.raycastTarget = false;
        tmp.isOrthographic = false;
        if (tmp.TryGetComponent(out RectTransform rt))
        {
            rt.sizeDelta = new Vector2(8f, 2f);
        }

        // Labels are overlay debug visuals — they should never participate in shadows,
        // probes or per-object motion vectors.
        MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
        if (meshRenderer != null)
        {
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        // Like the sphere/line gizmos, labels must draw ON TOP of the avatar/world
        // rather than be depth-occluded inside the body. TMP's default material
        // depth-tests; swap this instance to the Overlay variant (ZTest Always).
        // fontMaterial instantiates a per-label clone — keep the reference so the
        // pool can destroy it instead of leaking one per label ever created.
        Material fontMaterialInstance = tmp.fontMaterial;
        Shader overlay = GetTextOverlayShader();
        if (overlay != null && fontMaterialInstance != null)
        {
            fontMaterialInstance.shader = overlay;
        }

        BasisTextGizmos holder = go.AddComponent<BasisTextGizmos>();
        holder.Text = tmp;
        holder.Renderer = meshRenderer;
        holder.MaterialInstance = fontMaterialInstance;
        return holder;
    }

    private static void ReturnLabel(TextSlot slot)
    {
        BasisTextGizmos component = slot.Component;
        if (component == null)
        {
            return;
        }
        component.gameObject.SetActive(false);
        if (_labelPool.Count < MaxPooledLabels)
        {
            _labelPool.Push(component);
        }
        else
        {
            DestroyLabel(component);
        }
    }

    private static void DestroyLabel(BasisTextGizmos component)
    {
        if (component == null)
        {
            return;
        }
        if (component.MaterialInstance != null)
        {
            UnityEngine.Object.Destroy(component.MaterialInstance);
        }
        UnityEngine.Object.Destroy(component.gameObject);
    }

    /// <summary>
    /// Rotation that faces a world point toward a camera, upright. Shared by every
    /// gizmo system that billboards a text label so the math lives in one place.
    /// </summary>
    public static Quaternion BillboardRotation(Vector3 worldPos, Vector3 cameraPos)
    {
        Vector3 dir = worldPos - cameraPos;
        if (dir.sqrMagnitude < 1e-6f)
        {
            dir = Vector3.forward;
        }
        return Quaternion.LookRotation(dir, Vector3.up);
    }

    /// <summary>
    /// Recolors an existing gizmo (sphere, line or text) in place. Colors are plain
    /// slot data, so this is safe to call every frame for gizmos whose color encodes
    /// a live value (e.g. audio gain or playback health).
    /// </summary>
    public static bool UpdateGizmoColor(int linkedID, Color color)
    {
        if (_sphereByID.TryGetValue(linkedID, out int slot))
        {
            _spheres[slot].Color = color;
            return true;
        }
        if (_linesByID.TryGetValue(linkedID, out LineSlot line))
        {
            line.UniformColor = color;
            line.Gradient = null;
            line.PointColors = null;
            return true;
        }
        if (_textByID.TryGetValue(linkedID, out TextSlot text) && text.Component != null)
        {
            text.Component.SetColor(color);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Toggles a gizmo's visibility without destroying it. Used by sub-toggles that
    /// hide/show subsets of gizmos under the master ShowGizmos.
    /// </summary>
    public static void SetGizmoActive(int linkedID, bool active)
    {
        if (_sphereByID.TryGetValue(linkedID, out int slot))
        {
            _spheres[slot].Active = active;
            return;
        }
        if (_linesByID.TryGetValue(linkedID, out LineSlot line))
        {
            line.Active = active;
            return;
        }
        if (_textByID.TryGetValue(linkedID, out TextSlot text))
        {
            text.Active = active;
            if (!active && text.Component != null)
            {
                text.Component.SetVisible(false);
            }
        }
    }

    /// <summary>True while a gizmo with this ID exists in any of the stores.</summary>
    public static bool Exists(int linkedID)
    {
        return _sphereByID.ContainsKey(linkedID) || _linesByID.ContainsKey(linkedID) || _textByID.ContainsKey(linkedID);
    }

    /// <summary>
    /// True while this text gizmo survived the last nearest-<see cref="MaxVisibleLabels"/>
    /// ranking. Consumers can skip building label strings for hidden labels — the string
    /// re-derives from live state on the frame the label becomes visible again.
    /// </summary>
    public static bool IsTextVisible(int linkedID)
    {
        return _textByID.TryGetValue(linkedID, out TextSlot slot) && slot.Active && slot.Visible;
    }

    /// <summary>
    /// Destroys a gizmo with the specified ID.
    /// </summary>
    public static void DestroyGizmo(int linkedID)
    {
        if (_sphereByID.TryGetValue(linkedID, out int slot))
        {
            _sphereByID.Remove(linkedID);
            _spheres[slot] = default;
            _sphereFree.Push(slot);
        }
        else if (_linesByID.Remove(linkedID))
        {
        }
        else if (_textByID.TryGetValue(linkedID, out TextSlot text))
        {
            _textByID.Remove(linkedID);
            ReturnLabel(text);
        }
        else
        {
            BasisDebug.LogWarning($"No Gizmo found with ID {linkedID} to destroy.", BasisDebug.LogTag.Gizmo);
        }
    }

    /// <summary>
    /// Tears down every gizmo, the label pool and the shared parent in one pass.
    /// The master gizmo gate calls this when the last toggle goes off; cached IDs
    /// in consumers become stale (they hook <see cref="OnUseGizmosChanged"/>).
    /// </summary>
    public static void DestroyAll()
    {
        foreach (KeyValuePair<int, TextSlot> kvp in _textByID)
        {
            DestroyLabel(kvp.Value.Component);
        }
        _textByID.Clear();
        while (_labelPool.Count > 0)
        {
            DestroyLabel(_labelPool.Pop());
        }

        _sphereByID.Clear();
        _sphereFree.Clear();
        Array.Clear(_spheres, 0, _spheres.Length);
        _sphereHighWater = 0;

        _linesByID.Clear();
        if (_lineMesh != null)
        {
            UnityEngine.Object.Destroy(_lineMesh);
            _lineMesh = null;
        }
        _lineVerts = null;
        _lineVertexCapacity = 0;
        _lineIndexCountInUse = -1;

        DestroyParent();
    }

    private static int AllocSphereSlot()
    {
        if (_sphereFree.Count > 0)
        {
            return _sphereFree.Pop();
        }
        if (_sphereHighWater == _spheres.Length)
        {
            Array.Resize(ref _spheres, _spheres.Length * 2);
        }
        return _sphereHighWater++;
    }

    // ── Per-frame batched rendering ─────────────────────────────────────────

    // Matches UNITY_INSTANCED_ARRAY_SIZE (500) so a chunk never straddles the engine's
    // internal instanced-constant-buffer split, which would slice the per-instance
    // color array separately from the matrices.
    private const int SphereChunkSize = 500;

    private static Mesh _sphereMesh;
    private static Material _sphereMaterial;
    private static Matrix4x4[] _sphereChunkMatrices;
    private static Vector4[] _sphereChunkColors;
    private static Matrix4x4[] _solidChunkMatrices;
    private static readonly List<MaterialPropertyBlock> _sphereChunkBlocks = new List<MaterialPropertyBlock>();
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");

    // Field order mirrors the attribute order Unity requires in a vertex layout
    // (Position, then Color, then TexCoords) — SetVertexBufferParams rejects
    // declarations out of that canonical order.
    [StructLayout(LayoutKind.Sequential)]
    internal struct LineVertex
    {
        public Vector3 Position;
        public Color32 Color;
        public Vector3 OtherEnd;
        public Vector2 SideWidth;
    }

    internal static readonly VertexAttributeDescriptor[] LineVertexLayout =
    {
        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2),
    };

    /// <summary>
    /// Appends one segment as a camera-facing ribbon quad: two verts anchored at each end,
    /// each carrying the opposite endpoint. Because the shader's offset direction flips with
    /// the segment direction, the far end's side signs are mirrored — that exact pairing is
    /// what keeps the quad untwisted, and it is pinned by tests.
    /// </summary>
    internal static void AppendSegmentVertices(LineVertex[] vertices, ref int vertexCursor, Vector3 a, Vector3 b, Color32 colorA, Color32 colorB, float halfWidth)
    {
        vertices[vertexCursor++] = new LineVertex { Position = a, Color = colorA, OtherEnd = b, SideWidth = new Vector2(-1f, halfWidth) };
        vertices[vertexCursor++] = new LineVertex { Position = a, Color = colorA, OtherEnd = b, SideWidth = new Vector2(1f, halfWidth) };
        vertices[vertexCursor++] = new LineVertex { Position = b, Color = colorB, OtherEnd = a, SideWidth = new Vector2(1f, halfWidth) };
        vertices[vertexCursor++] = new LineVertex { Position = b, Color = colorB, OtherEnd = a, SideWidth = new Vector2(-1f, halfWidth) };
    }

    /// <summary>
    /// Fills the static per-quad index pattern: triangles (0,1,2) and (2,1,3) off each
    /// group of four ribbon verts, sharing the 1–2 diagonal so the quad cannot bowtie.
    /// </summary>
    internal static void FillQuadIndices(uint[] indices, int quadCount)
    {
        for (int q = 0; q < quadCount; q++)
        {
            int i = q * 6;
            uint baseVertex = (uint)(q * 4);
            indices[i] = baseVertex;
            indices[i + 1] = baseVertex + 1;
            indices[i + 2] = baseVertex + 2;
            indices[i + 3] = baseVertex + 2;
            indices[i + 4] = baseVertex + 1;
            indices[i + 5] = baseVertex + 3;
        }
    }

    private const MeshUpdateFlags LineMeshFlags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;

    private static Mesh _lineMesh;
    private static Material _lineMaterial;
    private static LineVertex[] _lineVerts;
    private static int _lineVertexCapacity;
    private static int _lineIndexCountInUse = -1;

    private static readonly HashSet<string> _missingShadersLogged = new HashSet<string>();

    private struct LabelDistance
    {
        public float DistSq;
        public TextSlot Slot;
    }

    private static readonly List<LabelDistance> _labelRanking = new List<LabelDistance>();
    private static readonly Comparison<LabelDistance> LabelComparison = (a, b) => a.DistSq.CompareTo(b.DistSq);

    /// <summary>
    /// Submits every live gizmo for this frame's rendering and resolves which text
    /// labels are visible. Call once per frame after the gizmo consumers ticked;
    /// <paramref name="viewerPosition"/> is the listener camera, used for distance
    /// culling and the nearest-<see cref="MaxVisibleLabels"/> label ranking.
    /// </summary>
    public static void Render(Vector3 viewerPosition)
    {
        if (_sphereByID.Count == 0 && _linesByID.Count == 0 && _textByID.Count == 0)
        {
            return;
        }
        float maxDistSq = float.IsPositiveInfinity(MaxDrawDistance) ? float.PositiveInfinity : MaxDrawDistance * MaxDrawDistance;
        RenderSpheres(viewerPosition, maxDistSq);
        RenderLines(viewerPosition, maxDistSq);
        UpdateLabelVisibility(viewerPosition);
    }

    private static void RenderSpheres(Vector3 viewer, float maxDistSq)
    {
        if (_sphereByID.Count == 0 || !EnsureSphereResources())
        {
            return;
        }

        bool drawSolid = SolidSphereMaterial != null;
        int n = 0;
        int chunk = 0;
        int solidCount = 0;
        for (int i = 0; i < _sphereHighWater; i++)
        {
            ref SphereSlot s = ref _spheres[i];
            if (!s.Used || !s.Active)
            {
                continue;
            }
            if (s.Solid && !drawSolid)
            {
                continue;
            }
            if (maxDistSq < float.PositiveInfinity && (s.Position - viewer).sqrMagnitude > maxDistSq)
            {
                continue;
            }

            Matrix4x4 m;
            if (s.HasRotation)
            {
                m = Matrix4x4.TRS(s.Position, s.Rotation, s.Scale);
            }
            else
            {
                m = default;
                m.m00 = s.Scale.x;
                m.m11 = s.Scale.y;
                m.m22 = s.Scale.z;
                m.m33 = 1f;
                m.m03 = s.Position.x;
                m.m13 = s.Position.y;
                m.m23 = s.Position.z;
            }

            if (s.Solid)
            {
                _solidChunkMatrices[solidCount] = m;
                solidCount++;
                if (solidCount == SphereChunkSize)
                {
                    FlushSolidSphereChunk(solidCount);
                    solidCount = 0;
                }
                continue;
            }

            _sphereChunkMatrices[n] = m;
            _sphereChunkColors[n] = s.Color;
            n++;
            if (n == SphereChunkSize)
            {
                FlushSphereChunk(chunk++, n);
                n = 0;
            }
        }
        if (n > 0)
        {
            FlushSphereChunk(chunk, n);
        }
        if (solidCount > 0)
        {
            FlushSolidSphereChunk(solidCount);
        }
    }

    private static void FlushSolidSphereChunk(int count)
    {
        Material material = SolidSphereMaterial;
        if (!material.enableInstancing)
        {
            material.enableInstancing = true;
        }
        Graphics.DrawMeshInstanced(_sphereMesh, 0, material, _solidChunkMatrices, count, null,
            ShadowCastingMode.On, true, RenderLayer, null, LightProbeUsage.Off);
    }

    private static void FlushSphereChunk(int chunkIndex, int count)
    {
        while (_sphereChunkBlocks.Count <= chunkIndex)
        {
            _sphereChunkBlocks.Add(new MaterialPropertyBlock());
        }
        MaterialPropertyBlock block = _sphereChunkBlocks[chunkIndex];
        block.SetVectorArray(ColorProperty, _sphereChunkColors);
        Graphics.DrawMeshInstanced(_sphereMesh, 0, _sphereMaterial, _sphereChunkMatrices, count, block,
            ShadowCastingMode.Off, false, RenderLayer, null, LightProbeUsage.Off);
    }

    private static void RenderLines(Vector3 viewer, float maxDistSq)
    {
        if (_linesByID.Count == 0)
        {
            return;
        }

        int totalSegments = 0;
        foreach (KeyValuePair<int, LineSlot> kvp in _linesByID)
        {
            LineSlot slot = kvp.Value;
            if (!slot.Active || slot.Count < 2)
            {
                continue;
            }
            if (maxDistSq < float.PositiveInfinity && (slot.Points[0] - viewer).sqrMagnitude > maxDistSq)
            {
                continue;
            }
            totalSegments += slot.Count - 1 + (slot.Loop ? 1 : 0);
        }
        if (totalSegments == 0 || !EnsureLineResources(totalSegments * 4))
        {
            return;
        }

        int v = 0;
        float maxHalfWidth = 0f;
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (KeyValuePair<int, LineSlot> kvp in _linesByID)
        {
            LineSlot slot = kvp.Value;
            if (!slot.Active || slot.Count < 2)
            {
                continue;
            }
            if (maxDistSq < float.PositiveInfinity && (slot.Points[0] - viewer).sqrMagnitude > maxDistSq)
            {
                continue;
            }

            int count = slot.Count;
            int segments = count - 1 + (slot.Loop ? 1 : 0);
            float halfWidth = slot.HalfWidth;
            if (halfWidth > maxHalfWidth)
            {
                maxHalfWidth = halfWidth;
            }
            for (int seg = 0; seg < segments; seg++)
            {
                int ia = seg;
                int ib = seg + 1 == count ? 0 : seg + 1;
                Vector3 a = slot.Points[ia];
                Vector3 b = slot.Points[ib];
                Color32 colorA = slot.PointColors != null ? slot.PointColors[ia] : slot.UniformColor;
                Color32 colorB = slot.PointColors != null ? slot.PointColors[ib] : slot.UniformColor;

                AppendSegmentVertices(_lineVerts, ref v, a, b, colorA, colorB, halfWidth);

                min = Vector3.Min(min, Vector3.Min(a, b));
                max = Vector3.Max(max, Vector3.Max(a, b));
            }
        }

        _lineMesh.SetVertexBufferData(_lineVerts, 0, 0, v, 0, LineMeshFlags);
        int indexCount = totalSegments * 6;
        if (indexCount != _lineIndexCountInUse)
        {
            _lineMesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles), LineMeshFlags);
            _lineIndexCountInUse = indexCount;
        }
        Vector3 pad = new Vector3(maxHalfWidth, maxHalfWidth, maxHalfWidth);
        Bounds bounds = default;
        bounds.SetMinMax(min - pad, max + pad);
        _lineMesh.bounds = bounds;

        Graphics.DrawMesh(_lineMesh, Matrix4x4.identity, _lineMaterial, RenderLayer, null, 0, null,
            ShadowCastingMode.Off, false, null, false);
    }

    private static void UpdateLabelVisibility(Vector3 viewer)
    {
        if (_textByID.Count == 0)
        {
            return;
        }
        _labelRanking.Clear();
        foreach (KeyValuePair<int, TextSlot> kvp in _textByID)
        {
            TextSlot slot = kvp.Value;
            if (slot.Component == null)
            {
                continue;
            }
            if (!slot.Active)
            {
                slot.Visible = false;
                slot.Component.SetVisible(false);
                continue;
            }
            _labelRanking.Add(new LabelDistance { DistSq = (slot.Position - viewer).sqrMagnitude, Slot = slot });
        }
        int total = _labelRanking.Count;
        if (total > MaxVisibleLabels)
        {
            _labelRanking.Sort(LabelComparison);
        }
        for (int i = 0; i < total; i++)
        {
            TextSlot slot = _labelRanking[i].Slot;
            bool visible = i < MaxVisibleLabels;
            slot.Visible = visible;
            slot.Component.SetVisible(visible);
        }
    }

    private static bool EnsureSphereResources()
    {
        if (_sphereMaterial == null)
        {
            Shader shader = Resources.Load<Shader>("BasisGizmoSphereInstanced");
            if (shader == null)
            {
                shader = Shader.Find("Basis/GizmoSphereInstanced");
            }
            if (shader == null)
            {
                LogMissingShaderOnce("BasisGizmoSphereInstanced");
                return false;
            }
            _sphereMaterial = new Material(shader)
            {
                enableInstancing = true,
            };
        }
        if (_sphereMesh == null)
        {
            _sphereMesh = BuildSphereMesh();
        }
        if (_sphereChunkMatrices == null)
        {
            _sphereChunkMatrices = new Matrix4x4[SphereChunkSize];
            _sphereChunkColors = new Vector4[SphereChunkSize];
            _solidChunkMatrices = new Matrix4x4[SphereChunkSize];
        }
        return true;
    }

    private static bool EnsureLineResources(int vertexCount)
    {
        if (_lineMaterial == null)
        {
            Shader shader = Resources.Load<Shader>("BasisGizmoLine");
            if (shader == null)
            {
                shader = Shader.Find("Basis/GizmoLine");
            }
            if (shader == null)
            {
                LogMissingShaderOnce("BasisGizmoLine");
                return false;
            }
            _lineMaterial = new Material(shader);
        }
        if (_lineMesh == null)
        {
            _lineMesh = new Mesh
            {
                name = "BasisGizmoLines",
            };
            _lineMesh.MarkDynamic();
            _lineVertexCapacity = 0;
            _lineIndexCountInUse = -1;
        }
        if (vertexCount > _lineVertexCapacity)
        {
            int capacity = Mathf.Max(256, Mathf.NextPowerOfTwo(vertexCount));
            _lineVerts = new LineVertex[capacity];
            _lineMesh.SetVertexBufferParams(capacity, LineVertexLayout);

            int quadCount = capacity / 4;
            int indexCapacity = quadCount * 6;
            uint[] indices = new uint[indexCapacity];
            FillQuadIndices(indices, quadCount);
            _lineMesh.SetIndexBufferParams(indexCapacity, IndexFormat.UInt32);
            _lineMesh.SetIndexBufferData(indices, 0, 0, indexCapacity, LineMeshFlags);
            _lineMesh.subMeshCount = 1;
            _lineVertexCapacity = capacity;
            _lineIndexCountInUse = -1;
        }
        return true;
    }

    private static void LogMissingShaderOnce(string shaderName)
    {
        if (_missingShadersLogged.Add(shaderName))
        {
            BasisDebug.LogError($"Gizmo shader '{shaderName}' missing from Resources — gizmos will not render.", BasisDebug.LogTag.Gizmo);
        }
    }

    /// <summary>
    /// Icosphere with the same Ø1 sizing as the built-in sphere the old prefab used,
    /// at roughly a third of the vertex count. <paramref name="markNoLongerReadable"/>
    /// stays true at runtime; tests pass false so they can read the buffers back.
    /// </summary>
    internal static Mesh BuildSphereMesh(bool markNoLongerReadable = true)
    {
        float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
        List<Vector3> vertices = new List<Vector3>
        {
            new Vector3(-1,  t,  0), new Vector3( 1,  t,  0), new Vector3(-1, -t,  0), new Vector3( 1, -t,  0),
            new Vector3( 0, -1,  t), new Vector3( 0,  1,  t), new Vector3( 0, -1, -t), new Vector3( 0,  1, -t),
            new Vector3( t,  0, -1), new Vector3( t,  0,  1), new Vector3(-t,  0, -1), new Vector3(-t,  0,  1),
        };
        int[] faces =
        {
            0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
            1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
            3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
            4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1,
        };
        Dictionary<long, int> midpointCache = new Dictionary<long, int>();
        for (int subdivision = 0; subdivision < 2; subdivision++)
        {
            int[] subdivided = new int[faces.Length * 4];
            int w = 0;
            for (int f = 0; f < faces.Length; f += 3)
            {
                int a = faces[f];
                int b = faces[f + 1];
                int c = faces[f + 2];
                int ab = Midpoint(vertices, midpointCache, a, b);
                int bc = Midpoint(vertices, midpointCache, b, c);
                int ca = Midpoint(vertices, midpointCache, c, a);
                subdivided[w++] = a; subdivided[w++] = ab; subdivided[w++] = ca;
                subdivided[w++] = b; subdivided[w++] = bc; subdivided[w++] = ab;
                subdivided[w++] = c; subdivided[w++] = ca; subdivided[w++] = bc;
                subdivided[w++] = ab; subdivided[w++] = bc; subdivided[w++] = ca;
            }
            faces = subdivided;
        }
        int vertexCount = vertices.Count;
        Vector3[] normals = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 direction = vertices[i].normalized;
            vertices[i] = direction * 0.5f;
            normals[i] = direction;
            uvs[i] = new Vector2(0.5f, 0.5f);
        }
        Mesh mesh = new Mesh
        {
            name = "BasisGizmoSphere",
        };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(faces, 0, false);
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
        mesh.UploadMeshData(markNoLongerReadable);
        return mesh;
    }

    private static int Midpoint(List<Vector3> vertices, Dictionary<long, int> cache, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (cache.TryGetValue(key, out int index))
        {
            return index;
        }
        index = vertices.Count;
        vertices.Add((vertices[a] + vertices[b]) * 0.5f);
        cache[key] = index;
        return index;
    }
}
