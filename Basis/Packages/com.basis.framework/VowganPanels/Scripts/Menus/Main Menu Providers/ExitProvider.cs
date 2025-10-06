
#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;

namespace Basis.VowganUI
{
    public class ExitProvider : BasisMenuActionProvider
    {

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMainMenu.AddActionProvider(new ExitProvider());
        }

        public override string Title => "Exit";
        public override Sprite Icon => null;
        public override int Order => 10;

        public override void RunAction()
        {
            BasisMainMenu.Instance.OpenDialogue(
                "Basis VR",
                "Are you sure you want to close?",
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
