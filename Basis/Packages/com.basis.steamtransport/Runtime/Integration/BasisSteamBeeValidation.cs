using Basis.BasisUI;
using System;
using System.Threading;
using System.Threading.Tasks;
using static Basis.Scripts.UI.UI_Panels.BasisDataStoreItemKeys;

namespace Basis.Scripts.Networking.Steam
{
    public static class BasisSteamBeeValidation
    {
        public static async Task<BasisSteamBeeValidationResult> ValidateWorldAsync(string url, string password, CancellationToken cancellationToken = default)
        {
            BasisSteamBeeValidationResult result = new BasisSteamBeeValidationResult
            {
                WorldUrl = url ?? string.Empty,
                WorldPassword = password ?? string.Empty,
            };

            InputValidation.EntryValidationResponse validationResponse = InputValidation.ValidateEntry(url, password, Array.Empty<ItemKey>());
            if (validationResponse.Result != InputValidation.EntryValidationResult.Success)
            {
                result.ErrorMessage = validationResponse.Result switch
                {
                    InputValidation.EntryValidationResult.EmptyUrl => "URL cannot be empty.",
                    InputValidation.EntryValidationResult.InvalidUrlFormat => "URL format is invalid.",
                    InputValidation.EntryValidationResult.InvalidUrlScheme => "URL must start with http:// or https://",
                    InputValidation.EntryValidationResult.EmptyPassword => "Password cannot be empty.",
                    _ => "BEE validation input failed."
                };
                return result;
            }

            ItemKey tempItem = new ItemKey
            {
                Pass = validationResponse.Password,
                Url = validationResponse.ProcessedUrl,
                Mode = 0
            };

            var tempWrapper = LibraryProvider.CreateNewWrapperFromItem(tempItem);
            BasisProgressReport report = new BasisProgressReport();

            bool isValid = await BasisBeeManagement.HandleMetaOnlyLoad(tempWrapper.basisTrackedBundleWrapper, report, cancellationToken);
            if (!isValid)
            {
                result.ErrorMessage = "The provided BEE file could not be validated.";
                return result;
            }

            var loaded = await LibraryProvider.LoadWrapperFromDisc(tempItem, tempWrapper);
            if (loaded?.BasisLoadableBundle?.BasisBundleConnector == null)
            {
                result.ErrorMessage = "Validated BEE file did not provide bundle connector metadata.";
                return result;
            }

            BasisBundleConnector connector = loaded.BasisLoadableBundle.BasisBundleConnector;
            result.WorldName = connector.BasisBundleDescription?.AssetBundleName ?? validationResponse.ProcessedUrl;

            bool isWorld = false;
            if (connector.MetaData.ComponentNames != null)
            {
                foreach (BasisBundleConnector.BasisComponentName component in connector.MetaData.ComponentNames)
                {
                    if (string.Equals(component.Name, "BasisScene", StringComparison.OrdinalIgnoreCase))
                    {
                        isWorld = true;
                        break;
                    }
                }
            }

            if (!isWorld)
            {
                result.ErrorMessage = "The provided BEE file is valid, but it is not a world scene bundle.";
                return result;
            }

            result.WorldUrl = validationResponse.ProcessedUrl;
            result.WorldPassword = validationResponse.Password;
            result.IsValid = true;
            return result;
        }
    }
}
