#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

public partial class BasisProjectSetup : EditorWindow
{
    // ─────────────────────────── OpenCollective constants ───────────────────────────

    private const string OC_SLUG = "basis";
    private const string OC_API = "https://opencollective.com/" + OC_SLUG + ".json";
    private const string OC_DONATE = "https://opencollective.com/" + OC_SLUG + "/donate";
    private const string OC_PAGE = "https://opencollective.com/" + OC_SLUG;

    private static readonly Color OC_BarBackground = new Color(0.10f, 0.10f, 0.12f, 1f);
    private static readonly Color OC_BarBorder     = new Color(0.04f, 0.04f, 0.06f, 1f);
    private static readonly Color OC_CardBg        = new Color(0.18f, 0.18f, 0.20f, 1f);
    private static readonly Color OC_CardBorder    = new Color(0.08f, 0.08f, 0.10f, 1f);
    private static readonly Color OC_HeroStart     = new Color(0.12f, 0.30f, 0.55f, 1f);
    private static readonly Color OC_HeroEnd       = new Color(0.18f, 0.55f, 0.65f, 1f);
    private static readonly Color OC_SupportBg     = new Color(0.13f, 0.18f, 0.22f, 1f);
    private static readonly Color OC_FillStart     = new Color(0.25f, 0.65f, 1.00f, 1f);
    private static readonly Color OC_FillEnd       = new Color(0.30f, 0.95f, 0.85f, 1f);
    private static readonly Color OC_DonateStart   = new Color(0.95f, 0.30f, 0.45f, 1f);
    private static readonly Color OC_DonateEnd     = new Color(1.00f, 0.55f, 0.30f, 1f);

    // ─────────────────────────── State ───────────────────────────

    private OcCollectiveData _ocCollective;
    private bool _ocLoading;
    private string _ocStatus = "";
    private DateTime _ocLastFetchedUtc = DateTime.MinValue;
    private string _ocRawJson = "";
    private Vector2 _ocScroll;

    private float[] _ocDisplayedProgress = Array.Empty<float>();
    private float[] _ocTargetProgress = Array.Empty<float>();
    private double _ocLastTickTime;
    private float _ocShimmerPhase;
    private bool _ocTickHooked;

    private static GUIStyle _ocHeroStyle;
    private static GUIStyle _ocSubheroStyle;
    private static GUIStyle _ocStatValueStyle;
    private static GUIStyle _ocStatLabelStyle;
    private static GUIStyle _ocGoalTitleStyle;
    private static GUIStyle _ocGoalTypeStyle;
    private static GUIStyle _ocMessageStyle;
    private static GUIStyle _ocDonateLabelStyle;
    private static GUIStyle _ocAmountStyle;
    private static GUIStyle _ocSectionHeaderStyle;

    // ─────────────────────────── Lifecycle hooks (called from OnEnable/OnDisable) ───────────────────────────

    private void OnFundingEnable()
    {
        _ocLastTickTime = EditorApplication.timeSinceStartup;
        if (!_ocTickHooked)
        {
            EditorApplication.update += OnFundingTick;
            _ocTickHooked = true;
        }
        _ = FetchOpenCollectiveAsync();
    }

    private void OnFundingDisable()
    {
        if (_ocTickHooked)
        {
            EditorApplication.update -= OnFundingTick;
            _ocTickHooked = false;
        }
    }

    private void OnFundingTick()
    {
        double now = EditorApplication.timeSinceStartup;
        float dt = (float)(now - _ocLastTickTime);
        _ocLastTickTime = now;
        if (dt > 0.1f) dt = 0.1f;

        _ocShimmerPhase = (_ocShimmerPhase + dt * 0.45f) % 1f;

        for (int i = 0; i < _ocDisplayedProgress.Length; i++)
        {
            float diff = _ocTargetProgress[i] - _ocDisplayedProgress[i];
            if (Mathf.Abs(diff) > 0.0008f)
                _ocDisplayedProgress[i] = Mathf.Lerp(_ocDisplayedProgress[i], _ocTargetProgress[i], 1f - Mathf.Pow(0.001f, dt));
            else
                _ocDisplayedProgress[i] = _ocTargetProgress[i];
        }

        // Only repaint when this tab is in front to avoid wasted work.
        if (_tab == Tab.Funding) Repaint();
    }

