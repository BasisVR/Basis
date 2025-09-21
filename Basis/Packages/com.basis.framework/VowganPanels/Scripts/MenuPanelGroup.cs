using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Basis.VowganUI
{
    public class MenuPanelGroup : MonoBehaviour
    {
        public const string REFERENCE_GROUP = "PanelElement/PanelGroup";

        [Header("References")]
        [SerializeField] private Transform _groupOffset;
        [SerializeField] private Transform _groupRoot;

        [Header("Settings")]
        [SerializeField] private PanelData _demoData;
        [SerializeField] private PanelPlacementDirection _demoDirection = PanelPlacementDirection.Front;
        [SerializeField] private float _creationMargin = 64;

        [Header("Readout")]
        [SerializeField] private MenuPanel _focusedPanel;
        [SerializeField] private List<MenuPanel> _allPanels;


        /// <summary>
        /// Instantiate a new Panel Group.
        /// </summary>
        public static MenuPanelGroup CreateNew()
        {
            GameObject obj = Addressables.InstantiateAsync(REFERENCE_GROUP).WaitForCompletion();
            MenuPanelGroup group = obj.GetComponent<MenuPanelGroup>();
            return group;
        }

        private void OnEnable()
        {
            BasisCursorManagement.UnlockCursor(nameof(PanelGroupMover));
        }

        private void OnDisable()
        {
            BasisCursorManagement.LockCursor(nameof(PanelGroupMover));
        }


        [ContextMenu("DEMO: Create Panel")]
        private void DemoCreatePanel()
        {
            CreatePanelInGroup(_demoData, true);
        }

        [ContextMenu("DEMO: Create Panel, No Focus")]
        private void DemoCreatePanelNoFocus()
        {
            CreatePanelInGroup(_demoData, false);
        }

        /// <summary>
        /// Instantiate a new panel within this group with the given data.
        /// </summary>
        public void CreatePanelInGroup(PanelData data, bool focusNewPanel)
        {
            MenuPanel panel = MenuPanel.CreateNew(data, _groupRoot);
            panel.OnRelease += OnPanelReleased;

            if (_focusedPanel)
            {
                panel.PlaceRelativeToParent(_focusedPanel, _demoDirection, _creationMargin);
            }

            _allPanels.Add(panel);
            if (focusNewPanel) SetFocusedPanel(panel);
        }

        [ContextMenu("DEMO: Remove Panel")]
        private void DemoRemovePanel()
        {
            _allPanels.Remove(_focusedPanel);
            _focusedPanel.Release();
            _focusedPanel = null;
        }

        /// <summary>
        /// Select and focus the given panel.
        /// </summary>
        public void SetFocusedPanel(MenuPanel panel)
        {
            _focusedPanel = panel;

            Vector3 target = _groupOffset.position;
            Vector3 delta = target - panel.transform.position;
            _groupRoot.position += delta;
        }

        private void OnPanelReleased(MenuPanel panel)
        {
            _allPanels.Remove(panel);
            if (panel == _focusedPanel && panel.Parent)
                SetFocusedPanel(panel.Parent);
        }

        /// <summary>
        /// Release this addressable instance and destroy the GameObject.
        /// </summary>
        [ContextMenu("Release Panel")]
        public void Release()
        {
            foreach (MenuPanel child in _allPanels)
            {
                if (child) child.Release(true);
            }

            Addressables.ReleaseInstance(gameObject);
        }
    }
}
