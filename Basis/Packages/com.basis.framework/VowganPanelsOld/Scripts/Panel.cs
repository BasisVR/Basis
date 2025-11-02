using System;
using System.Collections.Generic;
using Basis.Scripts.UI;
using Basis.BasisUI;
using TMPro;
using UnityEngine;

namespace Basis.VowganUIOld
{
    [Serializable]
    public struct PanelData
    {
        public string Title;
        public Vector2 PanelSize;
    }

    public enum PanelPlacementDirection
    {
        Center,
        Left,
        Up,
        Right,
        Down,
        Front,
        Behind,
    }

    public class Panel : AddressableUIInstanceBase
    {
        public static class Styles
        {
            public static string Default => "BasisUI/Panel";
            public static string Page => "BasisUI/Panel-Page";
        }

        [Header("References")]
        public TextMeshProUGUI TitleLabel;
        public RectTransform ContentParent;

        [Header("Readout")]
        public PanelData Data;
        public Panel ParentPanel;
        public List<Panel> ChildPanels = new();


        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// </summary>
        public static Panel CreateNew(PanelData data, Component parent) => CreateNew(data, parent, Styles.Default);

        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// </summary>
        public static Panel CreateNew(PanelData data, Component parent, string referencePath)
        {
            Panel panel = CreateNew<Panel>(referencePath, parent);
            panel.LoadData(data);
            return panel;
        }

        public void LoadData(PanelData data)
        {
            Data = data;

            gameObject.name = data.Title;
            transform.localScale = Vector3.one;

            rectTransform.sizeDelta = data.PanelSize;
            BasisGraphicUIRayCaster.SetBoxColliderToRectTransform(gameObject);

            if (TitleLabel) TitleLabel.text = data.Title;
        }

        public override void OnReleaseEvent()
        {
            base.OnReleaseEvent();
            if (ParentPanel) ParentPanel.ChildPanels.Remove(this);
        }

        public void PlaceRelativeToParent(
            Transform parent,
            Vector3 offset)
        {
            if (!parent)
            {
                Debug.LogWarning($"Attempted to assign a null parent panel to {gameObject}.", this);
                return;
            }

            rectTransform.position = parent.TransformPoint(offset);
        }

        /// <summary>
        /// Place relative to an existing panel.
        /// </summary>
        public void PlaceRelativeToParent(
            Panel parentPanel,
            PanelPlacementDirection direction,
            float margin = 64)
        {
            if (!parentPanel)
            {
                Debug.LogWarning($"Attempted to assign a null parent panel to {gameObject}.", this);
                return;
            }

            ParentPanel = parentPanel;
            ParentPanel.ChildPanels.Add(this);

            switch (direction)
            {
                case PanelPlacementDirection.Center:
                    rectTransform.anchoredPosition = parentPanel.rectTransform.anchoredPosition;
                    break;
                case PanelPlacementDirection.Left:
                    rectTransform.anchoredPosition = Vector2.left * (GetOffsetWidth(this, parentPanel) + margin);
                    break;
                case PanelPlacementDirection.Up:
                    rectTransform.anchoredPosition = Vector2.up * (GetOffsetHeight(this, parentPanel) + margin);
                    break;
                case PanelPlacementDirection.Right:
                    rectTransform.anchoredPosition = Vector2.right * (GetOffsetWidth(this, parentPanel) + margin);
                    break;
                case PanelPlacementDirection.Down:
                    rectTransform.anchoredPosition = Vector2.down * (GetOffsetHeight(this, parentPanel) + margin);
                    break;
                case PanelPlacementDirection.Front:
                    rectTransform.localPosition += Vector3.back * margin;
                    break;
                case PanelPlacementDirection.Behind:
                    rectTransform.localPosition += Vector3.forward * margin;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction), direction, null);
            }

            rectTransform.localPosition += parentPanel.rectTransform.localPosition;
        }

        /// <summary>
        /// Return the amount of offset needed to stack a panel horizontally.
        /// </summary>
        private static float GetOffsetWidth(Panel panel1, Panel panel2) =>
            ((panel1.Data.PanelSize.x / 2f) + (panel2.Data.PanelSize.x / 2f));

        /// <summary>
        /// Return the amount of offset needed to stack a panel vertically.
        /// </summary>
        private static float GetOffsetHeight(Panel panel1, Panel panel2) =>
            ((panel1.Data.PanelSize.y / 2f) + (panel2.Data.PanelSize.y / 2f));

    }
}
