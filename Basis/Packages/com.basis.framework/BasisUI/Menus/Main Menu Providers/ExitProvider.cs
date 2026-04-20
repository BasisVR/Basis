
#if UNITY_EDITOR
using UnityEditor;
#endif

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
        public override int Order => 9999;

        public override bool Hidden => false;

        public override void OnButtonCreated(PanelButton button)
        {
            base.OnButtonCreated(button);
            button.ButtonStyling.SetStyle("Hotbar Button Danger");
        }

        public override void RunAction()
        {
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
        }
    }
}
