using System;
using System.Collections.Generic;
using Basis.Scripts.UI;
using UnityEngine;

namespace Basis.VowganUI
{
    [Serializable]
    public struct PanelData
    {
        public string Name;
        public Vector2 PanelSize;
    }

    public class Panel : AddressableUIInstanceBase
    {
        public static class Styles
        {
            public static string Default => "BasisUI/Panel";
            public static string Page => "BasisUI/Panel-Page";
        }

        [Header("References")]
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

            gameObject.name = data.Name;
            transform.localScale = Vector3.one;

            RectTrans.sizeDelta = data.PanelSize;
            BasisGraphicUIRayCaster.SetBoxColliderToRectTransform(gameObject);
        }

        protected override void OnDestroyEvent()
        {
            base.OnDestroyEvent();
            if (ParentPanel) ParentPanel.RemoveChildPanel(this);
        }

        public void AddChildPanel(Panel panel)
        {
            ChildPanels.Add(panel);
        }

        public void RemoveChildPanel(Panel panel)
        {
            ChildPanels.Remove(panel);
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
            ParentPanel.AddChildPanel(this);

            switch (direction)
            {
                case PanelPlacementDirection.Center:
                    RectTrans.anchoredPosition = parentPanel.RectTrans.anchoredPosition;
                    break;
                case PanelPlacementDirection.Left:
                    RectTrans.anchoredPosition = Vector2.left * (GetOffsetWidth(this, parentPanel) + margin);
                    break;
                case PanelPlacementDirection.Up:
                    RectTrans.anchoredPosition = Vector2.up * (GetOffsetHeight(this, parentPanel) + margin);
                    break;
                case PanelPlacementDirection.Right:
                    RectTrans.anchoredPosition = Vector2.right * (GetOffsetWidth(this, parentPanel) + margin);
                    break;
                case PanelPlacementDirection.Down:
                    RectTrans.anchoredPosition = Vector2.down * (GetOffsetHeight(this, parentPanel) + margin);
                    break;
                case PanelPlacementDirection.Front:
                    RectTrans.localPosition += Vector3.back * margin;
                    break;
                case PanelPlacementDirection.Behind:
                    RectTrans.localPosition += Vector3.forward * margin;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction), direction, null);
            }

            RectTrans.localPosition += parentPanel.RectTrans.localPosition;
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