    // ─────────────────────────── Public-ish helper ───────────────────────────

    [MenuItem("Basis/Community/OpenCollective Donations")]
    public static void ShowFundingTab()
    {
        var window = GetWindow<BasisProjectSetup>("Basis Project Setup");
        window.minSize = new Vector2(640, 560);
        window._tab = Tab.Funding;
        EditorPrefs.SetInt(PREF_TAB, (int)Tab.Funding);
        window.Show();
    }

    // ─────────────────────────── Tab body ───────────────────────────

    private void DrawTab_Funding()
    {
        EnsureFundingStyles();

        _ocScroll = EditorGUILayout.BeginScrollView(_ocScroll);

        DrawFundingHero();
        EditorGUILayout.Space(8);
        DrawSupportMessageCard();
        EditorGUILayout.Space(10);
        DrawFundingActionRow();
        EditorGUILayout.Space(12);

        if (!string.IsNullOrEmpty(_ocStatus))
        {
            EditorGUILayout.HelpBox(_ocStatus, MessageType.Info);
            EditorGUILayout.Space(6);
        }

        if (_ocCollective != null)
        {
            DrawStatCardsRow();
            EditorGUILayout.Space(14);
            DrawGoalsSection();
            EditorGUILayout.Space(14);
            DrawAboutCard();
            EditorGUILayout.Space(14);
            DrawQuickStartSection();
            EditorGUILayout.Space(10);
            DrawFundingFooter();
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawFundingHero()
    {
        Rect rect = EditorGUILayout.GetControlRect(false, 96);
        Rect padded = OcInset(rect, 4, 0);
        OcDrawGradientRect(padded, OC_HeroStart, OC_HeroEnd);
        OcDrawBorder(padded, OC_CardBorder);

        Rect inner = OcInset(padded, 18, 14);
        Rect title = new Rect(inner.x, inner.y, inner.width, 32);
        Rect sub = new Rect(inner.x, inner.y + 36, inner.width, inner.height - 36);

        GUI.Label(title, Tr("projectSetup.funding.heroTitle", "Welcome to Basis"), _ocHeroStyle);
        GUI.Label(sub, Tr("projectSetup.funding.heroSubtitle",
            "Open-source VR framework, sustained by community donations through Open Collective. " +
            "Every dollar pays for more developer hours on Basis."), _ocSubheroStyle);
    }

    private void DrawSupportMessageCard()
    {
        Rect rect = EditorGUILayout.GetControlRect(false, 64);
        Rect padded = OcInset(rect, 4, 0);
        OcDrawCard(padded, OC_SupportBg, OC_CardBorder);

        Rect accent = new Rect(padded.x, padded.y, 4, padded.height);
        OcDrawGradientRect(accent, OC_FillStart, OC_FillEnd);

        Rect inner = new Rect(padded.x + 14, padded.y + 8, padded.width - 26, padded.height - 16);
        GUI.Label(inner,
            Tr("projectSetup.funding.supportMessage",
                "More funding = more people working on Basis. Donations directly translate into contributor hours building, fixing, and improving the open-source VR framework."),
            _ocMessageStyle);
    }

    private void DrawFundingActionRow()
    {
        Rect row = EditorGUILayout.GetControlRect(false, 40);
        float spacing = 6f;
        float x = row.x + 4;
        float w = (row.width - 8 - 2 * spacing) / 3f;

        DrawDonateButton(new Rect(x, row.y + 4, w, 32));
        x += w + spacing;

        DrawPillButton(new Rect(x, row.y + 4, w, 32), Tr("projectSetup.funding.openPage", "Open Page"), () => Application.OpenURL(OC_PAGE));
        x += w + spacing;

        DrawPillButton(new Rect(x, row.y + 4, w, 32),
            _ocLoading ? Tr("projectSetup.funding.refreshing", "Refreshing...") : Tr("projectSetup.funding.refresh", "Refresh"),
            () => { if (!_ocLoading) _ = FetchOpenCollectiveAsync(); },
            disabled: _ocLoading);
    }

    private void DrawDonateButton(Rect rect)
    {
        bool hover = rect.Contains(Event.current.mousePosition);
        float pulse = 0.55f + 0.45f * Mathf.Sin((float)EditorApplication.timeSinceStartup * 2.4f);

        Rect glow = new Rect(rect.x - 3, rect.y - 3, rect.width + 6, rect.height + 6);
        EditorGUI.DrawRect(glow, new Color(OC_DonateStart.r, OC_DonateStart.g, OC_DonateStart.b, 0.20f * pulse));

        Color s = hover ? OcLighten(OC_DonateStart, 0.08f) : OC_DonateStart;
        Color e = hover ? OcLighten(OC_DonateEnd, 0.08f) : OC_DonateEnd;
        OcDrawGradientRect(rect, s, e);
        OcDrawBorder(rect, new Color(0.4f, 0.0f, 0.1f, 1f));
        EditorGUI.DrawRect(new Rect(rect.x + 1, rect.y + 1, rect.width - 2, 1), new Color(1, 1, 1, 0.30f));

        GUI.Label(rect, Tr("projectSetup.funding.donate", "Donate to Basis"), _ocDonateLabelStyle);

        EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
        {
            Application.OpenURL(OC_DONATE);
            Event.current.Use();
        }
    }

    private void DrawPillButton(Rect rect, string label, Action onClick, bool disabled = false)
    {
        bool hover = rect.Contains(Event.current.mousePosition) && !disabled;
        Color bg = disabled
            ? new Color(0.18f, 0.18f, 0.20f, 1f)
            : (hover ? new Color(0.30f, 0.30f, 0.34f, 1f) : new Color(0.24f, 0.24f, 0.27f, 1f));
        EditorGUI.DrawRect(rect, bg);
        OcDrawBorder(rect, OC_CardBorder);

        var s = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 12,
            normal = { textColor = disabled ? new Color(1, 1, 1, 0.4f) : new Color(1, 1, 1, 0.92f) }
        };
        GUI.Label(rect, label, s);

        if (!disabled)
        {
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                onClick?.Invoke();
                Event.current.Use();
            }
        }
    }

