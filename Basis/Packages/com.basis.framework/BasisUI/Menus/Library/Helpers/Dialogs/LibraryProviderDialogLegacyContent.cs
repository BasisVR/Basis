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
                "Specify Content Type",
                "Your content is marked legacy. Please specify what type of content you are adding.",
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
            createdInformationTextField.Descriptor.SetTitle("Why is this showing?");
            createdInformationTextField.Descriptor.SetIcon(AddressableAssets.Sprites.Information);
            createdInformationTextField.Descriptor.SetDescription($"BEE files now contain metadata of what's inside it, this is used to determine things about your BEE file which will be important in future features. Please to consider updating your content to include this metadata.");

            createdInformationTextField.Descriptor.SetHeight(100);
            createdInformationTextField.Descriptor.SetWidth(800);

            // the item type dropdown determines which library tab the new item will appear in.
            PanelDropdown contentTypeDropDown = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.OverlayEntry, legacyCotentDefineDialogBox.Descriptor);
            var modeNames = Enum.GetValues(typeof(BundledContentHolder.Mode))
                    .Cast<BundledContentHolder.Mode>()
                    .Where(m => m != BundledContentHolder.Mode.Legacy) // remove legacy from selection
                    .Select(m => m.ToString())
                    .ToArray();
            contentTypeDropDown.Descriptor.SetTitle("Content Type");
            contentTypeDropDown.Descriptor.SetIcon(AddressableAssets.Sprites.FileTray);
            contentTypeDropDown.Descriptor.SetDescription("What content are you adding?");
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
            yesPanel.Descriptor.SetTitle("Add");
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