/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections.Generic;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.Settings;
using UnityEditor;
using static Meta.XR.Editor.ToolingSupport.ToolDescriptor;

namespace Meta.XR.Editor.PlayCompanion
{
    [InitializeOnLoad]
    internal static class Manager
    {
        private static readonly List<Item> Items = new List<Item>();

        public static readonly CustomBool Enabled =
            new UserBool()
            {
                Owner = null,
                Uid = "PlayCompanion.Enabled",
                Default = true
            };

        static Manager()
        {
            SetupMenu();

            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += OnEditorQuitting;
            OVRUserSettingsProvider.Register("Toolbar", OnSettingsGUI);

            Update();
        }

        public static IReadOnlyList<Item> RegisteredItems => Items;
        public static Item SelectedItem { get; private set; }

        public static void RegisterItem(Item item)
        {
            if (Items.Contains(item)) return;

            Items.Add(item);
            Items.Sort((x, y) => x.Order.CompareTo(y.Order));
        }

        public static void UnregisterItem(Item item)
        {
            Items.Remove(item);
        }

        public static void Select(Item item, bool automated = false)
        {
            if (!automated && EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (SelectedItem == item) return;

            if (!automated || item != null)
            {
                SelectedItem?.OnUnselect?.Invoke();
            }

            SelectedItem = item;

            if (!automated)
            {
                SelectedItem?.OnSelect?.Invoke();
            }
        }

        public static void Toggle(Item item)
        {
            Select(SelectedItem == item ? null : item);
        }

        private static void Update()
        {
            TestItems();
        }

        private static void TestItems()
        {
            foreach (var item in RegisteredItems)
            {
                if (item.ShouldBeSelected?.Invoke() ?? false)
                {
                    Select(item, true);
                }

                if (item.ShouldBeUnselected?.Invoke() ?? false)
                {
                    Select(null, true);
                }
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange stateChange)
        {
            switch (stateChange)
            {
                case PlayModeStateChange.ExitingEditMode:
                    SelectedItem?.OnEnteringPlayMode?.Invoke();
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    SelectedItem?.OnExitingPlayMode?.Invoke();
                    break;
            }
        }

        private static void OnEditorQuitting()
        {
            SelectedItem?.OnEditorQuitting?.Invoke();
        }

        private static void SetupMenu()
        {
            var defaultItem = new Item()
            {
                Order = 0,
                Name = "Default Play Mode",
                Icon = Styles.Contents.DefaultPlayModeIcon,
                Color = UserInterface.Styles.Colors.LightGray,
                ShouldBeSelected = () => Manager.SelectedItem == null,
                Show = false
            };
            Manager.RegisterItem(defaultItem);

            var buildItem = new Item()
            {
                Order = 99,
                Name = "Build and Run on headset",
                Icon = Styles.Contents.BuildIcon,
                Color = UserInterface.Styles.Colors.LightGray,
                Show = false
            };
            Manager.RegisterItem(buildItem);
        }

        private static void OnSettingsGUI()
        {
            Enabled.DrawForGUI(Origins.UserSettings, null);
        }
    }
}
