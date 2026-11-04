#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Basis.Shims.Editor
{
    internal sealed class BasisCreatorGuideWindow : EditorWindow
    {
        private const string GuideFolder = "Packages/com.basis.shim/Shims/Editor/CreatorGuide/";
        private const string UssPath = "Packages/com.basis.shim/Shims/Editor/BasisCreatorGuide.uss";
        private const string SiteRoot = "https://docs.basisvr.org";
        private const string DocsPrefix = "/en/docs/";
        private const string ApiPrefix = "cilbox-api/";
        private const string RulesId = "cilbox-api/rules";
        [Serializable]
        private sealed class GuideIndex
        {
            public string baseUrl = SiteRoot + DocsPrefix;
            public GuideSection[] sections = Array.Empty<GuideSection>();
        }
        [Serializable]
        private sealed class GuideSection
        {
            public string title = string.Empty;
            public GuidePage[] pages = Array.Empty<GuidePage>();
        }
        [Serializable]
        private sealed class GuidePage
        {
            public string id = string.Empty, title = string.Empty, description = string.Empty, file = string.Empty;
        }
        private sealed class NavPage
        {
            public string Id, Title, Description, Body, Url;
            public Label Item;
        }
        private sealed class NavSection
        {
            public Label Header;
            public readonly List<NavPage> Pages = new List<NavPage>();
        }
        [SerializeField] private string _selected;
        private readonly List<NavSection> _sections = new List<NavSection>();
        private readonly Dictionary<string, NavPage> _pages = new Dictionary<string, NavPage>();
        private readonly Dictionary<string, VisualElement> _anchors = new Dictionary<string, VisualElement>();
        private ScrollView _content;
        private Label _noMatch;
        private string _search = string.Empty;
        private Font _mono;
        [MenuItem("Basis/Creator Guide", false, 1)]
        internal static void Open() => Open(null);
        internal static void Open(string pageId)
        {
            var window = GetWindow<BasisCreatorGuideWindow>();
            window.titleContent = new GUIContent(BasisCilboxLoc.Get("sdk.guide.window.title"));
            window.minSize = new Vector2(720f, 480f);
            window.Show();
            if (!string.IsNullOrEmpty(pageId)) window.Select(pageId, null);
        }
        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.AddToClassList("bcg-root");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (sheet != null) root.styleSheets.Add(sheet);
            _mono = EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf") as Font;
            var toolbar = new Toolbar();
            var search = new ToolbarSearchField();
            search.AddToClassList("bcg-search");
            search.RegisterValueChangedCallback(e => ApplySearch(e.newValue));
            toolbar.Add(search);
            root.Add(toolbar);
            var split = new TwoPaneSplitView(0, 230f, TwoPaneSplitViewOrientation.Horizontal);
            var nav = new ScrollView(ScrollViewMode.Vertical);
            nav.AddToClassList("bcg-nav");
            _content = new ScrollView(ScrollViewMode.Vertical);
            _content.AddToClassList("bcg-content");
            split.Add(nav);
            split.Add(_content);
            root.Add(split);
            BuildNavigation(nav);
            _noMatch = new Label(BasisCilboxLoc.Get("sdk.guide.noMatch"));
            _noMatch.AddToClassList("bcg-nav-empty");
            _noMatch.style.display = DisplayStyle.None;
            nav.Add(_noMatch);
            if (_pages.Count == 0)
            {
                _content.Add(Paragraph(Inline(BasisCilboxLoc.Get("sdk.guide.empty"))));
                return;
            }
            Select(_selected != null && _pages.ContainsKey(_selected) ? _selected : _sections[0].Pages[0].Id, null);
        }
        private void BuildNavigation(VisualElement nav)
        {
            _sections.Clear();
            _pages.Clear();
            var indexAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(GuideFolder + "index.json");
            GuideIndex index = indexAsset != null ? JsonUtility.FromJson<GuideIndex>(indexAsset.text) : null;
            if (index?.sections != null)
            {
                foreach (GuideSection section in index.sections)
                {
                    var navSection = AddSection(nav, section.title);
                    foreach (GuidePage page in section.pages)
                    {
                        var body = AssetDatabase.LoadAssetAtPath<TextAsset>(GuideFolder + page.file);
                        if (body == null) continue;
                        AddPage(nav, navSection, new NavPage { Id = page.id, Title = page.title, Description = page.description, Body = body.text, Url = index.baseUrl + page.id });
                    }
                }
            }
            var api = AddSection(nav, BasisCilboxLoc.Get("sdk.guide.section.api"));
            AddPage(nav, api, new NavPage { Id = RulesId, Title = BasisCilboxLoc.Get("sdk.cilbox.card.notes"), Description = string.Empty, Body = ApiSearchText(null) });
            foreach (string group in BasisCilboxApiCatalog.GroupOrder)
            {
                AddPage(nav, api, new NavPage { Id = ApiPrefix + group, Title = BasisCilboxLoc.Get(group), Description = string.Empty, Body = ApiSearchText(group) });
            }
            _sections.RemoveAll(s => s.Pages.Count == 0);
        }
        private NavSection AddSection(VisualElement nav, string title)
        {
            var section = new NavSection { Header = new Label(title) };
            section.Header.AddToClassList("bcg-nav-section");
            nav.Add(section.Header);
            _sections.Add(section);
            return section;
        }
        private void AddPage(VisualElement nav, NavSection section, NavPage page)
        {
            page.Item = new Label(page.Title);
            page.Item.AddToClassList("bcg-nav-item");
            page.Item.RegisterCallback<ClickEvent>(e => Select(page.Id, null));
            nav.Add(page.Item);
            section.Pages.Add(page);
            _pages[page.Id] = page;
        }
        private static string ApiSearchText(string group)
        {
            var text = new StringBuilder();
            if (group == null)
            {
                foreach (CilboxApiNote note in BasisCilboxApiCatalog.Notes)
                {
                    text.Append(BasisCilboxLoc.Get(note.TitleKey)).Append('\n').Append(BasisCilboxLoc.Get(note.BodyKey)).Append('\n').Append(note.Example).Append('\n');
                }
                return text.ToString();
            }
            foreach (CilboxApiEntry entry in BasisCilboxApiCatalog.Entries)
            {
                if (entry.GroupKey != group) continue;
                text.Append(BasisCilboxLoc.Get(entry.TitleKey)).Append('\n').Append(BasisCilboxLoc.Get(entry.SummaryKey)).Append('\n').Append(entry.Example).Append('\n');
            }
            return text.ToString();
        }
        private void ApplySearch(string value)
        {
            _search = value?.Trim() ?? string.Empty;
            bool any = false;
            foreach (NavSection section in _sections)
            {
                bool sectionVisible = false;
                foreach (NavPage page in section.Pages)
                {
                    bool visible = _search.Length == 0 || Contains(page.Title) || Contains(page.Description) || Contains(page.Body);
                    page.Item.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                    sectionVisible |= visible;
                }
                section.Header.style.display = sectionVisible ? DisplayStyle.Flex : DisplayStyle.None;
                any |= sectionVisible;
            }
            _noMatch.style.display = any ? DisplayStyle.None : DisplayStyle.Flex;
        }
        private bool Contains(string text) => !string.IsNullOrEmpty(text) && text.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0;
        private void Select(string id, string anchor)
        {
            if (_content == null) { _selected = id; return; }
            if (!_pages.TryGetValue(id, out NavPage page)) return;
            _selected = id;
            foreach (NavPage other in _pages.Values) other.Item.EnableInClassList("bcg-nav-item--selected", other == page);
            _content.Clear();
            _anchors.Clear();
            var header = new VisualElement();
            header.AddToClassList("bcg-page-header");
            var title = new Label(page.Title);
            title.AddToClassList("bcg-h1");
            header.Add(title);
            if (!string.IsNullOrEmpty(page.Description)) header.Add(Paragraph(Inline(page.Description), "bcg-lead"));
            var actions = new VisualElement();
            actions.AddToClassList("bcg-actions");
            if (!string.IsNullOrEmpty(page.Url)) actions.Add(new Button(() => Application.OpenURL(page.Url)) { text = BasisCilboxLoc.Get("sdk.guide.button.openOnline") });
            if (id.StartsWith(ApiPrefix, StringComparison.Ordinal)) actions.Add(new Button(() => EditorApplication.ExecuteMenuItem("Basis/Tools/Cilbox Permissions")) { text = BasisCilboxLoc.Get("sdk.cilbox.window.title") });
            header.Add(actions);
            _content.Add(header);
            if (id == RulesId) RenderRules();
            else if (id.StartsWith(ApiPrefix, StringComparison.Ordinal)) RenderApiGroup(id.Substring(ApiPrefix.Length));
            else RenderMarkdown(page.Body);
            _content.scrollOffset = Vector2.zero;
            if (!string.IsNullOrEmpty(anchor) && _anchors.TryGetValue(anchor, out VisualElement target))
            {
                _content.schedule.Execute(() => _content.ScrollTo(target)).ExecuteLater(50);
            }
        }
        private void RenderApiGroup(string group)
        {
            _content.Add(Paragraph(Inline(BasisCilboxLoc.Get("sdk.cilbox.page.api.legend")), "bcg-muted"));
            foreach (CilboxApiEntry entry in BasisCilboxApiCatalog.Entries)
            {
                if (entry.GroupKey != group) continue;
                var card = new VisualElement();
                card.AddToClassList("bcg-card");
                card.Add(Heading(Inline(BasisCilboxLoc.Get(entry.TitleKey)), 3));
                var pills = new VisualElement();
                pills.AddToClassList("bcg-pills");
                foreach (CilboxBoxInfo box in BasisCilboxPermissionModel.Boxes)
                {
                    var pill = new Label(BasisCilboxLoc.Get(box.LocalizationKey));
                    pill.AddToClassList("bcg-pill");
                    pill.AddToClassList(entry.AvailableIn(box) ? "bcg-pill--on" : "bcg-pill--off");
                    pills.Add(pill);
                }
                card.Add(pills);
                card.Add(Paragraph(Inline(BasisCilboxLoc.Get(entry.SummaryKey))));
                if (!string.IsNullOrEmpty(entry.Example)) card.Add(CodeBlock(entry.Example, null));
                _content.Add(card);
            }
        }
        private void RenderRules()
        {
            foreach (CilboxApiNote note in BasisCilboxApiCatalog.Notes)
            {
                var card = new VisualElement();
                card.AddToClassList("bcg-card");
                card.Add(Heading(Inline(BasisCilboxLoc.Get(note.TitleKey)), 3));
                card.Add(Paragraph(Inline(BasisCilboxLoc.Get(note.BodyKey))));
                if (!string.IsNullOrEmpty(note.Example)) card.Add(CodeBlock(note.Example, null));
                _content.Add(card);
            }
        }
        private static readonly Regex FenceOpen = new Regex(@"^```(\w*)(?:\s+title=""([^""]*)"")?\s*$");
        private static readonly Regex HeadingLine = new Regex(@"^(#{1,4})\s+(.*)$");
        private static readonly Regex ListLine = new Regex(@"^\s*(?:[-*]|(\d+)\.)\s+(.*)$");
        private static readonly Regex CalloutLine = new Regex(@"^>\s*\[!(\w+)\]\s*$");
        private void RenderMarkdown(string source)
        {
            string[] lines = source.Replace("\r\n", "\n").Split('\n');
            var paragraph = new StringBuilder();
            void Flush()
            {
                if (paragraph.Length == 0) return;
                _content.Add(Paragraph(Inline(paragraph.ToString())));
                paragraph.Clear();
            }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }
                Match fence = FenceOpen.Match(line);
                if (fence.Success)
                {
                    Flush();
                    var code = new StringBuilder();
                    for (i++; i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal); i++) code.Append(lines[i]).Append('\n');
                    _content.Add(CodeBlock(code.ToString().TrimEnd('\n'), fence.Groups[2].Success ? fence.Groups[2].Value : null));
                    continue;
                }
                Match heading = HeadingLine.Match(line);
                if (heading.Success)
                {
                    Flush();
                    string text = heading.Groups[2].Value;
                    Label label = Heading(Inline(text), heading.Groups[1].Length);
                    _anchors[Slug(text)] = label;
                    _content.Add(label);
                    continue;
                }
                if (line.StartsWith(">", StringComparison.Ordinal))
                {
                    Flush();
                    Match kind = CalloutLine.Match(line);
                    var callout = new VisualElement();
                    callout.AddToClassList("bcg-callout");
                    callout.AddToClassList("bcg-callout--" + (kind.Success ? kind.Groups[1].Value : "info"));
                    var text = new StringBuilder();
                    if (!kind.Success) text.Append(line.TrimStart('>').Trim()).Append(' ');
                    for (; i + 1 < lines.Length && lines[i + 1].StartsWith(">", StringComparison.Ordinal); i++)
                    {
                        string inner = lines[i + 1].Substring(1).Trim();
                        if (inner.Length == 0)
                        {
                            if (text.Length > 0) callout.Add(Paragraph(Inline(text.ToString())));
                            text.Clear();
                        }
                        else text.Append(inner).Append(' ');
                    }
                    if (text.Length > 0) callout.Add(Paragraph(Inline(text.ToString())));
                    _content.Add(callout);
                    continue;
                }
                if (line.StartsWith("|", StringComparison.Ordinal))
                {
                    Flush();
                    var rows = new List<string>();
                    for (; i < lines.Length && lines[i].StartsWith("|", StringComparison.Ordinal); i++) rows.Add(lines[i]);
                    i--;
                    _content.Add(Table(rows));
                    continue;
                }
                Match item = ListLine.Match(line);
                if (item.Success)
                {
                    Flush();
                    var list = new VisualElement();
                    list.AddToClassList("bcg-list");
                    for (; i < lines.Length; i++)
                    {
                        Match next = ListLine.Match(lines[i]);
                        if (!next.Success) break;
                        var row = new VisualElement();
                        row.AddToClassList("bcg-list-row");
                        var bullet = new Label(next.Groups[1].Success ? next.Groups[1].Value + "." : "•");
                        bullet.AddToClassList("bcg-list-bullet");
                        row.Add(bullet);
                        row.Add(Paragraph(Inline(next.Groups[2].Value), "bcg-list-text"));
                        list.Add(row);
                    }
                    i--;
                    _content.Add(list);
                    continue;
                }
                paragraph.Append(paragraph.Length > 0 ? " " : string.Empty).Append(line.Trim());
            }
            Flush();
        }
        private VisualElement Table(List<string> rows)
        {
            var table = new VisualElement();
            table.AddToClassList("bcg-table");
            bool header = true;
            foreach (string raw in rows)
            {
                string[] cells = raw.Trim().Trim('|').Split('|');
                if (Array.TrueForAll(cells, c => Regex.IsMatch(c.Trim(), @"^:?-{2,}:?$"))) continue;
                var row = new VisualElement();
                row.AddToClassList("bcg-table-row");
                if (header) row.AddToClassList("bcg-table-row--header");
                foreach (string cell in cells)
                {
                    Label label = Paragraph(Inline(cell.Trim()), "bcg-table-cell");
                    row.Add(label);
                }
                table.Add(row);
                header = false;
            }
            return table;
        }
        private Label Heading(string richText, int level)
        {
            var label = new Label(richText);
            label.AddToClassList("bcg-h" + Mathf.Clamp(level, 1, 4));
            label.RegisterCallback<UnityEngine.UIElements.Experimental.PointerUpLinkTagEvent>(e => OpenLink(e.linkID));
            return label;
        }
        private Label Paragraph(string richText, string extraClass = null)
        {
            var label = new Label(richText);
            label.AddToClassList("bcg-p");
            if (extraClass != null) label.AddToClassList(extraClass);
            label.RegisterCallback<UnityEngine.UIElements.Experimental.PointerUpLinkTagEvent>(e => OpenLink(e.linkID));
            return label;
        }
        private VisualElement CodeBlock(string code, string title)
        {
            var block = new VisualElement();
            block.AddToClassList("bcg-code");
            var bar = new VisualElement();
            bar.AddToClassList("bcg-code-bar");
            var name = new Label(title ?? string.Empty);
            name.AddToClassList("bcg-code-title");
            bar.Add(name);
            Button copy = null;
            copy = new Button(() =>
            {
                EditorGUIUtility.systemCopyBuffer = code;
                copy.text = BasisCilboxLoc.Get("sdk.guide.button.copied");
                copy.schedule.Execute(() => copy.text = BasisCilboxLoc.Get("sdk.cilbox.button.copy")).ExecuteLater(1500);
            }) { text = BasisCilboxLoc.Get("sdk.cilbox.button.copy") };
            copy.AddToClassList("bcg-code-copy");
            bar.Add(copy);
            block.Add(bar);
            var scroll = new ScrollView(ScrollViewMode.Horizontal);
            scroll.AddToClassList("bcg-code-scroll");
            var text = new Label(code) { enableRichText = false };
            text.AddToClassList("bcg-code-text");
            text.selection.isSelectable = true;
            if (_mono != null) text.style.unityFontDefinition = FontDefinition.FromFont(_mono);
            scroll.Add(text);
            block.Add(scroll);
            return block;
        }
        private void OpenLink(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (url.StartsWith("#", StringComparison.Ordinal))
            {
                if (_anchors.TryGetValue(url.Substring(1), out VisualElement target)) _content.ScrollTo(target);
                return;
            }
            if (url.StartsWith(DocsPrefix, StringComparison.Ordinal))
            {
                string path = url.Substring(DocsPrefix.Length);
                string anchor = null;
                int hash = path.IndexOf('#');
                if (hash >= 0)
                {
                    anchor = path.Substring(hash + 1);
                    path = path.Substring(0, hash);
                }
                if (_pages.ContainsKey(path.TrimEnd('/')))
                {
                    Select(path.TrimEnd('/'), anchor);
                    return;
                }
                Application.OpenURL(SiteRoot + url);
                return;
            }
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) Application.OpenURL(url);
        }
        private static string Slug(string heading)
        {
            var slug = new StringBuilder();
            foreach (char c in heading.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') slug.Append(c);
                else if (c == ' ') slug.Append('-');
            }
            return slug.ToString();
        }
        private static readonly Regex InlineToken = new Regex(@"`([^`]+)`|\*\*(.+?)\*\*|\[([^\]]+)\]\(([^)\s]+)\)");
        private static string Inline(string markdown)
        {
            var output = new StringBuilder();
            int last = 0;
            foreach (Match m in InlineToken.Matches(markdown))
            {
                output.Append(Plain(markdown.Substring(last, m.Index - last)));
                if (m.Groups[1].Success) output.Append("<color=#D7BA7D><noparse>").Append(m.Groups[1].Value).Append("</noparse></color>");
                else if (m.Groups[2].Success) output.Append("<b>").Append(Inline(m.Groups[2].Value)).Append("</b>");
                else output.Append("<link=\"").Append(m.Groups[4].Value).Append("\"><color=#58A6FF><u>").Append(Inline(m.Groups[3].Value)).Append("</u></color></link>");
                last = m.Index + m.Length;
            }
            output.Append(Plain(markdown.Substring(last)));
            return output.ToString();
        }
        private static string Plain(string text) => text.IndexOf('<') >= 0 ? "<noparse>" + text + "</noparse>" : text;
    }
}
#endif
