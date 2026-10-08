using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using Basis.BTween;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>One thing the search popup can offer to go to.</summary>
    public readonly struct BasisPanelSearchResult
    {
        /// <summary>The control's own label — what the user typed towards.</summary>
        public readonly string Title;

        /// <summary>Where it lives, e.g. "Graphics › Shadows". Shown under the title.</summary>
        public readonly string Detail;

        /// <summary>Navigates to it. The popup closes itself first, so this can rebuild the menu.</summary>
        public readonly Action Activate;

        public BasisPanelSearchResult(string title, string detail, Action activate)
        {
            Title = title;
            Detail = detail;
            Activate = activate;
        }
    }

    /// <summary>Fills <paramref name="results"/> with everything matching <paramref name="query"/>.</summary>
    public delegate void BasisPanelSearchQuery(string query, List<BasisPanelSearchResult> results);

    /// <summary>
    /// The search palette: a field and a list of matches, opened from a panel's header rather than
    /// sitting at the top of every page. One field the user asks for, instead of fifteen they have to
    /// scroll past.
    /// <para>
    /// Driven onto a stock page panel at runtime rather than subclassing <see cref="BasisMenuPanel"/>:
    /// the shared Page prefab carries a plain BasisMenuPanel, so asking Addressables to hand back a
    /// subclass of it finds nothing and the instantiate fails. Same shape as
    /// <see cref="BasisPanelMoveHandle"/> — a behaviour added to the panel it drives.
    /// </para>
    /// <para>
    /// The popup owns the typing loop and nothing else. Whoever opened it supplies a query delegate
    /// and keeps its own index; when that index grows — Settings builds the tabs the user never
    /// opened in the background — it calls <see cref="MarkDirty"/> and the list is asked again.
    /// </para>
    /// </summary>
    [AutoStaticsCleanup]
    public partial class BasisPanelSearchPopup : MonoBehaviour
    {
        public const string TitleKey = "menu.panel.search";
        public const string TooltipKey = "menu.panel.search.tooltip";
        public const string PromptKey = "menu.panel.search.prompt";
        public const string NoResultsKey = "ui.search.noResults";
        public const string NoResultsDescriptionKey = "ui.search.noResults.description";

        /// <summary>Quiet time after the last keystroke before the query runs. Collecting is not free.</summary>
        private const float QueryDelay = 0.2f;

        /// <summary>Cap on listed rows. Past this the footer says how many were left off.</summary>
        private const int MaxResults = 60;

        public static BasisPanelSearchPopup Instance { get; private set; }
        public static bool HasInstance => Instance != null;

        public static BasisMenuPanel.PanelData SearchPanelData => new BasisMenuPanel.PanelData
        {
            Title = BasisLocalization.Get(TitleKey),
            PanelSize = new Vector2(1100, 820),
            PanelPosition = new Vector3(0, -20, -5),
        };

        private BasisMenuPanel _panel;
        private BasisPanelSearchQuery _query;
        private PanelTextField _field;
        private RectTransform _resultRoot;
        private PanelElementDescriptor _status;
        private readonly List<PanelButton> _rows = new();
        private readonly List<BasisPanelSearchResult> _results = new();
        private Coroutine _queryRoutine;
        private string _pendingQuery = string.Empty;

        /// <summary>
        /// Opens the palette over the menu, replacing whatever it was last pointed at. Returns null
        /// when there is no menu to sit in.
        /// </summary>
        public static BasisPanelSearchPopup Open(string title, BasisPanelSearchQuery query)
        {
            if (query == null || BasisMainMenu.Instance == null)
            {
                return null;
            }

            BasisMenuInstance menu = BasisMainMenu.Instance.MenuObjectInstance;
            if (menu == null || menu.PanelRoot == null)
            {
                return null;
            }

            Close();

            BasisMenuPanel.PanelData data = SearchPanelData;
            if (!string.IsNullOrEmpty(title))
            {
                data.Title = title;
            }

            BasisMenuPanel panel = BasisMenuPanel.CreateNew(data, menu.PanelRoot, BasisMenuPanel.PanelStyles.Page);
            if (panel == null)
            {
                return null;
            }

            panel.SetLayer(BasisMenuPanel.PanelLayer.Overlay);

            BasisPanelSearchPopup popup = panel.gameObject.AddComponent<BasisPanelSearchPopup>();
            popup._panel = panel;
            popup._query = query;
            popup.Build();

            panel.transform.SetAsLastSibling();
            panel.OnInstanceReleased += popup.OnPanelReleased;

            BasisPanelMoveHandle.Attach(panel, nameof(BasisPanelSearchPopup));
            UIAnimations.PopIn(panel);

            Instance = popup;
            return popup;
        }

        public static void Close()
        {
            BasisPanelSearchPopup popup = Instance;
            if (popup == null)
            {
                return;
            }

            Instance = null;
            if (popup._panel != null && !popup._panel.IsReleased)
            {
                popup._panel.ReleaseInstance();
            }
        }

        /// <summary>
        /// Tells an open palette its source has more to offer than last time — Settings finishing a
        /// background page, say — so the list is collected again. No-op when nothing is open.
        /// </summary>
        public static void MarkDirty()
        {
            if (HasInstance)
            {
                Instance.RunQuery();
            }
        }

        private void OnPanelReleased()
        {
            StopQueryRoutine();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void OnDisable() => StopQueryRoutine();

        private void OnDestroy() => OnPanelReleased();

        private void Build()
        {
            // One vertical page holding everything, exactly as the other panels build themselves. The
            // panel's own content holder has no layout on it, so a row parented straight there keeps
            // whatever position its prefab was authored at instead of joining the column — which is
            // how the field and its icon ended up off the edge of the panel.
            PanelTabPage page = PanelTabPage.CreateVertical(_panel.Descriptor.ContentParent);
            RectTransform root = page != null ? page.Descriptor.ContentParent : _panel.Descriptor.ContentParent;
            _resultRoot = root;

            // The no-title entry is the search box this menu already uses (see the Library tab bar):
            // the whole panel is the label, so a row that spends half its width repeating "Search"
            // just makes the field smaller.
            _field = PanelTextField.CreateNew(PanelTextField.TextFieldStyles.EntryWithNoTitle, root);
            if (_field != null)
            {
                if (_field._placeholderLabel != null)
                {
                    _field._placeholderLabel.text = BasisLocalization.Get(PromptKey);
                }
                _field.Descriptor.SetTooltip(BasisLocalization.Get(TooltipKey));
                // Bound to the input rather than PanelTextField.OnValueChanged, which only fires on
                // commit — a search that waits for Enter reads as broken.
                _field._inputField.onValueChanged.AddListener(OnFieldEdited);
            }

            // Only speaks up when there is something to say — nothing found, or results left off.
            // The prompt lives in the field's placeholder, so repeating it here as a block of text
            // would just be the same sentence twice.
            _status = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, root);
            ShowStatus(null, null);
        }

        private void OnFieldEdited(string value)
        {
            _pendingQuery = value ?? string.Empty;
            StopQueryRoutine();

            if (_pendingQuery.Trim().Length == 0)
            {
                // Clearing is instant: there is nothing to collect, and waiting to empty the list
                // reads as the popup having got stuck.
                RunQuery();
                return;
            }

            if (isActiveAndEnabled)
            {
                _queryRoutine = StartCoroutine(RunQueryAfterDelay());
            }
            else
            {
                RunQuery();
            }
        }

        private IEnumerator RunQueryAfterDelay()
        {
            yield return new WaitForSecondsRealtime(QueryDelay);
            _queryRoutine = null;
            RunQuery();
        }

        private void StopQueryRoutine()
        {
            if (_queryRoutine == null) return;
            StopCoroutine(_queryRoutine);
            _queryRoutine = null;
        }

        private void RunQuery()
        {
            _results.Clear();

            string text = _pendingQuery.Trim();
            if (text.Length > 0)
            {
                _query?.Invoke(text, _results);
            }

            int shown = Mathf.Min(_results.Count, MaxResults);
            for (int Index = 0; Index < shown; Index++)
            {
                SetRow(Index, _results[Index]);
            }
            ReleaseRowsFrom(shown);

            if (text.Length == 0)
            {
                ShowStatus(null, null);
            }
            else if (_results.Count == 0)
            {
                ShowStatus(BasisLocalization.Get(NoResultsKey), BasisLocalization.Get(NoResultsDescriptionKey));
            }
            else if (_results.Count > shown)
            {
                ShowStatus(BasisLocalization.Get("settings.search.otherTabs.more", _results.Count - shown), null);
            }
            else
            {
                ShowStatus(null, null);
            }
        }

        /// <summary>
        /// Rows are reused rather than rebuilt — a keystroke that rewrites forty buttons costs a full
        /// layout pass, which is what the user feels. Reassigns OnClicked instead of adding to it, or
        /// a reused row would fire every result it has ever stood for.
        /// </summary>
        private void SetRow(int index, BasisPanelSearchResult result)
        {
            while (_rows.Count <= index)
            {
                PanelButton created = PanelButton.CreateNew(PanelButton.ButtonStyles.Default, _resultRoot);
                if (created == null) return;
                _rows.Add(created);
            }

            PanelButton row = _rows[index];
            if (row == null) return;

            row.gameObject.SetActive(true);
            row.Descriptor.SetTitle(result.Title);
            row.Descriptor.SetDescription(result.Detail);

            Action activate = result.Activate;
            row.OnClicked = () =>
            {
                // Closed first: navigating can rebuild the menu underneath us, and a palette that
                // outlives the panel it was searching would be pointing at nothing.
                Close();
                activate?.Invoke();
            };
        }

        private void ReleaseRowsFrom(int index)
        {
            for (int Index = _rows.Count - 1; Index >= index; Index--)
            {
                PanelButton row = _rows[Index];
                if (row != null && !row.IsReleased)
                {
                    row.ReleaseInstance();
                }
                _rows.RemoveAt(Index);
            }
        }

        private void ShowStatus(string title, string description)
        {
            if (_status == null) return;

            bool show = !string.IsNullOrEmpty(title);
            _status.SetActive(show);
            if (!show) return;

            _status.SetTitle(title);
            _status.SetDescription(description);
            _status.transform.SetAsLastSibling();
        }
    }
}
