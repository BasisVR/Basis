using System.Threading.Tasks;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using static Basis.BasisUI.PanelButton;

namespace Basis.BasisUI
{
    public class LibraryProviderDialogShare
    {
        public static void BuildDialogButtons(DialogBox<bool> shareDialog)
        {
            PanelTabGroup actionsPanel = PanelTabGroup.CreateNew(shareDialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);

            actionsPanel.Descriptor.SetHeight(60);

            PanelButton noPanelButton = PanelButton.CreateNew(ButtonStyles.StandardButton, actionsPanel.TabButtonParent);
            noPanelButton.Descriptor.SetTitle("No");
            noPanelButton.Descriptor.SetWidth(200);
            noPanelButton.Descriptor.SetHeight(60);

            noPanelButton.OnClicked += () =>
            {
                if (shareDialog.IsBusy) return;
                shareDialog.IsBusy = true;

                shareDialog.CloseWithResult(false);
            };

            PanelButton yesPanelButton = PanelButton.CreateNew(ButtonStyles.AcceptButton, actionsPanel.TabButtonParent);
            yesPanelButton.Descriptor.SetTitle("Yes");
            yesPanelButton.Descriptor.SetWidth(200);
            yesPanelButton.Descriptor.SetHeight(60);

            yesPanelButton.OnClicked += () =>
            {
                if (shareDialog.IsBusy) return;
                shareDialog.IsBusy = true;

                shareDialog.CloseWithResult(true);
            };
        }

        /// <summary>
        /// Prompts the user to confirm before sharing content with others on the server.
        /// </summary>
        public static async Task<bool> PromptUserForShare(BasisMenuPanel panel, BasisDataStoreItemKeys.ItemKey item, BasisBundleDescription description)
        {
            DialogBox<bool> shareDialog = DialogBox<bool>.Create(panel, new Vector2(650, 180),
                $"Share {LibraryProviderStrUtil.TitleToCase(description.AssetBundleName)}?",
                $"Are you sure you want to share this {item.Mode} with everyone on the server?",
                AddressableAssets.Sprites.Information,
                true
            );

            BuildDialogButtons(shareDialog);

            return await shareDialog.WaitAsync();
        }
    }
}
