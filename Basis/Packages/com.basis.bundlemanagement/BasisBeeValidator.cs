using System;
public static class BasisBeeValidator
{
    public static string BuildResultError(string prefix, bool hasInnerError, bool hasCode, string inner, long code)
    {
        string codeStr = hasCode ? " (HTTP " + code + ")" : string.Empty;
        return string.IsNullOrWhiteSpace(inner) ? prefix + codeStr + "." : prefix + codeStr + ": " + inner;
    }
    public static string FormatException(string prefix, Exception ex)
    {
        string innerMsg = ex.InnerException != null ? " | Inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message : string.Empty;
        return prefix + ": " + ex.GetType().Name + ": " + ex.Message + innerMsg + "\n" + ex.StackTrace;
    }
    public static bool IsValidBundleWrapper(BasisTrackedBundleWrapper bundleWrapper, out string error)
    {
        error = string.Empty;
        if (bundleWrapper is null)
        {
            error = "BasisTrackedBundleWrapper is null.";
            BasisDebug.LogError(error);
            return false;
        }
        BasisLoadableBundle lb = bundleWrapper.LoadableBundle;
        if (lb is null)
        {
            error = "LoadableBundle is null.";
            BasisDebug.LogError(error);
            return false;
        }
        if (lb.BasisRemoteBundleEncrypted is null)
        {
            error = "BasisRemoteBundleEncrypted is null.";
            BasisDebug.LogError(error);
            return false;
        }
        if (lb.BasisLocalEncryptedBundle is null)
        {
            BasisDebug.Log("BasisLocalEncryptedBundle is null (will be set after download if needed).");
        }
        return true;
    }
    public static bool IsValidUrl(string url, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "URL is null or empty.";
            BasisDebug.LogError(error);
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            error = "URL is not a valid absolute URI: '" + url + "'.";
            BasisDebug.LogError(error);
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "Unsupported URL scheme '" + uri.Scheme + "'. Only HTTP/HTTPS are supported.";
            BasisDebug.LogError(error);
            return false;
        }
        return true;
    }

    public static bool IsValidConnector(BasisBundleConnector connector, out string error)
    {
        if (connector is null)
        {
            error = "Failed to decrypt meta file: BasisBundleConnector is null.";
            BasisDebug.LogError(error);
            return false;
        }
        error = string.Empty;
        return true;
    }
    /// <summary>
    /// Validates wrapper, password, and URL in one pass (used by download flows).
    /// </summary>
    public static bool ValidateWrapperPasswordAndUrl(BasisTrackedBundleWrapper wrapper, out string url, out string error)
    {
        url = string.Empty;
        error = string.Empty;
        if (!IsValidBundleWrapper(wrapper, out string wrapperErr))
        {
            error = wrapperErr;
            return false;
        }
        if (string.IsNullOrWhiteSpace(wrapper.LoadableBundle!.UnlockPassword))
        {
            error = "Unlock password is null or empty.";
            return false;
        }
        url = wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
        if (!IsValidUrl(url, out string urlErr))
        {
            error = urlErr;
            return false;
        }
        return true;
    }
}