    private void DrawStatCardsRow()
    {
        Rect row = EditorGUILayout.GetControlRect(false, 78);
        float spacing = 8f;
        float x = row.x + 4;
        float w = (row.width - 8 - 2 * spacing) / 3f;

        DrawStatCard(new Rect(x, row.y, w, row.height),
            FormatMoney(_ocCollective.balance, _ocCollective.currency),
            Tr("projectSetup.funding.statBalance", "Current Balance"),
            new Color(0.30f, 0.62f, 1.00f), new Color(0.45f, 0.95f, 0.85f));
        x += w + spacing;

        DrawStatCard(new Rect(x, row.y, w, row.height),
            FormatMoney(_ocCollective.yearlyIncome, _ocCollective.currency),
            Tr("projectSetup.funding.statYearly", "Yearly Budget"),
            new Color(0.96f, 0.55f, 0.30f), new Color(1.00f, 0.85f, 0.20f));
        x += w + spacing;

        DrawStatCard(new Rect(x, row.y, w, row.height),
            _ocCollective.backersCount.ToString("N0"),
            Tr("projectSetup.funding.statBackers", "Backers"),
            new Color(0.60f, 0.35f, 0.95f), new Color(1.00f, 0.45f, 0.78f));
    }

    private void DrawStatCard(Rect rect, string value, string label, Color stripStart, Color stripEnd)
    {
        OcDrawCard(rect, OC_CardBg, OC_CardBorder);
        Rect strip = new Rect(rect.x + 1, rect.y + 1, rect.width - 2, 4);
        OcDrawGradientRect(strip, stripStart, stripEnd);

        Rect valueRect = new Rect(rect.x, rect.y + 16, rect.width, 32);
        Rect labelRect = new Rect(rect.x, rect.y + 50, rect.width, 16);
        GUI.Label(valueRect, value, _ocStatValueStyle);
        GUI.Label(labelRect, label.ToUpper(), _ocStatLabelStyle);
    }

