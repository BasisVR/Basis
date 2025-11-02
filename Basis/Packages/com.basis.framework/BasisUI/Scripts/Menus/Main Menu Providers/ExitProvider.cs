
#if UNITY_EDITOR
using UnityEditor;
#endif

using Basis.BasisUI.Styling;
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

        public override string Title => "Exit";
        public override Sprite Icon => AddressableAssets.GetSprite(AddressableAssets.Sprites.Exit);
        public override bool IconIsAddressable => true;
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
