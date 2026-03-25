using System;
using System.Threading;
using System.Threading.Tasks;
using Basis.BasisUI.Styling;
using Basis.Scripts.UI.UI_Panels;
using TMPro;
using UnityEngine;
using static Basis.BasisUI.LibraryProvider;
using static Basis.BasisUI.PanelButton;
using static Basis.BasisUI.PanelPasswordField;
using static Basis.BasisUI.PanelTextField;

namespace Basis.BasisUI
{
    public class LibraryProviderDialogAdd
    {
        #region PromptUserForNewContent, AddNewNewItemKey, ChangeInputFieldStyle

         private static void ChangeInputFieldStyle(GameObject inputFieldObject, bool isError)
        {
            if (inputFieldObject == null) return;

            if (!inputFieldObject.TryGetComponent(out UiStyleImage styleImage))
                return;

            string newStyle = isError ? "Button Caution" : "Button Standard";

            if (styleImage.ColorStyle == newStyle)
                return;

            styleImage.SetStyle(newStyle);
        }

        // not super clean but will do for now, used to update interactable input fields
        private static void UpdateInputFieldInteractability(PanelTextField URLTextField, PanelPasswordField PasswordTextField, DialogBox<BasisDataStoreItemKeys.ItemKey> activeDialog)
        {
            URLTextField._inputField.interactable = !activeDialog.IsBusy;
            PasswordTextField._inputField.interactable = !activeDialog.IsBusy;
        }

