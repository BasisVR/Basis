using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The shared Basis editor look, for IMGUI windows and inspectors.
///
/// It mirrors the UI Toolkit identity used by the synced-pickup inspector and the video player
/// windows: dark translucent rounded cards, a bold pink title, muted body text, pill-shaped status
/// chips and flat accent buttons. Windows written with <c>OnGUI</c> can adopt it without being
/// rewritten in UI Toolkit — wrap the body in <see cref="BeginPage"/>/<see cref="EndPage"/>, put
/// each block in a <see cref="Card"/> and use the row/pill/bar helpers instead of raw LabelFields.
/// </summary>
[NoAutoStaticsCleanup]
public static class BasisEditorUI
{
    /// <summary>
    /// True while the editor is on the light skin. The identity is the same in both — same pink,
    /// same layout — with the surfaces and text swapped so a card still lifts off the window
    /// background and body text still reads against it.
    /// </summary>
    public static bool Light => !EditorGUIUtility.isProSkin;

    public static Color Accent => Light ? new Color(214f / 255f, 26f / 255f, 78f / 255f) : new Color(239f / 255f, 40f / 255f, 90f / 255f);
    public static Color Good => Light ? new Color(0.11f, 0.50f, 0.20f) : new Color(0.36f, 0.80f, 0.42f);
    public static Color Warn => Light ? new Color(0.60f, 0.42f, 0.02f) : new Color(0.90f, 0.75f, 0.20f);
    public static Color Bad => Light ? new Color(0.72f, 0.15f, 0.15f) : new Color(0.90f, 0.36f, 0.36f);
    public static Color Muted => Light ? new Color(0.34f, 0.34f, 0.34f) : new Color(0.72f, 0.72f, 0.72f);
    public static Color Value => Light ? new Color(0.13f, 0.13f, 0.13f) : new Color(0.90f, 0.90f, 0.90f);

    private static Color CardBg => Light ? new Color(1f, 1f, 1f, 0.58f) : new Color(0f, 0f, 0f, 0.54f);
    private static Color HeaderBg => Light ? new Color(1f, 1f, 1f, 0.75f) : new Color(0f, 0f, 0f, 0.40f);
    private static Color TrackBg => Light ? new Color(0.78f, 0.78f, 0.78f, 1f) : new Color(0.15f, 0.15f, 0.15f, 1f);
    private static Color InfoBg => Light ? new Color(205f / 255f, 224f / 255f, 245f / 255f, 0.90f) : new Color(40f / 255f, 70f / 255f, 100f / 255f, 0.60f);
    private static Color WarnBg => Light ? new Color(250f / 255f, 232f / 255f, 180f / 255f, 0.90f) : new Color(110f / 255f, 80f / 255f, 20f / 255f, 0.70f);
    private static Color ErrorBg => Light ? new Color(250f / 255f, 210f / 255f, 210f / 255f, 0.90f) : new Color(120f / 255f, 30f / 255f, 30f / 255f, 0.70f);

    private const float Radius = 5f;

    /// <summary>How a status value reads at a glance — drives the pill / value colour.</summary>
    public enum State { Neutral, Good, Warn, Bad }

    // ---------------------------------------------------------------- styles

    private static GUIStyle _card;
    private static GUIStyle _title;
    private static GUIStyle _subtitle;
    private static GUIStyle _sectionTitle;
    private static GUIStyle _note;
    private static GUIStyle _label;
    private static GUIStyle _value;
    private static GUIStyle _pill;
    private static GUIStyle _foldout;
    private static GUIStyle _primary;
    private static GUIStyle _secondary;
    private static GUIStyle _tab;
    private static GUIStyle _help;
    private static GUIStyle _page;
    private static GUIStyle _mono;
    private static GUIStyle _reasonPass;
    private static GUIStyle _reasonFail;
    private static GUIStyle _tabActive;
    private static GUIStyle _tabIdle;
    private static GUIStyle _onDark;

