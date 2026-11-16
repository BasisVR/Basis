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

        private static string DescribeUnreachable(string url, string error)
        {
            string host = Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? uri.Host : url;
            error ??= string.Empty;
            if (error.IndexOf("Unable to complete SSL connection", StringComparison.OrdinalIgnoreCase) >= 0)
                return Basis.BasisUI.BasisLocalization.Get("library.dialog.add.error.tlsHandshake", host);
            string blocked = Between(error, "resolves to a blocked address (", ").");
            if (blocked != null)
                return Basis.BasisUI.BasisLocalization.Get("library.dialog.add.error.blockedAddress", Between(error, "host '", "'") ?? host, blocked);
            string reason = Between(error, "could not be validated (", ").") ?? Between(error, "Network error: ", ". Accept-Ranges=") ?? error;
            return Basis.BasisUI.BasisLocalization.Get("library.dialog.add.error.unreachable", host, reason);
        }

        private static string Between(string text, string open, string close)
        {
            int start = text.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) return null;
            start += open.Length;
            int end = text.IndexOf(close, start, StringComparison.Ordinal);
            return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
        }

        /// <summary>
        /// Invoked on the add new content is pressed in the library provider menu, to prompt the user to enter new content with a dialog box
        /// </summary>
        /// <param name="prefillUrl">Fills the URL field on open. Used by the BEE file drop, which
        /// already knows the <c>file://</c> location of what the user dropped.</param>
        /// <param name="prefillPassword">Fills the password field on open — the password a drop
        /// found next to the file but could not use unattended.</param>
        /// <param name="noticeTitle">Shown in the dialog's message row on open, so a drop can say
        /// why it handed the entry back to the user instead of adding it silently.</param>
        /// <param name="noticeBody">Body text for <paramref name="noticeTitle"/>.</param>
        public static async Task<BasisDataStoreItemKeys.ItemKey> PromptUserForNewContent(BasisMenuPanel panel,
            string prefillUrl = null,
            string prefillPassword = null,
            string noticeTitle = null,
            string noticeBody = null)
        {
            // Build overlay using DialogBox helper
            DialogBox<BasisDataStoreItemKeys.ItemKey> newItemDialogBox = DialogBox<BasisDataStoreItemKeys.ItemKey>.Create(panel, new Vector2(930, 600),
                Basis.BasisUI.BasisLocalization.Get("library.dialog.add.title"),
                Basis.BasisUI.BasisLocalization.Get("library.dialog.add.description"),
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
            URL._placeholderLabel.text = Basis.BasisUI.BasisLocalization.Get("library.dialog.add.urlPlaceholder");
            URL._inputField.contentType = TMP_InputField.ContentType.Standard;
            // Pasted links arrive wrapped in quotes often enough — Windows' "Copy as path", chat
            // clients — that dropping them as they land is worth it, so the box shows the URL that
            // will actually be fetched. ValidateEntry strips them again for the paths this misses.
            URL._inputField.onValidateInput = InputValidation.RejectQuoteCharacter;
            URL.Descriptor.SetHeight(115);
            URL.Descriptor.SetWidth(700);
            URL.Descriptor.SetTitle(Basis.BasisUI.BasisLocalization.Get("library.beeFileUrl"));
            URL.Descriptor.SetIcon(AddressableAssets.Sprites.Network);
            URL.Descriptor.SetDescription(Basis.BasisUI.BasisLocalization.Get("library.dialog.add.urlDescription"));
            URL.SetRequired(Basis.BasisUI.BasisLocalization.Get("ui.validation.requiredNamed",
                Basis.BasisUI.BasisLocalization.Get("library.beeFileUrl")), gradeImmediately: false);

            PanelPasswordField Password = PanelPasswordField.CreateNew(PasswordFieldStyles.EntryVertical, panelGroup.TabButtonParent);
            Password._placeholderField.text = Basis.BasisUI.BasisLocalization.Get("library.dialog.add.passwordPlaceholder");
            Password.Descriptor.SetHeight(115);
            Password.Descriptor.SetWidth(700);

            Password.Descriptor.SetTitle(Basis.BasisUI.BasisLocalization.Get("library.beeFilePassword"));
            Password.Descriptor.SetIcon(AddressableAssets.Sprites.Unlocked);
            Password.Descriptor.SetDescription(Basis.BasisUI.BasisLocalization.Get("library.beeFilePassword.description"));

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

            // A drop opens this dialog with what it already worked out. Assigning the input text
            // runs the fields' own change handlers, so Value/Password and the required-field
            // grading end up exactly where typing the same characters would have left them.
            if (!string.IsNullOrEmpty(prefillUrl))
            {
                URL._inputField.text = prefillUrl;
            }
            if (!string.IsNullOrEmpty(prefillPassword))
            {
                Password._inputField.text = prefillPassword;
            }
            if (!string.IsNullOrEmpty(noticeTitle) || !string.IsNullOrEmpty(noticeBody))
            {
                validationMessageField.Descriptor.gameObject.SetActive(true);
                validationMessageField.Descriptor.SetTitle(noticeTitle ?? string.Empty);
                validationMessageField.Descriptor.SetDescription(noticeBody ?? string.Empty);
            }

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
            yesPanel.Descriptor.SetTitle(Basis.BasisUI.BasisLocalization.Get("library.dialog.add.addButton"));
            yesPanel.Descriptor.SetWidth(900);
            yesPanel.Descriptor.SetHeight(60);

            // Add does the async work, then closes.
            yesPanel.OnClicked += async () =>
            {
                if (newItemDialogBox.IsBusy) return;
                if (!URL.Validate()) return;
                newItemDialogBox.IsBusy = true;

                // update interactability for fields based on dialog busy
                UpdateInputFieldInteractability(URL, Password, newItemDialogBox);

                try
                {

                    // perform input validation, pass our current url and password along with the existing library entries to check for duplicates
                    InputValidation.EntryValidationResponse validationResponse = InputValidation.ValidateEntry(URL.Value, Password.Password, BasisDataStoreItemKeys.DisplayKeys());
                    if (validationResponse.Result == InputValidation.EntryValidationResult.EmptyPassword)
                    {
                        InputValidation.EntryValidationResponse vrmResponse = InputValidation.ValidateEntry(URL.Value, string.Empty, BasisDataStoreItemKeys.DisplayKeys(), allowEmptyPassword: true);
                        if (vrmResponse.Result != InputValidation.EntryValidationResult.Success || await BasisVrmDocument.IsVrmLocationAsync(vrmResponse.ProcessedUrl, CancellationToken.None))
                        {
                            validationResponse = vrmResponse;
                        }
                    }

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

                                validationMessageField.Descriptor.SetTitle(Basis.BasisUI.BasisLocalization.Get("library.dialog.add.validating"));
                                validationMessageField.Descriptor.SetDescription(Basis.BasisUI.BasisLocalization.Get("library.dialog.add.checkingMetadata"));

                                (BundledContentHolder.Mode itemType, BasisMetaLoadResult meta) = await LibraryProvider.TryDetectModeFromUrl(
                                    validationResponse.ProcessedUrl,
                                    validationResponse.Password);

                                if (meta.IsTransient)
                                {
                                    ChangeInputFieldStyle(URL._inputField.gameObject, true);
                                    validationMessageField.Descriptor.SetTitle(Basis.BasisUI.BasisLocalization.Get("library.dialog.add.error.unreachable.title"));
                                    validationMessageField.Descriptor.SetDescription(DescribeUnreachable(validationResponse.ProcessedUrl, meta.Error));
                                    newItemDialogBox.IsBusy = false;
                                    UpdateInputFieldInteractability(URL, Password, newItemDialogBox);
                                    return;
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
                                TrySwitchToTabFromItemType(itemType);

                                // switch to the page
                                await RefreshCurrentTab();
                            }
                            catch (Exception ex)
                            {
                                BasisDebug.LogError(ex);
                                ChangeInputFieldStyle(URL._inputField.gameObject, true);
                                ChangeInputFieldStyle(Password._inputField.gameObject, true);

                                if (!validationMessageField.Descriptor.gameObject.activeSelf)
                                    validationMessageField.Descriptor.gameObject.SetActive(true);

                                validationMessageField.Descriptor.SetTitle(Basis.BasisUI.BasisLocalization.Get("library.dialog.add.validationError"));
                                validationMessageField.Descriptor.SetDescription(Basis.BasisUI.BasisLocalization.Get("library.dialog.add.validationErrorBody", ex.Message));

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
                        InputValidation.EntryValidationResult.EmptyUrl => Basis.BasisUI.BasisLocalization.Get("library.dialog.add.error.emptyUrl"),
                        InputValidation.EntryValidationResult.InvalidUrlFormat => Basis.BasisUI.BasisLocalization.Get("library.dialog.add.error.invalidUrlFormat"),
                        InputValidation.EntryValidationResult.InvalidUrlScheme => Basis.BasisUI.BasisLocalization.Get("library.dialog.add.error.invalidUrlScheme"),
                        InputValidation.EntryValidationResult.EmptyPassword => Basis.BasisUI.BasisLocalization.Get("library.dialog.add.error.emptyPassword"),
                        InputValidation.EntryValidationResult.DuplicateEntry => Basis.BasisUI.BasisLocalization.Get("library.dialog.add.error.duplicateEntry"),
                        _ => Basis.BasisUI.BasisLocalization.Get("library.dialog.add.error.unknown")
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