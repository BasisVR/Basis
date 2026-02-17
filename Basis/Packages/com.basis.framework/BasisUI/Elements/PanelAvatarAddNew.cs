using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelAvatarAddNew : PanelComponent
    {
        public GameObject NewAvatarOverlay;
        public TMP_InputField NewURLField;
        public PanelPasswordField NewPasswordField;
        public TextMeshProUGUI ErrorLabel;
        public PanelButton AddButton;
        public PanelButton CancelButton;
        public GameObject OptionsPanel;
        public GameObject LoadingPanel;
        public PanelAvatarList AvatarList;

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();

            AddButton.OnClicked += () => _ = TryAddAvatar();
            CancelButton.OnClicked += () => NewAvatarOverlay.SetActive(false);
        }

        public void Show()
        {
            ErrorLabel.SetText(string.Empty);
            NewAvatarOverlay.SetActive(true);
            NewURLField.SetTextWithoutNotify(string.Empty);
            NewPasswordField.SetValue(false);
            NewPasswordField.SetPassword(string.Empty);
            OptionsPanel.SetActive(true);
            LoadingPanel.SetActive(false);
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }

        private async Task TryAddAvatar()
        {
            try
            {
                // Trim leading and trailing whitespace from the URL
                string processedUrl = NewURLField.text.Trim();
                string password = NewPasswordField.Password.Trim();

                // The fragment may contain an avatar password.
                // Strip it, and overwrite the URL to prevent it from showing up in logs.
                // Additionally, `ApplyPlatformConversionOfUrl` may drop the fragment when it converts the URL.
                // Therefore, this must happen before then.
                string[] fragments = processedUrl.Split('#', 2);
                processedUrl = fragments[0];

                string fragment = fragments.ElementAtOrDefault(1);

                if (ApplyPlatformConversionOfUrl(processedUrl, out string link))
                {
                    processedUrl = link;
                }

                ValidateURL(processedUrl, out string errorReason, out bool isValid);
                if (!isValid)
                {
                    ErrorLabel.SetText($"Invalid URL format: {errorReason}");
                    return;
                }

                NewURLField.SetTextWithoutNotify(processedUrl);

                if (!string.IsNullOrEmpty(fragment))
                {
                    try
                    {
                        password = Encoding.UTF8.GetString(Convert.FromBase64String(fragment));
                        NewPasswordField.SetPassword(password);
                    }
                    catch { }
                }

                // Password validation
                if (string.IsNullOrEmpty(password))
                {
                    ErrorLabel.SetText("The password field is empty.");
                    return;
                }

                // Does the avatar list contain the given URL?
                BasisDataStoreAvatarKeys.AvatarKey[] activeKeys = BasisDataStoreAvatarKeys.DisplayKeys();
                bool keyExists = false;
                for (int keysIndex = 0; keysIndex < activeKeys.Length; keysIndex++)
                {
                    var cur = activeKeys[keysIndex];
                    if (cur != null && cur.Url == processedUrl && cur.Pass == password)
                    {
                        keyExists = true;
                        break;
                    }
                }

                if (keyExists)
                {
                    ErrorLabel.SetText("The avatar key with the same URL and Password already exists. No duplicate will be added.");
                    return;
                }

                // Data validated! Now to load the avatar...

                OptionsPanel.SetActive(false);
                LoadingPanel.SetActive(true);

                BasisLoadableBundle loadableBundle = new()
                {
                    UnlockPassword = password,
                    BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = processedUrl },
                    BasisBundleConnector = new BasisBundleConnector(),
                    BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle()
                };

                await BasisLocalPlayer.Instance.CreateAvatar(0, loadableBundle);
                BasisDataStoreAvatarKeys.AvatarKey avatarKey = new() { Url = processedUrl, Pass = password };
                await BasisDataStoreAvatarKeys.AddNewKey(avatarKey);

                NewAvatarOverlay.SetActive(false);

                await AvatarList.AppendNewAvatar(loadableBundle, true);
            }
            catch (Exception ex)
            {
                ErrorLabel.SetText($"Error during avatar creation: {ex.Message}");
            }
        }

        public bool ApplyPlatformConversionOfUrl(string sharedLink, out string convertedLink)
        {
            if (IsGoogleDriveLink(sharedLink))
            {
                BasisDebug.Log("Was a Google Drive Link Converting!");
                string fileId = ExtractFileId(sharedLink);
                if (!string.IsNullOrEmpty(fileId))
                {
                    convertedLink = $"https://drive.google.com/uc?export=download&id={fileId}";
                    return true;
                }
                else
                {
                    BasisDebug.LogError("Could not extract File ID from the shared link. Was detected as a google drive", BasisDebug.LogTag.System);
                }
            }
            convertedLink = string.Empty;
            return false;
        }

        private bool IsGoogleDriveLink(string url)
        {
            return Regex.IsMatch(url, @"^https:\/\/drive\.google\.com\/file\/d\/[a-zA-Z0-9_-]+\/");
        }

        private string ExtractFileId(string url)
        {
            Match match = Regex.Match(url, @"\/file\/d\/([a-zA-Z0-9_-]+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        private void ValidateURL(string url, out string errorReason, out bool valid)
        {
            valid = Uri.TryCreate(url, UriKind.Absolute, out Uri uriResult);
            if (!valid)
            {
                errorReason = "URL is not a valid absolute URL.";
                return;
            }

            if (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps)
            {
                valid = false;
                errorReason = "URL must start with http:// or https://";
                return;
            }

            errorReason = string.Empty;
            valid = true;
        }

    }
}