    /// <summary>
    /// Mini label for text drawn on top of an always-dark surface — a meter track, a plot canvas.
    /// It stays light on both skins, where <c>EditorStyles.miniLabel</c> would turn dark-on-dark
    /// as soon as the user switches to the light theme.
    /// </summary>
    public static GUIStyle OnDarkMiniLabel
    {
        get
        {
            Init();
            if (_onDark == null)
            {
                _onDark = new GUIStyle(EditorStyles.miniLabel);
                _onDark.normal.textColor = new Color(0.88f, 0.88f, 0.90f);
            }
            return _onDark;
        }
    }

    private static bool _stylesAreLight;

    private static void Init()
    {
        // Styles bake their colours in, so they have to be rebuilt when the user switches skin.
        if (_card != null && _stylesAreLight == Light) return;
        _stylesAreLight = Light;
        _helpStyles.Clear();
        _reasonPass = null;
        _reasonFail = null;
        _tabActive = null;
        _tabIdle = null;
        _onDark = null;

        _card = new GUIStyle { padding = new RectOffset(8, 8, 6, 8), margin = new RectOffset(0, 0, 0, 6) };
        _page = new GUIStyle { padding = new RectOffset(8, 8, 8, 8) };

        _title = new GUIStyle(EditorStyles.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            wordWrap = true,
        };
        _title.normal.textColor = Light ? new Color(0.10f, 0.10f, 0.10f) : Color.white;

        _subtitle = new GUIStyle(EditorStyles.label) { fontSize = 11, wordWrap = true };
        _subtitle.normal.textColor = Muted;

        _sectionTitle = new GUIStyle(EditorStyles.label) { fontSize = 12, fontStyle = FontStyle.Bold };
        _sectionTitle.normal.textColor = Accent;

        _note = new GUIStyle(EditorStyles.label) { fontSize = 10, wordWrap = true };
        _note.normal.textColor = Light ? new Color(0.42f, 0.42f, 0.42f) : new Color(0.63f, 0.63f, 0.63f);

        _label = new GUIStyle(EditorStyles.label) { wordWrap = false };
        _label.normal.textColor = Light ? new Color(0.25f, 0.25f, 0.25f) : new Color(0.78f, 0.78f, 0.78f);

        _value = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight, wordWrap = true };
        _value.normal.textColor = Value;

        _mono = new GUIStyle(EditorStyles.label) { font = EditorStyles.miniFont, fontSize = 11, wordWrap = true, richText = true };
        _mono.normal.textColor = Value;

        _pill = new GUIStyle(EditorStyles.label)
        {
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(8, 8, 1, 1),
        };
        _pill.normal.textColor = Color.white;

