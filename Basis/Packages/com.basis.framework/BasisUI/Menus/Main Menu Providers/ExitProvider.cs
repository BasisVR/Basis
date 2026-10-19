
#if UNITY_EDITOR
using UnityEditor;
#endif

using Basis.Scripts.Networking;
using UnityEngine;

namespace Basis.BasisUI
{
    public class ExitProvider : BasisMenuActionProvider<BasisMainMenu>
    {

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new ExitProvider());
        }

        public override string Title => BasisLocalization.Get("menu.provider.exit");
        public override string IconAddress => AddressableAssets.Sprites.Exit;
        public override int Order => 9999; // always last

        public override bool Hidden => false;

        public override void OnButtonCreated(PanelButton button)
        {
            base.OnButtonCreated(button);
            button.ButtonStyling.SetStyle("Hotbar Button Danger");

            // Half the normal hotbar button width — a compact exit button.
            float width = button.rectTransform.sizeDelta.x;
            if (width > 0f) button.SetWidth(width * 0.5f);
        }

        public override void RunAction()
        {
            if (BasisMainMenu.Instance == null || BasisMainMenu.Instance.Dialogue != null) return;

            BasisMainMenu.Instance.OpenDialogue(
                BasisLocalization.Get("menu.exit.dialog.title"),
                BasisLocalization.Get("menu.exit.dialog.body"),
                BasisLocalization.Get("ui.cancel"),
                BasisLocalization.Get("menu.exit.dialog.confirm"),
                value =>
                {
                    if (value) return;
#if UNITY_EDITOR
                    EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif

                });

            if (BasisMainMenu.Instance.Dialogue == null) return;
            BasisMainMenu.Instance.Dialogue.CaptureOnClose = false;

            if (BasisAppRelaunch.IsSupported)
            {
                string label = BasisLocalization.Get(BasisNetworkConnection.LocalPlayerIsConnected
                    ? "menu.exit.dialog.reboot"
                    : "menu.exit.dialog.restart");
                BasisMainMenu.Instance.Dialogue.EnableAlternate(
                    label,
                    () => BasisAppRelaunch.RebootAndReconnect());
            }
        }
    }
}
