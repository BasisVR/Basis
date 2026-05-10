using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using static Basis.BasisUI.PanelButton;
using static Basis.BasisUI.PanelTextField;

namespace Basis.BasisUI
{
    public class LibraryProviderDialogLegacyContent
    {
 
        #region PromptUserToDefineLegacyContent
        /// <summary>
        /// We invoke this when we have detected legacy content or a BEE file that has no metadata
        /// We then ask the user what content type you are adding?
        /// </summary>
        public static async Task<BundledContentHolder.Mode> PromptUserToDefineLegacyContent(BasisMenuPanel panel)
        {
            DialogBox<BundledContentHolder.Mode> legacyCotentDefineDialogBox = DialogBox<BundledContentHolder.Mode>.Create(panel, new Vector2(830, 430),
                Basis.BasisUI.BasisLocalization.Get("library.dialog.legacy.title"),
                Basis.BasisUI.BasisLocalization.Get("library.dialog.legacy.description"),
                AddressableAssets.Sprites.Add,
                true
            );

            // create the exit button for the dialog box
            var button = PanelButton.CreateNew(ButtonStyles.ExitButton, legacyCotentDefineDialogBox.Descriptor.Header);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            button.OnClicked += () => legacyCotentDefineDialogBox.Cancel(BundledContentHolder.Mode.Legacy);

            // information to give to the user
            PanelTextField createdInformationTextField = PanelTextField.CreateNew(TextFieldStyles.EntryVertical, legacyCotentDefineDialogBox.Descriptor);
            createdInformationTextField._inputField.gameObject.SetActive(false); // disable the text input field box
            createdInformationTextField.Descriptor.SetTitle(Basis.BasisUI.BasisLocalization.Get("library.dialog.legacy.whyShowing"));
            createdInformationTextField.Descriptor.SetIcon(AddressableAssets.Sprites.Information);
            createdInformationTextField.Descriptor.SetDescription(Basis.BasisUI.BasisLocalization.Get("library.dialog.legacy.whyShowing.description"));

            createdInformationTextField.Descriptor.SetHeight(100);
            createdInformationTextField.Descriptor.SetWidth(800);

            // the item type dropdown determines which library tab the new item will appear in.
            PanelDropdown contentTypeDropDown = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.OverlayEntry, legacyCotentDefineDialogBox.Descriptor);
            var modeNames = Enum.GetValues(typeof(BundledContentHolder.Mode))
                    .Cast<BundledContentHolder.Mode>()
                    .Where(m => m != BundledContentHolder.Mode.Legacy) // remove legacy from selection
                    .Select(m => m.ToString())
                    .ToArray();
            contentTypeDropDown.Descriptor.SetTitle(Basis.BasisUI.BasisLocalization.Get("library.dialog.legacy.contentType"));
            contentTypeDropDown.Descriptor.SetIcon(AddressableAssets.Sprites.FileTray);
            contentTypeDropDown.Descriptor.SetDescription(Basis.BasisUI.BasisLocalization.Get("library.dialog.legacy.contentType.description"));
            contentTypeDropDown.AssignEntries(modeNames.ToList());

            // derive the default selected mode from the currently active tab, so if the user is browsing avatars and clicks "Add New CachedContent"
            contentTypeDropDown.SetValueWithoutNotify(modeNames[0]);
            contentTypeDropDown.Descriptor.SetHeight(50);
            contentTypeDropDown.Descriptor.SetWidth(800);

            // Add and Cancel buttons
            PanelTabGroup acceptOrDenyPanel = PanelTabGroup.CreateNew(legacyCotentDefineDialogBox.Descriptor, LayoutDirection.HorizontalNoBackground);

            acceptOrDenyPanel.Descriptor.SetHeight(50);
            acceptOrDenyPanel.Descriptor.SetWidth(800);

            PanelButton yesPanel = PanelButton.CreateNew(ButtonStyles.AcceptButton, acceptOrDenyPanel.TabButtonParent); //ButtonStyles.Cancel
            yesPanel.Descriptor.SetTitle(Basis.BasisUI.BasisLocalization.Get("library.dialog.add.addButton"));
            yesPanel.Descriptor.SetWidth(800);
            yesPanel.Descriptor.SetHeight(60);

            // Add does the async work, then closes.
            yesPanel.OnClicked += () =>
            {
                if (legacyCotentDefineDialogBox.IsBusy) return;
                legacyCotentDefineDialogBox.IsBusy = true;

                var selected = contentTypeDropDown.Value;
                var mode = (BundledContentHolder.Mode)Enum.Parse(typeof(BundledContentHolder.Mode), selected);
                legacyCotentDefineDialogBox.CloseWithResult(mode);

            };

            return await legacyCotentDefineDialogBox.WaitAsync();

        }

        #endregion

    }

}