    private void DrawGoalsSection()
    {
        var goals = _ocCollective.GetGoals();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(Tr("projectSetup.funding.goalsHeader", "Funding Goals"), _ocSectionHeaderStyle, GUILayout.Height(20));
        GUILayout.FlexibleSpace();
        EditorGUILayout.LabelField(string.Format(
            goals.Count == 1
                ? Tr("projectSetup.funding.goalsCount", "{0} goal")
                : Tr("projectSetup.funding.goalsCountPlural", "{0} goals"),
            goals.Count),
            EditorStyles.miniLabel, GUILayout.Width(80));
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(4);

        if (_ocDisplayedProgress.Length != goals.Count)
        {
            int oldLen = _ocDisplayedProgress.Length;
            Array.Resize(ref _ocDisplayedProgress, goals.Count);
            Array.Resize(ref _ocTargetProgress, goals.Count);
            for (int i = oldLen; i < goals.Count; i++) _ocDisplayedProgress[i] = 0f;
        }

        if (goals.Count == 0)
        {
            EditorGUILayout.HelpBox(
                Tr("projectSetup.funding.noGoals",
                    "No goals are configured on the OpenCollective page yet. " +
                    "They can be set on opencollective.com under Settings > Goals."),
                MessageType.None);
            return;
        }

        for (int i = 0; i < goals.Count; i++)
        {
            DrawFancyGoal(goals[i], i);
            EditorGUILayout.Space(6);
        }
    }

    private void DrawFancyGoal(OcGoalData goal, int index)
    {
        long current = ResolveGoalCurrent(goal);
        long target = goal.amount;
        _ocTargetProgress[index] = target > 0 ? Mathf.Clamp01((float)current / target) : 0f;
        float displayed = _ocDisplayedProgress[index];

        bool reached = _ocTargetProgress[index] >= 1f;
        Rect rect = EditorGUILayout.GetControlRect(false, 88);
        Rect padded = OcInset(rect, 4, 0);
        OcDrawCard(padded, OC_CardBg, OC_CardBorder);

        Rect inner = new Rect(padded.x + 12, padded.y + 10, padded.width - 24, padded.height - 20);

        string title = string.IsNullOrEmpty(goal.title) ? FormatGoalType(goal.type) : goal.title;
        Rect titleRect = new Rect(inner.x, inner.y, inner.width - 110, 18);
        Rect typeRect = new Rect(inner.x + inner.width - 110, inner.y, 110, 18);
        GUI.Label(titleRect, title, _ocGoalTitleStyle);
        GUI.Label(typeRect, reached ? Tr("projectSetup.funding.reached", "REACHED") : FormatGoalType(goal.type).ToUpper(), _ocGoalTypeStyle);

        Rect barRect = new Rect(inner.x, inner.y + 24, inner.width, 22);
        Color start = reached ? new Color(0.35f, 0.95f, 0.55f) : OC_FillStart;
        Color end = reached ? new Color(1.00f, 0.85f, 0.25f) : OC_FillEnd;
        DrawFancyProgressBar(barRect, displayed, start, end);

        Rect amountRect = new Rect(inner.x, inner.y + 52, inner.width, 14);
        string amountLine = target > 0
            ? string.Format(
                Tr("projectSetup.funding.amountLine", "{0}  raised of  {1}  ({2:F1}%)"),
                FormatMoney(current, _ocCollective.currency),
                FormatMoney(target, _ocCollective.currency),
                _ocTargetProgress[index] * 100f)
            : FormatMoney(current, _ocCollective.currency);
        GUI.Label(amountRect, amountLine, _ocAmountStyle);
    }

