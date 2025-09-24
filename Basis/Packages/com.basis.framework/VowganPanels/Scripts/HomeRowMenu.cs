using UnityEngine;

namespace Basis.VowganUI
{
    public class HomeRowMenu : MonoBehaviour
    {

        public static PanelGroup Group;
        public static Panel RootPanel;

        public static PanelButton SettingsButton;
        public static PanelButton ServersButton;
        public static PanelButton AvatarsButton;
        public static PanelButton RespawnButton;
        public static PanelButton CameraButton;
        public static PanelButton MirrorButton;
        public static PanelButton ExitButton;

        public static void ToggleMenu()
        {
            if (Group) Group.DestroyInstance();
            else CreateMenu();
        }

        public static void CreateMenu()
        {
            if (Group) Group.DestroyInstance();
            Group = PanelGroup.CreateNew();
            CreateItems();
            BindItems();
        }

        private static void CreateItems()
        {
            RootPanel = Group.CreatePanelInGroup(new PanelData
                {
                    Name = "Home Row Panel",
                    PanelSize = new Vector2(800, 150),
                },
                PanelPlacementDirection.Center);

            LayoutContainer layout = LayoutContainer.CreateNew(RootPanel.ContentParent, LayoutDirection.Horizontal);
            layout.CopyLayoutOptions(new LayoutContainerOptions
            {
                Alignment = TextAnchor.MiddleCenter,
                Constrained = false,
                StretchItemWidth = false,
                StretchItemHeight = false,
                SpreadItemWidth = true,
                SpreadItemHeight = true
            });

            SettingsButton = PanelButton.CreateNew(layout, "Settings");
            ServersButton = PanelButton.CreateNew(layout, "Servers");
            AvatarsButton = PanelButton.CreateNew(layout, "Avatars");
            RespawnButton = PanelButton.CreateNew(layout, "Respawn");
            CameraButton = PanelButton.CreateNew(layout, "Camera");
            MirrorButton = PanelButton.CreateNew(layout, "Mirror");
            ExitButton = PanelButton.CreateNew(layout, "Exit");
        }

        private static void BindItems()
        {
            SettingsButton.OnClicked.AddListener(OnSettingsButtonClicked);
        }

        private static void OnSettingsButtonClicked()
        {
            if (Group.FocusedPanel == RootPanel)
            {
                CreatePanel();
            }
            else if(Group.FocusedPanel)
            {
                Group.FocusedPanel.DestroyInstance();
            }

            return;

            void CreatePanel()
            {
                Group.CreatePanelInGroup(new PanelData
                    {
                        Name = "Settings Panel",
                        PanelSize = new Vector2(800, 400)
                    },
                    PanelPlacementDirection.Up,
                    16,
                    true,
                    Panel.Styles.Page);
            }
        }
    }
}
