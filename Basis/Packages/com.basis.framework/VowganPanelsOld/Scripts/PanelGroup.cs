using System.Collections.Generic;
using Basis.VowganUI;
using UnityEngine;

namespace Basis.VowganUIOld
{
    public class PanelGroup : AddressableUIInstanceBase
    {

        public static string ReferencePath => "BasisUI/PanelGroup";

        [Header("References")]
        public PanelGroupMover GroupMover;
        [Tooltip("Offsets from the current tracking mode.")]
        public Transform GroupOffset;
        [Tooltip("Used to move all child panels for focusing a specific panel.")]
        public Transform MovementRoot;
        public Transform StaticRoot;

        [Header("Readout")]
        public Panel FocusedPanel;
        public List<Panel> MovementPanels = new();
        public List<Panel> StaticPanels = new();


        public static PanelGroup CreateNew()
        {
            return CreateNew<PanelGroup>(ReferencePath);
        }

        public override void OnCreateEvent()
        {
            BasisCursorManagement.UnlockCursor(nameof(PanelGroupMover));
        }

        public override void OnReleaseEvent()
        {
            BasisCursorManagement.LockCursor(nameof(PanelGroupMover));
        }

        public void RemoveAllMovementPanels()
        {
            List<Panel> panels = new();
            panels.AddRange(MovementPanels);
            foreach (Panel panel in panels)
            {
                panel.ReleaseInstance();
            }
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
            Panel panel = Panel.CreateNew(data, MovementRoot, style);
            panel.OnReleased += () => OnMovementPanelReleased(panel);

            if (FocusedPanel)
            {
                panel.PlaceRelativeToParent(FocusedPanel, direction, margin);
            }

            MovementPanels.Add(panel);
            if (focusNewPanel) SetFocusedPanel(panel);

            return panel;
        }

        /// <summary>
        /// Instantiate a new Page Panel within this group with the given data.
        /// </summary>
        public Panel CreateRootPanelInGroup(
            PanelData data,
            string style)
        {
            Panel panel = Panel.CreateNew(data, MovementRoot, style);
            panel.OnReleased += () => OnMovementPanelReleased(panel);

            RemoveAllMovementPanels();

            MovementPanels.Add(panel);
            SetFocusedPanel(panel);

            return panel;
        }

        /// <summary>
        /// Instantiate a new Page Panel within this group with the given data.
        /// This panel will not move when new movement panels are focused.
        /// </summary>
        public Panel CreateStaticPanelInGroup(
            PanelData data,
            Vector3 offset) =>
            CreateStaticPanelInGroup(data, offset, Panel.Styles.Default);

        /// <summary>
        /// Instantiate a new Page Panel within this group with the given data.
        /// This panel will not move when new movement panels are focused.
        /// </summary>
        public Panel CreateStaticPanelInGroup(
            PanelData data,
            Vector3 offset,
            string style)
        {
            Panel panel = Panel.CreateNew(data, StaticRoot, style);
            panel.OnReleased += () => OnStaticPanelDestroyed(panel);

            panel.PlaceRelativeToParent(StaticRoot, offset);

            StaticPanels.Add(panel);

            return panel;
        }


        /// <summary>
        /// Select and focus the given panel.
        /// </summary>
        public void SetFocusedPanel(Panel panel)
        {
            FocusedPanel = panel;

            Vector3 target = GroupOffset.position;
            Vector3 delta = target - panel.transform.position;
            MovementRoot.position += delta;
        }

        private void OnMovementPanelReleased(Panel panel)
        {
            MovementPanels.Remove(panel);
        }

        private void OnStaticPanelDestroyed(Panel panel)
        {
            StaticPanels.Remove(panel);
        }
    }
}