        _foldout = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold, fontSize = 12 };
        _foldout.normal.textColor = Accent;
        _foldout.onNormal.textColor = Accent;
        _foldout.focused.textColor = Accent;
        _foldout.onFocused.textColor = Accent;
        _foldout.active.textColor = Accent;
        _foldout.onActive.textColor = Accent;
        _foldout.hover.textColor = Accent;
        _foldout.onHover.textColor = Accent;

        _primary = new GUIStyle(EditorStyles.miniButton)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 12,
            padding = new RectOffset(12, 12, 6, 6),
        };
        _primary.normal.background = null;
        _primary.normal.textColor = Color.white;
        _primary.hover.textColor = Color.white;
        _primary.active.textColor = Color.white;

        _secondary = new GUIStyle(_primary) { fontSize = 11 };
        if (Light)
        {
            // The neutral button is a pale fill on the light skin, so its label has to darken with it.
            Color secondaryText = new Color(0.13f, 0.13f, 0.13f);
            _secondary.normal.textColor = secondaryText;
            _secondary.hover.textColor = secondaryText;
            _secondary.active.textColor = secondaryText;
        }

        _tab = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 12, padding = new RectOffset(10, 10, 5, 5) };

        _help = new GUIStyle(EditorStyles.label)
        {
            wordWrap = true,
            padding = new RectOffset(8, 8, 5, 5),
        };
    }

    // ---------------------------------------------------------------- primitives

    /// <summary>Rounded, alpha-blended fill. No-op outside Repaint, so it is safe to call from layout code.</summary>
    public static void Fill(Rect rect, Color color, float radius = Radius)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color,
            Vector4.zero, new Vector4(radius, radius, radius, radius));
    }

    /// <summary>A 1px horizontal rule with the card's spacing.</summary>
    public static void Divider()
    {
        Rect r = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
        r.y += 2f;
        Fill(new Rect(r.x, r.y, r.width, 1f), Light ? new Color(0f, 0f, 0f, 0.16f) : new Color(1f, 1f, 1f, 0.14f), 0f);
        GUILayout.Space(4f);
    }

    // ---------------------------------------------------------------- page

    private static int _pageDepth;

    /// <summary>Opens a padded, scrolling window body. Always pair with <see cref="EndPage"/>.</summary>
    public static Vector2 BeginPage(Vector2 scroll)
    {
        Init();
        Vector2 s = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.BeginVertical(_page);
        _pageDepth++;
        return s;
    }

    public static void EndPage()
    {
        if (_pageDepth <= 0) return;
        _pageDepth--;
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndScrollView();
    }

    /// <summary>The window's title block: bold white title over a muted one-line explanation.</summary>
    public static void Header(string title, string subtitle = null)
    {
        Init();
        Rect r = EditorGUILayout.BeginVertical(_card);
        Fill(r, HeaderBg);
        if (Event.current != null && Event.current.type == EventType.Repaint)
        {
            Fill(new Rect(r.x, r.yMax - 3f, r.width, 3f), Accent, 2f);
        }
        EditorGUILayout.LabelField(title, _title);
        if (!string.IsNullOrEmpty(subtitle))
        {
            EditorGUILayout.LabelField(subtitle, _subtitle);
        }
        EditorGUILayout.EndVertical();
        GUILayout.Space(2f);
    }

    // ---------------------------------------------------------------- cards

    /// <summary>
    /// A titled dark card. Use with <c>using (BasisEditorUI.Card("Status")) { ... }</c>; everything drawn
    /// inside the using block lands in the card body.
    /// </summary>
    public static CardScope Card(string title = null) => new CardScope(title);

    public struct CardScope : IDisposable
    {
        public CardScope(string title)
        {
            Init();
            Rect r = EditorGUILayout.BeginVertical(_card);
            Fill(r, CardBg);
            if (!string.IsNullOrEmpty(title))
            {
                EditorGUILayout.LabelField(title, _sectionTitle);
            }
        }

        public void Dispose() => EditorGUILayout.EndVertical();
    }

    /// <summary>
    /// A card whose body collapses behind a pink foldout header. Always pair with <see cref="EndFoldout"/>
    /// — the card's vertical group has to close whether or not the body is visible.
    /// </summary>
    public static bool BeginFoldout(ref bool expanded, string title)
    {
        Init();
        Rect r = EditorGUILayout.BeginVertical(_card);
        Fill(r, CardBg);
        expanded = EditorGUILayout.Foldout(expanded, title, true, _foldout);
        return expanded;
    }

    public static void EndFoldout() => EditorGUILayout.EndVertical();

    /// <summary>A pink section heading for grouping inside a card.</summary>
    public static void SectionTitle(string text)
    {
        Init();
        EditorGUILayout.LabelField(text, _sectionTitle);
    }

    // ---------------------------------------------------------------- text

    /// <summary>Small muted explanatory line — the "what this actually means" text under a control.</summary>
    public static void Note(string text)
    {
        Init();
        EditorGUILayout.LabelField(text, _note);
    }

    /// <summary>Rounded, tinted help panel. Replaces <c>EditorGUILayout.HelpBox</c> inside Basis windows.</summary>
    public static void Help(string text, MessageType type = MessageType.Info)
    {
        Init();
        Color bg;
        switch (type)
        {
            case MessageType.Warning: bg = WarnBg; break;
            case MessageType.Error: bg = ErrorBg; break;
            case MessageType.None: bg = Light ? new Color(1f, 1f, 1f, 0.55f) : new Color(0f, 0f, 0f, 0.25f); break;
            default: bg = InfoBg; break;
        }

        GUIStyle style = HelpStyle(type);
        Rect r = GUILayoutUtility.GetRect(new GUIContent(text), style, GUILayout.ExpandWidth(true));
        Fill(r, bg, 4f);
        GUI.Label(r, text, style);
        GUILayout.Space(3f);
    }

    private static readonly Dictionary<MessageType, GUIStyle> _helpStyles = new Dictionary<MessageType, GUIStyle>();

    private static GUIStyle HelpStyle(MessageType type)
    {
        if (_helpStyles.TryGetValue(type, out GUIStyle cached)) return cached;
        Color fg;
        switch (type)
        {
            case MessageType.Warning: fg = Light ? new Color(0.36f, 0.26f, 0.02f) : new Color(1f, 0.94f, 0.82f); break;
            case MessageType.Error: fg = Light ? new Color(0.45f, 0.09f, 0.09f) : new Color(1f, 0.86f, 0.86f); break;
            case MessageType.None: fg = Muted; break;
            default: fg = Light ? new Color(0.10f, 0.22f, 0.36f) : new Color(0.86f, 0.90f, 0.94f); break;
        }
        var style = new GUIStyle(_help);
        style.normal.textColor = fg;
        _helpStyles[type] = style;
        return style;
    }

    /// <summary>Multi-line readout in the mini font (CSV paths, stat dumps).</summary>
    public static void Readout(string text)
    {
        Init();
        EditorGUILayout.LabelField(text, _mono);
    }

    // ---------------------------------------------------------------- rows

    /// <summary>Label on the left, bold value on the right — the card's basic status line.</summary>
    public static void Row(string label, string value) => Row(label, value, Value);

    public static void Row(string label, string value, State state) => Row(label, value, ColorFor(state));

    public static void Row(string label, string value, Color valueColor)
    {
        Init();
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, _label, GUILayout.MinWidth(60f));
        Color prev = _value.normal.textColor;
        _value.normal.textColor = valueColor;
        EditorGUILayout.LabelField(value, _value);
        _value.normal.textColor = prev;
        EditorGUILayout.EndHorizontal();
    }

    /// <summary>Label on the left, a coloured pill on the right (ON/OFF, PASS/FAIL, connection state).</summary>
    public static void PillRow(string label, string value, State state)
    {
        Init();
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, _label, GUILayout.MinWidth(60f));
        Pill(value, state);
        EditorGUILayout.EndHorizontal();
    }

    /// <summary>A standalone pill chip, laid out at its natural width.</summary>
    public static void Pill(string value, State state)
    {
        Init();
        GUIContent content = new GUIContent(value);
        Rect r = GUILayoutUtility.GetRect(content, _pill, GUILayout.MinWidth(46f), GUILayout.ExpandWidth(false));
        Fill(r, PillBg(state), r.height * 0.5f);
        GUI.Label(r, content, _pill);
    }

    /// <summary>
    /// A labelled progress/level bar with the value printed on the right. Widen
    /// <paramref name="labelWidth"/> when the labels are long enough to clip — a table of them reads
    /// as one column only if they all use the same width.
    /// </summary>
    public static void Bar(string label, float fill01, string value, Color? fillColor = null, float labelWidth = 130f)
    {
        Init();
        EditorGUILayout.BeginHorizontal();
        if (!string.IsNullOrEmpty(label))
        {
            EditorGUILayout.LabelField(label, _label, GUILayout.Width(labelWidth));
        }

        Rect track = GUILayoutUtility.GetRect(40f, 14f, GUILayout.ExpandWidth(true));
        Fill(track, TrackBg, 3f);
        float t = Mathf.Clamp01(fill01);
        if (t > 0f)
        {
            Fill(new Rect(track.x, track.y, track.width * t, track.height), fillColor ?? Accent, 3f);
        }

        if (!string.IsNullOrEmpty(value))
        {
            EditorGUILayout.LabelField(value, _value, GUILayout.Width(92f));
        }
        EditorGUILayout.EndHorizontal();
    }

    /// <summary>Big PASS/FAIL banner plus the gate's reason, as used by the test/sweep windows.</summary>
    public static void PassFail(bool pass, string reason)
    {
        Init();
        EditorGUILayout.BeginHorizontal();
        Pill(BasisEditorLocalization.Get(pass ? "sdk.editorUi.pass" : "sdk.editorUi.fail"), pass ? State.Good : State.Bad);
        GUILayout.Space(6f);
        if (_reasonPass == null)
        {
            _reasonPass = new GUIStyle(_help) { padding = new RectOffset(0, 0, 3, 3) };
            _reasonPass.normal.textColor = Muted;
            _reasonFail = new GUIStyle(_reasonPass);
            _reasonFail.normal.textColor = Light ? new Color(0.62f, 0.10f, 0.10f) : new Color(1f, 0.80f, 0.80f);
        }
        EditorGUILayout.LabelField(reason ?? string.Empty, pass ? _reasonPass : _reasonFail);
        EditorGUILayout.EndHorizontal();
    }

    // ---------------------------------------------------------------- buttons

    /// <summary>The accent-filled call to action. One per card at most.</summary>
    public static bool PrimaryButton(string text, float height = 28f, params GUILayoutOption[] options)
    {
        Init();
        return FlatButton(text, Accent, _primary, height, options);
    }

    /// <summary>A neutral grey action button.</summary>
    public static bool SecondaryButton(string text, float height = 22f, params GUILayoutOption[] options)
    {
        Init();
        return FlatButton(text, Light ? new Color(0.82f, 0.82f, 0.82f) : new Color(0.31f, 0.31f, 0.31f), _secondary, height, options);
    }

    private static bool FlatButton(string text, Color bg, GUIStyle style, float height, GUILayoutOption[] options)
    {
        GUIContent content = new GUIContent(text);
        var opts = new List<GUILayoutOption>(options ?? new GUILayoutOption[0]);
        if (height > 0f) opts.Add(GUILayout.Height(height));
        Rect r = GUILayoutUtility.GetRect(content, style, opts.ToArray());

        bool hover = r.Contains(Event.current.mousePosition);
        Color hovered = Light ? Color.Lerp(bg, Color.black, 0.10f) : Color.Lerp(bg, Color.white, 0.12f);
        Color off = Color.Lerp(bg, Light ? Color.white : Color.black, 0.45f);
        Color tint = GUI.enabled ? (hover ? hovered : bg) : off;
        Fill(r, tint, 6f);
        return GUI.Button(r, content, style);
    }

    /// <summary>A pink-underlined tab strip. Returns the selected index.</summary>
    public static int Tabs(int selected, params string[] labels)
    {
        Init();
        if (labels == null || labels.Length == 0) return selected;

        Rect strip = GUILayoutUtility.GetRect(10f, 24f, GUILayout.ExpandWidth(true));
        Fill(strip, Light ? new Color(0f, 0f, 0f, 0.09f) : new Color(0f, 0f, 0f, 0.30f), 4f);

        if (_tabActive == null)
        {
            _tabActive = new GUIStyle(_tab);
            _tabActive.normal.textColor = Light ? new Color(0.10f, 0.10f, 0.10f) : Color.white;
            _tabIdle = new GUIStyle(_tab);
            _tabIdle.normal.textColor = Muted;
        }

        float w = strip.width / labels.Length;
        for (int i = 0; i < labels.Length; i++)
        {
            Rect r = new Rect(strip.x + w * i, strip.y, w, strip.height);
            bool active = i == selected;
            if (active)
            {
                Fill(new Rect(r.x + 2f, r.yMax - 2f, r.width - 4f, 2f), Accent, 1f);
            }

            if (GUI.Button(r, labels[i], active ? _tabActive : _tabIdle)) selected = i;
        }

        GUILayout.Space(4f);
        return selected;
    }

    // ---------------------------------------------------------------- helpers

    public static Color ColorFor(State state)
    {
        switch (state)
        {
            case State.Good: return Good;
            case State.Warn: return Warn;
            case State.Bad: return Bad;
            default: return Value;
        }
    }

    private static Color PillBg(State state)
    {
        switch (state)
        {
            case State.Good: return new Color(45f / 255f, 140f / 255f, 60f / 255f);
            case State.Warn: return new Color(170f / 255f, 130f / 255f, 30f / 255f);
            case State.Bad: return new Color(160f / 255f, 50f / 255f, 50f / 255f);
            default: return new Color(0.31f, 0.31f, 0.31f);
        }
    }

    /// <summary>Picks a state from a value against a warn/bad threshold, for "higher is worse" numbers.</summary>
    public static State Threshold(float value, float warnAbove, float badAbove)
        => value >= badAbove ? State.Bad : value >= warnAbove ? State.Warn : State.Good;

    public static State OnOff(bool on) => on ? State.Good : State.Neutral;
}