    private void DrawFancyProgressBar(Rect rect, float progress, Color start, Color end)
    {
        EditorGUI.DrawRect(rect, OC_BarBackground);
        OcDrawBorder(rect, OC_BarBorder);

        progress = Mathf.Clamp01(progress);
        if (progress <= 0f) return;

        Rect fill = new Rect(rect.x + 1, rect.y + 1, (rect.width - 2) * progress, rect.height - 2);
        if (fill.width < 1f) return;

        OcDrawGradientRect(fill, start, end);

        EditorGUI.DrawRect(new Rect(fill.x, fill.y, fill.width, 1), new Color(1, 1, 1, 0.28f));
        EditorGUI.DrawRect(new Rect(fill.x, fill.y + fill.height - 1, fill.width, 1), new Color(0, 0, 0, 0.25f));

        float shimmerWidth = Mathf.Min(fill.width, 70f);
        float shimmerX = fill.x + (fill.width + shimmerWidth) * _ocShimmerPhase - shimmerWidth;
        float clipL = Mathf.Max(shimmerX, fill.x);
        float clipR = Mathf.Min(shimmerX + shimmerWidth, fill.x + fill.width);
        for (float px = clipL; px < clipR; px += 1f)
        {
            float t = (px - shimmerX) / shimmerWidth;
            float a = Mathf.Sin(t * Mathf.PI) * 0.32f;
            EditorGUI.DrawRect(new Rect(px, fill.y, 1, fill.height), new Color(1, 1, 1, a));
        }
    }

    private void DrawQuickStartSection()
    {
        EditorGUILayout.LabelField(Tr("projectSetup.funding.quickStart", "Quick Start"), _ocSectionHeaderStyle);
        EditorGUILayout.Space(2);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(Tr("projectSetup.funding.quickStartBody", "Jump straight into the right docs."), EditorStyles.wordWrappedLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(Tr("projectSetup.funding.linkAvatar", "Avatar Docs"))) Application.OpenURL(BASIS_AVATARS);
                if (GUILayout.Button(Tr("projectSetup.funding.linkWorld", "World Docs"))) Application.OpenURL(BASIS_WORLDS);
                if (GUILayout.Button(Tr("projectSetup.funding.linkGettingStarted", "Getting Started"))) Application.OpenURL(BASIS_GETTING_STARTED);
                if (GUILayout.Button(Tr("projectSetup.funding.linkSite", "basisvr.org"))) Application.OpenURL(BASIS_SITE);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Tr("projectSetup.funding.settingUp", "What are you setting up today?"), EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawFirstRunRadio(FirstRunKind.Avatar, Tr("projectSetup.funding.kindAvatar", "Avatar"));
                DrawFirstRunRadio(FirstRunKind.World, Tr("projectSetup.funding.kindWorld", "World"));
                DrawFirstRunRadio(FirstRunKind.Project, Tr("projectSetup.funding.kindProject", "Project"));
            }

            if (GUI.changed)
                EditorPrefs.SetInt(PREF_FIRST_RUN_KIND, (int)_firstRunKind);

