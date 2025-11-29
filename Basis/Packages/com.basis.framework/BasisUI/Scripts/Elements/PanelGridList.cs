using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    [RequireComponent(typeof(ScrollRect))]
    public class PanelGridList : PanelDataComponent<int>
    {
        [SerializeField] private int _columnCount = 3;
        [SerializeField] private float _cellWidth = 200;
        [SerializeField] private float _cellHeight = 220;
        [SerializeField] private float _spacing = 15;
        [SerializeField] private int _rowBuffer = 1;
        [SerializeField] private List<PanelGridItem> _itemPool = new();

        public List<PanelGridItem.Data> ListData { get; protected set; }

        public ScrollRect Scroll => _scrollRect ??= _scrollRect = GetComponent<ScrollRect>();
        private ScrollRect _scrollRect;

        private int _totalCount;
        private int _totalRowCount;
        private int _topRowIndex;


        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            Scroll.onValueChanged.AddListener(_ => UpdateGridLayout());
            ListData = new List<PanelGridItem.Data>();
            RebuildView();
        }

        public void RebuildView()
        {
            _totalCount = ListData.Count;
            _totalRowCount = Mathf.CeilToInt(_totalCount / (float)_columnCount);

            float rowHeight = _cellHeight + _spacing;
            float contentHeight = Mathf.Max(0, (_totalRowCount * rowHeight) - _spacing);
            Scroll.content.sizeDelta = new Vector2(Scroll.content.sizeDelta.x, contentHeight);

            foreach (PanelGridItem item in _itemPool)
            {
                item.Descriptor.SetPivot(Vector2.up);
                item.Descriptor.SetSize(new Vector2(_cellWidth, _cellHeight));
            }

            _topRowIndex = 0;
            UpdateGridLayout(true);
        }

        public void AddItem(PanelGridItem.Data data)
        {
            ListData.Add(data);
            RebuildView();
        }


        private void UpdateGridLayout(bool ignoreExistingRow = false)
        {
            if (_totalRowCount == 0 || _totalCount == 0) return;

            float rowHeight = _cellHeight + _spacing;
            float scrollY = Mathf.Max(0f, Scroll.content.anchoredPosition.y);

            float viewportHeight = Scroll.viewport.rect.height;
            int rowsInViewport = Mathf.CeilToInt(viewportHeight / rowHeight);

            int newTopRow = Mathf.FloorToInt(scrollY / rowHeight) - _rowBuffer;
            newTopRow = Mathf.Clamp(newTopRow, 0, Mathf.Max(0, _totalRowCount - rowsInViewport - _rowBuffer));

            if (!ignoreExistingRow && newTopRow == _topRowIndex) return;

            _topRowIndex = newTopRow;

            for (int i = 0; i < _itemPool.Count; i++)
            {
                int row = (i / _columnCount) + _topRowIndex;
                int col = i % _columnCount;
                int dataIndex = row * _columnCount + col;

                PanelGridItem item = _itemPool[i];
                Vector2 anchorPos = new Vector2(
                    col * (_cellWidth + _spacing),
                    -row * (_cellHeight + _spacing));

                if (dataIndex >= _totalCount)
                {
                    item.Descriptor.SetActive(false);
                }
                else
                {
                    item.Descriptor.SetActive(true);
                    item.Descriptor.SetAnchorPosition(anchorPos);
                    item.BindDataIndex(this, dataIndex);
                }
            }
        }

        public void OnItemSelected(int index)
        {
            SetValue(index);
            foreach (PanelGridItem item in _itemPool)
            {
                item.RefreshVisuals();
            }
        }
    }
}
