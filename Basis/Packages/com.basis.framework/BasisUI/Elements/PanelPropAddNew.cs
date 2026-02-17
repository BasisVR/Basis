using Basis.Scripts.UI.UI_Panels;
using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class PanelPropAddNew : PanelComponent
    {
        [Header("UI Refs")]
        public GameObject NewPropOverlay;
        public TMP_InputField NewURLField;
        public PanelPasswordField NewPasswordField;
        public TextMeshProUGUI ErrorLabel;
        public PanelButton AddButton;
        public PanelButton CancelButton;
        public GameObject OptionsPanel;
        public GameObject LoadingPanel;
        public Toggle NewPersistentToggle;

        [Header("Target List")]
        public PanelPropsList PropsList;

        [Header("Behaviour")]
        [Tooltip("If true, automatically selects the newly added entry.")]
        public bool SelectAfterAdd = true;

        [Tooltip("If true, immediately spawns/loads after adding to the list.")]
        public bool AutoSpawnOnAdd = false;

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            AddButton.OnClicked += () => _ = TryAddProp();
            CancelButton.OnClicked += () => NewPropOverlay.SetActive(false);
        }

        public void Show()
        {
            ErrorLabel.SetText(string.Empty);
            NewPropOverlay.SetActive(true);

            NewURLField.SetTextWithoutNotify(string.Empty);
            NewPasswordField.SetValue(false);
            NewPasswordField.SetPassword(string.Empty);

            if (NewPersistentToggle != null)
                NewPersistentToggle.SetIsOnWithoutNotify(false);

            OptionsPanel.SetActive(true);
            LoadingPanel.SetActive(false);
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }

        private async Task TryAddProp()
        {
            try
            {
                if (PropsList == null)
                {
                    ErrorLabel.SetText("PropsList reference is missing.");
                    return;
                }

                // Trim URL + password.
                string processedUrl = (NewURLField.text ?? string.Empty).Trim();
                string password = (NewPasswordField.Password ?? string.Empty).Trim();

                // Handle fragment password (#base64password)
                string[] fragments = processedUrl.Split('#', 2);
                processedUrl = fragments[0];
                string fragment = fragments.ElementAtOrDefault(1);

                // Convert platform link (e.g. Google Drive share) to direct download.
                if (ApplyPlatformConversionOfUrl(processedUrl, out string link))
                    processedUrl = link;

                // Validate URL
                ValidateURL(processedUrl, out string errorReason, out bool isValid);
                if (!isValid)
                {
                    ErrorLabel.SetText($"Invalid URL format: {errorReason}");
                    return;
                }

                NewURLField.SetTextWithoutNotify(processedUrl);

                // If URL fragment exists, attempt to decode it as password.
                if (!string.IsNullOrEmpty(fragment))
                {
                    try
                    {
                        password = Encoding.UTF8.GetString(Convert.FromBase64String(fragment));
                        NewPasswordField.SetPassword(password);
                    }
                    catch
                    {
                        // ignore invalid base64
                    }
                }

                // Password validation
                if (string.IsNullOrEmpty(password))
                {
                    ErrorLabel.SetText("The password field is empty.");
                    return;
                }

                bool persistent = NewPersistentToggle != null && NewPersistentToggle.isOn;

                // Duplicate check against stored prop keys
                BasisDataStoreItemKeys.ItemKey[] activeKeys = BasisDataStoreItemKeys.DisplayKeys();
                bool keyExists = false;

                for (int i = 0; i < activeKeys.Length; i++)
                {
                    var cur = activeKeys[i];
                    if (cur != null && cur.Url == processedUrl && cur.Pass == password)
                    {
                        keyExists = true;
                        break;
                    }
                }

                if (keyExists)
                {
                    ErrorLabel.SetText("A prop/world entry with the same URL and Password already exists. No duplicate will be added.");
                    return;
                }

                // UI: show loading
                OptionsPanel.SetActive(false);
                LoadingPanel.SetActive(true);

                // Create a minimal loadable bundle entry (meta/icon fills later in PanelPropsList via HandleMetaOnlyLoad)
                BasisLoadableBundle loadableBundle = new()
                {
                    UnlockPassword = password,
                    BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = processedUrl },
                    BasisBundleConnector = new BasisBundleConnector(),
                    BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle()
                };

                // Store key (includes Persistent)
                BasisDataStoreItemKeys.ItemKey propKey = new()
                {
                    Url = processedUrl,
                    Pass = password,
                    ISEmbedded = false,
                };

                await BasisDataStoreItemKeys.AddNewKey(propKey);

                // Close overlay
                NewPropOverlay.SetActive(false);

                // Add to list UI
                await PropsList.AppendNewProp(loadableBundle, SelectAfterAdd);

                // Optionally spawn immediately
                if (AutoSpawnOnAdd)
                    await PropsList.SpawnSelectedNewInstance();
            }
            catch (Exception ex)
            {
                ErrorLabel.SetText($"Error during prop/world add: {ex.Message}");
            }
            finally
            {
                // Ensure panels reset if overlay stays open (in error cases)
                if (NewPropOverlay.activeSelf)
                {
                    OptionsPanel.SetActive(true);
                    LoadingPanel.SetActive(false);
                }
            }
        }

        // --------------------------------------------------------------------
        // URL conversions (same behaviour as your avatar version)
        // --------------------------------------------------------------------
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
            return Regex.IsMatch(url ?? string.Empty, @"^https:\/\/drive\.google\.com\/file\/d\/[a-zA-Z0-9_-]+\/");
        }

        private string ExtractFileId(string url)
        {
            Match match = Regex.Match(url ?? string.Empty, @"\/file\/d\/([a-zA-Z0-9_-]+)");
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
