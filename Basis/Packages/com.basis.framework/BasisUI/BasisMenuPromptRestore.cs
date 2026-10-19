using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using TMPro;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    /// <summary>
    /// Unsolicited prompts (incoming direct connections, moderation notices, world load
    /// requests, the crowd and eye-tracking suggestions) force the main menu open so they
    /// are actually seen. Forcing it open rebuilds the menu from scratch, and everything
    /// parented to its PanelRoot goes down with it — the page the user was on and the
    /// virtual keyboard they were typing on.
    ///
    /// <see cref="BasisMainMenu.Open"/> snapshots both on the way down; the prompt calls
    /// <see cref="RestoreAfterPrompt"/> when its panel closes and they come back, the same
    /// way Settings is reopened after a reset. The snapshot is bound to the menu instance
    /// it was taken for, so a release that lands after the menu was closed or rebuilt again
    /// (Unity defers Destroy to the end of the frame) restores nothing.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisMenuPromptRestore
    {
        private static BasisMenuInstance _owner;
        private static string _providerTitle = string.Empty;
        private static bool _keyboardWasOpen;
        private static InputField _keyboardInputField;
        private static TMP_InputField _keyboardTMPInputField;

        private static bool HasSnapshot => _keyboardWasOpen || !string.IsNullOrEmpty(_providerTitle);

        /// <summary>
        /// Record what the menu currently has open, immediately before the old menu is
        /// released. Called from <see cref="BasisMainMenu.Open"/> so every forced re-open is
        /// covered without each prompt having to remember to ask for it.
        /// </summary>
        public static void Capture()
        {
            string providerTitle = BasisMainMenu.ActiveMenuTitle;
            bool keyboard = BasisMenuVirtualKeyboardPanel.HasInstance;

            // A second prompt arriving while the first is still up finds an already-cleared
            // menu. Keep the original snapshot instead of overwriting it with nothing, so
            // whatever the first prompt displaced is still what comes back.
            if (!keyboard && string.IsNullOrEmpty(providerTitle) && HasSnapshot) return;

            _providerTitle = providerTitle;
            _keyboardWasOpen = keyboard;
            _keyboardInputField = keyboard ? BasisMenuVirtualKeyboardPanel.Instance.InputField : null;
            _keyboardTMPInputField = keyboard ? BasisMenuVirtualKeyboardPanel.Instance.TMPInputField : null;
        }

        /// <summary>
        /// Bind the snapshot to the menu that replaced the captured one, so it is only ever
        /// restored into that menu.
        /// </summary>
        public static void SetOwner(BasisMenuInstance owner)
        {
            if (!HasSnapshot) return;
            _owner = owner;
        }

        /// <summary>Drop the snapshot — the menu was closed outright, nothing to put back.</summary>
        public static void Clear()
        {
            _owner = null;
            _providerTitle = string.Empty;
            _keyboardWasOpen = false;
            _keyboardInputField = null;
            _keyboardTMPInputField = null;
        }

        /// <summary>
        /// Put the page and the virtual keyboard the prompt displaced back. Safe to call from
        /// any prompt's release callback: it does nothing when there is no snapshot, when the
        /// menu is gone, or when the menu has since been rebuilt for something else.
        /// </summary>
        public static void RestoreAfterPrompt()
        {
            if (!HasSnapshot || BasisMainMenu.Instance == null) return;

            BasisMenuInstance live = BasisMainMenu.Instance.MenuObjectInstance;
            if (live == null || live != _owner || live.PanelRoot == null) return;

            string providerTitle = _providerTitle;
            bool keyboard = _keyboardWasOpen;
            InputField inputField = _keyboardInputField;
            TMP_InputField tmpInputField = _keyboardTMPInputField;
            Clear();

            // Page first so the keyboard is created last and sits in front of the field it
            // types into — the order the user had them in.
            if (!string.IsNullOrEmpty(providerTitle) && BasisMainMenu.ActiveMenuTitle != providerTitle)
            {
                RunProvider(providerTitle);
            }

            if (!keyboard || BasisMenuVirtualKeyboardPanel.HasInstance) return;

            // The rebuild destroyed any field the keyboard was typing into that lived under the
            // menu; one owned by the world or a HUD survives. Collapsing Unity-null to a real
            // null hands the keyboard "no target" rather than a dead component — the input
            // module re-points it as soon as the user selects a field again.
            BasisMenuVirtualKeyboardPanel.CreateNew(
                inputField ? inputField : null,
                tmpInputField ? tmpInputField : null);
        }

        private static void RunProvider(string title)
        {
            List<BasisMenuActionProvider<BasisMainMenu>> providers = BasisMainMenu.Providers;
            for (int Index = 0; Index < providers.Count; Index++)
            {
                if (providers[Index].Title != title) continue;
                providers[Index].RunAction();
                return;
            }
        }
    }
}