        /// <summary>
        /// Invoked on the add new content is pressed in the library provider menu, to prompt the user to enter new content with a dialog box
        /// </summary>
        public static async Task<BasisDataStoreItemKeys.ItemKey> PromptUserForNewContent(BasisMenuPanel panel)
        {
            // Build overlay using DialogBox helper
            DialogBox<BasisDataStoreItemKeys.ItemKey> newItemDialogBox = DialogBox<BasisDataStoreItemKeys.ItemKey>.Create(panel, new Vector2(930, 600),
                "Add New Content",
                "Please provide the URL and password for your BEE file. Ensure your url and pass are correct or the item wont be included in your library.",
                AddressableAssets.Sprites.Add);

            // create the exit button for the dialog box
            var button = PanelButton.CreateNew(ButtonStyles.ExitButton, newItemDialogBox.Descriptor.Header);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            button.OnClicked += () => newItemDialogBox.Cancel(null);

            // panel group for the fields
            PanelTabGroup panelGroup = PanelTabGroup.CreateNew(PanelTabGroup.TabGroupStyles.VerticalStackedNoBackground, newItemDialogBox.Descriptor.ContentParent);
            panelGroup.Descriptor.SetHeight(400);
            panelGroup.Descriptor.SetWidth(900);

            // BEE file URL field
            PanelTextField URL = PanelTextField.CreateNew(TextFieldStyles.EntryVertical, panelGroup.TabButtonParent);
            URL._placeholderLabel.text = "URL";
            URL._inputField.contentType = TMP_InputField.ContentType.Standard;
            URL.Descriptor.SetHeight(115);
            URL.Descriptor.SetWidth(700);
            URL.Descriptor.SetTitle("BEE File URL:");
            URL.Descriptor.SetIcon(AddressableAssets.Sprites.Network);
            URL.Descriptor.SetDescription("This should be a direct link to your BEE file.");

            PanelPasswordField Password = PanelPasswordField.CreateNew(PasswordFieldStyles.EntryVertical, panelGroup.TabButtonParent);
            Password._placeholderField.text = "Enter password";
            Password.Descriptor.SetHeight(115);
            Password.Descriptor.SetWidth(700);

            Password.Descriptor.SetTitle("BEE File Password:");
            Password.Descriptor.SetIcon(AddressableAssets.Sprites.Unlocked);
            Password.Descriptor.SetDescription("This is the password that was generated with you BEE file.");

            // create a text field to show validation error messages, initially empty
            PanelTextField validationMessageField = PanelTextField.CreateNew(TextFieldStyles.EntryWarning, panelGroup.TabButtonParent);
            validationMessageField.Descriptor.gameObject.SetActive(false);
            validationMessageField._inputField.gameObject.SetActive(false); // disable the text input field box
            validationMessageField.Descriptor.SetTitle("AWAITING_INPUT");
            validationMessageField.Descriptor.SetDescription("AWAITING_INPUT");
            validationMessageField.Descriptor.TitleLabel.color = Color.yellow;
            validationMessageField.Descriptor.DescriptionLabel.color = Color.yellow;

            validationMessageField.Descriptor.SetHeight(50);
            validationMessageField.Descriptor.SetWidth(700);

            // //load immediate
            // bool loadImmediate = false; // recommended to be false
            // PanelToggle contentPersistenceToggle = PanelToggle.CreateNew(panelGroup.TabButtonParent, PanelToggle.Styles.Entry);
            // contentPersistenceToggle.SetValueWithoutNotify(loadImmediate);
            // contentPersistenceToggle.Descriptor.SetTitle("Load Immediate");
            // contentPersistenceToggle.Descriptor.SetIcon(AddressableAssets.Sprites.FileTray);
            // contentPersistenceToggle.Descriptor.SetDescription("Loads content straight after verification.");
            // contentPersistenceToggle.Descriptor.SetSize(new Vector2(700, 50));
            // contentPersistenceToggle.OnValueChanged = (val) =>
            // {
            //     loadImmediate = val;
            // };

            // Add and Cancel buttons
            PanelTabGroup acceptOrDenyPanel = PanelTabGroup.CreateNew(newItemDialogBox.Descriptor, LayoutDirection.HorizontalNoBackground);

            acceptOrDenyPanel.Descriptor.SetHeight(50);
            acceptOrDenyPanel.Descriptor.SetWidth(900);

            PanelButton yesPanel = PanelButton.CreateNew(ButtonStyles.AcceptButton, acceptOrDenyPanel.TabButtonParent); //ButtonStyles.Cancel
            yesPanel.Descriptor.SetTitle("Add");
            yesPanel.Descriptor.SetWidth(900);
            yesPanel.Descriptor.SetHeight(60);

            // Add does the async work, then closes.
            yesPanel.OnClicked += async () =>
            {
                if (newItemDialogBox.IsBusy) return;
                newItemDialogBox.IsBusy = true;

                // update interactability for fields based on dialog busy
                UpdateInputFieldInteractability(URL, Password, newItemDialogBox);

                try
                {

                    // perform input validation, pass our current url and password along with the existing library entries to check for duplicates
                    InputValidation.EntryValidationResponse validationResponse = InputValidation.ValidateEntry(URL.Value, Password.Password, BasisDataStoreItemKeys.DisplayKeys());

                    // get the result of the validationResponse
                    InputValidation.EntryValidationResult validationResult = validationResponse.Result;

                    // we now use the validation result to determine whether to proceed with adding the item or show an error message
                    switch (validationResult)
                    {
                        case InputValidation.EntryValidationResult.Success:
                            // if validation succeeded, proceed with adding the item

                            if (validationMessageField.enabled)
                            {
                                validationMessageField.enabled = false; // hide any previous error message
                            }

                            // reset the fields
                            ChangeInputFieldStyle(URL._inputField.gameObject, false);
                            ChangeInputFieldStyle(Password._inputField.gameObject, false);

                            // perform a meta-only validation of the provided BEE file before adding the key
                            try
                            {
                                if (!validationMessageField.Descriptor.gameObject.activeSelf)
                                    validationMessageField.Descriptor.gameObject.SetActive(true);

                                validationMessageField.Descriptor.SetTitle("Validating BEE file");
                                validationMessageField.Descriptor.SetDescription("Checking BEE metadata...");

                                // temp item do not use to add new item with!
                                BasisDataStoreItemKeys.ItemKey tempItem = new BasisDataStoreItemKeys.ItemKey
                                {
                                    Pass = validationResponse.Password,
                                    Url = validationResponse.ProcessedUrl,
                                    Mode = 0, // we are going to infer from the type of data the item is
                                };

                                var tempWrapper = CreateNewWrapperFromItem(tempItem);

                                BasisProgressReport Report = new BasisProgressReport();
                                CancellationTokenSource CancellationSource = new CancellationTokenSource();

                                // Attempt a meta-only load (this will download or read connector info and cache meta on disk)
                                bool isValid = await BasisBeeManagement.HandleMetaOnlyLoad(tempWrapper.basisTrackedBundleWrapper, Report, CancellationSource.Token);

                                if (isValid)
                                {
                                    // Attempt to read the metadata back from disk into the wrapper
                                    BasisLoadableBundleWrapper loaded = await LoadWrapperFromDisc(tempItem, tempWrapper);

                                    // infered item type
                                    BundledContentHolder.Mode itemType = BundledContentHolder.Mode.Legacy;

                                    // grab the meta data
                                    if (loaded.BasisLoadableBundle?.BasisBundleConnector?.MetaData != null)
                                    {
                                        if (loaded.BasisLoadableBundle.BasisBundleConnector.MetaData.ComponentNames != null)
                                        {
                                            //BasisDebug.Log($"BasisComponentNames = {loaded.BasisLoadableBundle.BasisBundleConnector.MetaData.ComponentNames}");
                                            //BasisDebug.Log($"BasisComponentNamesLength = {loaded.BasisLoadableBundle.BasisBundleConnector.MetaData.ComponentNames.Length}");

                                            // lets attempt to find out what type of item it is?

                                            // grab components
                                            foreach (BasisBundleConnector.BasisComponentName comp in loaded.BasisLoadableBundle.BasisBundleConnector.MetaData.ComponentNames)
                                            {
                                                //BasisDebug.Log($"BasisComponentName = {comp.Name} count = {comp.count}");
                                                switch (comp.Name.ToLower())
                                                {
                                                    case "basisprop":
                                                        itemType = BundledContentHolder.Mode.Prop;
                                                        break;
                                                    case "basisavatar":
                                                        itemType = BundledContentHolder.Mode.Avatar;
                                                        break;
                                                    case "basisscene":
                                                        itemType = BundledContentHolder.Mode.World;
                                                        break;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            BasisDebug.LogWarning($"Warning BEE file from url = {tempItem.Url} does not contain metadata ComponentNames, consider updating it!");
                                        }
                                    }
                                    else
                                    {
                                        BasisDebug.LogWarning($"Warning BEE file from url = {tempItem.Url} does not contain metadata, consider updating it!");
                                    }

                                    // if the provided content did not change the item type assume its legacy or old BEE file with no metadata
                                    if (itemType == BundledContentHolder.Mode.Legacy)
                                    {
                                        // prompt them for what content
                                        itemType = await LibraryProviderDialogLegacyContent.PromptUserToDefineLegacyContent(panel);

                                        // if for whatever reason they did not enter anything else other than legacy?
                                        if (itemType == BundledContentHolder.Mode.Legacy)
                                        {
                                            // Still legacy? Yea no goodbye
                                            throw new Exception("Request Denied. Please specify content type for your legacy content.");
                                        }
                                    }

                                    // add the item to the basis key store
                                    await AddNewNewItemKey(itemType, validationResponse.ProcessedUrl, validationResponse.Password);
                                    
                                    // just close the overlay
                                    newItemDialogBox.CloseWithResult(null);

                                    // set the tab
                                    TrySwitchToTabFromItemType( itemType );

                                    // switch to the page
                                    await RefreshCurrentTab();
                                }
                                else
                                {
                                    throw new Exception("The provided BEE file url could not provide the bundle array.");
                                }
                            }
                            catch (Exception ex)
                            {
                                BasisDebug.LogError(ex);
                                ChangeInputFieldStyle(URL._inputField.gameObject, true);
                                ChangeInputFieldStyle(Password._inputField.gameObject, true);

                                if (!validationMessageField.Descriptor.gameObject.activeSelf)
                                    validationMessageField.Descriptor.gameObject.SetActive(true);

                                validationMessageField.Descriptor.SetTitle("BEE Validation Error");
                                validationMessageField.Descriptor.SetDescription($"Failed to validate BEE file: {ex.Message}");

                                newItemDialogBox.IsBusy = false;

                                // update interactability for fields based on dialog busy
                                UpdateInputFieldInteractability(URL, Password, newItemDialogBox);

                                return;
                            }

                            return;
                        case InputValidation.EntryValidationResult.EmptyUrl:
                            ChangeInputFieldStyle(URL._inputField.gameObject, true);
                            break;
                        case InputValidation.EntryValidationResult.InvalidUrlFormat:
                            ChangeInputFieldStyle(URL._inputField.gameObject, true);
                            break;
                        case InputValidation.EntryValidationResult.InvalidUrlScheme:
                            ChangeInputFieldStyle(URL._inputField.gameObject, true);
                            break;
                        case InputValidation.EntryValidationResult.EmptyPassword:
                            ChangeInputFieldStyle(URL._inputField.gameObject, false);
                            ChangeInputFieldStyle(Password._inputField.gameObject, true);
                            break;
                        case InputValidation.EntryValidationResult.DuplicateEntry:
                            ChangeInputFieldStyle(URL._inputField.gameObject, true);
                            ChangeInputFieldStyle(Password._inputField.gameObject, true);
                            break;
                        default:
                            BasisDebug.LogWarning("validation result returned unknown result unable to handle visual representation on UI.");
                            break;
                    }

                    // re-enable input
                    URL._inputField.interactable = true;
                    Password._inputField.interactable = true;

                    // if validation failed, show an error message and do not proceed
                    string errorMessage = validationResult switch
                    {
                        InputValidation.EntryValidationResult.EmptyUrl => "URL cannot be empty.",
                        InputValidation.EntryValidationResult.InvalidUrlFormat => "URL format is invalid.",
                        InputValidation.EntryValidationResult.InvalidUrlScheme => "URL must start with http:// or https://",
                        InputValidation.EntryValidationResult.EmptyPassword => "Password cannot be empty.",
                        InputValidation.EntryValidationResult.DuplicateEntry => "An entry with this URL already exists in your library.",
                        _ => "Unknown validation error."
                    };

                    if (!validationMessageField.Descriptor.gameObject.activeSelf)
                        validationMessageField.Descriptor.gameObject.SetActive(true);

                    // setting the title and desc auto enables the game object anyway
                    validationMessageField.Descriptor.SetTitle(validationResult.ToString());
                    validationMessageField.Descriptor.SetDescription(errorMessage);

                    // For simplicity, using Debug.LogWarning. In a real implementation, you would want to show this in the UI.
                    BasisDebug.LogWarning(errorMessage);
                    newItemDialogBox.IsBusy = false;

                    // update interactability for fields based on dialog busy
                    UpdateInputFieldInteractability(URL, Password, newItemDialogBox);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                    newItemDialogBox.IsBusy = false;
                    // update interactability for fields based on dialog busy
                    UpdateInputFieldInteractability(URL, Password, newItemDialogBox);
                }
            };

            // await until user closes or accepts
            return await newItemDialogBox.WaitAsync();
        }

        #endregion
    }

}