using System;
using System.Collections.Generic;
using Basis.Scripts.Common;

namespace Basis.BasisUI
{
    /// <summary>
    /// Process-wide collection point for popups. Any popup that is closed before the
    /// user accepts or denies it is captured here as a pending entry that can be
    /// re-opened later; answered popups are kept as history. State is in-memory only
    /// and resets when the application restarts (pending entries hold live callbacks,
    /// which cannot be serialised across sessions).
    /// </summary>
    public static class BasisNotificationCenter
    {
        private static readonly List<BasisNotification> _all = new();

        // Oldest entries are trimmed once the list grows past this, so a long session
        // doesn't accumulate notifications without bound.
        private const int MaxEntries = 200;

        /// <summary>All notifications in insertion order (oldest first).</summary>
        public static IReadOnlyList<BasisNotification> All => _all;

        /// <summary>
        /// Raised whenever the collection changes so any open notification UI (the
        /// panel and the hotbar badge) can refresh.
        /// </summary>
        public static event Action Changed;

        public static int PendingCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _all.Count; i++)
                {
                    if (_all[i].Status == BasisNotificationStatus.Pending) count++;
                }
                return count;
            }
        }

        // ── Ignore ("do not disturb") mode ───────────────────────────────────
        private const string IgnoreModeStoreKey = "NotificationsIgnoreMode.BAS";
        private static bool _ignoreModeLoaded;
        private static bool _ignoreMode;

        /// <summary>
        /// When enabled, new interruptive popups (accept/deny dialogues and load
        /// prompts) are routed straight into the pending list instead of being shown,
        /// so they never interrupt the user — they can still be brought up from the
        /// notification panel. Persists across sessions.
        /// </summary>
        public static bool IgnoreMode
        {
            get
            {
                if (!_ignoreModeLoaded)
                {
                    _ignoreMode = BasisDataStore.LoadInt(IgnoreModeStoreKey, 0) != 0;
                    _ignoreModeLoaded = true;
                }
                return _ignoreMode;
            }
            set
            {
                _ignoreModeLoaded = true;
                if (_ignoreMode == value) return;
                _ignoreMode = value;
                BasisDataStore.SaveInt(value ? 1 : 0, IgnoreModeStoreKey);
                Changed?.Invoke();
            }
        }

        // ── History filtering ────────────────────────────────────────────────
        // Applies to the History section only. Pending entries are always listed in
        // full so a filter can never hide something still waiting on the user.
        private const string HistoryCategoryStoreKey = "NotificationsHistoryCategory.BAS";
        private const int HistoryCategoryAll = -1;
        private static bool _historyCategoryLoaded;
        private static BasisNotificationCategory? _historyCategory;

        /// <summary>
        /// Category the history list is narrowed to, or null for all categories.
        /// Persists across sessions.
        /// </summary>
        public static BasisNotificationCategory? HistoryCategory
        {
            get
            {
                if (!_historyCategoryLoaded)
                {
                    int stored = BasisDataStore.LoadInt(HistoryCategoryStoreKey, HistoryCategoryAll);
                    _historyCategory = Enum.IsDefined(typeof(BasisNotificationCategory), stored)
                        ? (BasisNotificationCategory)stored
                        : null;
                    _historyCategoryLoaded = true;
                }
                return _historyCategory;
            }
            set
            {
                _historyCategoryLoaded = true;
                if (_historyCategory == value) return;
                _historyCategory = value;
                BasisDataStore.SaveInt(value.HasValue ? (int)value.Value : HistoryCategoryAll, HistoryCategoryStoreKey);
                Changed?.Invoke();
            }
        }

        private static string _historySearch = string.Empty;

        /// <summary>
        /// Free-text narrowing of the history list, matched against title and
        /// description. Session-only — a search box that survived a restart would hide
        /// history with no obvious cause.
        /// </summary>
        public static string HistorySearch
        {
            get => _historySearch;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_historySearch, next, StringComparison.Ordinal)) return;
                _historySearch = next;
                Changed?.Invoke();
            }
        }

        /// <summary>True when the history list is currently narrowed by either filter.</summary>
        public static bool HistoryFiltered => HistoryCategory.HasValue || !string.IsNullOrWhiteSpace(HistorySearch);

        /// <summary>Whether a terminal entry survives the active category and search filters.</summary>
        public static bool PassesHistoryFilter(BasisNotification notification)
        {
            if (notification == null) return false;
            if (HistoryCategory.HasValue && notification.Category != HistoryCategory.Value) return false;

            string query = HistorySearch;
            if (string.IsNullOrWhiteSpace(query)) return true;
            query = query.Trim();

            return Contains(notification.Title, query) || Contains(notification.Description, query);
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack)
                && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ── Forced suppression scopes ────────────────────────────────────────
        // Some contexts (the admin and moderator panels) require every popup to be
        // routed into the list while they are open, regardless of the user's toggle.
        // They push a scope on open and pop it on close; normal handling resumes once
        // all scopes are gone.
        private static int _forcedScopes;

        /// <summary>True while an open context is forcing popups into the list.</summary>
        public static bool SuppressionForced => _forcedScopes > 0;

        /// <summary>
        /// Whether new popups should be routed into the list instead of shown — because
        /// the user enabled ignore mode, or because a forcing context (admin/moderator
        /// panel) is currently open.
        /// </summary>
        public static bool RouteToNotifications => IgnoreMode || SuppressionForced;

        /// <summary>
        /// Begin a context that forces popups into the list (call from a panel's
        /// OnEnable). Must be balanced with <see cref="EndForcedScope"/> on close.
        /// </summary>
        public static void BeginForcedScope() => _forcedScopes++;

        /// <summary>End a forcing context (call from the matching OnDisable).</summary>
        public static void EndForcedScope()
        {
            if (_forcedScopes > 0) _forcedScopes--;
        }

        /// <summary>
        /// Trim the oldest entries once the list grows past <see cref="MaxEntries"/>.
        /// A trimmed entry that is still pending has its callback resolved (declined) so
        /// the source isn't left waiting on a request we silently dropped.
        /// </summary>
        private static void TrimToCap()
        {
            while (_all.Count > MaxEntries)
            {
                BasisNotification oldest = _all[0];
                _all.RemoveAt(0);
                if (oldest.Status == BasisNotificationStatus.Pending)
                {
                    Action onDismiss = oldest.OnDismiss;
                    oldest.OnDismiss = null;
                    onDismiss?.Invoke();
                }
            }
        }

        /// <summary>
        /// Capture a popup that was closed before being answered. The entry stays
        /// pending until <see cref="Reopen"/> brings the popup back (and it is then
        /// answered) or <see cref="Dismiss"/> drops it.
        /// </summary>
        public static BasisNotification AddPending(
            string title,
            string description,
            string iconAddress,
            Action reopen,
            Action onDismiss = null,
            BasisNotificationCategory category = BasisNotificationCategory.System)
        {
            BasisNotification notification = new BasisNotification
            {
                Title = title,
                Description = description,
                IconAddress = iconAddress,
                Category = category,
                Status = BasisNotificationStatus.Pending,
                Reopen = reopen,
                OnDismiss = onDismiss,
            };

            _all.Add(notification);
            TrimToCap();
            Changed?.Invoke();
            return notification;
        }

        /// <summary>
        /// Record a popup that reached a terminal state (accepted/denied/dismissed)
        /// without ever having been captured as pending.
        /// </summary>
        public static BasisNotification LogResolved(
            string title,
            string description,
            string iconAddress,
            BasisNotificationStatus outcome,
            BasisNotificationCategory category = BasisNotificationCategory.System)
        {
            if (outcome == BasisNotificationStatus.Pending)
            {
                outcome = BasisNotificationStatus.Dismissed;
            }

            BasisNotification notification = new BasisNotification
            {
                Title = title,
                Description = description,
                IconAddress = iconAddress,
                Category = category,
                Status = outcome,
                ResolvedUtc = DateTime.UtcNow,
            };

            _all.Add(notification);
            TrimToCap();
            Changed?.Invoke();
            return notification;
        }

        /// <summary>
        /// Bring a pending popup back up. The re-spawned popup logs its own outcome,
        /// so the stale pending entry is removed before the factory runs.
        /// </summary>
        public static void Reopen(BasisNotification notification)
        {
            if (notification == null) return;

            Action reopen = notification.Reopen;
            _all.Remove(notification);
            Changed?.Invoke();

            // Close the open menu page (e.g. the Notifications tab) so the re-opened
            // modal isn't raycast-blocked by its large canvas collider underneath.
            if (BasisMainMenu.Instance != null)
            {
                BasisMainMenu.CloseActivePanel();
            }

            reopen?.Invoke();
        }

        /// <summary>
        /// Drop a pending entry without re-opening it, letting the source resolve its
        /// callback. The entry is kept as <see cref="BasisNotificationStatus.Dismissed"/>
        /// history.
        /// </summary>
        public static void Dismiss(BasisNotification notification)
        {
            if (notification == null || notification.Status != BasisNotificationStatus.Pending) return;

            Action onDismiss = notification.OnDismiss;
            notification.OnDismiss = null;
            notification.Reopen = null;
            notification.Status = BasisNotificationStatus.Dismissed;
            notification.ResolvedUtc = DateTime.UtcNow;
            Changed?.Invoke();
            onDismiss?.Invoke();
        }

        /// <summary>Remove every terminal (non-pending) entry.</summary>
        public static void ClearHistory()
        {
            _all.RemoveAll(n => n.Status != BasisNotificationStatus.Pending);
            Changed?.Invoke();
        }
    }
}
