using System.Threading.Tasks;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using static Basis.BasisUI.PanelButton;

namespace Basis.BasisUI
{
    public class LibraryProviderDialogRemove
    {
        #region PromptUserForRemoval(BasisDataStoreItemKeys.ItemKey item)

        public static void BuildDialogButtons(DialogBox<bool> contentRemovalDialog)
        {
            // Delete & Load Buttons
            PanelTabGroup actionsPanel = PanelTabGroup.CreateNew(contentRemovalDialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);

            actionsPanel.Descriptor.SetHeight(60);
            //actionsPanel.Descriptor.SetWidth(900);

            PanelButton noPanelButton = PanelButton.CreateNew(ButtonStyles.CancelButton, actionsPanel.TabButtonParent); //ButtonStyles.Cancel
            noPanelButton.Descriptor.SetTitle("No");
            noPanelButton.Descriptor.SetWidth(200);
            noPanelButton.Descriptor.SetHeight(60);

            // upon delete we do these actions
            noPanelButton.OnClicked += () =>
            {
                if (contentRemovalDialog.IsBusy) return;
                contentRemovalDialog.IsBusy = true;

                contentRemovalDialog.CloseWithResult(false);
            };


            PanelButton yesPanelButton = PanelButton.CreateNew(ButtonStyles.AcceptButton, actionsPanel.TabButtonParent);
            yesPanelButton.Descriptor.SetTitle("Yes");
            yesPanelButton.Descriptor.SetWidth(200);
            yesPanelButton.Descriptor.SetHeight(60);
            // on load of a item we do these actions
            yesPanelButton.OnClicked += () =>
            {
                if (contentRemovalDialog.IsBusy) return;
                contentRemovalDialog.IsBusy = true;

                // just close the overlay instead.
                contentRemovalDialog.CloseWithResult(true);
            };
        }

        /// <summary>
        /// Invoked when the user wants to remove a desired item
        /// we prompt them with this to wait for a result
        /// </summary>
        public static async Task<bool> PromptUserForRemoval(BasisMenuPanel panel, BasisDataStoreItemKeys.ItemKey item, BasisBundleDescription description)
        {
            DialogBox<bool> contentRemovalDialog = DialogBox<bool>.Create(panel, new Vector2(650, 180),
                $"Delete {LibraryProviderStrUtil.TitleToCase(description.AssetBundleName)}?",
                $"Are you sure you want to remove this {item.Mode}?",
                AddressableAssets.Sprites.Information,
                true
            );

            BuildDialogButtons(contentRemovalDialog);

            return await contentRemovalDialog.WaitAsync();
        }

        // alternate prompt direct with string
        public static async Task<bool> PromptUserForRemoval(BasisMenuPanel panel, string assetName, string type)
        {
            DialogBox<bool> contentRemovalDialog = DialogBox<bool>.Create(panel, new Vector2(650, 180),
                $"Delete {assetName}?",
                $"Are you sure you want to remove this {type}?",
                AddressableAssets.Sprites.Information,
                true
            );

            BuildDialogButtons(contentRemovalDialog);

            return await contentRemovalDialog.WaitAsync();
        }

        #endregion
    }

}