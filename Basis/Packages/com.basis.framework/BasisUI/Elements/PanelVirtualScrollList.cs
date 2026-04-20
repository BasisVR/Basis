using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    /// <summary>
    /// Lightweight vertical virtualized list. Attach to a ScrollRect (or let the
    /// static helper do it for you) and supply a row factory plus a bind delegate.
    /// Only enough rows to cover the viewport (+ a small buffer) are ever created,
    /// and scrolling rebinds existing rows to new data indices instead of
    /// instantiating new ones.
    ///
    /// Typical usage:
    ///   var descriptor = PanelElementDescriptor.CreateNew(ScrollViewVertical, parent);
    ///   var list = PanelVirtualScrollList.AttachTo(descriptor, rowHeight: 90f);
    ///   list.SetDataSource(
    ///       count: items.Count,
    ///       createRow: content => PanelButton.CreateNew(content).transform as RectTransform,
    ///       bindRow:   (row, i) => row.GetComponent&lt;PanelButton&gt;().Descriptor.SetTitle(items[i]));
    /// </summary>
    public class PanelVirtualScrollList : MonoBehaviour
    {
        [SerializeField] private float _rowHeight = 90f;
        [SerializeField] private float _spacing = 8f;
        [SerializeField] private int _rowBuffer = 2;

        private ScrollRect _scroll;
        private RectTransform _content;
        private RectTransform _viewport;

        private int _totalCount;
        private int _topRow = int.MinValue;
        private bool _dataBound;

        private Func<RectTransform, RectTransform> _createRow;
        private Action<RectTransform, int> _bindRow;

        private readonly List<RectTransform> _rowPool = new List<RectTransform>();

        public ScrollRect Scroll => _scroll;
        public int Count => _totalCount;
        public float RowStride => _rowHeight + _spacing;

        /// <summary>
        /// Attach (or retrieve an existing) PanelVirtualScrollList to the GameObject
        /// that hosts a ScrollRect. Walks up from <paramref name="anyChild"/> until
        /// a ScrollRect is found.
        /// </summary>
        public static PanelVirtualScrollList AttachTo(RectTransform anyChild, float rowHeight, float spacing = 8f, int rowBuffer = 2)
        {
            ScrollRect scroll = anyChild.GetComponentInParent<ScrollRect>();
            if (scroll == null)
            {
                BasisDebug.LogError("PanelVirtualScrollList.AttachTo: no ScrollRect found in parents of " + anyChild.name);
                return null;
            }
            return AttachTo(scroll, rowHeight, spacing, rowBuffer);
        }

        public static PanelVirtualScrollList AttachTo(PanelElementDescriptor scrollDescriptor, float rowHeight, float spacing = 8f, int rowBuffer = 2)
        {
            return AttachTo(scrollDescriptor.ContentParent, rowHeight, spacing, rowBuffer);
        }

        public static PanelVirtualScrollList AttachTo(ScrollRect scroll, float rowHeight, float spacing = 8f, int rowBuffer = 2)
        {
            if (!scroll.TryGetComponent(out PanelVirtualScrollList list))
            {
                list = scroll.gameObject.AddComponent<PanelVirtualScrollList>();
            }
            list._scroll = scroll;
            list._content = scroll.content;
            list._viewport = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
            list._rowHeight = rowHeight;
            list._spacing = spacing;
            list._rowBuffer = rowBuffer;

            // Virtual scrolling drives anchoredPosition directly — disable any
            // layout group or ContentSizeFitter on the content so they don't
            // fight us by reflowing children.
            if (list._content != null)
            {
                if (list._content.TryGetComponent(out LayoutGroup group)) group.enabled = false;
                if (list._content.TryGetComponent(out ContentSizeFitter fitter)) fitter.enabled = false;

                // Anchor content to the top so row y = -index * stride is correct.
                list._content.anchorMin = new Vector2(0f, 1f);
                list._content.anchorMax = new Vector2(1f, 1f);
                list._content.pivot = new Vector2(0.5f, 1f);
                list._content.anchoredPosition = Vector2.zero;
            }

            list.HookScroll();
            return list;
        }

        private void Awake()
        {
            if (_scroll == null) TryGetComponent(out _scroll);
            if (_scroll != null)
            {
                if (_content == null) _content = _scroll.content;
                if (_viewport == null) _viewport = _scroll.viewport != null ? _scroll.viewport : (RectTransform)_scroll.transform;
                HookScroll();
            }
        }

        private void OnEnable()
        {
            if (_dataBound) UpdateVisibleRows(true);
        }

        private bool _scrollHooked;
        private void HookScroll()
        {
            if (_scrollHooked || _scroll == null) return;
            _scroll.onValueChanged.AddListener(OnScroll);
            _scrollHooked = true;
        }

        private void OnScroll(Vector2 _)
        {
            UpdateVisibleRows(false);
        }

        /// <summary>
        /// Set or replace the data source. Creates the pool if needed, clamps content
        /// size, and refreshes visible rows. Safe to call repeatedly as the data
        /// count changes — only the delta is applied.
        /// </summary>
        public void SetDataSource(int count, Func<RectTransform, RectTransform> createRow, Action<RectTransform, int> bindRow)
        {
            _createRow = createRow;
            _bindRow = bindRow;
            _totalCount = Mathf.Max(0, count);
            _dataBound = true;

            if (_content != null)
            {
                float contentHeight = _totalCount > 0
                    ? (_totalCount * RowStride) - _spacing
                    : 0f;
                Vector2 size = _content.sizeDelta;
                size.y = Mathf.Max(0f, contentHeight);
                _content.sizeDelta = size;
            }

            _topRow = int.MinValue; // force rebind
            UpdateVisibleRows(true);
        }

        /// <summary>
        /// Update the data count without changing row factories. Cheaper than
        /// SetDataSource when only the count is changing.
        /// </summary>
        public void Refresh(int newCount)
        {
            if (_createRow == null || _bindRow == null)
            {
                BasisDebug.LogError("PanelVirtualScrollList.Refresh: no data source configured — call SetDataSource first.");
                return;
            }
            SetDataSource(newCount, _createRow, _bindRow);
        }

        /// <summary>
        /// Rebind every currently-visible row against its data index. Use when
        /// the underlying data changed but the count did not.
        /// </summary>
        public void RebindVisible()
        {
            if (!_dataBound || _bindRow == null) return;
            for (int i = 0; i < _rowPool.Count; i++)
            {
                RectTransform row = _rowPool[i];
                if (row == null || !row.gameObject.activeSelf) continue;
                int dataIndex = _topRow + i;
                if (dataIndex < 0 || dataIndex >= _totalCount) continue;
                _bindRow(row, dataIndex);
            }
        }

        public void ClearRows()
        {
            for (int i = 0; i < _rowPool.Count; i++)
            {
                if (_rowPool[i] != null) Destroy(_rowPool[i].gameObject);
            }
            _rowPool.Clear();
            _topRow = int.MinValue;
            _totalCount = 0;
            _dataBound = false;
            if (_content != null)
            {
                Vector2 size = _content.sizeDelta;
                size.y = 0f;
                _content.sizeDelta = size;
            }
        }

        private void UpdateVisibleRows(bool force)
        {
            if (!_dataBound || _createRow == null || _bindRow == null) return;
            if (_content == null || _viewport == null) return;

            if (_totalCount <= 0)
            {
                for (int i = 0; i < _rowPool.Count; i++)
                {
                    if (_rowPool[i] != null) _rowPool[i].gameObject.SetActive(false);
                }
                return;
            }

            float stride = RowStride;
            float viewportHeight = _viewport.rect.height;
            if (viewportHeight <= 0f) viewportHeight = stride * 4f; // pre-layout fallback

            int visibleRows = Mathf.CeilToInt(viewportHeight / stride) + (_rowBuffer * 2);
            visibleRows = Mathf.Min(visibleRows, _totalCount);

            float scrollY = Mathf.Max(0f, _content.anchoredPosition.y);
            int newTopRow = Mathf.FloorToInt(scrollY / stride) - _rowBuffer;
            newTopRow = Mathf.Clamp(newTopRow, 0, Mathf.Max(0, _totalCount - visibleRows));

            bool poolChanged = false;
            while (_rowPool.Count < visibleRows)
            {
                RectTransform row = _createRow(_content);
                if (row == null) break;
                row.SetParent(_content, false);
                row.anchorMin = new Vector2(0f, 1f);
                row.anchorMax = new Vector2(1f, 1f);
                row.pivot = new Vector2(0.5f, 1f);
                _rowPool.Add(row);
                poolChanged = true;
            }

            if (!force && !poolChanged && newTopRow == _topRow) return;

            _topRow = newTopRow;

            for (int i = 0; i < _rowPool.Count; i++)
            {
                RectTransform row = _rowPool[i];
                if (row == null) continue;

                int dataIndex = _topRow + i;
                if (dataIndex >= _totalCount || i >= visibleRows)
                {
                    if (row.gameObject.activeSelf) row.gameObject.SetActive(false);
                    continue;
                }

                if (!row.gameObject.activeSelf) row.gameObject.SetActive(true);

                Vector2 anchored = row.anchoredPosition;
                anchored.x = 0f;
                anchored.y = -dataIndex * stride;
                row.anchoredPosition = anchored;

                _bindRow(row, dataIndex);
            }
        }
    }
}
