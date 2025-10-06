
#if UNITY_EDITOR
using UnityEditor;
#endif

using Basis.VowganUI.Styling;
using UnityEngine;

namespace Basis.VowganUI
{
    public class ExitProvider : BasisMenuActionProvider<BasisMainMenu>
    {

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new ExitProvider());
        }

        public override string Title => "Exit";
        public override Sprite Icon => null;
        public override int Order => 10;
        public override PaletteStyle NormalStyle => PaletteStyle.DangerColor;

        public override void RunAction()
        {
            BasisMainMenu.Instance.OpenDialogue(
                "Basis VR",
                "Are you sure you want to close Basis?",
                BasisMenuDialoguePanel.AcceptDefault,
                BasisMenuDialoguePanel.DenyDefault, value =>
                {
                    if (!value) return;
#if UNITY_EDITOR
                    EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif

                });
        }
    }
}
