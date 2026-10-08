using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Globalization;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Settings;
using UnityEngine;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisMenuStateMemory
    {
        private const string ActiveTabKey = "menu.state.activetab";
        private const string ScrollPrefix = "menu.state.scroll.";
        private const string SectionPrefix = "menu.state.section.";

        private static string _sectionScope = string.Empty;
        private static readonly Dictionary<string, int> _scopeTitleCounts = new();
        private static bool _hooked;

        public static bool WasOpen;
        public static string ActiveProviderTitle = string.Empty;

        public static bool Enabled => BasisSettingsDefaults.RememberMenuState.RawValue;

        [RuntimeInitializeOnLoadMethod]
        private static void Hook()
        {
            if (_hooked)
            {
                return;
            }
            _hooked = true;
            BasisLocalPlayer.OnLocalPlayerInitialized -= RestoreIfWasOpen;
            BasisLocalPlayer.OnLocalPlayerInitialized += RestoreIfWasOpen;
            BasisScene.Ready -= OnSceneReady;
            BasisScene.Ready += OnSceneReady;
        }

        private static void OnSceneReady(BasisScene scene)
        {
            if (scene != null && scene.gameObject.scene.name == BasisSceneFactory.BasisLoadingSceneKey)
            {
                return;
            }
            RestoreIfWasOpen();
        }

        public static string ActiveTab
        {
            get => BasisSettingsSystem.HasSaveData(ActiveTabKey)
                ? BasisSettingsSystem.LoadString(ActiveTabKey, string.Empty)
                : string.Empty;
            set => BasisSettingsSystem.SaveStringQuiet(ActiveTabKey, value ?? string.Empty);
        }

        public static float GetScroll(string tabKey, float fallback)
        {
            string key = ScrollPrefix + tabKey;
            return BasisSettingsSystem.HasSaveData(key)
                ? BasisSettingsSystem.LoadFloat(key, fallback)
                : fallback;
        }

        public static void SetScroll(string tabKey, float value)
        {
            BasisSettingsSystem.SaveStringQuiet(ScrollPrefix + tabKey, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// While set, expanding or collapsing a section is not remembered. Panel search opens every
        /// section to look inside it and closes them again when the query clears; those are not the
        /// user's choices, so they must not overwrite the layout they left behind.
        /// </summary>
        public static bool SuppressSectionWrites;

        public static bool GetSection(string sectionKey, bool fallback)
        {
            return BasisSettingsSystem.HasSaveData(sectionKey)
                ? BasisSettingsSystem.LoadBool(sectionKey, fallback)
                : fallback;
        }

        public static void SetSection(string sectionKey, bool value)
        {
            if (SuppressSectionWrites)
            {
                return;
            }

            BasisSettingsSystem.SaveStringQuiet(sectionKey, value ? "true" : "false");
        }

        public static bool HasScope => !string.IsNullOrEmpty(_sectionScope);

        public static void BeginScope(string scope)
        {
            _sectionScope = scope ?? string.Empty;
            _scopeTitleCounts.Clear();
        }

        public static void EndScope()
        {
            _sectionScope = string.Empty;
            _scopeTitleCounts.Clear();
        }

        public static string NextSectionKey(string title)
        {
            string baseTitle = string.IsNullOrEmpty(title) ? "section" : title;
            _scopeTitleCounts.TryGetValue(baseTitle, out int count);
            _scopeTitleCounts[baseTitle] = count + 1;
            string suffix = count == 0 ? string.Empty : "#" + count.ToString(CultureInfo.InvariantCulture);
            return SectionPrefix + _sectionScope + "/" + baseTitle + suffix;
        }

        public static void RestoreIfWasOpen()
        {
            if (!Enabled || !WasOpen)
            {
                return;
            }

            BasisMenuBase<BasisMainMenu> instance = BasisMainMenu.Instance;
            if (instance != null && instance.MenuObjectInstance != null && instance.MenuObjectInstance.PanelRoot != null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(ActiveProviderTitle))
            {
                BasisMainMenu.OpenWithProvider(ActiveProviderTitle);
            }
            else
            {
                BasisMainMenu.Open();
            }
        }
    }
}
