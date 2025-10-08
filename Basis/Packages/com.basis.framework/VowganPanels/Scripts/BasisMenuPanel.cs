using System;
using Basis.Scripts.UI;
using TMPro;
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

            public static PanelData Hotbar(string title) => new()
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
