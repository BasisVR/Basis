using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Helpers;
using UnityEngine;

namespace Basis.VowganUI
{
    public class PanelGroup : AddressableUIInstanceBase
    {

        public static string ReferencePath => "BasisUI/PanelGroup";

        [Header("References")]
        public PanelGroupMover GroupMover;
        [Tooltip("Offsets from the current tracking mode.")]
        public Transform GroupOffset;
        [Tooltip("Used to move all child panels for focusing a specific panel.")]
        public Transform GroupRoot;

        [Header("Settings")]
        public PanelData DemoData;
        public PanelPlacementDirection DemoDirection = PanelPlacementDirection.Front;
        public float DemoMargin = 64;

        [Header("Readout")]
        public Panel FocusedPanel;
        public List<Panel> AllPanels;


        [ContextMenu("DEMO: Create Panel")]
        private void DemoCreatePanel()
        {
            CreatePanelInGroup(DemoData, DemoDirection, DemoMargin);
        }

        [ContextMenu("DEMO: Create Panel, No Focus")]
        private void DemoCreatePanelNoFocus()
        {
            CreatePanelInGroup(DemoData, DemoDirection, DemoMargin, false);
        }

        public static PanelGroup CreateNew()
        {
            return CreateNew<PanelGroup>(ReferencePath);
        }

        protected override void OnCreateEvent()
        {
            BasisCursorManagement.UnlockCursor(nameof(PanelGroupMover));
        }

        protected override void OnDestroyEvent()
        {
            BasisCursorManagement.LockCursor(nameof(PanelGroupMover));
        }

        /// <summary>
        /// Instantiate a new Page Panel within this group with the given data.
        /// </summary>
        public Panel CreatePanelInGroup(
            PanelData data,
            PanelPlacementDirection direction,
            float margin = 16,
            bool focusNewPanel = true) =>
            CreatePanelInGroup(data, direction, margin, focusNewPanel, Panel.Styles.Default);

        /// <summary>
        /// Instantiate a new Page Panel within this group with the given data.
        /// </summary>
        public Panel CreatePanelInGroup(
            PanelData data,
            PanelPlacementDirection direction,
            float margin,
            bool focusNewPanel,
            string style)
        {
            Panel panel = Panel.CreateNew(data, GroupRoot, style);
            panel.OnDestroyed += () => OnPanelDestroyed(panel);

            if (FocusedPanel)
            {
                panel.PlaceRelativeToParent(FocusedPanel, direction, margin);
            }

            AllPanels.Add(panel);
            if (focusNewPanel) SetFocusedPanel(panel);

            return panel;
        }

        [ContextMenu("DEMO: Remove Panel")]
        private void DemoRemovePanel()
        {
            AllPanels.Remove(FocusedPanel);
            FocusedPanel.DestroyInstance();
            FocusedPanel = null;
        }

        /// <summary>
        /// Select and focus the given panel.
        /// </summary>
        public void SetFocusedPanel(Panel panel)
        {
            FocusedPanel = panel;

            Vector3 target = GroupOffset.position;
            Vector3 delta = target - panel.transform.position;
            GroupRoot.position += delta;
        }

        private void OnPanelDestroyed(Panel panel)
        {
            AllPanels.Remove(panel);
            if (panel == FocusedPanel && panel.ParentPanel)
                SetFocusedPanel(panel.ParentPanel);
        }
    }
}
