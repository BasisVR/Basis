using System.Threading.Tasks;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using static Basis.BasisUI.PanelButton;
using static Basis.BasisUI.PanelTextField;
using static SerializableBasis;

namespace Basis.BasisUI
{
    public class LibraryProviderDialogFolder
    {
        public static async Task<string> PromptUserForFolderName(BasisMenuPanel panel, string title, string initial)
        {
            DialogBox<string> dialog = DialogBox<string>.Create(panel, new Vector2(900, 380), title, BasisLocalization.Get("library.dialog.folderName.body"), AddressableAssets.Sprites.FileTray, true);
            if (dialog.Descriptor == null) return await dialog.WaitAsync();

            string fieldTitle = BasisLocalization.Get("library.dialog.folderName.field");
            PanelTextField nameField = PanelTextField.CreateNew(TextFieldStyles.EntryVertical, dialog.Descriptor.ContentParent);
            nameField.Descriptor.SetTitle(fieldTitle);
            nameField.Descriptor.SetIcon(AddressableAssets.Sprites.FileTray);
            nameField.Descriptor.SetHeight(115);
            nameField._placeholderLabel.text = BasisLocalization.Get("library.dialog.folderName.placeholder");
            nameField._inputField.characterLimit = ContentShareFolder.MaxNameLength;
            if (!string.IsNullOrEmpty(initial)) nameField._inputField.text = initial;
            nameField.SetRequired(BasisLocalization.Get("ui.validation.requiredNamed", fieldTitle), gradeImmediately: false);

            PanelTabGroup actionsPanel = PanelTabGroup.CreateNew(dialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);
            actionsPanel.Descriptor.SetHeight(60);

            PanelButton cancelButton = PanelButton.CreateNew(ButtonStyles.CancelButton, actionsPanel.TabButtonParent);
            cancelButton.Descriptor.SetTitle(BasisLocalization.Get("ui.cancel"));
            cancelButton.Descriptor.SetWidth(200);
            cancelButton.Descriptor.SetHeight(60);
            cancelButton.OnClicked += () =>
            {
                if (dialog.IsBusy) return;
                dialog.IsBusy = true;
                dialog.CloseWithResult(null);
            };

            PanelButton saveButton = PanelButton.CreateNew(ButtonStyles.AcceptButton, actionsPanel.TabButtonParent);
            saveButton.Descriptor.SetTitle(BasisLocalization.Get("ui.save"));
            saveButton.Descriptor.SetWidth(200);
            saveButton.Descriptor.SetHeight(60);
            saveButton.OnClicked += () =>
            {
                if (dialog.IsBusy) return;
                string name = BasisDataStoreItemFolders.CleanName(nameField.Value);
                if (name.Length == 0)
                {
                    nameField.Validate();
                    return;
                }
                dialog.IsBusy = true;
                dialog.CloseWithResult(name);
            };

            return await dialog.WaitAsync();
        }

        public static async Task<bool> PromptUserForFolderRemoval(BasisMenuPanel panel, string folderTitle)
        {
            DialogBox<bool> dialog = DialogBox<bool>.Create(panel, new Vector2(830, 220),
                BasisLocalization.Get("library.dialog.remove.title", folderTitle),
                BasisLocalization.Get("library.dialog.removeFolder.body"),
                AddressableAssets.Sprites.Information,
                true
            );
            if (dialog.Descriptor == null) return await dialog.WaitAsync();

            LibraryProviderDialogRemove.BuildDialogButtons(dialog);

            return await dialog.WaitAsync();
        }

        public static async Task<bool> PromptUserForFolderShare(BasisMenuPanel panel, string folderTitle, int shared, int skipped)
        {
            string body = BasisLocalization.Get("library.dialog.shareFolder.body", shared);
            if (skipped > 0) body += "\n\n" + BasisLocalization.Get("library.dialog.shareFolder.skipped", skipped);
            DialogBox<bool> dialog = DialogBox<bool>.Create(panel, new Vector2(830, skipped > 0 ? 330 : 250),
                BasisLocalization.Get("library.dialog.share.title", folderTitle),
                body,
                AddressableAssets.Sprites.Information,
                true
            );
            if (dialog.Descriptor == null) return await dialog.WaitAsync();

            LibraryProviderDialogShare.BuildDialogButtons(dialog);

            return await dialog.WaitAsync();
        }
    }
}
