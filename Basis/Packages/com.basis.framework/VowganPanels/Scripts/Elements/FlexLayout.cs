using System;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.VowganUI
{
    public class FlexLayout : LayoutGroup
    {
        public enum FlexFitType
        {
            Uniform,
            Width,
            Height,
            FixedRows,
            FixedColumns,
        }

        [Header("Flexible Grid")]
        public FlexFitType FitType = FlexFitType.Uniform;

        [Min(1)] public int Rows;
        [Min(1)] public int Columns;

        public Vector2 CellSize;
        public Vector2 Spacing;

        public bool FitX;
        public bool FitY;


        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            int childCount = rectChildren.Count;
            childCount = Mathf.Max(childCount, 0);

            if (FitType == FlexFitType.Width ||
                FitType == FlexFitType.Height ||
                FitType == FlexFitType.Uniform)
            {
                float squareRoot = Mathf.Sqrt(Mathf.Max(childCount, 1));
                Rows = Columns = Mathf.CeilToInt(squareRoot);

                switch (FitType)
                {
                    case FlexFitType.Width:
                        FitX = true;
                        FitY = false;
                        break;
                    case FlexFitType.Height:
                        FitX = false;
                        FitY = true;
                        break;
                    case FlexFitType.Uniform:
                        FitX = true;
                        FitY = true;
                        break;
                }
            }

            if (FitType == FlexFitType.Width ||
                FitType == FlexFitType.FixedColumns)
            {
                Rows = Mathf.CeilToInt(childCount / (float)Mathf.Max(Columns, 1));
            }

            if (FitType == FlexFitType.Height ||
                FitType == FlexFitType.FixedRows)
            {
                Columns = Mathf.CeilToInt(childCount / (float)Mathf.Max(Rows, 1));
            }

            Rows = Mathf.Max(Rows, 1);
            Columns = Mathf.Max(Columns, 1);

            float parentWidth = rectTransform.rect.width;
            float parentHeight = rectTransform.rect.height;

            float totalHSpacing = Spacing.x * (Columns - 1);
            float totalVSpacing = Spacing.y * (Rows - 1);

            float cellWidth = (parentWidth - padding.horizontal - totalHSpacing) / Columns;
            float cellHeight = (parentHeight - padding.vertical - totalVSpacing) / Rows;

            CellSize.x = FitX ? cellWidth : CellSize.x;
            CellSize.y = FitY ? cellHeight : CellSize.y;

            float contentWidth = Columns * CellSize.x + totalHSpacing;
            float contentHeight = Rows * CellSize.y + totalVSpacing;

            float startX = GetStartOffset(0, contentWidth);
            float startY = GetStartOffset(1, contentHeight);

            for (int i = 0; i < rectChildren.Count; i++)
            {
                int row = i / Columns;
                int column = i % Columns;

                RectTransform item = rectChildren[i];

                float x = startX + column * (CellSize.x + Spacing.x);
                float y = startY + row * (CellSize.y + Spacing.y);

                SetChildAlongAxis(item, 0, x, CellSize.x);
                SetChildAlongAxis(item, 1, y, CellSize.y);
            }
        }

        public override void CalculateLayoutInputVertical()
        {
        }

        public override void SetLayoutHorizontal()
        {
        }

        public override void SetLayoutVertical()
        {
        }
    }
}
