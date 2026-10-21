using Unity.Scripting.LifecycleManagement;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Search across the whole Settings menu, driven from the panel header's Search button rather
    /// than a field on every page. The popup owns the typing and the result list; this side owns the
    /// index — every tab is walked for matches, including the ones the user never opened, so a
    /// setting can be found from wherever they happen to be.
    ///
    /// <para>Building the tabs nobody has opened — and expanding their lazy sections so their rows
    /// exist to be found — instantiates a page worth of prefabs each, so it runs one page per frame
    /// in the background and tells the popup to ask again as each one lands.</para>
    /// </summary>
    [NoAutoStaticsCleanup]
    public partial class SettingsProvider
    {
        /// <summary>
        /// Query length before the unopened tabs get built. Building a tab runs its whole setup —
        /// pollers start, lists fetch — so a single stray character should not trigger all of it.
        /// </summary>
        private const int MinIndexQueryLength = 2;

        private sealed class TabSearch
        {
            public string Key;
            public int Index;
            public PanelTabPage Page;
            public BasisPanelSearch Search;
            public bool Prepared;
        }

        private static readonly List<TabSearch> _tabSearches = new();
        private static readonly List<BasisPanelSearchHit> _hitBuffer = new();

        private static PanelTabGroup _searchTabGroup;
        private static string _searchQuery = string.Empty;
        private static Coroutine _searchIndexRoutine;

        /// <summary>
        /// Drops all search state and binds to a freshly built tab group. Called when the panel is
        /// (re)opened and when it is released, so nothing points at a torn-down page.
        /// </summary>
        private static void ResetSearch(PanelTabGroup tabGroup)
        {
            StopSearchRoutines();
            _tabSearches.Clear();
            _lazyTabs.Clear();
            _hitBuffer.Clear();
            _searchQuery = string.Empty;
            _searchTabGroup = tabGroup;
        }

        private static void StopSearchRoutines()
        {
            if (_searchTabGroup != null && _searchIndexRoutine != null)
            {
                _searchTabGroup.StopCoroutine(_searchIndexRoutine);
            }

            _searchIndexRoutine = null;
        }

        /// <summary>
        /// Makes a freshly built tab searchable. Headless: the page keeps its whole layout for
        /// content, and the header popup is what the user types into.
        /// </summary>
        private static void AttachTabSearch(string tabKey, int index, PanelTabPage page)
        {
            if (page == null || _searchTabGroup == null) return;

            BasisPanelSearch search = BasisPanelSearch.AttachHeadless(page.Descriptor.ContentParent, page);
            if (search == null) return;

            _tabSearches.Add(new TabSearch
            {
                Key = tabKey,
                Index = index,
                Page = page,
                Search = search,
            });
        }

        // ------------------
        // ANSWERING THE POPUP
        // ------------------

        /// <summary>
        /// Everything matching, across every tab indexed so far. Kicks off indexing for the tabs that
        /// have not been walked yet; each one calls the popup back as it lands, so an answer arrives
        /// immediately and fills in rather than blocking on the whole menu being built.
        /// </summary>
        private static void CollectSearchResults(string query, List<BasisPanelSearchResult> results)
        {
            _searchQuery = BasisPanelSearch.Normalize(query);
            if (_searchQuery.Length == 0) return;

            StartSearchIndexing();

            for (int i = 0; i < _tabSearches.Count; i++)
            {
                TabSearch tab = _tabSearches[i];
                if (!tab.Prepared || tab.Search == null) continue;
                if (!IsTabButtonActive(tab.Index)) continue;

                tab.Search.CollectHits(_searchQuery, _hitBuffer);

                string tabName = TabLabel(tab.Key);
                for (int h = 0; h < _hitBuffer.Count; h++)
                {
                    BasisPanelSearchHit hit = _hitBuffer[h];
                    string title = hit.Title;
                    string section = hit.SectionTitle;

                    string tabKey = tab.Key;
                    PanelSectionToggle targetSection = hit.Section;

                    results.Add(new BasisPanelSearchResult(
                        title,
                        string.IsNullOrEmpty(section) || section == title ? tabName : $"{tabName} › {section}",
                        () => OpenSearchResult(tabKey, targetSection, title)));
                }
            }

            _hitBuffer.Clear();
        }

        private static TabSearch FindTabSearch(string tabKey)
        {
            for (int i = 0; i < _tabSearches.Count; i++)
            {
                if (_tabSearches[i].Key == tabKey) return _tabSearches[i];
            }
            return null;
        }

        /// <summary>
        /// Whether a tab is currently offered on the strip. Permission-gated tabs are registered but
        /// hidden when the local player lacks the permission — search must not build or surface them,
        /// both because their settings are not available and because building the admin page starts
        /// its server fetches.
        /// </summary>
        private static bool IsTabButtonActive(int index)
        {
            if (_searchTabGroup == null || index < 0 || index >= _searchTabGroup.SelectionButtons.Count)
            {
                return false;
            }

            PanelButton button = _searchTabGroup.SelectionButtons[index];
            return button != null && button.gameObject.activeSelf;
        }

        // ------------------
        // BACKGROUND INDEXING
        // ------------------

        private static void StartSearchIndexing()
        {
            if (_searchIndexRoutine != null) return;
            if (_searchQuery.Length < MinIndexQueryLength) return;
            if (_searchTabGroup == null || !_searchTabGroup.isActiveAndEnabled) return;

            _searchIndexRoutine = _searchTabGroup.StartCoroutine(IndexRemainingTabs());
        }

        /// <summary>
        /// Makes the tabs the user never opened searchable — building the page, then opening the lazy
        /// sections whose rows only exist while expanded. Both instantiate prefabs, so it is one page
        /// per frame, and the popup is told to ask again after each so results arrive as they are found.
        /// </summary>
        private static IEnumerator IndexRemainingTabs()
        {
            for (int i = 0; i < _lazyTabs.Count; i++)
            {
                if (_searchTabGroup == null || _searchQuery.Length == 0) break;
                if (_lazyTabs[i].Built) continue;
                if (!IsTabButtonActive(_lazyTabs[i].Index)) continue;

                RealizeTab(_searchTabGroup, _lazyTabs[i], forSearch: true);
                yield return null;
            }

            // _tabSearches grows while the loop above runs, so this walks it by index rather than
            // snapshotting a count that is already stale.
            for (int i = 0; i < _tabSearches.Count; i++)
            {
                if (_searchTabGroup == null || _searchQuery.Length == 0) break;

                TabSearch entry = _tabSearches[i];
                if (entry.Search == null) continue;

                // Re-prepared rather than skipped once indexed: a section the user has collapsed since
                // took its rows with it, and search that cannot see them is answering for a page that
                // is no longer there. Costs a walk when nothing has moved.
                bool first = !entry.Prepared;
                bool changed = entry.Search.Prepare(rescan: true);
                entry.Prepared = true;
                if (!first && !changed) continue;

                BasisPanelSearchPopup.MarkDirty();
                yield return null;
            }

            _searchIndexRoutine = null;
        }

        // ------------------
        // GOING TO A RESULT
        // ------------------

        /// <summary>
        /// Jumps to the tab a result lives on. The popup has already closed itself by the time this
        /// runs, so the page the user lands on is the one they were looking for with nothing over it.
        /// </summary>
        private static void OpenSearchResult(string tabKey, PanelSectionToggle section, string title)
        {
            NavigateToTab(_searchTabGroup, tabKey);

            // A lazy section rebuilds its rows when it reopens, so expanding has to come after the
            // navigation rather than with the hit. That also means the row is a new object, which is
            // why ScrollTo finds it by title. Every section over the hit is opened, not just the one
            // it is filed under — that one is no use while the section holding it is still closed.
            BasisPanelSearch.RevealSection(section);

            FindTabSearch(tabKey)?.Search?.ScrollTo(title);
        }
    }
}
