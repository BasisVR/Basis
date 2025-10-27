using System;
using Basis.Scripts.UI;
using UnityEngine;

namespace Basis.VowganUI
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    [RequireComponent(typeof(BasisGraphicUIRayCaster))]
    public class BasisMenuPanel : PanelElement
    {
        [Serializable]
        public struct PanelData
        {
            public string Title;
            public Vector2 PanelSize;
            public Vector3 PanelPosition;

            public static PanelData Standard(string title) => new()
            {
                Title = title,
                PanelSize = new Vector2(1000, 600),
                PanelPosition = default,
            };

            public static PanelData Toolbar(string title) => new()
            {
                Title = title,
                PanelSize = new Vector2(1000, 150),
                PanelPosition = new Vector3(0, -450),
            };
        }

        public static class Styles
        {
            public static string Default => "VowganUI/Panel";
            public static string Page => "VowganUI/Panel-Page";
            public static string TabPage => "Packages/com.basis.framework/VowganPanels/Prefabs/Menu Panel - Tab Page.prefab";
        }

        [Header("Readout")]
        public PanelData Data;

        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// </summary>
        public static BasisMenuPanel CreateNew(PanelData data, Component parent) => CreateNew(data, parent, Styles.Default);


        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// </summary>
        public static BasisMenuPanel CreateNewTabPage(PanelData data, Component parent, out PanelTabGroup tabGroup)
        {
            BasisMenuPanel page = CreateNew(data, parent, Styles.TabPage);
            tabGroup = page.GetComponentInChildren<PanelTabGroup>();
            return page;
        }

        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// </summary>
        public static BasisMenuPanel CreateNew(PanelData data, Component parent, string referencePath)
        {
            BasisMenuPanel panel = CreateNew<BasisMenuPanel>(referencePath, parent);
            panel.LoadData(data);
            return panel;
        }

        public void LoadData(PanelData data)
        {
            Data = data;

            gameObject.name = data.Title;
            transform.localScale = Vector3.one;
            transform.localPosition = data.PanelPosition;

            rectTransform.sizeDelta = data.PanelSize;
            BasisGraphicUIRayCaster.SetBoxColliderToRectTransform(gameObject);

            if (TitleLabel) TitleLabel.text = data.Title;
        }
    }
}
