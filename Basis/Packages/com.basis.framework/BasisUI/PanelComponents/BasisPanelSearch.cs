using System;
using System.Collections;
using System.Collections.Generic;
using Basis.BTween;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    /// <summary>One control that matched a query, with the section it sits under.</summary>
    public readonly struct BasisPanelSearchHit
    {
        public readonly PanelElementDescriptor Descriptor;

        /// <summary>
        /// The section the control is filed under, or null when it sits outside every section. Held
        /// as the toggle rather than its title so a caller that jumps to this result can open it.
        /// </summary>
        public readonly PanelSectionToggle Section;

        public readonly string Title;

        public string SectionTitle =>
            Section != null && Section.Descriptor != null ? Section.Descriptor.Title : null;

        public BasisPanelSearchHit(PanelElementDescriptor descriptor, string title, PanelSectionToggle section)
        {
            Descriptor = descriptor;
            Title = title;
            Section = section;
        }
    }

    /// <summary>
    /// A search field pinned to the top of a panel page that filters the page down to the rows whose
    /// title, description or tooltip contain what was typed.
    ///
    /// <para>Nothing has to be registered. Every control in the menu is a <see cref="PanelComponent"/>
    /// carrying a <see cref="PanelElementDescriptor"/>, so the built hierarchy already holds every
    /// label the user can read; filtering reads that hierarchy instead of a parallel index, which
    /// means a page picks up search for free as rows are added to it.</para>
    ///
    /// <para>The hierarchy is walked once, into a flat pre-order array whose labels are already
    /// lowercased — see <see cref="Prepare"/>. A keystroke is then two linear passes over that array,
    /// so cost does not scale with how deeply the page nests or how many components a row carries.
    /// Applying the result is debounced: the string work is cheap, but switching a few hundred rows
    /// on and off is a uGUI layout pass, and one of those per character is what makes typing drag.</para>
    ///
    /// <para>Only rows this search hid are ever shown again. A page that keeps a control hidden for
    /// its own reasons — one that applies to a single mode, say — keeps it hidden, and a control the
    /// page reveals mid-search is filtered like any other on the next <see cref="Refresh"/>.</para>
    /// </summary>
    public sealed class BasisPanelSearch
    {
        /// <summary>
        /// Quiet time after the last keystroke before the page is re-filtered. Long enough that a run
        /// of characters costs one layout pass instead of one each, short enough to read as live.
        /// </summary>
        private const float FilterDelay = 0.1f;

        /// <summary>How deep a stack of lazy sections inside lazy sections is followed while opening.</summary>
        private const int MaxExpandPasses = 8;

        private static readonly List<PanelSectionToggle> _sectionBuffer = new();
        private static readonly List<PanelSectionToggle> _chainBuffer = new();
        private static readonly List<PanelElementDescriptor> _descriptorBuffer = new();
        private static readonly List<Transform> _contentBuffer = new();
        private static readonly List<int> _indexBuffer = new();
        private static readonly Dictionary<Transform, int> _lookup = new();

        /// <summary>
        /// One row of the flattened page. Pre-order, so a node always precedes its descendants and
        /// its whole subtree is the contiguous range <c>[self, End)</c>.
        /// </summary>
        private struct Node
        {
            public Transform Transform;
            public RectTransform Rect;
            public GameObject Object;
            public PanelElementDescriptor Descriptor;
            public PanelComponent Control;
            public PanelSectionToggle Section;
            public PanelSectionToggle OwningSection;
            public string Haystack;
            public string Title;
            public int Parent;
            public int End;
            public bool Managed;
            public bool Exempt;

            /// <summary>Carries a layout group or fitter, so its height has to be recomputed by hand.</summary>
            public bool HasLayout;
        }

        private readonly RectTransform _page;
        private readonly PanelTextField _field;
        private readonly HashSet<GameObject> _exempt = new();
        private readonly HashSet<GameObject> _hidden = new();
        private readonly Dictionary<PanelSectionToggle, bool> _sectionState = new();
        private readonly List<PanelSectionToggle> _lazyExpanded = new();
        private readonly List<int> _sectionNodes = new();
        private readonly List<int[]> _sectionContents = new();

        private Node[] _nodes = Array.Empty<Node>();
        private bool[] _visible = Array.Empty<bool>();
        private bool[] _dirty = Array.Empty<bool>();
        private int _count;
        private bool _indexed;
        private bool _prepared;

        private string _query = string.Empty;
        private string _queryLower = string.Empty;
        private string _pendingQuery = string.Empty;
        private bool _pendingApply;
        private Coroutine _filterRoutine;
        private int _matchCount;
        private PanelElementDescriptor _noResults;

        /// <summary>The container being filtered — the page's scroll content.</summary>
        public RectTransform Page => _page;

        /// <summary>The field itself, for callers that want to move it or read its state.</summary>
        public PanelTextField Field => _field;

        /// <summary>The trimmed query currently applied, or empty when the page is unfiltered.</summary>
        public string Query => _query;

        /// <summary>How many rows the last applied query matched. Zero while not searching.</summary>
        public int MatchCount => _matchCount;

        /// <summary>
        /// Raised as soon as the query changes — before the page is re-filtered, so an owner can start
        /// its own work in the same beat rather than a debounce behind.
        /// </summary>
        public Action<string> OnQueryChanged;

        public static BasisPanelSearch Attach(RectTransform page) =>
            Attach(page, BasisLocalization.Get("ui.search"), BasisLocalization.Get("ui.search.tooltip"));

        public static BasisPanelSearch Attach(RectTransform page, string title, string tooltip)
        {
            if (page == null)
            {
                BasisDebug.LogError($"{nameof(BasisPanelSearch)} requires a page to filter.");
                return null;
            }

            PanelTextField field = PanelTextField.CreateNew(PanelTextField.TextFieldStyles.Entry, page);
            return field == null ? null : new BasisPanelSearch(page, field, null, title, tooltip);
        }

        /// <summary>
        /// Indexes a page without putting a field on it. For pages searched from somewhere else — the
        /// header search popup drives the query and reads <see cref="CollectHits"/>, so the page keeps
        /// its whole layout for content instead of spending its first row on a field.
        /// <para><paramref name="host"/> is any live behaviour on the page, used to run the one-frame
        /// settle before <see cref="ScrollTo"/> measures a row that a reopened section just rebuilt.</para>
        /// </summary>
        public static BasisPanelSearch AttachHeadless(RectTransform page, MonoBehaviour host)
        {
            if (page == null)
            {
                BasisDebug.LogError($"{nameof(BasisPanelSearch)} requires a page to index.");
                return null;
            }

            return new BasisPanelSearch(page, null, host, null, null);
        }

        private BasisPanelSearch(RectTransform page, PanelTextField field, MonoBehaviour host, string title, string tooltip)
        {
            _page = page;
            _field = field;
            _host = host;

            if (_field == null) return;

            _field.Descriptor.SetTitle(title);
            _field.Descriptor.SetTooltip(tooltip);
            _field.Descriptor.SetIcon(AddressableAssets.Sprites.Search);
            _field.transform.SetSiblingIndex(0);
            KeepVisible(_field);

            // Hooked on the input field rather than PanelTextField.OnValueChanged: that one only
            // fires once the field is committed, and a search that waits for Enter reads as broken.
            _field._inputField.onValueChanged.AddListener(OnFieldEdited);
        }

        private readonly MonoBehaviour _host;

        /// <summary>Whatever is alive on this page and can run a coroutine — the field, or the page itself.</summary>
        private MonoBehaviour CoroutineHost
        {
            get
            {
                if (_field != null && _field.isActiveAndEnabled) return _field;
                return _host != null && _host.isActiveAndEnabled ? _host : null;
            }
        }

        /// <summary>
        /// Excludes an element from filtering. Use it for rows that frame the page rather than belong
        /// to it — the search field itself, a "which camera" selector — which stay useful with a query
        /// typed. Exemption covers the element's whole subtree.
        /// </summary>
        public void KeepVisible(Component element)
        {
            if (element == null) return;
            _exempt.Add(element.gameObject);
            _indexed = false;
        }

        /// <summary>
        /// Applies a query the user did not type here. Used to carry one search across sibling pages
        /// so switching to another tab lands on it already filtered.
        /// </summary>
        public void SetQuery(string query, bool notify = true)
        {
            string text = query ?? string.Empty;
            if (_field != null) _field.SetValueWithoutNotify(text);
            RequestQuery(text, notify, defer: false);
        }

        /// <summary>
        /// Records a query without touching the page. For a page that is off screen: filtering costs a
        /// layout pass whose result nobody sees, so it waits for <see cref="ApplyPendingQuery"/>.
        /// </summary>
        public void SetQueryDeferred(string query)
        {
            string text = query ?? string.Empty;
            if (_field != null) _field.SetValueWithoutNotify(text);
            RequestQuery(text, notify: false, defer: true);
        }

        /// <summary>Catches the page up to the last query set on it. No-op when it is already current.</summary>
        public void ApplyPendingQuery()
        {
            if (!_pendingApply) return;
            ApplyNow(fromEdit: false);
        }

        /// <summary>Re-applies the current query. Call after the page adds, removes or re-shows rows.</summary>
        public void Refresh()
        {
            if (_query.Length == 0 && !_pendingApply) return;
            ApplyNow(fromEdit: false);
        }

        /// <summary>
        /// Drops the cached view of the page. Call when rows are added or removed behind search's
        /// back; the next filter rebuilds it.
        /// </summary>
        public void Invalidate() => _indexed = false;

        /// <summary>Drops the filter and puts the page back the way it was.</summary>
        public void Clear() => SetQuery(string.Empty);

        private void OnFieldEdited(string text) => RequestQuery(text, notify: true, defer: false);

        private void RequestQuery(string text, bool notify, bool defer)
        {
            string next = Normalize(text);
            if (string.Equals(next, _pendingQuery, StringComparison.Ordinal)) return;

            _pendingQuery = next;
            _pendingApply = true;

            if (notify) OnQueryChanged?.Invoke(next);

            if (defer)
            {
                StopFilterRoutine();
                return;
            }

            // Clearing is applied at once — the page should come back the instant the field empties,
            // and there is no run of keystrokes left to coalesce.
            if (next.Length == 0)
            {
                StopFilterRoutine();
                ApplyNow(fromEdit: true);
                return;
            }

            ScheduleApply();
        }

        /// <summary>
        /// Restarts the quiet timer. Each keystroke drops the pass the previous one queued rather than
        /// stacking another on top, so a burst of typing costs one filter, not one per character.
        /// </summary>
        private void ScheduleApply()
        {
            StopFilterRoutine();

            MonoBehaviour host = CoroutineHost;
            if (host == null)
            {
                ApplyNow(fromEdit: true);
                return;
            }

            _filterHost = host;
            _filterRoutine = host.StartCoroutine(ApplyAfterDelay());
        }

        private IEnumerator ApplyAfterDelay()
        {
            yield return new WaitForSecondsRealtime(FilterDelay);
            _filterRoutine = null;
            ApplyNow(fromEdit: true);
        }

        private void StopFilterRoutine()
        {
            if (_filterRoutine == null) return;
            // Stopped on whoever started it: the host can differ between passes once the field is gone.
            if (_filterHost != null) _filterHost.StopCoroutine(_filterRoutine);
            _filterRoutine = null;
            _filterHost = null;
        }

        private MonoBehaviour _filterHost;

        private void ApplyNow(bool fromEdit)
        {
            bool queryChanged = !string.Equals(_pendingQuery, _query, StringComparison.Ordinal);
            _pendingApply = false;
            _query = _pendingQuery;
            _queryLower = _query.ToLowerInvariant();

            if (_query.Length == 0)
            {
                Restore();
                return;
            }

            Prepare();
            Filter();

            // A new query rearranges the page under the user, so put them back at the top of it —
            // otherwise the matches are up there and the view is still down where they were reading.
            if (fromEdit && queryChanged && _matchCount > 0) ScrollToTop();
        }

        public static string Normalize(string query) => query == null ? string.Empty : query.Trim();

        /// <summary>True when any of the descriptor's user-readable text contains the query.</summary>
        public static bool Matches(PanelElementDescriptor descriptor, string query)
        {
            if (descriptor == null || string.IsNullOrEmpty(query)) return false;
            return Contains(descriptor.Title, query)
                || Contains(descriptor.Description, query)
                || Contains(descriptor.Tooltip, query);
        }

        private static bool Contains(string value, string query) =>
            !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Ordinal test against an already-lowercased haystack. Case-insensitive comparison is what a
        /// per-keystroke scan cannot afford, so it is paid once when the page is indexed instead.
        /// </summary>
        private static bool ContainsLower(string haystack, string lowerQuery) =>
            haystack != null && haystack.IndexOf(lowerQuery, StringComparison.Ordinal) >= 0;

        // ------------------
        // PREPARING THE PAGE
        // ------------------

        /// <summary>
        /// Makes the page searchable: opens the sections whose rows only exist while expanded, then
        /// flattens the hierarchy into the array every later pass reads. Idempotent, and the expensive
        /// half of search — a caller with several pages to make searchable should spread these out
        /// rather than run them all on one keystroke.
        /// </summary>
        public bool Prepare() => Prepare(false);

        /// <summary>
        /// <paramref name="rescan"/> looks for sections that have closed since the page was last
        /// prepared and opens them again, at the cost of a walk of the page — for a caller preparing
        /// once per search rather than once per keystroke. Returns whether the page moved, so that
        /// caller can tell an answer that is now stale from one that still stands.
        /// </summary>
        public bool Prepare(bool rescan)
        {
            bool expanded = ExpandLazySections(rescan);
            bool indexed = EnsureIndex();
            return expanded || indexed;
        }

        private bool ExpandLazySections(bool rescan)
        {
            if (_prepared && !rescan) return false;
            _prepared = true;

            bool expanded = false;
            bool suppressed = BasisMenuStateMemory.SuppressSectionWrites;
            BasisMenuStateMemory.SuppressSectionWrites = true;
            try
            {
                // Opening a lazy section builds rows that carry sections of their own, and those are
                // collapsed as they land, so one pass only ever reaches the outermost layer. Repeats
                // until a pass finds nothing left to open.
                for (int pass = 0; pass < MaxExpandPasses; pass++)
                {
                    if (!ExpandLazySectionPass()) break;
                    expanded = true;
                }
            }
            finally
            {
                BasisMenuStateMemory.SuppressSectionWrites = suppressed;
                _sectionBuffer.Clear();
                _contentBuffer.Clear();
            }

            if (expanded) _indexed = false;
            return expanded;
        }

        private bool ExpandLazySectionPass()
        {
            _page.GetComponentsInChildren(true, _sectionBuffer);

            // Recorded before this pass expands anything, and only for sections not seen before, so a
            // section search opened keeps the state the user left it in rather than the one it is in
            // because search opened it.
            for (int i = 0; i < _sectionBuffer.Count; i++)
            {
                PanelSectionToggle section = _sectionBuffer[i];
                if (section != null && !_sectionState.ContainsKey(section))
                {
                    _sectionState[section] = section.Expanded;
                }
            }

            bool expanded = false;
            for (int i = 0; i < _sectionBuffer.Count; i++)
            {
                PanelSectionToggle section = _sectionBuffer[i];
                if (section == null || section.Expanded) continue;

                // A collapsed section with nothing registered under it is a lazy one: its rows
                // were destroyed when it closed, so they have to be rebuilt to be searchable.
                GetSectionContents(section, _contentBuffer);
                if (_contentBuffer.Count > 0) continue;

                section.SetExpanded(true);
                if (!_lazyExpanded.Contains(section)) _lazyExpanded.Add(section);
                expanded = true;
            }

            return expanded;
        }

        /// <summary>Puts the page back the way it was found and drops everything search cached.</summary>
        private void Restore()
        {
            bool changed = _hidden.Count > 0;
            foreach (GameObject hidden in _hidden)
            {
                if (hidden != null) hidden.SetActive(true);
            }
            _hidden.Clear();
            _matchCount = 0;

            if (_noResults != null) _noResults.SetActive(false);

            if (_prepared)
            {
                _prepared = false;

                bool suppressed = BasisMenuStateMemory.SuppressSectionWrites;
                BasisMenuStateMemory.SuppressSectionWrites = true;
                try
                {
                    // Notify on the way back down as well, so a lazy section drops the rows search
                    // made it build instead of leaving them alive behind a closed header. Innermost
                    // first, so a nested one is closed while it still exists to be closed.
                    for (int i = _lazyExpanded.Count - 1; i >= 0; i--)
                    {
                        PanelSectionToggle section = _lazyExpanded[i];
                        if (section != null) section.SetExpanded(false);
                    }
                    _lazyExpanded.Clear();

                    foreach (KeyValuePair<PanelSectionToggle, bool> entry in _sectionState)
                    {
                        PanelSectionToggle section = entry.Key;
                        if (section != null && section.Expanded != entry.Value)
                        {
                            section.SetExpandedWithoutNotify(entry.Value);
                        }
                    }
                    _sectionState.Clear();
                }
                finally
                {
                    BasisMenuStateMemory.SuppressSectionWrites = suppressed;
                }
            }

            // Every row coming back at once grows the cards that were holding them; without this the
            // page keeps the compact shape it had while filtered and the rows overlap.
            if (changed && _indexed) RebuildAllLayout();

            _indexed = false;
        }

        // ------------------
        // THE FLATTENED PAGE
        // ------------------

        private bool EnsureIndex()
        {
            if (_indexed && (_count == 0 || _nodes[0].Transform != null)) return false;

            _count = 0;
            _sectionNodes.Clear();
            _sectionContents.Clear();
            _lookup.Clear();

            AppendChildren(_page, -1);

            if (_visible.Length < _count)
            {
                int size = Mathf.NextPowerOfTwo(Mathf.Max(64, _count));
                _visible = new bool[size];
                _dirty = new bool[size];
            }

            BuildSectionContents();

            _lookup.Clear();
            _indexed = true;
            return true;
        }

        private void AppendChildren(Transform parent, int parentIndex)
        {
            int children = parent.childCount;
            for (int i = 0; i < children; i++)
            {
                Transform child = parent.GetChild(i);
                int index = Append(child, parentIndex);
                AppendChildren(child, index);
                _nodes[index].End = _count;
            }
        }

        private int Append(Transform transform, int parent)
        {
            if (_count == _nodes.Length)
            {
                Array.Resize(ref _nodes, _nodes.Length == 0 ? 256 : _nodes.Length * 2);
            }

            int index = _count++;
            GameObject go = transform.gameObject;

            transform.TryGetComponent(out PanelElementDescriptor descriptor);
            transform.TryGetComponent(out PanelComponent control);
            transform.TryGetComponent(out PanelSectionToggle section);

            _nodes[index] = new Node
            {
                Transform = transform,
                Rect = transform as RectTransform,
                Object = go,
                HasLayout = transform.GetComponent<ILayoutController>() != null,
                Descriptor = descriptor,
                Control = control,
                Section = section,
                Title = descriptor != null ? descriptor.Title : null,
                Haystack = BuildHaystack(descriptor),
                Parent = parent,
                End = index + 1,
                Managed = descriptor != null || AllChildrenAreRows(transform),
                Exempt = _exempt.Contains(go) || (parent >= 0 && _nodes[parent].Exempt),
            };

            if (section != null) _sectionNodes.Add(index);
            _lookup[transform] = index;
            return index;
        }

        /// <summary>
        /// Everything a query is matched against, lowercased once here so a keystroke is an ordinal
        /// substring test rather than a case-insensitive scan per row per character.
        /// </summary>
        private static string BuildHaystack(PanelElementDescriptor descriptor)
        {
            if (descriptor == null) return null;

            string title = descriptor.Title;
            string description = descriptor.Description;
            string tooltip = descriptor.Tooltip;

            bool hasTitle = !string.IsNullOrEmpty(title);
            bool hasDescription = !string.IsNullOrEmpty(description);
            bool hasTooltip = !string.IsNullOrEmpty(tooltip);
            if (!hasTitle && !hasDescription && !hasTooltip) return null;

            string joined = hasTitle ? title : string.Empty;
            if (hasDescription) joined = joined.Length == 0 ? description : joined + "\n" + description;
            if (hasTooltip) joined = joined.Length == 0 ? tooltip : joined + "\n" + tooltip;
            return joined.ToLowerInvariant();
        }

        /// <summary>
        /// Rows this search owns the visibility of. Anything carrying a descriptor is one; so is a
        /// bare layout row built by <see cref="PanelElementDescriptor.BuildActionRow"/>, which has no
        /// descriptor of its own and would otherwise keep its padding after every button inside it was
        /// filtered away.
        /// </summary>
        private static bool AllChildrenAreRows(Transform node)
        {
            int children = node.childCount;
            if (children == 0) return false;
            for (int i = 0; i < children; i++)
            {
                if (node.GetChild(i).GetComponent<PanelElementDescriptor>() == null) return false;
            }
            return true;
        }

        /// <summary>
        /// Resolves each section's rows once, and stamps every row with the section that owns it, so
        /// neither has to be walked again while the user types.
        /// </summary>
        private void BuildSectionContents()
        {
            for (int s = 0; s < _sectionNodes.Count; s++)
            {
                int index = _sectionNodes[s];
                PanelSectionToggle section = _nodes[index].Section;

                GetSectionContents(section, _contentBuffer);
                _indexBuffer.Clear();
                for (int c = 0; c < _contentBuffer.Count; c++)
                {
                    Transform content = _contentBuffer[c];
                    if (content != null && _lookup.TryGetValue(content, out int contentIndex))
                    {
                        _indexBuffer.Add(contentIndex);
                        StampOwningSection(contentIndex, section);
                    }
                }

                _sectionContents.Add(_indexBuffer.ToArray());
            }

            _contentBuffer.Clear();
            _indexBuffer.Clear();
        }

        /// <summary>
        /// Marks a content subtree as belonging to a section. Nested sections claim their own rows
        /// afterwards, so the innermost section wins — which is the one a result should name.
        /// </summary>
        private void StampOwningSection(int index, PanelSectionToggle section)
        {
            int end = _nodes[index].End;
            for (int i = index; i < end; i++)
            {
                _nodes[i].OwningSection = section;
            }
        }

        /// <summary>
        /// The containers holding a section's rows. Sections built through the helpers register them;
        /// a flat lazy section registers nothing, so its rows are read off the siblings between this
        /// header and the next — valid only while it is open, since collapsed those rows are gone and
        /// the siblings belong to whatever section follows.
        /// </summary>
        private static void GetSectionContents(PanelSectionToggle section, List<Transform> results)
        {
            section.GetContentContainers(results);
            if (results.Count > 0 || !section.Expanded) return;

            Transform parent = section.transform.parent;
            if (parent == null) return;

            for (int i = section.transform.GetSiblingIndex() + 1; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.GetComponent<PanelSectionToggle>() != null) break;
                results.Add(child);
            }
        }

        // ------------------
        // FILTERING
        // ------------------

        private void Filter()
        {
            _matchCount = 0;
            string query = _queryLower;

            // Forward pass: a node matches on its own text, or inherits the match from an ancestor so
            // a group that matched keeps everything under it. Parents precede children here, and
            // _visible carries that inherited flag until the reverse pass overwrites it.
            for (int i = 0; i < _count; i++)
            {
                ref Node node = ref _nodes[i];
                if (node.Exempt)
                {
                    _visible[i] = true;
                    continue;
                }

                bool forced = node.Parent >= 0 && _visible[node.Parent];
                if (!forced && ContainsLower(node.Haystack, query))
                {
                    forced = true;
                    _matchCount++;
                }
                _visible[i] = forced;
            }

            ApplyVisibility();
            ApplySections();
            RebuildChangedLayout();

            // Last: the empty-state card is a direct child of the page, and creating it the first time
            // re-indexes — which would leave the pass above nothing to work from.
            ShowNoResults(_matchCount == 0);
        }

        /// <summary>
        /// Reverse pass: children precede parents, so a row's real visibility — after the page's own
        /// hiding is honoured — is known by the time its parent is decided.
        /// </summary>
        private void ApplyVisibility()
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                ref Node node = ref _nodes[i];
                if (node.Object == null) continue;

                bool visible;
                if (node.Exempt)
                {
                    ShowIfHidden(i);
                    visible = true;
                }
                else if (node.Managed)
                {
                    visible = SetManagedVisible(i, _visible[i]);
                }
                else
                {
                    visible = _visible[i];
                }

                _visible[i] = visible;
                if (visible && node.Parent >= 0) _visible[node.Parent] = true;
            }
        }

        /// <summary>
        /// Resizes the groups whose contents changed, innermost first. Hiding a row inside a card
        /// leaves that card at its old height unless it is rebuilt before whatever holds it measures
        /// again — which is why this runs deepest-first, and why rebuilding the page root instead does
        /// not work. Only the branches that actually changed are touched.
        /// </summary>
        private void RebuildChangedLayout()
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                if (!_dirty[i]) continue;
                _dirty[i] = false;

                ref Node node = ref _nodes[i];
                if (node.HasLayout && node.Rect != null && node.Object != null && node.Object.activeInHierarchy)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(node.Rect);
                }

                if (node.Parent >= 0) _dirty[node.Parent] = true;
            }
        }

        /// <summary>Bottom-up rebuild of the whole page, for when the filter comes off in one go.</summary>
        private void RebuildAllLayout()
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                ref Node node = ref _nodes[i];
                if (node.HasLayout && node.Rect != null && node.Object != null && node.Object.activeInHierarchy)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(node.Rect);
                }
            }
        }

        /// <summary>
        /// Re-shows the section headers whose content survived filtering, and opens them so the rows
        /// underneath are actually reachable. Runs back to front so a nested section resolves before
        /// the one wrapping it asks whether it still has anything to show.
        /// </summary>
        private void ApplySections()
        {
            for (int s = _sectionNodes.Count - 1; s >= 0; s--)
            {
                int index = _sectionNodes[s];
                ref Node node = ref _nodes[index];
                if (node.Exempt || node.Section == null || node.Object == null) continue;

                bool titleMatch = ContainsLower(node.Haystack, _queryLower);
                int[] contents = _sectionContents[s];

                bool contentVisible = false;
                for (int c = 0; c < contents.Length; c++)
                {
                    int contentIndex = contents[c];
                    GameObject content = _nodes[contentIndex].Object;
                    if (content == null) continue;
                    if (titleMatch) ShowSubtree(contentIndex);
                    if (content.activeSelf) contentVisible = true;
                }

                bool show = titleMatch || contentVisible;
                SetManagedVisible(index, show);
                if (show && !node.Section.Expanded) node.Section.SetExpandedWithoutNotify(true);
            }
        }

        /// <summary>
        /// Applies a visibility decision, but only ever undoes this search's own hiding — a row the
        /// page turned off stays off, so filtering cannot reveal a control the page means to keep out
        /// of reach. Flags the row for a layout pass when the state actually moved.
        /// </summary>
        private bool SetManagedVisible(int index, bool visible)
        {
            GameObject go = _nodes[index].Object;
            if (go == null) return false;

            if (visible)
            {
                ShowIfHidden(index);
                return go.activeSelf;
            }

            if (go.activeSelf)
            {
                go.SetActive(false);
                _hidden.Add(go);
                _dirty[index] = true;
            }
            return false;
        }

        private void ShowIfHidden(int index)
        {
            GameObject go = _nodes[index].Object;
            if (go == null || !_hidden.Remove(go)) return;

            go.SetActive(true);
            _dirty[index] = true;
        }

        private void ShowSubtree(int index)
        {
            int end = _nodes[index].End;
            for (int i = index; i < end; i++)
            {
                ShowIfHidden(i);
            }
        }

        private void ShowNoResults(bool show)
        {
            if (show && _noResults == null)
            {
                _noResults = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, _page);
                if (_noResults == null) return;
                _noResults.SetTitle(BasisLocalization.Get("ui.search.noResults"));
                _noResults.SetDescription(BasisLocalization.Get("ui.search.noResults.description"));
                KeepVisible(_noResults);
                EnsureIndex();
            }

            if (_noResults == null) return;
            _noResults.SetActive(show);
            if (show && _field != null) _noResults.transform.SetSiblingIndex(_field.transform.GetSiblingIndex() + 1);
        }

        // ------------------
        // GOING TO A RESULT
        // ------------------

        /// <summary>
        /// Opens a section along with every section it is nested inside, outermost first. A section
        /// opened on its own is still inside whatever closed one holds it, and a lazy parent has to
        /// rebuild its rows before the header nested under it exists to be opened at all.
        /// </summary>
        public static void RevealSection(PanelSectionToggle section)
        {
            if (section == null) return;

            PanelSectionToggle.GetSectionChain(section, _chainBuffer);
            for (int i = 0; i < _chainBuffer.Count; i++)
            {
                PanelSectionToggle step = _chainBuffer[i];
                if (step != null && !step.Expanded) step.SetExpanded(true);
            }

            _chainBuffer.Clear();
        }

        /// <summary>
        /// Brings a row into view and gives it a nudge so the eye lands on it. Matched by title rather
        /// than by reference: a lazy section rebuilds its rows when it reopens, so the descriptor a
        /// result was collected from is usually gone by the time the user gets there.
        /// </summary>
        public void ScrollTo(string title)
        {
            if (string.IsNullOrEmpty(title)) return;

            MonoBehaviour host = CoroutineHost;
            if (host != null)
            {
                host.StartCoroutine(ScrollToDeferred(title));
                return;
            }

            RevealRow(title);
            ScrollToNow(title);
        }

        private IEnumerator ScrollToDeferred(string title)
        {
            // The page was only just switched to and the section holding the row may have rebuilt it,
            // so let the layout those changes queued settle before asking where anything is.
            yield return null;

            // Whatever the result was filed under, the row itself is the last word on which sections
            // are still closed over it — a stale hit can name a section that no longer holds it.
            if (RevealRow(title)) yield return null;

            ScrollToNow(title);
        }

        private bool RevealRow(string title)
        {
            RectTransform row = FindRow(title);
            if (row == null || row.gameObject.activeInHierarchy) return false;

            RevealSection(PanelSectionToggle.GetOwningSection(row));
            return true;
        }

        private void ScrollToNow(string title)
        {
            RectTransform row = FindRow(title);
            if (row == null || !row.gameObject.activeInHierarchy) return;

            ScrollRect scroll = _page.GetComponentInParent<ScrollRect>();
            if (scroll != null && scroll.content != null && scroll.viewport != null)
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);

                float viewHeight = scroll.viewport.rect.height;
                float scrollable = scroll.content.rect.height - viewHeight;
                if (scrollable > 0f)
                {
                    // Measured off the content rect rather than anchored positions, so it holds
                    // whatever pivot and anchoring the scroll-view prefab happens to use.
                    Vector3 centre = scroll.content.InverseTransformPoint(row.TransformPoint(row.rect.center));
                    float fromTop = scroll.content.rect.yMax - centre.y;
                    float offset = Mathf.Clamp(fromTop - viewHeight * 0.5f, 0f, scrollable);
                    scroll.verticalNormalizedPosition = 1f - (offset / scrollable);
                }

                // The page restores where it was last left for a few frames after it is shown, which
                // is exactly the window this lands in and would put the view straight back.
                PanelScrollMemory.Adopt(scroll);
            }

            PanelSearchBounce.Play(row);
        }

        private void ScrollToTop()
        {
            ScrollRect scroll = _page.GetComponentInParent<ScrollRect>();
            if (scroll != null && scroll.vertical) scroll.verticalNormalizedPosition = 1f;
        }

        /// <summary>
        /// Finds a row by its title, skipping the page's own search chrome. Prefers one that is
        /// actually on screen, but falls back to a hidden one so a caller can open whatever is closed
        /// over it rather than give up on a row that is there.
        /// </summary>
        private RectTransform FindRow(string title)
        {
            _page.GetComponentsInChildren(true, _descriptorBuffer);

            RectTransform match = null;
            RectTransform hidden = null;
            for (int i = 0; i < _descriptorBuffer.Count; i++)
            {
                PanelElementDescriptor descriptor = _descriptorBuffer[i];
                if (descriptor == null || _exempt.Contains(descriptor.gameObject)) continue;
                if (!string.Equals(descriptor.Title, title, StringComparison.OrdinalIgnoreCase)) continue;

                if (descriptor.gameObject.activeInHierarchy)
                {
                    match = descriptor.rectTransform;
                    break;
                }

                hidden ??= descriptor.rectTransform;
            }

            _descriptorBuffer.Clear();
            return match != null ? match : hidden;
        }

        // ------------------
        // REPORTING MATCHES ELSEWHERE
        // ------------------

        /// <summary>
        /// Lists what would match on this page without changing what the page shows. Lets one page
        /// report results that live on another the user is not currently looking at. Requires
        /// <see cref="Prepare"/>; rows kept out of filtering are skipped, so a page's own search
        /// chrome never turns up as a result.
        /// </summary>
        public void CollectHits(string query, List<BasisPanelSearchHit> results)
        {
            if (results == null) return;
            results.Clear();

            string filter = Normalize(query);
            if (filter.Length == 0 || !_indexed) return;

            string lower = filter.ToLowerInvariant();
            for (int i = 0; i < _count; i++)
            {
                ref Node node = ref _nodes[i];
                if (node.Exempt || node.Control == null || node.Control is PanelTabPage) continue;
                if (!ContainsLower(node.Haystack, lower)) continue;

                string title = node.Title;
                if (string.IsNullOrEmpty(title)) title = node.Descriptor != null ? node.Descriptor.Description : null;
                if (string.IsNullOrEmpty(title)) continue;

                // A section header is its own result — searching for the section name should offer the
                // section, not every row filed under it.
                PanelSectionToggle section = node.Section != null ? node.Section : node.OwningSection;
                results.Add(new BasisPanelSearchHit(node.Descriptor, title, section));
            }
        }
    }
}