            if (_firstRunKind == FirstRunKind.Avatar || _firstRunKind == FirstRunKind.World)
            {
                EditorGUILayout.HelpBox(
                    Tr("projectSetup.funding.installModulesHelp",
                        "Install Windows, Linux, and Android Build Support via Unity Hub. Use IL2CPP for best compatibility (required on Android)."),
                    MessageType.Info);
            }
        }

        if (_firstRunKind == FirstRunKind.Avatar || _firstRunKind == FirstRunKind.World || _firstRunKind == FirstRunKind.Project)
        {
            EditorGUILayout.Space(6);
            DrawFirstRunSteps();
        }
    }

    private void DrawAboutCard()
    {
        Rect rect = EditorGUILayout.GetControlRect(false, 96);
        Rect padded = OcInset(rect, 4, 0);
        OcDrawCard(padded, OC_CardBg, OC_CardBorder);

        Rect inner = new Rect(padded.x + 14, padded.y + 12, padded.width - 28, padded.height - 24);
        Rect titleRect = new Rect(inner.x, inner.y, inner.width, 20);
        Rect bodyRect = new Rect(inner.x, inner.y + 24, inner.width, 32);
        Rect linkRect = new Rect(inner.x, inner.y + inner.height - 26, 160, 26);

        GUI.Label(titleRect, Tr("projectSetup.funding.aboutTitle", "About Basis"), _ocSectionHeaderStyle);
        GUI.Label(bodyRect,
            Tr("projectSetup.funding.aboutBody",
                "Creator-First, Creative Freedom — Basis helps you stand up VR projects quickly. " +
                "Open-Source (MIT). Strong systems for networking, input, and presence."),
            _ocMessageStyle);

        DrawPillButton(linkRect, Tr("projectSetup.funding.visitSite", "Visit basisvr.org"), () => Application.OpenURL(BASIS_SITE));
    }

    private void DrawFundingFooter()
    {
        if (_ocLastFetchedUtc != DateTime.MinValue)
        {
            EditorGUILayout.LabelField(
                string.Format(Tr("projectSetup.funding.lastUpdated", "Last updated {0}    Source: {1}"),
                    _ocLastFetchedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    OC_API),
                EditorStyles.miniLabel);
        }
    }

    // ─────────────────────────── Style init ───────────────────────────

    private static void EnsureFundingStyles()
    {
        if (_ocHeroStyle != null) return;

        _ocHeroStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 22,
            wordWrap = true,
            normal = { textColor = new Color(1f, 1f, 1f, 0.98f) }
        };
        _ocSubheroStyle = new GUIStyle(EditorStyles.label)
        {
            fontSize = 12,
            wordWrap = true,
            normal = { textColor = new Color(1f, 1f, 1f, 0.82f) }
        };
        _ocStatValueStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 18,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.65f, 0.95f, 0.85f, 1f) }
        };
        _ocStatLabelStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 9,
            normal = { textColor = new Color(1f, 1f, 1f, 0.55f) }
        };
        _ocGoalTitleStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 13,
            normal = { textColor = new Color(1f, 1f, 1f, 0.95f) }
        };
        _ocGoalTypeStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(1f, 1f, 1f, 0.5f) }
        };
        _ocMessageStyle = new GUIStyle(EditorStyles.label)
        {
            wordWrap = true,
            fontSize = 12,
            normal = { textColor = new Color(1f, 1f, 1f, 0.88f) }
        };
        _ocDonateLabelStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 13,
            normal = { textColor = Color.white }
        };
        _ocAmountStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = new Color(1f, 1f, 1f, 0.75f) }
        };
        _ocSectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 14,
            normal = { textColor = new Color(1f, 1f, 1f, 0.92f) }
        };
    }

    // ─────────────────────────── Drawing primitives ───────────────────────────

    private static Rect OcInset(Rect rect, float padX, float padY)
    {
        return new Rect(rect.x + padX, rect.y + padY, rect.width - padX * 2f, rect.height - padY * 2f);
    }

    private static void OcDrawGradientRect(Rect rect, Color start, Color end)
    {
        int slices = Mathf.Max(1, Mathf.RoundToInt(rect.width));
        for (int i = 0; i < slices; i++)
        {
            float t = slices > 1 ? i / (float)(slices - 1) : 0f;
            EditorGUI.DrawRect(new Rect(rect.x + i, rect.y, 1, rect.height), Color.Lerp(start, end, t));
        }
    }

    private static void OcDrawBorder(Rect rect, Color color)
    {
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), color);
        EditorGUI.DrawRect(new Rect(rect.x, rect.y + rect.height - 1, rect.width, 1), color);
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1, rect.height), color);
        EditorGUI.DrawRect(new Rect(rect.x + rect.width - 1, rect.y, 1, rect.height), color);
    }

    private static void OcDrawCard(Rect rect, Color bg, Color border)
    {
        EditorGUI.DrawRect(rect, bg);
        OcDrawBorder(rect, border);
    }

    private static Color OcLighten(Color c, float amt)
    {
        return new Color(Mathf.Clamp01(c.r + amt), Mathf.Clamp01(c.g + amt), Mathf.Clamp01(c.b + amt), c.a);
    }

    // ─────────────────────────── Data helpers ───────────────────────────

    private long GetTotalRaised()
    {
        if (_ocCollective.totalAmountRaised > 0) return _ocCollective.totalAmountRaised;
        if (_ocCollective.totalAmountReceived > 0) return _ocCollective.totalAmountReceived;
        return 0;
    }

    private long ResolveGoalCurrent(OcGoalData goal)
    {
        if (goal.amountRaised > 0) return goal.amountRaised;

        switch (goal.type?.ToLowerInvariant())
        {
            case "yearlybudget":
            case "yearly":
                return _ocCollective.yearlyIncome;
            case "monthlybudget":
            case "monthly":
                return _ocCollective.yearlyIncome / 12;
            case "balance":
            default:
                return _ocCollective.balance;
        }
    }

    private static string FormatGoalType(string type)
    {
        if (string.IsNullOrEmpty(type)) return Tr("projectSetup.funding.goalDefault", "Goal");
        switch (type.ToLowerInvariant())
        {
            case "yearlybudget":
            case "yearly":   return Tr("projectSetup.funding.goalYearly", "Yearly Budget");
            case "monthlybudget":
            case "monthly":  return Tr("projectSetup.funding.goalMonthly", "Monthly Budget");
            case "balance":  return Tr("projectSetup.funding.goalBalance", "Balance");
            default:         return type;
        }
    }

    private static string FormatMoney(long cents, string currency)
    {
        decimal whole = cents / 100m;
        string code = string.IsNullOrEmpty(currency) ? "USD" : currency.ToUpperInvariant();
        string symbol;
        switch (code)
        {
            case "USD": symbol = "$"; break;
            case "EUR": symbol = "€"; break;
            case "GBP": symbol = "£"; break;
            case "JPY": symbol = "¥"; break;
            default: symbol = ""; break;
        }
        return string.IsNullOrEmpty(symbol)
            ? $"{whole:N2} {code}"
            : $"{symbol}{whole:N2} {code}";
    }

    // ─────────────────────────── Network ───────────────────────────

    private async Task FetchOpenCollectiveAsync()
    {
        if (_ocLoading) return;

        _ocLoading = true;
        _ocStatus = Tr("projectSetup.funding.statusContacting", "Contacting opencollective.com...");
        Repaint();

        try
        {
            using var req = UnityWebRequest.Get(OC_API);
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 15;

            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (req.result != UnityWebRequest.Result.Success)
            {
                _ocStatus = string.Format(Tr("projectSetup.funding.statusRequestFailed", "Request failed ({0}): {1}"), req.responseCode, req.error);
                return;
            }

            _ocRawJson = req.downloadHandler.text ?? "";
            OcCollectiveData parsed;
            try
            {
                parsed = JsonConvert.DeserializeObject<OcCollectiveData>(_ocRawJson);
            }
            catch (JsonException ex)
            {
                _ocStatus = string.Format(Tr("projectSetup.funding.statusParseFailed", "Failed to parse JSON: {0}"), ex.Message);
                return;
            }

            if (parsed == null)
            {
                _ocStatus = Tr("projectSetup.funding.statusEmptyPayload", "OpenCollective returned an empty payload.");
                return;
            }

            _ocCollective = parsed;

            var goals = _ocCollective.GetGoals();
            _ocDisplayedProgress = new float[goals.Count];
            _ocTargetProgress = new float[goals.Count];

            _ocLastFetchedUtc = DateTime.UtcNow;
            _ocStatus = "";
        }
        catch (Exception ex)
        {
            _ocStatus = string.Format(Tr("projectSetup.funding.statusError", "Error: {0}"), ex.Message);
            Debug.LogError("OpenCollective fetch error: " + ex);
        }
        finally
        {
            _ocLoading = false;
            Repaint();
        }
    }

    // ─────────────────────────── DTOs ───────────────────────────

    private class OcCollectiveData
    {
        public string slug;
        public string name;
        public string currency;
        public long balance;
        public long yearlyIncome;
        public long totalAmountReceived;
        public long totalAmountRaised;
        public int backersCount;

        public List<OcGoalData> goals;
        public OcCollectiveSettings settings;

        public List<OcGoalData> GetGoals()
        {
            if (goals != null && goals.Count > 0) return goals;
            if (settings?.goals != null && settings.goals.Count > 0) return settings.goals;
            return new List<OcGoalData>();
        }
    }

    private class OcCollectiveSettings
    {
        public List<OcGoalData> goals;
    }

    private class OcGoalData
    {
        public string type;
        public long amount;
        public string title;
        public string description;
        public long amountRaised;
    }
}
#endif
