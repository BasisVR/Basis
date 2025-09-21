using System;
using System.Collections.Generic;
using Basis.Scripts.UI;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Events;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Basis.VowganUI
{
    public class MenuPanel : MonoBehaviour
    {
        public const string REFERENCE_PANEL = "PanelElement/Panel";

        public PanelData Data => _data;
        public MenuPanel Parent => _parentPanel;
        public UnityAction<MenuPanel> OnRelease;

        [Header("References")]
        [SerializeField] private RectTransform _selfTransform;

        [Header("Readout")]
        [SerializeField] private PanelData _data;
        [SerializeField] private MenuPanel _parentPanel;
        [SerializeField] private List<MenuPanel> _childPanels = new();

        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// </summary>
        public static MenuPanel CreateNew(PanelData data, Transform parent)
        {
            GameObject obj = Addressables.InstantiateAsync(
                    REFERENCE_PANEL,
                    new InstantiationParameters(parent, false))
                .WaitForCompletion();
            MenuPanel panel = obj.GetComponent<MenuPanel>();
            panel.LoadData(data);
            return panel;
        }

        public void LoadData(PanelData data)
        {
            _data = data;

            gameObject.name = data.Name;
            transform.localScale = Vector3.one * _data.Scale;

            _selfTransform.sizeDelta = data.PanelSize;
            BasisGraphicUIRayCaster.SetBoxColliderToRectTransform(gameObject);
        }

        /// <summary>
        /// Release this addressable instance and destroy the GameObject.
        /// </summary>
        [ContextMenu("Release Panel")]
        public void Release() => Release(false);

        public void Release(bool ignoreCallback)
        {
            foreach (MenuPanel child in _childPanels)
                if (child)
                    child.Release(ignoreCallback);
            if (_parentPanel) _parentPanel.RemoveChild(this);

            // Release all children before calling this callback to
            // insure proper active panel selection in the PanelGroup.
            if (!ignoreCallback) OnRelease?.Invoke(this);
            Addressables.ReleaseInstance(gameObject);
        }

        public void AddChild(MenuPanel panel)
        {
            _childPanels.Add(panel);
        }

        public void RemoveChild(MenuPanel panel)
        {
            _childPanels.Remove(panel);
        }

        /// <summary>
        /// Place relative to an existing menu.
        /// </summary>
        public void PlaceRelativeToParent(
            MenuPanel parentMenu,
            PanelPlacementDirection direction,
            float margin = 64)
        {
            if (!parentMenu)
            {
                Debug.LogWarning($"Attempted to assign a null parent menu to {gameObject}.", this);
                return;
            }

            _parentPanel = parentMenu;
            _parentPanel.AddChild(this);

            switch (direction)
            {
                case PanelPlacementDirection.Left:
                    _selfTransform.anchoredPosition = Vector2.left * (GetOffsetWidth(this, parentMenu) + margin);
                    break;
                case PanelPlacementDirection.Up:
                    _selfTransform.anchoredPosition = Vector2.up * (GetOffsetHeight(this, parentMenu) + margin);
                    break;
                case PanelPlacementDirection.Right:
                    _selfTransform.anchoredPosition = Vector2.right * (GetOffsetWidth(this, parentMenu) + margin);
                    break;
                case PanelPlacementDirection.Down:
                    _selfTransform.anchoredPosition = Vector2.down * (GetOffsetHeight(this, parentMenu) + margin);
                    break;
                case PanelPlacementDirection.Front:
                    _selfTransform.localPosition += Vector3.back * margin;
                    break;
                case PanelPlacementDirection.Behind:
                    _selfTransform.localPosition += Vector3.forward * margin;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction), direction, null);
            }

            _selfTransform.localPosition += parentMenu._selfTransform.localPosition;
        }

        /// <summary>
        /// Return the amount of offset needed to stack a menu horizontally.
        /// </summary>
        private static float GetOffsetWidth(MenuPanel panel1, MenuPanel panel2) =>
            ((panel1._data.PanelSize.x / 2f) + (panel2._data.PanelSize.x / 2f));

        /// <summary>
        /// Return the amount of offset needed to stack a menu vertically.
        /// </summary>
        private static float GetOffsetHeight(MenuPanel panel1, MenuPanel panel2) =>
            ((panel1._data.PanelSize.y / 2f) + (panel2._data.PanelSize.y / 2f));

    }
}
