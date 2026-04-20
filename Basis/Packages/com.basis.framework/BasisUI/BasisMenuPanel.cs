using System;
using Basis.Scripts.UI;
using UnityEngine;

namespace Basis.BasisUI
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PanelElementDescriptor))]
    [RequireComponent(typeof(RectTransform))]
    [RequireComponent(typeof(BasisGraphicUIRayCaster))]
    public class BasisMenuPanel : AddressableUIInstanceBase
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
                PanelSize = new Vector2(1500, 1000),
                PanelPosition = default,
            };

            public static PanelData Toolbar(string title) => new()
            {
                Title = title,
                PanelSize = new Vector2(1500, 200),
                PanelPosition = new Vector3(0, -630),
            };
        }

        public static class PanelStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Menu Panel.prefab";
            public static string Page => "Packages/com.basis.sdk/Prefabs/Menu Panel - Page.prefab";
        }

        public PanelData Data;
        public PanelElementDescriptor Descriptor { get; private set; }


        protected override void Awake()
        {
            base.Awake();
            Descriptor = GetComponent<PanelElementDescriptor>();
        }

        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// </summary>
        public static BasisMenuPanel CreateNew(PanelData data, Component parent) => CreateNew(data, parent, PanelStyles.Default);


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

            Descriptor.SetTitle(data.Title);
        }
    }
}
