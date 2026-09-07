using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Runtime debug-gizmo drawing. The Create/Update/Destroy id API is retained so callers
/// can hold onto gizmos across frames, but the backend is batched: spheres and lines are
/// plain data slots submitted once per frame as a handful of draws — instanced meshes for
/// spheres, one dynamic ribbon mesh per layer for the lines — so cost scales with visible
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
    /// reflection (whose camera culls to that layer), and restores it to
    /// <see cref="DefaultRenderLayer"/> on teardown.
    /// Individual gizmos can opt out with <see cref="SetGizmoLayer"/>.
    /// </summary>
    /// <remarks>
    /// Resolved on first read rather than by a field initializer, for two reasons: static
    /// initializers run in textual order, so an initializer here would read the backing field
    /// below as 0 and silently settle on the Default layer; and <c>LayerMask.NameToLayer</c> is a
    /// Unity call that has no business running during static construction.
    /// </remarks>
    public static int RenderLayer
    {
        get
        {
            if (renderLayer == LayerNotResolved)
            {
                renderLayer = DefaultRenderLayer;
            }
            return renderLayer;
        }
        set
        {
            if (renderLayer == value)
            {
                return;
            }
            renderLayer = value;
            _sphereColorsDirty = true;
            ApplySharedLayerToLabels();
        }
    }

    private const int LayerNotResolved = -2;

    /// <summary>
    /// Default — the layer the world itself is on, so every camera in the scene renders it.
    /// Where <see cref="RenderInAllCameras"/> puts gizmos.
    /// </summary>
    private const int AllCameraLayer = 0;

    private static int renderLayer = LayerNotResolved;
    private static int overlayLayer = LayerNotResolved;
    private static bool renderInAllCameras;

    /// <summary>
    /// Where gizmos live unless something moves them: OverlayUI.
    ///
    /// <para>Gizmos are debug drawing for the person operating the thing they describe, and the
    /// handheld camera's capture pass culls this layer — so a tracker marker, an IK probe or a
    /// dolly track can be read while a shot is being taken without ever landing in the shot. The
    /// player's own camera renders the layer, and it depth-tests against the world like anything
    /// else, which is what makes the waypoint markers already sitting on it grabbable.</para>
    ///
    /// <para>Falls back to the Default layer in a project that does not define OverlayUI, which is
    /// where gizmos used to live — visible to every camera including the capture. Turning
    /// <see cref="RenderInAllCameras"/> on asks for that same layer deliberately.</para>
    /// </summary>
    public static int DefaultRenderLayer
    {
        get
        {
            if (renderInAllCameras)
            {
                return AllCameraLayer;
            }
            if (overlayLayer == LayerNotResolved)
            {
                int overlayUi = LayerMask.NameToLayer("OverlayUI");
                overlayLayer = overlayUi >= 0 ? overlayUi : AllCameraLayer;
            }
            return overlayLayer;
        }
    }

    /// <summary>
    /// Whether gizmos are drawn for every camera in the scene rather than only the ones that
    /// render <see cref="DefaultRenderLayer"/>. Off by default: gizmos belong to the person
    /// operating the thing they describe, so photos, streams and the follow PIP stay clean.
    /// On moves them to the Default layer the world itself is on, which every camera renders —
    /// captures, mirrors and any world camera then show what the player sees.
    /// <para>
    /// Gizmos parked on a layer of their own with <see cref="SetGizmoLayer"/> stay there: those
    /// are a camera rig's own markers (its frustum, its dolly track, the follow puck), kept out
    /// of its own shot on purpose rather than by this default.
    /// </para>
    /// </summary>
    public static bool RenderInAllCameras
    {
        get => renderInAllCameras;
        set
        {
            if (renderInAllCameras == value)
            {
                return;
            }
            int previousDefault = DefaultRenderLayer;
            renderInAllCameras = value;
            // Anything that pointed RenderLayer somewhere of its own — the calibration mirror
            // relay — keeps it, and restores to DefaultRenderLayer, which now reads the new value.
            if (renderLayer == LayerNotResolved || renderLayer == previousDefault)
            {
                RenderLayer = DefaultRenderLayer;
            }
        }
    }

    /// <summary>
    /// Whether gizmos punch through the world (ZTest Always, overlay queue) or are occluded
    /// by whatever is in front of them (ZTest LessEqual, transparent queue). Off by default:
    /// depth-testing is what tells you whether a probe is actually in front of the geometry
    /// it describes, and gizmos that ignore depth read as a flat overlay with no relationship
    /// to the scene. Covers all three primitive kinds: spheres, lines and text labels.
    /// </summary>
    public static bool DrawOnTop
    {
        get => drawOnTop;
        set
        {
            if (drawOnTop == value)
            {
                return;
            }
            drawOnTop = value;
            ApplyDepthMode();
        }
    }

    private static bool drawOnTop;

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
            Parent.layer = RenderLayer;
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
        public int Layer;          // -1 follows RenderLayer; see SetGizmoLayer
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
        public int Layer = -1;         // -1 follows RenderLayer; see SetGizmoLayer
        public bool Loop;
        public bool Active;
    }

    private static readonly Dictionary<int, LineSlot> _linesByID = new Dictionary<int, LineSlot>();

    // ── Text slots ──────────────────────────────────────────────────────────

    private class TextSlot
    {
        public BasisTextGizmos Component;
        public Vector3 Position;   // last requested — ranks nearest-K even while hidden
        public int Layer = -1;     // -1 follows RenderLayer; see SetGizmoLayer
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
        s.Layer = -1;
        s.HasRotation = false;
        s.Active = true;
        s.Used = true;
        _sphereByID[linkedID] = slot;
        _sphereColorsDirty = true;
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
        s.Layer = -1;
        s.HasRotation = false;
        s.Solid = true;
        s.Active = true;
        s.Used = true;
        _sphereByID[linkedID] = slot;
        _sphereColorsDirty = true;
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
    /// Pose, width and color of a two-point line in one lookup. For producers that replay a
    /// whole batch of transient lines every frame (the FBIK solve gizmo queue), where each
    /// pooled slot carries different geometry frame to frame and the separate
    /// UpdateLineGizmo/UpdateGizmoColor pair would cost two dictionary probes per line.
    /// </summary>
    public static bool UpdateLineGizmo(int linkedID, Vector3 start, Vector3 end, float width, Color32 color)
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
        }
        slot.Points[0] = start;
        slot.Points[1] = end;
        slot.HalfWidth = width * 0.5f;
        slot.UniformColor = color;
        slot.Loop = false;
        slot.Gradient = null;
        slot.PointColors = null;
        return true;
    }

    /// <summary>
    /// Position, uniform size and color of a sphere in one lookup. Batch counterpart to
    /// <see cref="UpdateLineGizmo(int, Vector3, Vector3, float, Color32)"/>.
    /// </summary>
    public static bool UpdateSphereGizmo(int linkedID, Vector3 position, float size, Color32 color)
    {
        if (!_sphereByID.TryGetValue(linkedID, out int slot))
        {
            BasisDebug.LogError($"No SphereGizmo found with ID {linkedID}. Use CreateSphereGizmo first.", BasisDebug.LogTag.Gizmo);
            return false;
        }
        ref SphereSlot s = ref _spheres[slot];
        s.Position = position;
        s.Scale = new Vector3(size, size, size);
        SetSphereColor(ref s, (Color)color);
        s.HasRotation = false;
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

    /// <summary>
    /// Colours a multi-point line a point at a time. A <see cref="Gradient"/> holds eight keys, so
    /// it cannot carry a value sampled densely along a path; this takes the samples straight.
    /// Entries past the line's own point count are ignored, and a short array repeats its last
    /// colour to the end rather than leaving the tail black.
    /// </summary>
    public static bool SetLineGizmoColors(int linkedID, Color32[] colors, int count)
    {
        if (!_linesByID.TryGetValue(linkedID, out LineSlot slot))
        {
            BasisDebug.LogError($"No LineGizmo found with ID {linkedID}. Use CreateLineGizmo first.", BasisDebug.LogTag.Gizmo);
            return false;
        }
        if (colors == null || count <= 0 || slot.Count <= 0)
        {
            slot.Gradient = null;
            slot.PointColors = null;
            return false;
        }

        count = Math.Min(count, colors.Length);
        slot.Gradient = null;
        if (slot.PointColors == null || slot.PointColors.Length != slot.Count)
        {
            slot.PointColors = new Color32[slot.Count];
        }
        for (int i = 0; i < slot.Count; i++)
        {
            slot.PointColors[i] = colors[i < count ? i : count - 1];
        }
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

    // The shader the font asset's own material came with, captured before the first overlay
    // swap so DrawOnTop can put it back. A font whose material is already an Overlay variant
    // has no depth-testing shader to return to, so the non-overlay counterpart is resolved by
    // name ("TextMeshPro/[Mobile/]Distance Field Overlay" -> the same minus " Overlay").
    private static Shader _textDepthShader;

    private static Shader ResolveLabelShader(Shader fontShader)
    {
        if (_textDepthShader == null && fontShader != null)
        {
            _textDepthShader = fontShader.name.EndsWith(" Overlay", StringComparison.Ordinal)
                ? Shader.Find(fontShader.name.Substring(0, fontShader.name.Length - " Overlay".Length)) ?? fontShader
                : fontShader;
        }
        if (!drawOnTop)
        {
            return _textDepthShader;
        }
        Shader overlay = GetTextOverlayShader();
        return overlay != null ? overlay : _textDepthShader;
    }

    private static void ApplyLabelDepthMode()
    {
        foreach (KeyValuePair<int, TextSlot> kvp in _textByID)
        {
            ApplyLabelDepthMode(kvp.Value.Component);
        }
        foreach (BasisTextGizmos pooled in _labelPool)
        {
            ApplyLabelDepthMode(pooled);
        }
    }

    private static void ApplyLabelDepthMode(BasisTextGizmos component)
    {
        if (component == null || component.MaterialInstance == null)
        {
            return;
        }
        Shader target = ResolveLabelShader(component.MaterialInstance.shader);
        if (target != null && component.MaterialInstance.shader != target)
        {
            component.MaterialInstance.shader = target;
        }
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
        // A label that came back from the pool still carries whatever layer SetGizmoLayer put
        // it on; the next renter has not asked for that, so it starts on the shared layer.
        component.gameObject.layer = RenderLayer;
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

        // Like the sphere/line gizmos, labels follow DrawOnTop: TMP's default material
        // depth-tests, so drawing on top swaps this instance to the Overlay variant
        // (ZTest Always) and depth-respecting mode leaves the font's own shader in place.
        // fontMaterial instantiates a per-label clone — keep the reference so the
        // pool can destroy it instead of leaking one per label ever created.
        Material fontMaterialInstance = tmp.fontMaterial;
        if (fontMaterialInstance != null)
        {
            Shader target = ResolveLabelShader(fontMaterialInstance.shader);
            if (target != null && fontMaterialInstance.shader != target)
            {
                fontMaterialInstance.shader = target;
            }
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
            SetSphereColor(ref _spheres[slot], color);
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
    /// Puts one gizmo on a specific Unity layer instead of the shared <see cref="RenderLayer"/>,
    /// so a camera that culls that layer cannot see it. Pass -1 to hand it back to RenderLayer.
    /// <para>
    /// The layer is the only thing that hides a gizmo from a particular camera. Draws are
    /// submitted for every camera in the frame, and the batch is built from wherever the
    /// producer last left it — <see cref="Render"/> runs at the tail of LateUpdate, so a
    /// producer that writes its positions later (in the before-render pass, as the player
    /// rig and anything parented to it do) is a frame ahead of the geometry being drawn.
    /// Parking geometry outside a camera's frustum is therefore not a guarantee: the shot is
    /// taken from this frame's pose while the gizmo still sits at the last one. The handheld
    /// camera's detached wireframe marker uses this to stay out of its own capture.
    /// </para>
    /// <para>
    /// Text labels are GameObjects rather than batched draws, so for those this writes the
    /// object's layer there and then — including the -1 case, which resolves to the current
    /// RenderLayer rather than tracking later changes to it.
    /// </para>
    /// </summary>
    public static bool SetGizmoLayer(int linkedID, int layer)
    {
        if (layer < -1 || layer > 31)
        {
            BasisDebug.LogError($"Gizmo layer {layer} is out of range; use 0-31 or -1 to follow RenderLayer.", BasisDebug.LogTag.Gizmo);
            return false;
        }
        if (_sphereByID.TryGetValue(linkedID, out int slot))
        {
            if (_spheres[slot].Layer != layer)
            {
                _spheres[slot].Layer = layer;
                _sphereColorsDirty = true;
            }
            return true;
        }
        if (_linesByID.TryGetValue(linkedID, out LineSlot line))
        {
            line.Layer = layer;
            return true;
        }
        if (_textByID.TryGetValue(linkedID, out TextSlot text) && text.Component != null)
        {
            text.Layer = layer;
            text.Component.gameObject.layer = ResolveLayer(layer);
            return true;
        }
        return false;
    }

    /// <summary>A slot's own layer, or the shared <see cref="RenderLayer"/> when it has none.</summary>
    private static int ResolveLayer(int slotLayer)
    {
        return slotLayer >= 0 ? slotLayer : RenderLayer;
    }

    /// <summary>
    /// Moves the label objects that follow the shared layer onto it. Spheres and lines are
    /// batched draws whose layer is read at submission, so they need nothing; labels are
    /// GameObjects and carry the layer they were rented on until something rewrites it.
    /// </summary>
    private static void ApplySharedLayerToLabels()
    {
        int layer = RenderLayer;
        if (Parent != null)
        {
            Parent.layer = layer;
        }
        foreach (KeyValuePair<int, TextSlot> kvp in _textByID)
        {
            TextSlot slot = kvp.Value;
            if (slot.Layer >= 0 || slot.Component == null)
            {
                continue;
            }
            slot.Component.gameObject.layer = layer;
        }
    }

    /// <summary>
    /// Toggles a gizmo's visibility without destroying it. Used by sub-toggles that
    /// hide/show subsets of gizmos under the master ShowGizmos.
    /// </summary>
    public static void SetGizmoActive(int linkedID, bool active)
    {
        if (_sphereByID.TryGetValue(linkedID, out int slot))
        {
            if (_spheres[slot].Active != active)
            {
                _spheres[slot].Active = active;
                _sphereColorsDirty = true;
            }
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

    /// <summary>
    /// One-line dump of everything between a submitted gizmo and a pixel: how many slots exist,
    /// how many pass the drawable test, the layer they are submitted on, and whether the line
    /// material resolved. Every one of these fails the same way from the outside -- nothing on
    /// screen -- so a caller chasing "my gizmo does not show" needs all of them at once.
    /// </summary>
    public static string DescribeState(Vector3 viewer)
    {
        int lineSlots = _linesByID.Count;
        int drawableLines = 0;
        float maxDistSq = float.IsPositiveInfinity(MaxDrawDistance) ? float.PositiveInfinity : MaxDrawDistance * MaxDrawDistance;
        int inactive = 0, degenerate = 0, culled = 0;
        foreach (KeyValuePair<int, LineSlot> kvp in _linesByID)
        {
            LineSlot slot = kvp.Value;
            if (!slot.Active) { inactive++; continue; }
            if (slot.Count < 2) { degenerate++; continue; }
            if (!(maxDistSq >= float.PositiveInfinity) && (slot.Points[0] - viewer).sqrMagnitude > maxDistSq) { culled++; continue; }
            drawableLines++;
        }
        return $"lines slots={lineSlots} drawable={drawableLines} (inactive={inactive} degenerate={degenerate} distanceCulled={culled})"
            + $" | spheres={_sphereByID.Count} labels={_textByID.Count}"
            + $" | layer={RenderLayer} ('{LayerMask.LayerToName(RenderLayer)}') renderInAllCameras={RenderInAllCameras}"
            + $" | lineMaterial={(_lineMaterial != null ? _lineMaterial.shader.name : "NULL -- shader missing")}"
            + $" | drawOnTop={DrawOnTop} maxDrawDistance={MaxDrawDistance}";
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
            _sphereColorsDirty = true;
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
        _sphereColorsDirty = true;

        _linesByID.Clear();
        foreach (KeyValuePair<int, LineBatch> kvp in _lineBatches)
        {
            if (kvp.Value.Mesh != null)
            {
                UnityEngine.Object.Destroy(kvp.Value.Mesh);
            }
        }
        _lineBatches.Clear();

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
    private static readonly List<int> _sphereLayerScratch = new List<int>();
    private sealed class SphereChunk
    {
        public readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
        public bool Uploaded;
    }
    private static readonly List<SphereChunk> _sphereChunks = new List<SphereChunk>();
    private static bool _sphereColorsDirty = true, _sphereUploadColors;
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");
    private static readonly int ZTestProperty = Shader.PropertyToID("_ZTest");

    /// <summary>
    /// Pushes <see cref="DrawOnTop"/> onto the shared sphere/line materials and every live
    /// label. Render state comes off the material itself — a MaterialPropertyBlock cannot
    /// override a <c>ZTest [_ZTest]</c> expression — so the two batched materials carry the
    /// mode for every gizmo drawn through them.
    /// </summary>
    private static void ApplyDepthMode()
    {
        ApplyMaterialDepthMode(_sphereMaterial);
        ApplyMaterialDepthMode(_lineMaterial);
        ApplyLabelDepthMode();
    }

    internal static void ApplyMaterialDepthMode(Material material)
    {
        if (material == null)
        {
            return;
        }
        material.SetFloat(ZTestProperty, (float)(drawOnTop ? CompareFunction.Always : CompareFunction.LessEqual));
        material.renderQueue = (int)(drawOnTop ? RenderQueue.Overlay : RenderQueue.Transparent);
    }

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

    /// <summary>
    /// The dynamic ribbon mesh every line on one layer is packed into. There is a single batch
    /// in the ordinary case; a layer only gets its own once a gizmo is moved onto it, and the
    /// batch then sticks around unused rather than being torn down and rebuilt as it empties
    /// and refills (bounded by the 32 layers Unity has).
    /// </summary>
    private sealed class LineBatch
    {
        public Mesh Mesh;
        public LineVertex[] Verts;
        public int VertexCapacity;
        public int IndexCountInUse = -1;
    }

    private static Material _lineMaterial;
    private static readonly Dictionary<int, LineBatch> _lineBatches = new Dictionary<int, LineBatch>();
    private static readonly Dictionary<int, int> _lineSegmentsByLayer = new Dictionary<int, int>();
    private static readonly List<int> _lineLayerOrder = new List<int>();

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

        // Bucket drawable slot indices per layer in ONE walk. One bucket unless
        // something called SetGizmoLayer, and a chunk cannot straddle two layers,
        // so each bucket then renders its own indices without re-walking the slots.
        _sphereLayerScratch.Clear();
        int bucketsUsed = 0;
        for (int i = 0; i < _sphereHighWater; i++)
        {
            ref SphereSlot s = ref _spheres[i];
            if (!IsSphereDrawable(ref s, drawSolid, viewer, maxDistSq))
            {
                continue;
            }
            int layer = ResolveLayer(s.Layer);
            int bucket = _sphereLayerScratch.IndexOf(layer);
            if (bucket < 0)
            {
                bucket = bucketsUsed;
                if (_sphereLayerBuckets.Count == bucket)
                {
                    _sphereLayerBuckets.Add(new List<int>());
                }
                _sphereLayerBuckets[bucket].Clear();
                _sphereLayerScratch.Add(layer);
                bucketsUsed++;
            }
            _sphereLayerBuckets[bucket].Add(i);
        }

        // The chunk counter keeps running across layers: each chunk owns a MaterialPropertyBlock
        // that has to survive until the frame renders, so two draws must never share one.
        int chunk = 0;
        _sphereUploadColors = _sphereColorsDirty || maxDistSq < float.PositiveInfinity;
        for (int Index = 0; Index < bucketsUsed; Index++)
        {
            RenderSphereLayer(_sphereLayerScratch[Index], _sphereLayerBuckets[Index], ref chunk);
        }
        _sphereColorsDirty = false;
    }

    private static readonly List<List<int>> _sphereLayerBuckets = new List<List<int>>();

    private static bool IsSphereDrawable(ref SphereSlot s, bool drawSolid, Vector3 viewer, float maxDistSq)
    {
        if (!s.Used || !s.Active)
        {
            return false;
        }
        if (s.Solid && !drawSolid)
        {
            return false;
        }
        return maxDistSq >= float.PositiveInfinity || (s.Position - viewer).sqrMagnitude <= maxDistSq;
    }

    private static void RenderSphereLayer(int layer, List<int> slotIndices, ref int chunk)
    {
        int n = 0;
        int solidCount = 0;
        int indexCount = slotIndices.Count;
        for (int Index = 0; Index < indexCount; Index++)
        {
            ref SphereSlot s = ref _spheres[slotIndices[Index]];

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
                    FlushSolidSphereChunk(solidCount, layer);
                    solidCount = 0;
                }
                continue;
            }

            _sphereChunkMatrices[n] = m;
            _sphereChunkColors[n] = s.Color;
            n++;
            if (n == SphereChunkSize)
            {
                FlushSphereChunk(chunk++, n, layer);
                n = 0;
            }
        }
        if (n > 0)
        {
            FlushSphereChunk(chunk++, n, layer);
        }
        if (solidCount > 0)
        {
            FlushSolidSphereChunk(solidCount, layer);
        }
    }

    private static void FlushSolidSphereChunk(int count, int layer)
    {
        Material material = SolidSphereMaterial;
        if (!material.enableInstancing)
        {
            material.enableInstancing = true;
        }
        Graphics.DrawMeshInstanced(_sphereMesh, 0, material, _solidChunkMatrices, count, null,
            ShadowCastingMode.On, true, layer, null, LightProbeUsage.Off);
    }

    private static void FlushSphereChunk(int chunkIndex, int count, int layer)
    {
        while (_sphereChunks.Count <= chunkIndex)
        {
            _sphereChunks.Add(new SphereChunk());
        }
        SphereChunk chunk = _sphereChunks[chunkIndex];
        if (_sphereUploadColors || !chunk.Uploaded)
        {
            chunk.Block.SetVectorArray(ColorProperty, _sphereChunkColors);
            chunk.Uploaded = true;
        }
        Graphics.DrawMeshInstanced(_sphereMesh, 0, _sphereMaterial, _sphereChunkMatrices, count, chunk.Block,
            ShadowCastingMode.Off, false, layer, null, LightProbeUsage.Off);
    }

    private static void SetSphereColor(ref SphereSlot s, Color color)
    {
        Vector4 next = color;
        if (s.Color.x != next.x || s.Color.y != next.y || s.Color.z != next.z || s.Color.w != next.w)
        {
            s.Color = next;
            _sphereColorsDirty = true;
        }
    }

    private static void RenderLines(Vector3 viewer, float maxDistSq)
    {
        if (_linesByID.Count == 0)
        {
            return;
        }

        // Segment tally per layer, bucketing the drawable slots in the same walk so
        // each layer renders its own list instead of re-walking the whole dictionary.
        // One entry unless something called SetGizmoLayer.
        _lineSegmentsByLayer.Clear();
        _lineLayerOrder.Clear();
        int bucketsUsed = 0;
        foreach (KeyValuePair<int, LineSlot> kvp in _linesByID)
        {
            LineSlot slot = kvp.Value;
            if (!IsLineDrawable(slot, viewer, maxDistSq))
            {
                continue;
            }
            int layer = ResolveLayer(slot.Layer);
            int segments = slot.Count - 1 + (slot.Loop ? 1 : 0);
            if (_lineSegmentsByLayer.TryGetValue(layer, out int running))
            {
                _lineSegmentsByLayer[layer] = running + segments;
                _lineLayerBuckets[_lineLayerOrder.IndexOf(layer)].Add(slot);
                continue;
            }
            _lineSegmentsByLayer.Add(layer, segments);
            if (_lineLayerBuckets.Count == bucketsUsed)
            {
                _lineLayerBuckets.Add(new List<LineSlot>());
            }
            _lineLayerBuckets[bucketsUsed].Clear();
            _lineLayerBuckets[bucketsUsed].Add(slot);
            _lineLayerOrder.Add(layer);
            bucketsUsed++;
        }

        for (int Index = 0; Index < _lineLayerOrder.Count; Index++)
        {
            int layer = _lineLayerOrder[Index];
            RenderLineLayer(layer, _lineSegmentsByLayer[layer], _lineLayerBuckets[Index]);
        }
    }

    private static readonly List<List<LineSlot>> _lineLayerBuckets = new List<List<LineSlot>>();

    private static bool IsLineDrawable(LineSlot slot, Vector3 viewer, float maxDistSq)
    {
        if (!slot.Active || slot.Count < 2)
        {
            return false;
        }
        return maxDistSq >= float.PositiveInfinity || (slot.Points[0] - viewer).sqrMagnitude <= maxDistSq;
    }

    private static void RenderLineLayer(int layer, int totalSegments, List<LineSlot> slots)
    {
        if (totalSegments == 0)
        {
            return;
        }
        LineBatch batch = GetLineBatch(layer);
        if (!EnsureLineResources(batch, totalSegments * 4))
        {
            return;
        }

        int v = 0;
        float maxHalfWidth = 0f;
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        int slotCount = slots.Count;
        for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
        {
            LineSlot slot = slots[slotIndex];

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

                AppendSegmentVertices(batch.Verts, ref v, a, b, colorA, colorB, halfWidth);

                min = Vector3.Min(min, Vector3.Min(a, b));
                max = Vector3.Max(max, Vector3.Max(a, b));
            }
        }

        batch.Mesh.SetVertexBufferData(batch.Verts, 0, 0, v, 0, LineMeshFlags);
        int indexCount = totalSegments * 6;
        if (indexCount != batch.IndexCountInUse)
        {
            batch.Mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles), LineMeshFlags);
            batch.IndexCountInUse = indexCount;
        }
        Vector3 pad = new Vector3(maxHalfWidth, maxHalfWidth, maxHalfWidth);
        Bounds bounds = default;
        bounds.SetMinMax(min - pad, max + pad);
        batch.Mesh.bounds = bounds;

        Graphics.DrawMesh(batch.Mesh, Matrix4x4.identity, _lineMaterial, layer, null, 0, null,
            ShadowCastingMode.Off, false, null, false);
    }

    private static LineBatch GetLineBatch(int layer)
    {
        if (!_lineBatches.TryGetValue(layer, out LineBatch batch))
        {
            batch = new LineBatch();
            _lineBatches.Add(layer, batch);
        }
        return batch;
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
            ApplyMaterialDepthMode(_sphereMaterial);
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

    private static bool EnsureLineResources(LineBatch batch, int vertexCount)
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
            ApplyMaterialDepthMode(_lineMaterial);
        }
        if (batch.Mesh == null)
        {
            batch.Mesh = new Mesh
            {
                name = "BasisGizmoLines",
            };
            batch.Mesh.MarkDynamic();
            batch.VertexCapacity = 0;
            batch.IndexCountInUse = -1;
        }
        if (vertexCount > batch.VertexCapacity)
        {
            int capacity = Mathf.Max(256, Mathf.NextPowerOfTwo(vertexCount));
            batch.Verts = new LineVertex[capacity];
            batch.Mesh.SetVertexBufferParams(capacity, LineVertexLayout);

            int quadCount = capacity / 4;
            int indexCapacity = quadCount * 6;
            uint[] indices = new uint[indexCapacity];
            FillQuadIndices(indices, quadCount);
            batch.Mesh.SetIndexBufferParams(indexCapacity, IndexFormat.UInt32);
            batch.Mesh.SetIndexBufferData(indices, 0, 0, indexCapacity, LineMeshFlags);
            batch.Mesh.subMeshCount = 1;
            batch.VertexCapacity = capacity;
            batch.IndexCountInUse = -1;
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
