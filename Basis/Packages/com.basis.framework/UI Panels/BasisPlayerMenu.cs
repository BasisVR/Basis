using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Enums;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Scripts.UI.UI_Panels
{
    public class BasisPlayerMenu : BasisUIBase
    {
        public static string AddressableID = "BasisPlayerMenu";

        public Button HomePanelButton;
        public Button AvatarPanelButton;
        public Button SettingsPanelButton;
        public Button CloseMenuButton;

        public GameObject HomePanel;
        public GameObject AvatarPanel;
        public GameObject SettingsPanel;

        public static BasisPlayerMenu Instance;

        public void Start()
        {
            Instance = this;
            HomePanelButton.onClick.AddListener(() => OpenPanel(HomePanel));
            AvatarPanelButton.onClick.AddListener(() => OpenPanel(AvatarPanel));
            SettingsPanelButton.onClick.AddListener(() => OpenPanel(SettingsPanel));
            CloseMenuButton.onClick.AddListener(CloseThisMenu);
        }

        public static void OpenPlayerMenu()
        {
            AddressableGenericResource resource = new AddressableGenericResource(AddressableID, AddressableExpectedResult.SingleItem);
            OpenMenuNow(resource);
        }

        public void OpenPanel(GameObject panel)
        {
            HomePanel.SetActive(false);
            AvatarPanel.SetActive(false);
            SettingsPanel.SetActive(false);
            panel.SetActive(true);
        }
        public override void InitalizeEvent()
        {
            BasisCursorManagement.UnlockCursor(nameof(BasisPlayerMenu));
        }
        public override void DestroyEvent()
        {
            BasisCursorManagement.LockCursor(nameof(BasisPlayerMenu));
        }
    }
}
